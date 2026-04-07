# Spec: L2 Segment Body Cache (Audit Item 8)

*Draft 2026-04-07 — suitable for handoff to Codex or another implementation agent*

Shared object-storage L2 cache for segment bodies, sitting behind the existing
per-node `LiveSegmentCache` (which becomes the L1). Enables multi-node
NZBDAV deployments to share cache across nodes, eliminating the bandwidth
duplication documented in the architectural audit (section 3.3).

This is the big multi-week architectural change from the audit. After this
spec lands, NZBDAV can scale past 3-4 streaming nodes without paying
proportionally more in NNTP bandwidth.

---

## Problem

Each streaming node today has its own `LiveSegmentCache` on local disk
(`/config/stream-cache`). HAProxy's `balance uri consistent hash` routes
the same stream URL to the same node so segments stick on one node —
good for steady-state, but bad during:

1. **Rebalance events.** When a node is added/removed, HAProxy reshuffles
   the hash ring. Streams that were owned by node A suddenly route to
   node B, which has a cold cache. Cold-cache misses cause NNTP refetches
   of segments that are already on node A's disk.

2. **Small-file precaching (SmallFilePrecacheService).** The ingest node
   pre-caches NFOs, posters, and subtitles to ITS disk. But those files
   are served by streaming nodes, which don't see the cache. Every small
   file is refetched from NNTP on first streaming-node access, completely
   defeating the precache's purpose.

3. **Media probing (MediaProbeService).** FFmpeg probe data lands on the
   ingest node's disk. Jellyfin reads it from any streaming node, which
   has to refetch the probe segments from NNTP.

4. **Two-node write races during rebalance flap.** If the hash ring
   briefly sends the same stream to two different nodes during a brief
   HAProxy flap, both nodes independently fetch the same segments from
   NNTP — double bandwidth cost, no coordination.

At 3-4 streaming nodes the bandwidth cost is manageable. At 10+ nodes the
duplication dominates and negates the point of horizontal scaling.

---

## Solution

Add a tier-2 shared object-storage cache that all nodes read from and
write to. The existing per-node `LiveSegmentCache` becomes L1; object
storage becomes L2. Segment IDs are immutable and content-addressed, so
no cache invalidation protocol is needed.

### Tiered Architecture

```
┌───────────────────────────────────────────────────────────────────────┐
│                          Segment Read Path                            │
├───────────────────────────────────────────────────────────────────────┤
│                                                                       │
│  1. L1 (LiveSegmentCache, per-node disk) — fastest, ~5ms              │
│        │                                                              │
│        │ miss                                                         │
│        ▼                                                              │
│  2. L2 (ObjectStorageCache, shared)      — ~20-50ms on local MinIO    │
│        │                                                              │
│        │ miss (404 from S3 GET)                                       │
│        ▼                                                              │
│  3. NNTP fetch (existing pipeline)       — ~100-500ms provider RTT    │
│        │                                                              │
│        │ success                                                      │
│        ▼                                                              │
│  4. Write-through:                                                    │
│     - L1: synchronous write (same as today)                           │
│     - L2: fire-and-forget enqueue to background writer                │
│                                                                       │
└───────────────────────────────────────────────────────────────────────┘
```

### Key Properties

1. **Read-through.** L1 miss → L2 GET → NNTP. Each tier is checked before
   the next more expensive one.
2. **Write-through (fire-and-forget).** NNTP fetch populates L1 synchronously
   and queues a background L2 write. L2 write failure does not fail the
   read.
3. **No invalidation.** yEnc article content is immutable for a given
   segment ID — once we have the bytes, they're correct forever. No TTL
   needed for correctness, only for cost.
4. **Eventually consistent.** A read that hits an L2 key BEFORE its
   write-through completes will see a 404 and fall back to NNTP. This is
   correct behavior: we'd rather refetch than return stale/missing data.
5. **Graceful degradation.** If L2 is unreachable, all reads fall through
   to NNTP (existing behavior). The L2 writer logs warnings but doesn't
   back-pressure reads.
6. **Feature-flagged.** L2 is opt-in via config. Single-node and existing
   multi-node deployments work unchanged until the operator enables L2.

---

## Dependencies

**New NuGet package:** `Minio` (official MinIO .NET SDK, also supports any
S3-compatible endpoint).

Rationale:
- Lighter than `AWSSDK.S3` (~500 KB vs several MB of native code)
- Cleaner API for the specific operations NZBDAV needs (GET/PUT/HEAD
  on one bucket, no policy/bucket-management APIs)
- Works against AWS S3, Backblaze B2, Cloudflare R2, DigitalOcean
  Spaces, and self-hosted MinIO identically
- Well-maintained, stable API

Add to `backend/NzbWebDAV.csproj`:

```xml
<PackageReference Include="Minio" Version="6.0.5" />
```

(Pin to a known-working version; update as needed during implementation.)

---

## Object Storage Layout

### Bucket

One bucket per NZBDAV deployment. Default name: `nzbdav-segments`.
Configurable via `cache.l2.bucket-name`.

### Object Key Structure

```
segments/{prefix}/{key}
```

Where:
- `{key}` = hex-encoded SHA-256 of the `segmentId` string (64 chars)
- `{prefix}` = first 2 characters of `{key}`

Example: segment `<abc123@example.com>` hashes to
`9f86d081884c7d659a2feaa0c55ad015...`, stored at
`segments/9f/9f86d081884c7d659a2feaa0c55ad015...`.

Rationale for this layout:
- **SHA-256 hash** normalizes length (segment IDs can be 200+ chars with
  special characters that confuse URL-safe key encoding)
- **2-char prefix sharding** distributes keys evenly for backends that
  list by prefix (helps debugging + housekeeping queries; modern S3
  doesn't need it for performance but costs nothing)
- **`segments/` root prefix** leaves room for future tiers
  (`probes/`, `manifests/`, etc.) in the same bucket
- **No file extension** — these are raw bytes, not typed files. S3 treats
  keys as opaque strings.

### Object Content

The raw decoded yEnc segment bytes. Identical to what L1 stores on disk
at `{CacheDirectory}/{segmentHash}`. L2 is just a copy of the same bytes
in a different place.

### Object Metadata

Set on each PUT:

| S3 Metadata Key | Value | Purpose |
|---|---|---|
| `x-amz-meta-segment-id` | Original (non-hashed) segment ID | Debugging: look up the original segment ID from the hashed key |
| `x-amz-meta-yenc-filename` | `yencHeader.FileName` | Debugging: identify the file this segment belongs to |
| `x-amz-meta-category` | `"video"` / `"small_file"` / `"unknown"` | L2 eviction tier hints (see below) |
| `x-amz-meta-owner-nzb-id` | `ownerNzbId` as GUID string (if set) | NZB deletion cleanup |
| `Content-Length` | Segment size in bytes | Standard S3 header, set automatically |

Note: `x-amz-meta-` is AWS-specific; MinIO translates this to `X-Amz-Meta-`
transparently. Backblaze B2 and Cloudflare R2 also translate it.

---

## New File: `ObjectStorageSegmentCache.cs`

```csharp
// backend/Clients/Usenet/Caching/ObjectStorageSegmentCache.cs

using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Minio;
using Minio.DataModel.Args;
using NzbWebDAV.Config;
using Serilog;

namespace NzbWebDAV.Clients.Usenet.Caching;

/// <summary>
/// L2 read-through / write-behind cache for segment bodies, backed by an
/// S3-compatible object store (MinIO, AWS S3, Backblaze B2, Cloudflare R2,
/// DigitalOcean Spaces). Shared across all nodes in a multi-node
/// deployment. See docs/plans/spec-l2-segment-body-cache.md for the full
/// design.
///
/// This class is ONLY instantiated when L2 is enabled via config. When
/// disabled, <see cref="LiveSegmentCache"/> operates in L1-only mode as
/// today.
/// </summary>
public sealed class ObjectStorageSegmentCache : IDisposable
{
    private readonly IMinioClient _client;
    private readonly string _bucketName;
    private readonly Channel<WriteRequest> _writeQueue;
    private readonly CancellationTokenSource _shutdownCts;
    private readonly Task _writerTask;
    private bool _disposed;

    // Counters for metrics (read via accessor properties)
    private long _l2Hits;
    private long _l2Misses;
    private long _l2Writes;
    private long _l2WriteFailures;
    private long _l2WritesDropped; // queue was full

    public long L2Hits => Interlocked.Read(ref _l2Hits);
    public long L2Misses => Interlocked.Read(ref _l2Misses);
    public long L2Writes => Interlocked.Read(ref _l2Writes);
    public long L2WriteFailures => Interlocked.Read(ref _l2WriteFailures);
    public long L2WritesDropped => Interlocked.Read(ref _l2WritesDropped);

    public ObjectStorageSegmentCache(ConfigManager configManager)
    {
        var endpoint = configManager.GetL2Endpoint()
            ?? throw new InvalidOperationException(
                "ObjectStorageSegmentCache requires cache.l2.endpoint to be set.");
        var accessKey = configManager.GetL2AccessKey() ?? string.Empty;
        var secretKey = configManager.GetL2SecretKey() ?? string.Empty;
        var useSsl = configManager.IsL2SslEnabled();
        _bucketName = configManager.GetL2BucketName();

        var builder = new MinioClient()
            .WithEndpoint(endpoint)
            .WithCredentials(accessKey, secretKey);
        if (useSsl) builder = builder.WithSSL();
        _client = builder.Build();

        // Bounded write queue. The 2048 capacity is sized for: 32 concurrent
        // streams × 64 in-flight segments each. Well beyond realistic
        // scenarios. BoundedChannelFullMode.DropWrite means the NEWEST
        // enqueue is dropped if the queue is full — that's intentional,
        // because if the L2 backend can't keep up with writes, we don't
        // want to back-pressure the read path.
        _writeQueue = Channel.CreateBounded<WriteRequest>(new BoundedChannelOptions(2048)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });

        _shutdownCts = new CancellationTokenSource();
        _writerTask = Task.Run(() => RunWriterAsync(_shutdownCts.Token));
    }

    /// <summary>
    /// Try to read a segment body from L2. Returns null on miss (404) or
    /// on any transient error (network, auth, etc.) — the caller should
    /// fall through to NNTP on null.
    /// </summary>
    public async Task<Stream?> TryReadAsync(
        string segmentId,
        CancellationToken cancellationToken)
    {
        if (_disposed) return null;

        var key = GetObjectKey(segmentId);
        try
        {
            var getArgs = new GetObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(key)
                .WithCallbackStream((stream, ct) =>
                {
                    // The MinIO SDK's GetObjectAsync uses a callback stream
                    // pattern — we copy the body into a MemoryStream here
                    // so the caller gets a seekable stream. Segment bodies
                    // are ~750 KB so this is fine.
                    var memoryStream = new MemoryStream();
                    stream.CopyTo(memoryStream);
                    memoryStream.Position = 0;
                    // Stash the result on the args object — Minio SDK doesn't
                    // have a clean return path, so we use a TaskCompletionSource
                    // shim pattern (see full implementation details).
                });

            // NOTE FOR IMPLEMENTER: The MinIO SDK's GetObjectAsync returns
            // an ObjectStat, not the stream directly. Use the "copy into
            // MemoryStream via callback" pattern above. The result needs
            // to be retrieved via a closure-captured variable or a
            // TaskCompletionSource. See MinIO .NET SDK docs for the canonical
            // example.
            //
            // Pseudocode for the clean version:
            //   var ms = new MemoryStream();
            //   await _client.GetObjectAsync(getArgs.WithCallbackStream(
            //       s => s.CopyTo(ms)), cancellationToken);
            //   ms.Position = 0;
            //   return ms;

            Interlocked.Increment(ref _l2Hits);
            throw new NotImplementedException(
                "IMPLEMENTER: complete the GetObjectAsync shim per comment above");
        }
        catch (Minio.Exceptions.ObjectNotFoundException)
        {
            Interlocked.Increment(ref _l2Misses);
            return null;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Transient error — log and miss. We do NOT throw because L2
            // is best-effort. The caller will fall through to NNTP.
            Log.Debug(ex, "L2 GET failed for segment {SegmentId} — falling back to NNTP", segmentId);
            Interlocked.Increment(ref _l2Misses);
            return null;
        }
    }

    /// <summary>
    /// Enqueue a segment body for background L2 write. Returns immediately.
    /// If the queue is full, the write is silently dropped (logged at debug).
    /// </summary>
    public void EnqueueWrite(
        string segmentId,
        byte[] body,
        SegmentCategory category,
        Guid? ownerNzbId,
        string yencFileName)
    {
        if (_disposed) return;

        var request = new WriteRequest(segmentId, body, category, ownerNzbId, yencFileName);
        if (!_writeQueue.Writer.TryWrite(request))
        {
            Interlocked.Increment(ref _l2WritesDropped);
            Log.Debug(
                "L2 write queue full — dropping write for segment {SegmentId}. " +
                "Check object storage backend health.",
                segmentId);
        }
    }

    private async Task RunWriterAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var request in _writeQueue.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await WriteOneAsync(request, cancellationToken).ConfigureAwait(false);
                    Interlocked.Increment(ref _l2Writes);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _l2WriteFailures);
                    Log.Debug(ex, "L2 write failed for segment {SegmentId}", request.SegmentId);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task WriteOneAsync(WriteRequest request, CancellationToken cancellationToken)
    {
        var key = GetObjectKey(request.SegmentId);
        using var bodyStream = new MemoryStream(request.Body);

        var metadata = new Dictionary<string, string>
        {
            ["segment-id"] = request.SegmentId,
            ["yenc-filename"] = request.YencFileName,
            ["category"] = request.Category.ToString().ToLowerInvariant(),
        };
        if (request.OwnerNzbId.HasValue)
            metadata["owner-nzb-id"] = request.OwnerNzbId.Value.ToString();

        var putArgs = new PutObjectArgs()
            .WithBucket(_bucketName)
            .WithObject(key)
            .WithStreamData(bodyStream)
            .WithObjectSize(request.Body.Length)
            .WithContentType("application/octet-stream")
            .WithHeaders(metadata);

        await _client.PutObjectAsync(putArgs, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// SHA-256 hash the segment ID to produce a URL-safe, length-normalized
    /// object key. Uses the "segments/{first2}/{full}" sharding layout for
    /// compatibility with backends that benefit from prefix distribution.
    /// </summary>
    private static string GetObjectKey(string segmentId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(segmentId));
        var hex = Convert.ToHexString(bytes).ToLowerInvariant();
        return $"segments/{hex[..2]}/{hex}";
    }

    /// <summary>
    /// One-time bucket creation on startup. Idempotent — existing buckets
    /// are left alone. Call this from the hosted initialization service.
    /// </summary>
    public async Task EnsureBucketExistsAsync(CancellationToken cancellationToken)
    {
        var existsArgs = new BucketExistsArgs().WithBucket(_bucketName);
        if (!await _client.BucketExistsAsync(existsArgs, cancellationToken).ConfigureAwait(false))
        {
            var makeArgs = new MakeBucketArgs().WithBucket(_bucketName);
            await _client.MakeBucketAsync(makeArgs, cancellationToken).ConfigureAwait(false);
            Log.Information("Created L2 bucket {Bucket}", _bucketName);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Signal the writer to stop. Give it up to 10 seconds to drain
        // any queued writes.
        _writeQueue.Writer.Complete();
        try
        {
            _writerTask.Wait(TimeSpan.FromSeconds(10));
        }
        catch
        {
            // Best effort.
        }

        _shutdownCts.Cancel();
        _shutdownCts.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed record WriteRequest(
        string SegmentId,
        byte[] Body,
        SegmentCategory Category,
        Guid? OwnerNzbId,
        string YencFileName);
}
```

### Implementer Notes for `TryReadAsync`

The MinIO .NET SDK's `GetObjectAsync` is awkward: it takes a callback that
receives the stream, rather than returning the stream directly. The
canonical pattern for "get and return the body as a MemoryStream":

```csharp
public async Task<Stream?> TryReadAsync(string segmentId, CancellationToken ct)
{
    if (_disposed) return null;
    var key = GetObjectKey(segmentId);
    var memoryStream = new MemoryStream();
    try
    {
        var getArgs = new GetObjectArgs()
            .WithBucket(_bucketName)
            .WithObject(key)
            .WithCallbackStream(stream => stream.CopyTo(memoryStream));
        await _client.GetObjectAsync(getArgs, ct).ConfigureAwait(false);
        memoryStream.Position = 0;
        Interlocked.Increment(ref _l2Hits);
        return memoryStream;
    }
    catch (Minio.Exceptions.ObjectNotFoundException)
    {
        await memoryStream.DisposeAsync();
        Interlocked.Increment(ref _l2Misses);
        return null;
    }
    catch (Exception ex) when (!ct.IsCancellationRequested)
    {
        await memoryStream.DisposeAsync();
        Log.Debug(ex, "L2 GET failed for {SegmentId}", segmentId);
        Interlocked.Increment(ref _l2Misses);
        return null;
    }
}
```

Replace the `throw new NotImplementedException` placeholder in the class
body above with this implementation. The stub is there so the spec is
clear about what NEEDS to happen, and the implementer can wire it without
re-reading the MinIO SDK docs.

---

## Integration with `LiveSegmentCache`

`LiveSegmentCache` becomes an L1 cache with an optional L2 backing. The
integration points:

### Modified: `LiveSegmentCache` constructor

Add an optional `ObjectStorageSegmentCache` dependency:

```csharp
public LiveSegmentCache(
    ConfigManager configManager,
    ObjectStorageSegmentCache? l2Cache = null)  // NEW — null when L2 disabled
{
    // ... existing initialization ...
    _l2Cache = l2Cache;
    // ...
}

private readonly ObjectStorageSegmentCache? _l2Cache;
```

DI in `Program.cs` registers `ObjectStorageSegmentCache` as a singleton
**only when `ConfigManager.IsL2Enabled()` is true**. Otherwise the
`LiveSegmentCache` receives `null` and operates in L1-only mode (current
behavior).

### Modified: `GetOrAddBodyAsync` — read path

The existing method currently tries L1, then falls through to NNTP. Insert
L2 in between:

```csharp
public async Task<BodyFetchResult> GetOrAddBodyAsync(
    string segmentId,
    Func<CancellationToken, Task<BodyFetchSource>> fetchBodyAsync,
    CancellationToken cancellationToken,
    SegmentCategory category = SegmentCategory.Unknown,
    Guid? ownerNzbId = null
)
{
    // L1 hit — same as today
    if (TryReadBody(segmentId, out var cachedResponse))
        return new BodyFetchResult(cachedResponse, UsedExistingFetch: false);

    Interlocked.Increment(ref _misses);

    // NEW: L2 hit — check object storage before NNTP
    if (_l2Cache != null)
    {
        var l2Stream = await _l2Cache.TryReadAsync(segmentId, cancellationToken)
            .ConfigureAwait(false);
        if (l2Stream != null)
        {
            // L2 hit. Promote to L1 and return.
            //
            // The "promote to L1" step is IMPORTANT: without it, every
            // subsequent read on this node would pay the L2 round-trip
            // again. With it, the second read is L1-fast.
            //
            // Use the same FetchAndStoreBodyAsync pipeline as NNTP, but
            // feed it the L2 stream instead of an NNTP fetch delegate.
            // The yEnc headers from L2 object metadata become the yenc
            // headers; we don't re-parse the body.
            var l2Source = await BuildBodyFetchSourceFromL2(
                segmentId, l2Stream, cancellationToken).ConfigureAwait(false);
            return await PromoteL2HitToL1(
                segmentId, l2Source, cancellationToken, category, ownerNzbId)
                .ConfigureAwait(false);
        }
    }

    // L2 miss (or L2 disabled) — fall through to the existing NNTP path
    var createdLazy = new Lazy<Task<CacheEntry>>(
        () => FetchAndStoreBodyAsync(segmentId, fetchBodyAsync, cancellationToken, category, ownerNzbId),
        LazyThreadSafetyMode.ExecutionAndPublication
    );

    // ... rest of existing method unchanged ...
}
```

**`BuildBodyFetchSourceFromL2` helper:**

```csharp
private static async Task<BodyFetchSource> BuildBodyFetchSourceFromL2(
    string segmentId,
    Stream l2Stream,
    CancellationToken cancellationToken)
{
    // The L2 object metadata contains the yenc filename and category.
    // We need the full UsenetYencHeader though — those fields need to
    // be reconstructed or re-parsed. OPTION A: store the full yEnc header
    // as JSON metadata on the L2 object (see "Object Metadata" section
    // above). OPTION B: re-parse from the stream.
    //
    // RECOMMENDED: option A — store full header as `x-amz-meta-yenc-header`
    // JSON. Trivially small (~200 bytes), parse is one JsonSerializer call.
    //
    // IMPLEMENTER: extend ObjectStorageSegmentCache.TryReadAsync to also
    // return the metadata dictionary, then reconstruct the UsenetYencHeader
    // from the JSON metadata.
    throw new NotImplementedException("IMPLEMENTER: wire L2 metadata → UsenetYencHeader");
}
```

### Modified: `FetchAndStoreBodyAsync` — write path

After successfully writing a segment to L1, enqueue an L2 write:

```csharp
private async Task<CacheEntry> FetchAndStoreBodyAsync(
    string segmentId,
    Func<CancellationToken, Task<BodyFetchSource>> fetchBodyAsync,
    CancellationToken cancellationToken,
    SegmentCategory category,
    Guid? ownerNzbId)
{
    // ... existing fetch + L1 write logic unchanged up until:
    //     File.Move(tempPath, finalPath);
    //     (the moment the body is durably in L1)

    // NEW: fire-and-forget L2 write
    if (_l2Cache != null)
    {
        // Read the body back from L1 into memory for the L2 write.
        // Segment bodies are ~750 KB so this is fine. Alternative is
        // to stream L1 → L2, but that complicates the background writer.
        var bodyBytes = await File.ReadAllBytesAsync(finalPath, cancellationToken)
            .ConfigureAwait(false);
        _l2Cache.EnqueueWrite(
            segmentId,
            bodyBytes,
            category,
            ownerNzbId,
            source.YencHeaders.FileName);
    }

    // ... rest of existing method unchanged ...
}
```

**Read-then-enqueue is intentional:** we read from L1 (which we just
wrote) rather than saving the original body bytes in memory during the
fetch. This keeps the fetch path's memory footprint unchanged. The cost
is one extra disk read (~1 ms) per NNTP fetch — well below the ~100-500 ms
NNTP fetch itself.

---

## Configuration Surface

### New `ConfigManager` methods

Add to `backend/Config/ConfigManager.cs`:

```csharp
/// <summary>
/// True when L2 object-storage cache is enabled. Controls whether
/// ObjectStorageSegmentCache is registered in DI and whether
/// LiveSegmentCache consults L2 on misses.
/// </summary>
public bool IsL2Enabled()
{
    var val = StringUtil.EmptyToNull(GetConfigValue("cache.l2.enabled"));
    return val != null && bool.Parse(val);
}

/// <summary>
/// S3-compatible endpoint (host:port or https://host). Example:
/// "minio:9000" for an in-cluster MinIO sidecar.
/// </summary>
public string? GetL2Endpoint()
    => StringUtil.EmptyToNull(GetConfigValue("cache.l2.endpoint"));

public string? GetL2AccessKey()
    => StringUtil.EmptyToNull(GetConfigValue("cache.l2.access-key"))
       ?? EnvironmentUtil.GetEnvironmentVariable("NZBDAV_L2_ACCESS_KEY");

public string? GetL2SecretKey()
    => StringUtil.EmptyToNull(GetConfigValue("cache.l2.secret-key"))
       ?? EnvironmentUtil.GetEnvironmentVariable("NZBDAV_L2_SECRET_KEY");

public string GetL2BucketName()
    => StringUtil.EmptyToNull(GetConfigValue("cache.l2.bucket-name"))
       ?? "nzbdav-segments";

public bool IsL2SslEnabled()
{
    var val = StringUtil.EmptyToNull(GetConfigValue("cache.l2.ssl"));
    return val != null && bool.Parse(val);
}
```

**Secret-key handling:** access key and secret key fall back to env vars
so operators can keep them out of the config database. They should be
added to `SensitiveConfigKeys` (from the V5 secret-encryption spec) once
both specs land.

### Frontend settings UI

Add a new "L2 Object Storage" section in `frontend/app/routes/settings/webdav/webdav.tsx`:

| Field | Type | Default | Validation |
|---|---|---|---|
| Enable L2 cache | checkbox | false | — |
| Endpoint | text | `""` | non-empty when enabled; format: `host:port` or `https://host` |
| Access key | password | `""` | non-empty when enabled |
| Secret key | password | `""` | non-empty when enabled |
| Bucket name | text | `nzbdav-segments` | S3 naming rules (lowercase, 3-63 chars, `a-z0-9-`) |
| Use SSL | checkbox | false | — |

Add to `defaultConfig` in `frontend/app/routes/settings/route.tsx`:

```typescript
"cache.l2.enabled": "false",
"cache.l2.endpoint": "",
"cache.l2.access-key": "",
"cache.l2.secret-key": "",
"cache.l2.bucket-name": "nzbdav-segments",
"cache.l2.ssl": "false",
```

Update `isWebdavSettingsUpdated()` and `isWebdavSettingsValid()` to cover
the new fields.

---

## DI Registration

In `backend/Program.cs`, after the existing `LiveSegmentCache` registration:

```csharp
.AddSingleton<LiveSegmentCache>()
// L2 is opt-in; only register when the operator has enabled it.
// LiveSegmentCache takes an optional ObjectStorageSegmentCache and
// operates in L1-only mode when it's null.
.AddSingleton<ObjectStorageSegmentCache?>(sp =>
{
    var cm = sp.GetRequiredService<ConfigManager>();
    if (!cm.IsL2Enabled()) return null;
    var cache = new ObjectStorageSegmentCache(cm);
    // Bucket creation runs on a background task to avoid blocking startup.
    // If the bucket can't be created (auth failure, backend unreachable),
    // all L2 operations will fail at their respective call sites and fall
    // back to NNTP — the node stays healthy.
    _ = Task.Run(async () =>
    {
        try
        {
            await cache.EnsureBucketExistsAsync(CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex,
                "Failed to ensure L2 bucket exists on startup — L2 will "
                + "operate in degraded mode until the backend is reachable.");
        }
    });
    return cache;
})
```

---

## Metrics

Extend `NzbdavMetricsCollector` with L2-specific gauges and counters:

```csharp
// In NzbdavMetricsCollector constructor, add:
_l2Hits = metricFactory.CreateCounter(
    "nzbdav_l2_cache_hits_total",
    "L2 object-storage cache hits");
_l2Misses = metricFactory.CreateCounter(
    "nzbdav_l2_cache_misses_total",
    "L2 object-storage cache misses");
_l2Writes = metricFactory.CreateCounter(
    "nzbdav_l2_cache_writes_total",
    "L2 object-storage writes completed");
_l2WriteFailures = metricFactory.CreateCounter(
    "nzbdav_l2_cache_write_failures_total",
    "L2 object-storage write failures");
_l2WritesDropped = metricFactory.CreateCounter(
    "nzbdav_l2_cache_writes_dropped_total",
    "L2 writes dropped due to full queue");
_l2Enabled = metricFactory.CreateGauge(
    "nzbdav_l2_cache_enabled",
    "1 if L2 cache is enabled, 0 otherwise");

// In CollectMetrics(), add:
var l2 = _getL2Cache();
_l2Enabled.Set(l2 != null ? 1 : 0);
if (l2 != null)
{
    IncrementCounter(_l2Hits, l2.L2Hits, ref _previousL2Hits);
    IncrementCounter(_l2Misses, l2.L2Misses, ref _previousL2Misses);
    IncrementCounter(_l2Writes, l2.L2Writes, ref _previousL2Writes);
    IncrementCounter(_l2WriteFailures, l2.L2WriteFailures, ref _previousL2WriteFailures);
    IncrementCounter(_l2WritesDropped, l2.L2WritesDropped, ref _previousL2WritesDropped);
}
```

Add constructor parameter `Func<ObjectStorageSegmentCache?> getL2Cache`
wired to the DI container.

---

## Graceful Shutdown

`ObjectStorageSegmentCache.Dispose` attempts to drain the write queue
within 10 seconds before shutting down the background writer. This means
in-flight L2 writes get a chance to complete on graceful shutdown, but we
don't hang the process indefinitely if the backend is unreachable.

No separate hosted service is needed — the DI container calls `Dispose`
on singletons during shutdown and the 10-second budget fits within the
`HostOptions.ShutdownTimeout = 30s`.

---

## Lifecycle Policy (Operator Guidance)

The spec does NOT automatically configure S3 lifecycle policies. That's
left to the operator via the object storage backend's native tools.

**Recommended MinIO lifecycle policy:**

```bash
# Delete segments older than 30 days. Adjust based on your library
# re-watch patterns — longer = more stable hit rate but more storage cost.
mc ilm rule add --expire-days 30 nzbdav-minio/nzbdav-segments
```

**Recommended S3/R2 lifecycle policy:**

```json
{
  "Rules": [{
    "ID": "expire-old-segments",
    "Status": "Enabled",
    "Filter": { "Prefix": "segments/" },
    "Expiration": { "Days": 30 }
  }]
}
```

Document this in `docs/deployment/setup-guide.md` under a new "L2 Cache
Setup" section.

---

## Deployment: Multi-Node Docker Compose

Add a `minio` service to `docs/deployment/docker-compose.multi-node.yml`:

```yaml
  minio:
    image: minio/minio:latest
    restart: unless-stopped
    environment:
      MINIO_ROOT_USER: ${MINIO_ROOT_USER:-nzbdav}
      MINIO_ROOT_PASSWORD: ${MINIO_ROOT_PASSWORD}
    command: server /data
    volumes:
      - minio-data:/data
    healthcheck:
      test: ["CMD", "mc", "ready", "local"]
      interval: 10s
      timeout: 3s
      retries: 3

volumes:
  minio-data:
```

Update each `nzbdav-*` service environment block:

```yaml
    environment:
      # ... existing vars ...
      NZBDAV_L2_ACCESS_KEY: ${MINIO_ROOT_USER:-nzbdav}
      NZBDAV_L2_SECRET_KEY: ${MINIO_ROOT_PASSWORD}
```

The operator then enables L2 via the Settings UI after first startup,
setting endpoint to `minio:9000`, ssl to false, and bucket name to the
default.

---

## Testing Strategy

### Unit tests (`backend.Tests/Clients/Usenet/Caching/`)

**`ObjectStorageSegmentCacheTests`** — use a mocked `IMinioClient`:

1. `GetObjectKey_IsDeterministic` — same segment ID hashes to same key
2. `GetObjectKey_HasCorrectShape` — matches `segments/{2-char}/{64-char-hex}`
3. `TryReadAsync_ReturnsStreamOnSuccess` — mock MinIO returns stream, assert non-null + correct bytes
4. `TryReadAsync_ReturnsNullOn404` — mock raises `ObjectNotFoundException`, assert null + L2Misses++
5. `TryReadAsync_ReturnsNullOnTransientError` — mock raises `IOException`, assert null (not throw) + L2Misses++ + warning logged
6. `EnqueueWrite_CompletesAsynchronously` — enqueue, verify returns immediately, verify writer processes it within 1s
7. `EnqueueWrite_DropsOnFullQueue` — fill queue to 2048, enqueue one more, verify L2WritesDropped++
8. `Dispose_DrainsWriterWithinBudget` — enqueue 10 writes, dispose, verify all 10 complete (or timeout cleanly after 10s)
9. `Dispose_IsIdempotent` — double-dispose doesn't throw

### Integration tests (`backend.Tests/Integration/`)

**`ObjectStorageIntegrationTests`** — use [Testcontainers](https://testcontainers.com)
to spin up a real MinIO container:

1. `EndToEnd_WriteAndRead` — write a segment via `EnqueueWrite`, wait for
   writer, read back via `TryReadAsync`, verify bytes match
2. `L1MissL2Hit_PromotesToL1` — insert directly into MinIO, trigger a
   `LiveSegmentCache.GetOrAddBodyAsync`, verify it returns without hitting
   NNTP and that a subsequent read is L1-fast
3. `L2Unreachable_FallsThroughToNNTP` — start the cache, stop MinIO, issue
   a read, verify it falls through to the NNTP pipeline without throwing
4. `BucketCreation_IsIdempotent` — start twice against the same MinIO,
   verify no error on second start
5. `MetadataRoundTrip` — write a segment with `category=SmallFile` and
   `ownerNzbId=<guid>`, read it back, verify the metadata is preserved

### Load tests (manual, documented for operator)

Not CI-gated, but document these in the PR description for the operator
to run before merging:

1. **Write throughput**: enqueue 10k writes, measure wall-clock until all
   complete. Expect ~100-500 writes/sec depending on MinIO backend speed.
2. **Read latency**: warm 1000 segments into L2, issue 1000 parallel
   reads from a cold L1, measure p50/p99. Expect p50 < 50ms, p99 < 200ms
   against local MinIO.
3. **Backend down**: 1000 reads with MinIO stopped. Expect all 1000 fall
   through to NNTP within normal budget. No hangs.

---

## Rollout

### Phase 1: MinIO in staging, L2 disabled

1. Deploy PgBouncer and the multi-node hardening spec first (Item 5).
2. Add the MinIO container to the compose file.
3. Deploy NZBDAV with the L2 code in place but `cache.l2.enabled = false`.
4. Verify all existing tests pass and performance is unchanged.

### Phase 2: Enable L2 in staging

1. Flip `cache.l2.enabled = true` in settings.
2. Watch `nzbdav_l2_cache_hits_total` rise as segments warm in.
3. Watch `nzbdav_cache_hits_total` vs `nzbdav_l2_cache_hits_total` ratio
   stabilize.
4. Verify that stopping MinIO mid-workload causes graceful degradation
   (falls through to NNTP, no user-visible errors).

### Phase 3: Production rollout

1. Deploy MinIO with production-sized storage (10-50 TB depending on
   library size).
2. Configure a 30-day lifecycle policy.
3. Enable L2 on production nodes during low-traffic window.
4. Monitor bandwidth to NNTP providers — expect a step-function drop
   over the first week as cache warms.

### Phase 4: Expand to more nodes

With L2 in place, adding streaming nodes no longer causes bandwidth
duplication. Operators can scale horizontally as needed.

---

## Open Questions for Review

1. **Write-behind vs write-through.** Spec uses fire-and-forget. Under a
   read-after-write race (rare, but possible with HAProxy flap), the
   second node might see a 404 and refetch. Alternative: synchronous
   write-through adds ~20-50ms to every NNTP fetch but eliminates the
   race. Worth the latency hit? (My default: no — the race is rare and
   refetching is cheap compared to always paying the latency.)

2. **L2 metadata storage.** I propose storing the full `UsenetYencHeader`
   as JSON in S3 object metadata. S3 metadata is limited to 2 KB total,
   which should be plenty for a header but edge cases (pathological
   filenames) could blow it. Alternative: write a separate `.meta` object
   per body. Adds one S3 call per write. Worth it for safety?

3. **Cache promotion on L2 hit.** Currently an L2 hit promotes the body
   to L1 by re-writing it to disk. This doubles the L1 write cost on
   cold-node-warmup scenarios. Alternative: don't promote, serve directly
   from L2 every time (loses the fast-L1-repeated-reads benefit but
   halves cold-warmup disk I/O). Pick based on expected access patterns.

4. **`LiveSegmentCache` constructor change.** Adding an optional
   `ObjectStorageSegmentCache` parameter is a breaking change for the
   test-friendly constructor (`LiveSegmentCache(string, long, TimeSpan?)`).
   Either (a) make the test constructor also accept optional L2, or (b)
   keep test constructor as-is (L1-only) and only the config-manager
   constructor gets L2.

5. **Eviction coordination.** If a row is evicted from L1 due to size
   pressure, should it also be deleted from L2? Current spec: no, L2 is
   bigger and operates on its own lifecycle policy. But over time L2 may
   contain objects that L1 never re-requests — wasted space. Alternative:
   DELETE on L1 eviction. Adds S3 calls; unclear benefit.

6. **SmallFilePrecacheService.** Should it write directly to L2 to solve
   the "small files cached on wrong node" problem? Current spec: yes,
   implicitly, via the normal L1→L2 write-through path. But this means
   the precache segments first populate L1 on the ingest node, THEN get
   written to L2. That's fine — the precache still completes, and all
   streaming nodes get the L2 hit on first access.

---

## Files Summary

### New Files (1)

| File | Purpose |
|---|---|
| `backend/Clients/Usenet/Caching/ObjectStorageSegmentCache.cs` | L2 cache implementation |

### Modified Files (~8)

| File | Change |
|---|---|
| `backend/NzbWebDAV.csproj` | Add `Minio` NuGet package |
| `backend/Clients/Usenet/Caching/LiveSegmentCache.cs` | Optional L2 dep; L2 read path in GetOrAddBodyAsync; L2 write path in FetchAndStoreBodyAsync |
| `backend/Config/ConfigManager.cs` | 6 new getters for L2 config |
| `backend/Program.cs` | Conditional ObjectStorageSegmentCache DI registration |
| `backend/Metrics/NzbdavMetricsCollector.cs` | L2 metrics |
| `frontend/app/routes/settings/route.tsx` | defaultConfig entries |
| `frontend/app/routes/settings/webdav/webdav.tsx` | L2 settings UI section |
| `docs/deployment/docker-compose.multi-node.yml` | MinIO service |
| `docs/deployment/setup-guide.md` | L2 cache setup section |

### Test Files (2)

| File | Coverage |
|---|---|
| `backend.Tests/Clients/Usenet/Caching/ObjectStorageSegmentCacheTests.cs` | Unit tests with mocked IMinioClient |
| `backend.Tests/Integration/ObjectStorageIntegrationTests.cs` | Testcontainers-based end-to-end |

---

## Non-Goals

- **Cross-region replication.** If the operator wants geo-redundancy,
  that's a property of the S3 backend, not NZBDAV.
- **L2 as the sole cache.** L1 stays. The hybrid is the whole point.
- **Encrypting L2 contents.** Segment bodies are already in the clear on
  NNTP; encrypting them in L2 buys nothing unless the entire NNTP
  pipeline is also encrypted. Out of scope.
- **Coordinating L2 size across nodes.** L2 is a single shared resource
  with one lifecycle policy. No per-node sizing.
- **Replacing `LiveSegmentCache`.** L1 stays as the fast-path and the
  primary cache. L2 is strictly additive.

---

## What This Unlocks

- **True horizontal scale.** Adding a streaming node no longer costs
  proportional NNTP bandwidth — new nodes warm their L1 from L2, not
  from the provider.
- **SmallFilePrecache becomes effective.** The ingest node's precache
  writes flow to L2, where all streaming nodes can read them. Cold
  cache on poster/NFO/subtitle files goes away.
- **MediaProbe becomes effective across nodes.** Same mechanism —
  probe-touched segments land in L2, streaming nodes read them from
  there instead of refetching NNTP.
- **HAProxy rebalance is cheap.** If the consistent hash reshuffles,
  the new owner reads from L2 (~50 ms) instead of refetching from the
  provider (~500 ms).
- **Cost predictability.** Operators with known library sizes can
  provision L2 storage once and know exactly what their cache hit rate
  will be.
