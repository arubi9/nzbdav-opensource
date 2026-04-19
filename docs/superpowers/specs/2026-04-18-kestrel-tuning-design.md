# Kestrel / .NET HTTP server tuning — design

## Problem

NZBDAV serves HTTP via Kestrel (via `WebApplication.CreateBuilder` +
ASP.NET Core pipeline). No explicit limits tuning in `Program.cs`.
Defaults target low-to-moderate-concurrency web apps, not a high-
concurrency streaming service.

Relevant defaults:
- `MaxConcurrentConnections`: unlimited (OK)
- `MaxConcurrentUpgradedConnections`: unlimited
- `KeepAliveTimeout`: 2 minutes
- `RequestHeadersTimeout`: 30 seconds
- `MaxRequestBodySize`: 30 MB (fine — we don't accept uploads on /api/stream)
- `Http2.MaxStreamsPerConnection`: 100
- Response buffering: enabled by default — buffers 4 KB before flushing

The response-buffering default causes **noticeable TTFB latency** on
Range responses: Kestrel waits for buffer fill before flushing first
bytes to client. For streaming this is the opposite of what we want.

The 2-minute `KeepAliveTimeout` closes idle conns between Range
requests during pause — client has to reconnect (costs 1-3 RTT) on
resume.

## Goal

Tune Kestrel for streaming workload characteristics:
1. No response buffering → minimum TTFB
2. Longer keep-alive → survive viewer pause
3. Support many concurrent HTTP/2 streams per connection (one TCP
   connection can serve all Range requests for one active viewer)
4. Enable `SendFile` for L1 disk responses → zero-copy kernel path
   instead of `read()` → `write()` round trip

## Changes

### 1. Kestrel limits (in `Program.cs`)

```csharp
builder.WebHost.ConfigureKestrel(options =>
{
    // Keep-alive long enough to survive user pause (10 min default is
    // normal Jellyfin session length idle threshold)
    options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(10);

    // Don't limit concurrent connections (OS ephemeral-port range limit
    // applies naturally); 0 means unbounded
    options.Limits.MaxConcurrentConnections = null;
    options.Limits.MaxConcurrentUpgradedConnections = null;

    // HTTP/2 multi-stream per connection — each concurrent Range gets
    // its own stream on one TCP conn. 256 gives good headroom.
    options.Limits.Http2.MaxStreamsPerConnection = 256;

    // We don't accept request bodies >1 MB anywhere — keep explicit
    // tight default to catch abuse
    options.Limits.MaxRequestBodySize = 1_048_576;

    // Request headers timeout — drop slowloris quickly
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);

    // Don't buffer response writes (see StreamExecutionService — we
    // already call Response.Body.FlushAsync aggressively; but ensure
    // Kestrel respects it)
    options.Limits.MinResponseDataRate = null;
});
```

### 2. Disable response buffering per-route

`StreamFileController.HandleStream` already writes to
`Response.Body` via `ServeStreamAsync`. Add:

```csharp
var feature = HttpContext.Features.Get<IHttpResponseBodyFeature>();
feature?.DisableBuffering();
```

So the first byte reaches the client immediately, not after 4 KB fill.

### 3. Enable SendFile for L1 disk reads

Kestrel's `Response.SendFileAsync(path, offset, count)` uses the
Linux `sendfile(2)` syscall — bytes move from disk to socket in kernel
space, bypassing user-space copy. For L1 cache hits this is ~20% CPU
savings and measurably lower latency.

In `StreamExecutionService.ServeStreamAsync` (or equivalent), branch
when the stream source is a `FileStream` with known file path:

```csharp
if (sourceStream is FileStream fs && HttpContext.Features.Get<IHttpResponseBodyFeature>() is { } bodyFeature)
{
    await bodyFeature.SendFileAsync(fs.Name, offset, length, ct);
    return;
}
// fallback: manual CopyToAsync
```

NZBDAV's `CachedYencStream` wraps a `FileStream`. Expose the
underlying file path for the SendFile fast path.

### 4. Thread pool warm-up

`.NET` thread pool grows on demand; cold start after restart shows
visible latency jitter for first ~30s. Pre-warm at startup:

```csharp
ThreadPool.SetMinThreads(workerThreads: 64, completionPortThreads: 64);
```

Values chosen for 8-vCPU box; scale with `Environment.ProcessorCount * 8`
for larger nodes.

### 5. Disable synchronous IO fallback

Default allows blocking reads/writes with warning. Streaming code must
stay async all the way — fail loudly if not:

```csharp
builder.WebHost.ConfigureKestrel(o => { /* ... */ });
builder.Services.Configure<KestrelServerOptions>(options =>
{
    options.AllowSynchronousIO = false;
});
```

## Verification

- **Load test:** `wrk -c 256 -d 60s -t 8 https://stream/api/stream/.../ --header "Range: bytes=0-1048575"` — observed p99 latency should not exceed p50 by >2×
- **TTFB test:** `curl -w 'ttfb=%{time_starttransfer}\n' -H 'Range: bytes=0-1023' https://.../` on a cached segment → should measure <20 ms
  (was ~48 ms pre-tune, mostly Kestrel response buffering)
- **SendFile verification:** `strace -p $(pidof dotnet) -e trace=sendfile,writev,write` during an active L1-hit stream → confirm
  `sendfile` syscalls predominating
- **Keep-alive verification:** Chrome DevTools → two Range requests in
  quick succession should share the same TCP connection (connection ID
  in the Timing tab)

## Performance expectations

Combined effect of response-buffering removal + SendFile + longer
keep-alive + pre-warmed thread pool:

| Metric | Before | After |
|---|---|---|
| TTFB on L1 hit | 48 ms | **~15 ms** |
| Cold start jitter first 30s after restart | visible 200-500 ms spikes | smooth |
| CPU per GB served (L1 hit) | ~2.5% of one core | **~1.8%** (sendfile savings) |
| Repeat Range within 10 min pause | new TCP+TLS handshake | same conn, 1 RTT |

Combined with the sysctl / BBR tuning already applied, this shaves
another 30-60 ms off typical cold-click-to-play and ~30% CPU off
L1-hit delivery.

## Rollback

Revert `Program.cs` Kestrel block, redeploy. All changes are host-side
only; no schema, no client-visible API changes.

## Non-goals

- Kestrel + Unix socket IPC to Caddy (we already use TCP localhost;
  perf delta is negligible)
- Enabling HTTP/3 on Kestrel itself (Caddy terminates h3 externally; internal hop stays h1/h2)
- Removing the OWASP-style header filters Caddy adds — those stay
