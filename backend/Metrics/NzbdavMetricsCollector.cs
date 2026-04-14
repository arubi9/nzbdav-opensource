using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Caching;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.Services.NntpLeasing;
using Prometheus;

namespace NzbWebDAV.Metrics;

public sealed class NzbdavMetricsCollector
{
    private readonly Func<LiveSegmentCacheStats> _getCacheStats;
    private readonly Func<long> _getMaxCacheBytes;
    private readonly Func<ConnectionPoolStats?> _getPoolStats;
    private readonly Func<int> _getHealthyProviders;
    private readonly Func<int> _getWarmingSessions;
    private readonly Func<int> _getQueueProcessing;
    private readonly Func<IReadOnlyList<NntpLeaseState.ProviderLeaseObservation>> _getLeaseObservations;
    private readonly Func<ObjectStorageSegmentCache?> _getL2Cache;
    private readonly Func<SharedHeaderCache?> _getSharedHeaderCache;
    private readonly Lock _leaseMetricLock = new();
    private readonly HashSet<int> _observedLeaseProviders = [];

    private readonly Gauge _cachedBytes;
    private readonly Gauge _cacheMaxBytes;
    private readonly Gauge _cachedSegments;
    private readonly Gauge _cacheHitRate;
    private readonly Counter _cacheHits;
    private readonly Counter _cacheMisses;
    private readonly Counter _cacheEvictions;
    private readonly Counter _cacheDedupes;
    private readonly Counter _l2Hits;
    private readonly Counter _l2Misses;
    private readonly Counter _l2Writes;
    private readonly Counter _l2WriteFailures;
    private readonly Counter _l2WritesDropped;
    private readonly Gauge _l2Enabled;
    private readonly Counter _sharedHeaderHits;
    private readonly Counter _sharedHeaderMisses;
    private readonly Counter _sharedHeaderWriteFailures;
    private readonly Gauge _nntpLive;
    private readonly Gauge _nntpIdle;
    private readonly Gauge _nntpActive;
    private readonly Gauge _nntpMax;
    private readonly Gauge _nntpProvidersHealthy;
    private readonly Gauge _nntpLeaseSlots;
    private readonly Gauge _nntpLeaseSlotsTotal;
    private readonly Gauge _nntpLeaseEpoch;
    private readonly Gauge _nntpLeaseFresh;
    private readonly Gauge _nntpLeaseExpiresInSeconds;
    private readonly Gauge _warmingSessions;
    private readonly Gauge _queueProcessing;

    private long _previousHits;
    private long _previousMisses;
    private long _previousEvictions;
    private long _previousDedupes;
    private long _previousL2Hits;
    private long _previousL2Misses;
    private long _previousL2Writes;
    private long _previousL2WriteFailures;
    private long _previousL2WritesDropped;
    private long _previousSharedHeaderHits;
    private long _previousSharedHeaderMisses;
    private long _previousSharedHeaderWriteFailures;

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
        QueueManager queue,
        NntpLeaseState leaseState,
        ObjectStorageSegmentCache? l2Cache = null,
        SharedHeaderCache? sharedHeaderCache = null
    ) : this(
        () => cache.GetStats(),
        () => cache.MaxCacheSizeBytes,
        () => usenetClient.PoolStats,
        () => usenetClient.HealthyProviderCount,
        () => warming.ActiveSessionCount,
        () => queue.GetInProgressQueueItem().queueItem != null ? 1 : 0,
        () => leaseState.GetProviderLeaseObservations(DateTime.UtcNow),
        () => l2Cache,
        () => sharedHeaderCache,
        Prometheus.Metrics.DefaultRegistry,
        Prometheus.Metrics.WithCustomRegistry(Prometheus.Metrics.DefaultRegistry)
    )
    {
    }

    public NzbdavMetricsCollector(
        Func<LiveSegmentCacheStats> getCacheStats,
        Func<long> getMaxCacheBytes,
        Func<ConnectionPoolStats?> getPoolStats,
        Func<int> getHealthyProviders,
        Func<int> getWarmingSessions,
        Func<int> getQueueProcessing,
        Func<IReadOnlyList<NntpLeaseState.ProviderLeaseObservation>> getLeaseObservations,
        Func<ObjectStorageSegmentCache?> getL2Cache,
        Func<SharedHeaderCache?> getSharedHeaderCache,
        CollectorRegistry registry,
        IMetricFactory metricFactory
    )
    {
        _getCacheStats = getCacheStats;
        _getMaxCacheBytes = getMaxCacheBytes;
        _getPoolStats = getPoolStats;
        _getHealthyProviders = getHealthyProviders;
        _getWarmingSessions = getWarmingSessions;
        _getQueueProcessing = getQueueProcessing;
        _getLeaseObservations = getLeaseObservations;
        _getL2Cache = getL2Cache;
        _getSharedHeaderCache = getSharedHeaderCache;

        _cachedBytes = metricFactory.CreateGauge(
            "nzbdav_cache_bytes",
            "Total bytes in segment cache");
        _cacheMaxBytes = metricFactory.CreateGauge(
            "nzbdav_cache_max_bytes",
            "Configured maximum cache size in bytes");
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
        _l2Hits = metricFactory.CreateCounter(
            "nzbdav_l2_cache_hits_total",
            "L2 object-storage cache hits");
        _l2Misses = metricFactory.CreateCounter(
            "nzbdav_l2_cache_misses_total",
            "L2 object-storage cache misses");
        _l2Writes = metricFactory.CreateCounter(
            "nzbdav_l2_cache_writes_total",
            "L2 object-storage cache writes");
        _l2WriteFailures = metricFactory.CreateCounter(
            "nzbdav_l2_cache_write_failures_total",
            "L2 object-storage write failures");
        _l2WritesDropped = metricFactory.CreateCounter(
            "nzbdav_l2_cache_writes_dropped_total",
            "L2 object-storage writes dropped due to full queue");
        _l2Enabled = metricFactory.CreateGauge(
            "nzbdav_l2_cache_enabled",
            "1 if L2 cache is enabled, 0 otherwise");
        _sharedHeaderHits = metricFactory.CreateCounter(
            "nzbdav_shared_header_cache_hits_total",
            "Shared (Postgres) header cache hits");
        _sharedHeaderMisses = metricFactory.CreateCounter(
            "nzbdav_shared_header_cache_misses_total",
            "Shared (Postgres) header cache misses");
        _sharedHeaderWriteFailures = metricFactory.CreateCounter(
            "nzbdav_shared_header_cache_write_failures_total",
            "Shared (Postgres) header cache write failures");

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
        _nntpLeaseSlots = metricFactory.CreateGauge(
            "nzbdav_nntp_lease_slots",
            "Current local NNTP lease slots by provider and slot kind",
            ["kind", "provider_index"]);
        _nntpLeaseSlotsTotal = metricFactory.CreateGauge(
            "nzbdav_nntp_lease_slots_total",
            "Current local NNTP lease slots summed across providers by slot kind",
            ["kind"]);
        _nntpLeaseEpoch = metricFactory.CreateGauge(
            "nzbdav_nntp_lease_epoch",
            "Current local NNTP lease epoch by provider",
            ["provider_index"]);
        _nntpLeaseFresh = metricFactory.CreateGauge(
            "nzbdav_nntp_lease_fresh",
            "1 if the local provider lease is fresh, 0 otherwise",
            ["provider_index"]);
        _nntpLeaseExpiresInSeconds = metricFactory.CreateGauge(
            "nzbdav_nntp_lease_expires_in_seconds",
            "Seconds until the local provider lease expires, clamped at 0",
            ["provider_index"]);

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
            _cacheMaxBytes.Set(_getMaxCacheBytes());
            _cachedSegments.WithLabels("video").Set(stats.VideoSegmentCount);
            _cachedSegments.WithLabels("small_file").Set(stats.SmallFileCount);
            _cachedSegments.WithLabels("unknown").Set(stats.UnknownCount);

            var totalLookups = stats.Hits + stats.Misses;
            _cacheHitRate.Set(totalLookups > 0 ? (double)stats.Hits / totalLookups : 0);

            IncrementCounter(_cacheHits, stats.Hits, ref _previousHits);
            IncrementCounter(_cacheMisses, stats.Misses, ref _previousMisses);
            IncrementCounter(_cacheEvictions, stats.Evictions, ref _previousEvictions);
            IncrementCounter(_cacheDedupes, stats.Dedupes, ref _previousDedupes);

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

            var sharedHeaderCache = _getSharedHeaderCache();
            if (sharedHeaderCache != null)
            {
                IncrementCounter(_sharedHeaderHits, sharedHeaderCache.Hits, ref _previousSharedHeaderHits);
                IncrementCounter(_sharedHeaderMisses, sharedHeaderCache.Misses, ref _previousSharedHeaderMisses);
                IncrementCounter(_sharedHeaderWriteFailures, sharedHeaderCache.WriteFailures, ref _previousSharedHeaderWriteFailures);
            }

            var poolStats = _getPoolStats();
            if (poolStats != null)
            {
                _nntpLive.Set(poolStats.TotalLive);
                _nntpIdle.Set(poolStats.TotalIdle);
                _nntpActive.Set(poolStats.TotalActive);
                _nntpMax.Set(poolStats.MaxPooled);
            }

            _nntpProvidersHealthy.Set(_getHealthyProviders());
            UpdateLeaseMetrics(_getLeaseObservations());

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

    private void UpdateLeaseMetrics(IReadOnlyList<NntpLeaseState.ProviderLeaseObservation> observations)
    {
        var freshObservations = observations.Where(x => x.IsFresh).ToList();
        var totalGranted = freshObservations.Sum(x => x.GrantedSlots);
        var totalReserved = freshObservations.Sum(x => x.ReservedSlots);
        var totalBorrowed = freshObservations.Sum(x => x.BorrowedSlots);

        _nntpLeaseSlotsTotal.WithLabels("granted").Set(totalGranted);
        _nntpLeaseSlotsTotal.WithLabels("reserved").Set(totalReserved);
        _nntpLeaseSlotsTotal.WithLabels("borrowed").Set(totalBorrowed);

        var currentProviderIndexes = observations
            .Select(x => x.ProviderIndex)
            .ToHashSet();

        lock (_leaseMetricLock)
        {
            foreach (var staleProviderIndex in _observedLeaseProviders.Except(currentProviderIndexes).ToList())
                SetLeaseMetrics(staleProviderIndex, grantedSlots: 0, reservedSlots: 0, borrowedSlots: 0, epoch: 0, isFresh: false, secondsUntilExpiry: 0);

            foreach (var observation in observations)
            {
                SetLeaseMetrics(
                    observation.ProviderIndex,
                    observation.GrantedSlots,
                    observation.ReservedSlots,
                    observation.BorrowedSlots,
                    observation.Epoch,
                    observation.IsFresh,
                    observation.SecondsUntilExpiry);
            }

            _observedLeaseProviders.Clear();
            _observedLeaseProviders.UnionWith(currentProviderIndexes);
        }
    }

    private void SetLeaseMetrics(
        int providerIndex,
        int grantedSlots,
        int reservedSlots,
        int borrowedSlots,
        long epoch,
        bool isFresh,
        int secondsUntilExpiry)
    {
        var providerIndexLabel = providerIndex.ToString();
        _nntpLeaseSlots.WithLabels("granted", providerIndexLabel).Set(grantedSlots);
        _nntpLeaseSlots.WithLabels("reserved", providerIndexLabel).Set(reservedSlots);
        _nntpLeaseSlots.WithLabels("borrowed", providerIndexLabel).Set(borrowedSlots);
        _nntpLeaseEpoch.WithLabels(providerIndexLabel).Set(epoch);
        _nntpLeaseFresh.WithLabels(providerIndexLabel).Set(isFresh ? 1 : 0);
        _nntpLeaseExpiresInSeconds.WithLabels(providerIndexLabel).Set(secondsUntilExpiry);
    }

    public static void IncrementActiveStreams() => ActiveStreamsGauge.Inc();
    public static void DecrementActiveStreams() => ActiveStreamsGauge.Dec();

    // No IDisposable — this is a singleton that lives for the app lifetime.
    // The AddBeforeCollectCallback registration is never removed.
}
