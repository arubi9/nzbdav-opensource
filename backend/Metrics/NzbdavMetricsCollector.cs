using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Caching;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using Prometheus;

namespace NzbWebDAV.Metrics;

public sealed class NzbdavMetricsCollector
{
    private readonly Func<LiveSegmentCacheStats> _getCacheStats;
    private readonly Func<ConnectionPoolStats?> _getPoolStats;
    private readonly Func<int> _getHealthyProviders;
    private readonly Func<int> _getWarmingSessions;
    private readonly Func<int> _getQueueProcessing;

    private readonly Gauge _cachedBytes;
    private readonly Gauge _cachedSegments;
    private readonly Gauge _cacheHitRate;
    private readonly Counter _cacheHits;
    private readonly Counter _cacheMisses;
    private readonly Counter _cacheEvictions;
    private readonly Counter _cacheDedupes;
    private readonly Gauge _nntpLive;
    private readonly Gauge _nntpIdle;
    private readonly Gauge _nntpActive;
    private readonly Gauge _nntpMax;
    private readonly Gauge _nntpProvidersHealthy;
    private readonly Gauge _warmingSessions;
    private readonly Gauge _queueProcessing;

    private long _previousHits;
    private long _previousMisses;
    private long _previousEvictions;
    private long _previousDedupes;

    private static readonly Gauge ActiveStreamsGauge = Prometheus.Metrics.CreateGauge(
        "nzbdav_streams_active",
        "Active video streams");

    static NzbdavMetricsCollector()
    {
        ActiveStreamsGauge.Set(0);
    }

    public NzbdavMetricsCollector(
        LiveSegmentCache cache,
        UsenetStreamingClient usenetClient,
        ReadAheadWarmingService warming,
        QueueManager queue
    ) : this(
        () => cache.GetStats(),
        () => usenetClient.PoolStats,
        () => usenetClient.HealthyProviderCount,
        () => warming.ActiveSessionCount,
        () => queue.GetInProgressQueueItem().queueItem != null ? 1 : 0,
        Prometheus.Metrics.DefaultRegistry,
        Prometheus.Metrics.WithCustomRegistry(Prometheus.Metrics.DefaultRegistry)
    )
    {
    }

    public NzbdavMetricsCollector(
        Func<LiveSegmentCacheStats> getCacheStats,
        Func<ConnectionPoolStats?> getPoolStats,
        Func<int> getHealthyProviders,
        Func<int> getWarmingSessions,
        Func<int> getQueueProcessing,
        CollectorRegistry registry,
        IMetricFactory metricFactory
    )
    {
        _getCacheStats = getCacheStats;
        _getPoolStats = getPoolStats;
        _getHealthyProviders = getHealthyProviders;
        _getWarmingSessions = getWarmingSessions;
        _getQueueProcessing = getQueueProcessing;

        _cachedBytes = metricFactory.CreateGauge(
            "nzbdav_cache_bytes",
            "Total bytes in segment cache");
        _cachedSegments = metricFactory.CreateGauge(
            "nzbdav_cache_segments",
            "Cached segments by category",
            ["category"]);
        _cacheHitRate = metricFactory.CreateGauge(
            "nzbdav_cache_hit_rate",
            "Cache hit rate (0.0-1.0)");
        _cacheHits = metricFactory.CreateCounter(
            "nzbdav_cache_hits_total",
            "Cache hits");
        _cacheMisses = metricFactory.CreateCounter(
            "nzbdav_cache_misses_total",
            "Cache misses");
        _cacheEvictions = metricFactory.CreateCounter(
            "nzbdav_cache_evictions_total",
            "Cache evictions");
        _cacheDedupes = metricFactory.CreateCounter(
            "nzbdav_cache_dedupes_total",
            "Deduplicated inflight requests");

        _nntpLive = metricFactory.CreateGauge(
            "nzbdav_nntp_connections_live",
            "Live NNTP connections");
        _nntpIdle = metricFactory.CreateGauge(
            "nzbdav_nntp_connections_idle",
            "Idle NNTP connections");
        _nntpActive = metricFactory.CreateGauge(
            "nzbdav_nntp_connections_active",
            "Active NNTP connections");
        _nntpMax = metricFactory.CreateGauge(
            "nzbdav_nntp_connections_max",
            "Max pooled NNTP connections");
        _nntpProvidersHealthy = metricFactory.CreateGauge(
            "nzbdav_nntp_providers_healthy",
            "Number of NNTP providers not in cooldown");

        _warmingSessions = metricFactory.CreateGauge(
            "nzbdav_warming_sessions_active",
            "Active read-ahead warming sessions");
        _queueProcessing = metricFactory.CreateGauge(
            "nzbdav_queue_processing",
            "1 if queue item is processing");

        registry.AddBeforeCollectCallback(CollectMetrics);
    }

    private void CollectMetrics()
    {
        try
        {
            var stats = _getCacheStats();
            _cachedBytes.Set(stats.CachedBytes);
            _cachedSegments.WithLabels("video").Set(stats.VideoSegmentCount);
            _cachedSegments.WithLabels("small_file").Set(stats.SmallFileCount);
            _cachedSegments.WithLabels("unknown").Set(stats.UnknownCount);

            var totalLookups = stats.Hits + stats.Misses;
            _cacheHitRate.Set(totalLookups > 0 ? (double)stats.Hits / totalLookups : 0);

            IncrementCounter(_cacheHits, stats.Hits, ref _previousHits);
            IncrementCounter(_cacheMisses, stats.Misses, ref _previousMisses);
            IncrementCounter(_cacheEvictions, stats.Evictions, ref _previousEvictions);
            IncrementCounter(_cacheDedupes, stats.Dedupes, ref _previousDedupes);

            var poolStats = _getPoolStats();
            if (poolStats != null)
            {
                _nntpLive.Set(poolStats.TotalLive);
                _nntpIdle.Set(poolStats.TotalIdle);
                _nntpActive.Set(poolStats.TotalActive);
                _nntpMax.Set(poolStats.MaxPooled);
            }

            _nntpProvidersHealthy.Set(_getHealthyProviders());

            _warmingSessions.Set(_getWarmingSessions());
            _queueProcessing.Set(_getQueueProcessing());
        }
        catch
        {
            // Metrics collection must never crash the app.
        }
    }

    private static void IncrementCounter(Counter counter, long currentValue, ref long previousValue)
    {
        var delta = currentValue - previousValue;
        if (delta > 0)
            counter.Inc(delta);
        previousValue = currentValue;
    }

    public static void IncrementActiveStreams() => ActiveStreamsGauge.Inc();
    public static void DecrementActiveStreams() => ActiveStreamsGauge.Dec();

    // No IDisposable — this is a singleton that lives for the app lifetime.
    // The AddBeforeCollectCallback registration is never removed.
}
