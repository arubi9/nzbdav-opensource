# Plan A: NZBDAV Observability — Prometheus Metrics

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a `/metrics` endpoint to NZBDAV that exposes Prometheus-format metrics for cache performance, NNTP connection pool utilization, active streams, queue state, and request latency.

**Architecture:** A single `NzbdavMetricsCollector` class reads existing stat objects (LiveSegmentCache.GetStats(), ConnectionPoolStats, ReadAheadWarmingService, QueueManager) on each Prometheus scrape and publishes them as gauges/counters. The prometheus-net library handles HTTP serialization. No new tracking infrastructure — we read what already exists.

**Tech Stack:** prometheus-net.AspNetCore 8.x, .NET 10, ASP.NET Core middleware

**Companion plan:** This plan's metrics infrastructure is used by Plan B (Jellyfin Plugin). The REST API endpoints created in Plan B will be instrumented with the same Prometheus histograms registered here.

---

### Task 1: Add prometheus-net NuGet package

**Files:**
- Modify: `backend/NzbWebDAV.csproj`

- [ ] **Step 1: Add package reference**

Add to the `<ItemGroup>` in `backend/NzbWebDAV.csproj`:

```xml
<PackageReference Include="prometheus-net.AspNetCore" Version="8.2.1" />
```

- [ ] **Step 2: Restore packages**

Run: `cd backend && dotnet restore`
Expected: Restore succeeds with no errors.

- [ ] **Step 3: Verify build**

Run: `cd backend && dotnet build`
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add backend/NzbWebDAV.csproj
git commit -m "Add prometheus-net.AspNetCore package for metrics"
```

---

### Task 2: Create NzbdavMetricsCollector

**Files:**
- Create: `backend/Metrics/NzbdavMetricsCollector.cs`

This class reads existing stat objects and publishes Prometheus metrics. It implements `IDisposable` and registers a periodic collection timer.

- [ ] **Step 1: Create the metrics collector**

```csharp
// backend/Metrics/NzbdavMetricsCollector.cs
using NzbWebDAV.Clients.Usenet.Caching;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using Prometheus;

namespace NzbWebDAV.Metrics;

public sealed class NzbdavMetricsCollector : IDisposable
{
    private readonly LiveSegmentCache _liveSegmentCache;
    private readonly ReadAheadWarmingService _warmingService;
    private readonly QueueManager _queueManager;
    private readonly Timer _timer;

    // Cache metrics
    private static readonly Gauge CachedBytes = Prometheus.Metrics.CreateGauge(
        "nzbdav_cache_bytes", "Total bytes in segment cache");
    private static readonly Gauge CachedSegments = Prometheus.Metrics.CreateGauge(
        "nzbdav_cache_segments_total", "Total cached segments", "category");
    private static readonly Counter CacheHits = Prometheus.Metrics.CreateCounter(
        "nzbdav_cache_hits_total", "Total cache hits");
    private static readonly Counter CacheMisses = Prometheus.Metrics.CreateCounter(
        "nzbdav_cache_misses_total", "Total cache misses");
    private static readonly Counter CacheEvictions = Prometheus.Metrics.CreateCounter(
        "nzbdav_cache_evictions_total", "Total cache evictions");
    private static readonly Counter CacheDedupes = Prometheus.Metrics.CreateCounter(
        "nzbdav_cache_dedupes_total", "Total deduplicated inflight requests");
    private static readonly Gauge CacheHitRate = Prometheus.Metrics.CreateGauge(
        "nzbdav_cache_hit_rate", "Cache hit rate (0.0-1.0)");

    // Warming metrics
    private static readonly Gauge WarmingSessions = Prometheus.Metrics.CreateGauge(
        "nzbdav_warming_sessions_active", "Active read-ahead warming sessions");

    // Queue metrics
    private static readonly Gauge QueueProcessing = Prometheus.Metrics.CreateGauge(
        "nzbdav_queue_processing", "1 if a queue item is being processed, 0 otherwise");

    // Active streams (updated externally via static methods)
    private static readonly Gauge ActiveStreams = Prometheus.Metrics.CreateGauge(
        "nzbdav_streams_active", "Active WebDAV video streams");

    // Snapshot tracking for counter deltas
    private long _lastHits;
    private long _lastMisses;
    private long _lastEvictions;
    private long _lastDedupes;

    public NzbdavMetricsCollector(
        LiveSegmentCache liveSegmentCache,
        ReadAheadWarmingService warmingService,
        QueueManager queueManager
    )
    {
        _liveSegmentCache = liveSegmentCache;
        _warmingService = warmingService;
        _queueManager = queueManager;

        // Collect every 5 seconds
        _timer = new Timer(_ => Collect(), null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    private void Collect()
    {
        try
        {
            // Cache stats
            var stats = _liveSegmentCache.GetStats();
            CachedBytes.Set(stats.CachedBytes);
            CachedSegments.WithLabels("video").Set(stats.VideoSegmentCount);
            CachedSegments.WithLabels("small_file").Set(stats.SmallFileCount);
            CachedSegments.WithLabels("unknown").Set(stats.UnknownCount);

            // Counters are monotonic — prometheus-net counters only support Inc().
            // We track deltas from the last snapshot.
            var hitDelta = stats.Hits - _lastHits;
            var missDelta = stats.Misses - _lastMisses;
            var evictionDelta = stats.Evictions - _lastEvictions;
            var dedupeDelta = stats.Dedupes - _lastDedupes;
            if (hitDelta > 0) CacheHits.Inc(hitDelta);
            if (missDelta > 0) CacheMisses.Inc(missDelta);
            if (evictionDelta > 0) CacheEvictions.Inc(evictionDelta);
            if (dedupeDelta > 0) CacheDedupes.Inc(dedupeDelta);
            _lastHits = stats.Hits;
            _lastMisses = stats.Misses;
            _lastEvictions = stats.Evictions;
            _lastDedupes = stats.Dedupes;

            var total = stats.Hits + stats.Misses;
            CacheHitRate.Set(total > 0 ? (double)stats.Hits / total : 0);

            // Warming sessions
            WarmingSessions.Set(_warmingService.ActiveSessionCount);

            // Queue
            var (queueItem, _) = _queueManager.GetInProgressQueueItem();
            QueueProcessing.Set(queueItem != null ? 1 : 0);
        }
        catch
        {
            // Metrics collection should never crash the app.
        }
    }

    public static void IncrementActiveStreams() => ActiveStreams.Inc();
    public static void DecrementActiveStreams() => ActiveStreams.Dec();

    public void Dispose()
    {
        _timer.Dispose();
    }
}
```

- [ ] **Step 2: Verify build**

Run: `cd backend && dotnet build`
Expected: Build will fail because `_warmingService.ActiveSessionCount` doesn't exist yet. That's expected — we'll add it in Task 3.

- [ ] **Step 3: Commit**

```bash
git add backend/Metrics/NzbdavMetricsCollector.cs
git commit -m "Add NzbdavMetricsCollector for Prometheus metrics"
```

---

### Task 3: Expose ActiveSessionCount on ReadAheadWarmingService

**Files:**
- Modify: `backend/Services/ReadAheadWarmingService.cs`

- [ ] **Step 1: Add property**

Add this property to `ReadAheadWarmingService`, after the `_sessions` field declaration:

```csharp
public int ActiveSessionCount => _sessions.Count;
```

- [ ] **Step 2: Verify build**

Run: `cd backend && dotnet build`
Expected: Build succeeded, 0 errors. The `NzbdavMetricsCollector` should now compile.

- [ ] **Step 3: Commit**

```bash
git add backend/Services/ReadAheadWarmingService.cs
git commit -m "Expose ActiveSessionCount for metrics"
```

---

### Task 4: Wire metrics into Program.cs

**Files:**
- Modify: `backend/Program.cs`

- [ ] **Step 1: Add using statements**

Add at the top of `Program.cs`:

```csharp
using NzbWebDAV.Metrics;
using Prometheus;
```

- [ ] **Step 2: Register NzbdavMetricsCollector in DI**

After the `.AddSingleton<ReadAheadWarmingService>()` line, add:

```csharp
.AddSingleton<NzbdavMetricsCollector>()
```

- [ ] **Step 3: Map the /metrics endpoint and start collector**

After `app.MapHealthChecks("/health");`, add:

```csharp
app.MapMetrics(); // prometheus-net: serves /metrics
// Start the metrics collector to begin periodic collection
_ = app.Services.GetRequiredService<NzbdavMetricsCollector>();
```

- [ ] **Step 4: Verify build**

Run: `cd backend && dotnet build`
Expected: Build succeeded, 0 errors.

- [ ] **Step 5: Commit**

```bash
git add backend/Program.cs
git commit -m "Wire Prometheus /metrics endpoint"
```

---

### Task 5: Track active WebDAV streams

**Files:**
- Modify: `backend/WebDav/Base/GetAndHeadHandlerPatch.cs`

The `GetAndHeadHandlerPatch` handles all WebDAV GET/HEAD requests. We increment the active stream gauge when a GET starts streaming and decrement when it completes.

- [ ] **Step 1: Add using**

Add at the top of `GetAndHeadHandlerPatch.cs`:

```csharp
using NzbWebDAV.Metrics;
```

- [ ] **Step 2: Instrument the streaming path**

In the `HandleRequestAsync` method, wrap the stream copy in a try/finally that tracks active streams. Find the section where `isHeadRequest` is checked (the `if (!isHeadRequest)` block). Change it to:

```csharp
// HEAD method doesn't require the actual item data
if (!isHeadRequest)
{
    NzbdavMetricsCollector.IncrementActiveStreams();
    try
    {
        await stream.CopyRangeToPooledAsync(
            response.Body,
            range?.Start ?? 0,
            range?.End,
            cancellationToken: httpContext.RequestAborted
        ).ConfigureAwait(false);
    }
    finally
    {
        NzbdavMetricsCollector.DecrementActiveStreams();
    }
}
```

- [ ] **Step 3: Verify build**

Run: `cd backend && dotnet build`
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Run tests**

Run: `cd backend.Tests && dotnet test`
Expected: All tests pass.

- [ ] **Step 5: Commit**

```bash
git add backend/WebDav/Base/GetAndHeadHandlerPatch.cs
git commit -m "Track active WebDAV streams in Prometheus metrics"
```

---

### Task 6: Add NNTP connection pool metrics

**Files:**
- Modify: `backend/Metrics/NzbdavMetricsCollector.cs`
- Modify: `backend/Clients/Usenet/UsenetStreamingClient.cs`

The connection pool stats are currently tracked by `ConnectionPoolStats` and sent via WebSocket. We need to expose them to the metrics collector.

- [ ] **Step 1: Add pool stats exposure to UsenetStreamingClient**

Add a public property to `UsenetStreamingClient` that exposes the current connection pool stats. In `backend/Clients/Usenet/UsenetStreamingClient.cs`, add after the constructor:

```csharp
// Expose for metrics
public (int TotalLive, int TotalIdle, int MaxPooled) GetPoolStats()
{
    // ConnectionPoolStats is created inside the factory method and not stored.
    // We need to store it. Add a field:
    return (_connectionPoolStats?.TotalLive ?? 0, _connectionPoolStats?.TotalIdle ?? 0, _connectionPoolStats?.MaxPooled ?? 0);
}
```

This requires storing the `ConnectionPoolStats` instance. Modify the class to store it:

Add a field:
```csharp
private ConnectionPoolStats? _connectionPoolStats;
```

In `CreateLiveStreamingClient`, store the stats:
```csharp
private LiveSegmentCachingNntpClient CreateLiveStreamingClient(...)
{
    var multiProviderClient = CreateMultiProviderClient(configManager, websocketManager);
    // _connectionPoolStats is set inside CreateMultiProviderClient
    ...
}
```

Actually, the simpler approach: add public properties to `ConnectionPoolStats`:

- [ ] **Step 2: Add public accessors to ConnectionPoolStats**

In `backend/Clients/Usenet/Connections/ConnectionPoolStats.cs`, add these public properties:

```csharp
public int TotalLive { get { lock (this) return _totalLive; } }
public int TotalIdle { get { lock (this) return _totalIdle; } }
public int MaxPooled => _max;
```

- [ ] **Step 3: Store ConnectionPoolStats in UsenetStreamingClient**

In `backend/Clients/Usenet/UsenetStreamingClient.cs`, the `CreateMultiProviderClient` method creates `ConnectionPoolStats` but doesn't store it. Add a field and store it:

Add field after class declaration:
```csharp
private volatile ConnectionPoolStats? _poolStats;
```

In `CreateLiveStreamingClient` (the static method that creates the pipeline), this won't work directly because it's static. Instead, make the method instance-based or pass the stats out.

**Simpler approach:** Have the metrics collector accept `UsenetStreamingClient` and expose pool stats through it. Since `UsenetStreamingClient` already wraps `CreateMultiProviderClient`, modify the instance constructor to store the stats:

In the constructor, after `CreateLiveStreamingClient`:
```csharp
public UsenetStreamingClient(ConfigManager configManager, WebsocketManager websocketManager, LiveSegmentCache liveSegmentCache)
    : base(CreateLiveStreamingClient(configManager, websocketManager, liveSegmentCache))
{
    _poolStats = _lastCreatedPoolStats;
    configManager.OnConfigChanged += (_, configEventArgs) =>
    {
        if (!configEventArgs.ChangedConfig.ContainsKey("usenet.providers")) return;
        var newUsenetClient = CreateLiveStreamingClient(configManager, websocketManager, liveSegmentCache);
        ReplaceUnderlyingClient(newUsenetClient);
        _poolStats = _lastCreatedPoolStats;
    };
}

[ThreadStatic] private static ConnectionPoolStats? _lastCreatedPoolStats;
private volatile ConnectionPoolStats? _poolStats;

public ConnectionPoolStats? PoolStats => _poolStats;
```

In `CreateMultiProviderClient`, store the stats:
```csharp
private static MultiProviderNntpClient CreateMultiProviderClient(...)
{
    var providerConfig = configManager.GetUsenetProviderConfig();
    var connectionPoolStats = new ConnectionPoolStats(providerConfig, websocketManager);
    _lastCreatedPoolStats = connectionPoolStats;
    ...
}
```

- [ ] **Step 4: Add pool metrics to NzbdavMetricsCollector**

Add to `NzbdavMetricsCollector.cs`:

Field declarations:
```csharp
private readonly UsenetStreamingClient _usenetClient;

private static readonly Gauge NntpPoolLive = Prometheus.Metrics.CreateGauge(
    "nzbdav_nntp_connections_live", "Live NNTP connections");
private static readonly Gauge NntpPoolIdle = Prometheus.Metrics.CreateGauge(
    "nzbdav_nntp_connections_idle", "Idle NNTP connections");
private static readonly Gauge NntpPoolMax = Prometheus.Metrics.CreateGauge(
    "nzbdav_nntp_connections_max", "Max pooled NNTP connections");
private static readonly Gauge NntpPoolActive = Prometheus.Metrics.CreateGauge(
    "nzbdav_nntp_connections_active", "Active (in-use) NNTP connections");
```

Add `UsenetStreamingClient` to the constructor parameters.

In the `Collect()` method, add:
```csharp
// NNTP pool stats
var poolStats = _usenetClient.PoolStats;
if (poolStats != null)
{
    NntpPoolLive.Set(poolStats.TotalLive);
    NntpPoolIdle.Set(poolStats.TotalIdle);
    NntpPoolMax.Set(poolStats.MaxPooled);
    NntpPoolActive.Set(poolStats.TotalLive - poolStats.TotalIdle);
}
```

- [ ] **Step 5: Verify build and test**

Run: `cd backend && dotnet build && cd ../backend.Tests && dotnet test`
Expected: Build succeeded, all tests pass.

- [ ] **Step 6: Commit**

```bash
git add backend/Metrics/NzbdavMetricsCollector.cs backend/Clients/Usenet/UsenetStreamingClient.cs backend/Clients/Usenet/Connections/ConnectionPoolStats.cs
git commit -m "Add NNTP connection pool metrics to Prometheus"
```

---

### Task 7: Add HTTP request duration histogram

**Files:**
- Create: `backend/Metrics/RequestDurationMiddleware.cs`
- Modify: `backend/Program.cs`

- [ ] **Step 1: Create request duration middleware**

```csharp
// backend/Metrics/RequestDurationMiddleware.cs
using System.Diagnostics;
using Prometheus;

namespace NzbWebDAV.Metrics;

public class RequestDurationMiddleware(RequestDelegate next)
{
    private static readonly Histogram RequestDuration = Prometheus.Metrics.CreateHistogram(
        "nzbdav_http_request_duration_seconds",
        "HTTP request duration in seconds",
        new HistogramConfiguration
        {
            LabelNames = new[] { "method", "route", "status_code" },
            Buckets = new[] { 0.01, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60, 120 }
        });

    public async Task InvokeAsync(HttpContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            stopwatch.Stop();
            var route = GetRoutePattern(context);
            RequestDuration
                .WithLabels(context.Request.Method, route, context.Response.StatusCode.ToString())
                .Observe(stopwatch.Elapsed.TotalSeconds);
        }
    }

    private static string GetRoutePattern(HttpContext context)
    {
        // Use the matched route pattern if available, otherwise fall back to path prefix
        var endpoint = context.GetEndpoint();
        if (endpoint is RouteEndpoint routeEndpoint)
            return routeEndpoint.RoutePattern.RawText ?? "unknown";

        // For WebDAV requests (no route endpoint), classify by method
        var path = context.Request.Path.Value ?? "/";
        if (path.StartsWith("/content/", StringComparison.OrdinalIgnoreCase)) return "/content/{path}";
        if (path.StartsWith("/view/", StringComparison.OrdinalIgnoreCase)) return "/view/{path}";
        if (path == "/metrics") return "/metrics";
        if (path == "/health") return "/health";
        if (path == "/ws") return "/ws";
        if (path.StartsWith("/api", StringComparison.OrdinalIgnoreCase)) return "/api";
        return "/webdav";
    }
}
```

- [ ] **Step 2: Register middleware in Program.cs**

In `Program.cs`, add the middleware **before** `ExceptionMiddleware` (so it wraps everything):

```csharp
app.UseMiddleware<RequestDurationMiddleware>();
app.UseMiddleware<ExceptionMiddleware>();
```

Add the using:
```csharp
using NzbWebDAV.Metrics;
```

- [ ] **Step 3: Verify build and test**

Run: `cd backend && dotnet build && cd ../backend.Tests && dotnet test`
Expected: Build succeeded, all tests pass.

- [ ] **Step 4: Commit**

```bash
git add backend/Metrics/RequestDurationMiddleware.cs backend/Program.cs
git commit -m "Add HTTP request duration histogram middleware"
```

---

### Task 8: Integration verification

- [ ] **Step 1: Run full test suite**

Run: `cd backend.Tests && dotnet test --verbosity normal`
Expected: All tests pass.

- [ ] **Step 2: Verify /metrics endpoint works**

Start the app locally and curl the metrics endpoint:

```bash
cd backend && dotnet run &
sleep 3
curl -s http://localhost:8080/metrics | head -50
```

Expected: Prometheus text format output with `nzbdav_cache_*`, `nzbdav_nntp_*`, `nzbdav_streams_*`, `nzbdav_http_request_duration_*` metrics.

- [ ] **Step 3: Verify /health still works**

```bash
curl -s http://localhost:8080/health
```

Expected: `Healthy`

- [ ] **Step 4: Final commit**

```bash
git add -A
git commit -m "Observability: Prometheus metrics for cache, NNTP, streams, and request latency"
```
