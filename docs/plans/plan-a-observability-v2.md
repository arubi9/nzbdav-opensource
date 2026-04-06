# Plan A v2: NZBDAV Observability — Prometheus Metrics

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a `/metrics` endpoint exposing Prometheus-format metrics for cache, NNTP pool, active streams, queue state, and request latency.

**Architecture:** Use prometheus-net's built-in HTTP request metrics and a single `Collector` that reads existing stat objects on scrape (not a timer). NNTP pool stats are surfaced through explicit ownership in the pipeline, not ambient static state. No custom latency middleware — prometheus-net's `UseHttpMetrics()` handles request duration natively.

**Tech Stack:** prometheus-net.AspNetCore 8.x, .NET 10

**Key design decisions from architect review:**
- Use prometheus-net's built-in `UseHttpMetrics()` for request duration — no custom middleware
- Use a `Collector` (scraped on-demand) instead of a timer-based poller
- Expose `ConnectionPoolStats` through explicit DI ownership, not `ThreadStatic` handoff
- Active stream counting via a shared `StreamExecutionService` (created in Plan B, prepared here)

---

### Task 1: Add prometheus-net NuGet package

**Files:**
- Modify: `backend/NzbWebDAV.csproj`

- [ ] **Step 1: Add package reference**

Add to the `<ItemGroup>` in `backend/NzbWebDAV.csproj`:

```xml
<PackageReference Include="prometheus-net.AspNetCore" Version="8.2.1" />
```

- [ ] **Step 2: Restore and build**

Run: `cd backend && dotnet restore && dotnet build`
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add backend/NzbWebDAV.csproj
git commit -m "Add prometheus-net.AspNetCore package"
```

---

### Task 2: Surface ConnectionPoolStats through explicit DI ownership

**Files:**
- Modify: `backend/Clients/Usenet/Connections/ConnectionPoolStats.cs`
- Modify: `backend/Clients/Usenet/UsenetStreamingClient.cs`

The current `ConnectionPoolStats` is created inside a static factory method and never stored. It needs to be accessible for metrics. Instead of `ThreadStatic` hacks, make `UsenetStreamingClient` own and expose its `ConnectionPoolStats` instance through a clean property.

- [ ] **Step 1: Add public read accessors to ConnectionPoolStats**

In `backend/Clients/Usenet/Connections/ConnectionPoolStats.cs`, add these properties after the existing fields:

```csharp
public int TotalLive { get { lock (this) return _totalLive; } }
public int TotalIdle { get { lock (this) return _totalIdle; } }
public int MaxPooled => _max;
public int TotalActive => TotalLive - TotalIdle;
```

- [ ] **Step 2: Store ConnectionPoolStats as an instance field in UsenetStreamingClient**

In `backend/Clients/Usenet/UsenetStreamingClient.cs`, the pipeline is created via static methods. Refactor so the instance stores its own `ConnectionPoolStats`:

Add a field:
```csharp
private volatile ConnectionPoolStats? _poolStats;
public ConnectionPoolStats? PoolStats => _poolStats;
```

Use a constructor/factory split so pipeline creation returns both the wrapped client and the stats object, and the private constructor passes the client to `base(...)` while retaining the stats in instance state. This keeps ownership explicit and avoids hidden shared state during construction.

```csharp
private readonly record struct PipelineResult(LiveSegmentCachingNntpClient Client, ConnectionPoolStats Stats);

public UsenetStreamingClient(
    ConfigManager configManager,
    WebsocketManager websocketManager,
    LiveSegmentCache liveSegmentCache
) : this(CreatePipeline(configManager, websocketManager, liveSegmentCache),
         configManager, websocketManager, liveSegmentCache)
{
}

private UsenetStreamingClient(
    PipelineResult pipeline,
    ConfigManager configManager,
    WebsocketManager websocketManager,
    LiveSegmentCache liveSegmentCache
) : base(pipeline.Client)
{
    _poolStats = pipeline.Stats;

    configManager.OnConfigChanged += (_, configEventArgs) =>
    {
        if (!configEventArgs.ChangedConfig.ContainsKey("usenet.providers")) return;

        var nextPipeline = CreatePipeline(configManager, websocketManager, liveSegmentCache);
        ReplaceUnderlyingClient(nextPipeline.Client);
        _poolStats = nextPipeline.Stats;
    };
}

private static PipelineResult CreatePipeline(
    ConfigManager configManager,
    WebsocketManager websocketManager,
    LiveSegmentCache liveSegmentCache)
{
    var multiProviderResult = CreateMultiProviderClient(configManager, websocketManager);
    var downloadingClient = new DownloadingNntpClient(multiProviderResult.Client, configManager);
    var cachingClient = new LiveSegmentCachingNntpClient(downloadingClient, liveSegmentCache);
    return new PipelineResult(cachingClient, multiProviderResult.Stats);
}

private static (MultiProviderNntpClient Client, ConnectionPoolStats Stats) CreateMultiProviderClient(
    ConfigManager configManager,
    WebsocketManager websocketManager)
{
    var providerConfig = configManager.GetUsenetProviderConfig();
    var connectionPoolStats = new ConnectionPoolStats(providerConfig, websocketManager);
    var providerClients = providerConfig.Providers
        .Select((provider, index) => CreateProviderClient(
            provider,
            connectionPoolStats.GetOnConnectionPoolChanged(index)
        ))
        .ToList();
    return (new MultiProviderNntpClient(providerClients), connectionPoolStats);
}
```

**Note:** This keeps the same `CreatePipeline(...)` path for both initial construction and provider reloads, which makes the dependency flow explicit and easier to extend later.

- [ ] **Step 3: Verify build**

Run: `cd backend && dotnet build`
Expected: Build succeeded.

- [ ] **Step 4: Run tests**

Run: `cd backend.Tests && dotnet test`
Expected: All tests pass.

- [ ] **Step 5: Commit**

```bash
git add backend/Clients/Usenet/Connections/ConnectionPoolStats.cs backend/Clients/Usenet/UsenetStreamingClient.cs
git commit -m "Expose ConnectionPoolStats through explicit DI ownership"
```

---

### Task 3: Expose ActiveSessionCount on ReadAheadWarmingService

**Files:**
- Modify: `backend/Services/ReadAheadWarmingService.cs`

- [ ] **Step 1: Add property**

Add after the `_sessions` field:

```csharp
public int ActiveSessionCount => _sessions.Count;
```

- [ ] **Step 2: Verify build and commit**

```bash
cd backend && dotnet build
git add backend/Services/ReadAheadWarmingService.cs
git commit -m "Expose ActiveSessionCount for metrics"
```

---

### Task 4: Create NzbdavMetricsCollector as a Prometheus Collector

**Files:**
- Create: `backend/Metrics/NzbdavMetricsCollector.cs`

This uses prometheus-net's `Collector` base class, which is called on-demand when `/metrics` is scraped — no background timer, no polling.

- [ ] **Step 1: Create the collector**

```csharp
// backend/Metrics/NzbdavMetricsCollector.cs
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Caching;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using Prometheus;

namespace NzbWebDAV.Metrics;

public sealed class NzbdavMetricsCollector : IDisposable
{
    // Cache metrics
    private static readonly Gauge CachedBytes = Prometheus.Metrics.CreateGauge(
        "nzbdav_cache_bytes", "Total bytes in segment cache");
    private static readonly Gauge CachedSegments = Prometheus.Metrics.CreateGauge(
        "nzbdav_cache_segments", "Cached segments by category", "category");
    private static readonly Gauge CacheHitRate = Prometheus.Metrics.CreateGauge(
        "nzbdav_cache_hit_rate", "Cache hit rate (0.0-1.0)");
    private static readonly Counter CacheHits = Prometheus.Metrics.CreateCounter(
        "nzbdav_cache_hits_total", "Cache hits");
    private static readonly Counter CacheMisses = Prometheus.Metrics.CreateCounter(
        "nzbdav_cache_misses_total", "Cache misses");
    private static readonly Counter CacheEvictions = Prometheus.Metrics.CreateCounter(
        "nzbdav_cache_evictions_total", "Cache evictions");
    private static readonly Counter CacheDedupes = Prometheus.Metrics.CreateCounter(
        "nzbdav_cache_dedupes_total", "Deduplicated inflight requests");

    // NNTP pool metrics
    private static readonly Gauge NntpLive = Prometheus.Metrics.CreateGauge(
        "nzbdav_nntp_connections_live", "Live NNTP connections");
    private static readonly Gauge NntpIdle = Prometheus.Metrics.CreateGauge(
        "nzbdav_nntp_connections_idle", "Idle NNTP connections");
    private static readonly Gauge NntpActive = Prometheus.Metrics.CreateGauge(
        "nzbdav_nntp_connections_active", "Active NNTP connections");
    private static readonly Gauge NntpMax = Prometheus.Metrics.CreateGauge(
        "nzbdav_nntp_connections_max", "Max pooled NNTP connections");

    // Warming + queue
    private static readonly Gauge WarmingSessions = Prometheus.Metrics.CreateGauge(
        "nzbdav_warming_sessions_active", "Active read-ahead warming sessions");
    private static readonly Gauge QueueProcessing = Prometheus.Metrics.CreateGauge(
        "nzbdav_queue_processing", "1 if queue item is processing");

    // Active streams — incremented/decremented by StreamExecutionService
    private static readonly Gauge ActiveStreamsGauge = Prometheus.Metrics.CreateGauge(
        "nzbdav_streams_active", "Active video streams");

    private readonly LiveSegmentCache _cache;
    private readonly UsenetStreamingClient _usenetClient;
    private readonly ReadAheadWarmingService _warming;
    private readonly QueueManager _queue;

    // Counter delta tracking
    private long _prevHits, _prevMisses, _prevEvictions, _prevDedupes;

    public NzbdavMetricsCollector(
        LiveSegmentCache cache,
        UsenetStreamingClient usenetClient,
        ReadAheadWarmingService warming,
        QueueManager queue)
    {
        _cache = cache;
        _usenetClient = usenetClient;
        _warming = warming;
        _queue = queue;

        // Register a before-collect callback so metrics are fresh on each scrape
        Prometheus.Metrics.DefaultRegistry.AddBeforeCollectCallback(CollectMetrics);
    }

    private void CollectMetrics()
    {
        try
        {
            // Cache
            var stats = _cache.GetStats();
            CachedBytes.Set(stats.CachedBytes);
            CachedSegments.WithLabels("video").Set(stats.VideoSegmentCount);
            CachedSegments.WithLabels("small_file").Set(stats.SmallFileCount);
            CachedSegments.WithLabels("unknown").Set(stats.UnknownCount);

            var total = stats.Hits + stats.Misses;
            CacheHitRate.Set(total > 0 ? (double)stats.Hits / total : 0);

            IncrementCounter(CacheHits, stats.Hits, ref _prevHits);
            IncrementCounter(CacheMisses, stats.Misses, ref _prevMisses);
            IncrementCounter(CacheEvictions, stats.Evictions, ref _prevEvictions);
            IncrementCounter(CacheDedupes, stats.Dedupes, ref _prevDedupes);

            // NNTP pool
            var pool = _usenetClient.PoolStats;
            if (pool != null)
            {
                NntpLive.Set(pool.TotalLive);
                NntpIdle.Set(pool.TotalIdle);
                NntpActive.Set(pool.TotalActive);
                NntpMax.Set(pool.MaxPooled);
            }

            // Warming + queue
            WarmingSessions.Set(_warming.ActiveSessionCount);
            var (queueItem, _) = _queue.GetInProgressQueueItem();
            QueueProcessing.Set(queueItem != null ? 1 : 0);
        }
        catch
        {
            // Metrics collection must never crash the app.
        }
    }

    private static void IncrementCounter(Counter counter, long currentValue, ref long previousValue)
    {
        var delta = currentValue - previousValue;
        if (delta > 0) counter.Inc(delta);
        previousValue = currentValue;
    }

    // Called by StreamExecutionService (Plan B) or GetAndHeadHandlerPatch
    public static void IncrementActiveStreams() => ActiveStreamsGauge.Inc();
    public static void DecrementActiveStreams() => ActiveStreamsGauge.Dec();

    public void Dispose()
    {
        // Nothing to dispose — no timer, no background task
        GC.SuppressFinalize(this);
    }
}
```

- [ ] **Step 2: Verify build**

Run: `cd backend && dotnet build`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add backend/Metrics/NzbdavMetricsCollector.cs
git commit -m "Add NzbdavMetricsCollector using scrape-time collection"
```

---

### Task 5: Wire metrics into Program.cs

**Files:**
- Modify: `backend/Program.cs`

- [ ] **Step 1: Add usings**

```csharp
using NzbWebDAV.Metrics;
using Prometheus;
```

- [ ] **Step 2: Register collector in DI**

After `.AddSingleton<ReadAheadWarmingService>()`:

```csharp
.AddSingleton<NzbdavMetricsCollector>()
```

- [ ] **Step 3: Map /metrics and enable built-in HTTP metrics**

After `app.MapHealthChecks("/health");`:

```csharp
app.UseHttpMetrics(); // prometheus-net built-in request duration/count metrics
app.MapMetrics();     // serves /metrics endpoint
// Eagerly resolve to register the before-collect callback
_ = app.Services.GetRequiredService<NzbdavMetricsCollector>();
```

- [ ] **Step 4: Verify build**

Run: `cd backend && dotnet build`
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add backend/Program.cs
git commit -m "Wire Prometheus /metrics with built-in HTTP metrics"
```

---

### Task 6: Track active streams in GetAndHeadHandlerPatch

**Files:**
- Modify: `backend/WebDav/Base/GetAndHeadHandlerPatch.cs`

- [ ] **Step 1: Add using**

```csharp
using NzbWebDAV.Metrics;
```

- [ ] **Step 2: Wrap the stream copy with active stream tracking**

Find the `if (!isHeadRequest)` block and wrap it:

```csharp
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

- [ ] **Step 3: Verify build and test**

Run: `cd backend && dotnet build && cd ../backend.Tests && dotnet test`
Expected: All pass.

- [ ] **Step 4: Commit**

```bash
git add backend/WebDav/Base/GetAndHeadHandlerPatch.cs
git commit -m "Track active WebDAV streams in Prometheus"
```

---

### Task 7: Integration verification

- [ ] **Step 1: Full test suite**

Run: `cd backend.Tests && dotnet test --verbosity normal`
Expected: All tests pass.

- [ ] **Step 2: Manual /metrics verification**

```bash
cd backend && dotnet run &
sleep 3
curl -s http://localhost:8080/metrics | grep nzbdav_
```

Expected output includes:
```
nzbdav_cache_bytes 0
nzbdav_cache_segments{category="video"} 0
nzbdav_cache_hit_rate 0
nzbdav_nntp_connections_max 0
nzbdav_warming_sessions_active 0
nzbdav_streams_active 0
nzbdav_queue_processing 0
```

Plus prometheus-net's built-in `http_request_duration_seconds` histograms.

- [ ] **Step 3: Verify /health still works**

```bash
curl -s http://localhost:8080/health
```

Expected: `Healthy`

- [ ] **Step 4: Final commit**

```bash
git add -A
git commit -m "Plan A complete: Prometheus observability for cache, NNTP, streams, queue"
```
