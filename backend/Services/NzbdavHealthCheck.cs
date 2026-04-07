using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Caching;
using NzbWebDAV.Database;

namespace NzbWebDAV.Services;

public class NzbdavHealthCheck(
    LiveSegmentCache liveSegmentCache,
    UsenetStreamingClient usenetClient,
    IHostApplicationLifetime lifetime
) : IHealthCheck
{
    // HYSTERESIS BANDS for saturation detection. Degraded state ENTERS at
    // 90% utilization but only EXITS at 80%. Without this, a cache or
    // NNTP pool that hovers near the 90% boundary flaps the health status
    // every few seconds as segments evict/add. With HAProxy's /ready
    // backpressure routing, that flap causes nodes to rapid-fire in and
    // out of rotation — which is worse than either staying in or staying
    // out. The 10-point gap is conservative enough that normal load
    // oscillations don't cross both thresholds.
    private const double DegradedEnterThreshold = 0.90;
    private const double DegradedExitThreshold = 0.80;

    // Static latches remembering the previous "degraded" decision per
    // subsystem. These are process-wide — health checks are stateless
    // per-call but the hysteresis needs memory. Volatile bool is atomic
    // on all .NET-supported architectures; a race between two concurrent
    // health-check calls only causes a one-tick delay in flap resolution,
    // not incorrect state.
    private static volatile bool _cacheDegradedLatch;
    private static volatile bool _nntpDegradedLatch;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        // Per failure model section 2: return Unhealthy during shutdown for LB draining
        if (lifetime.ApplicationStopping.IsCancellationRequested)
            return HealthCheckResult.Unhealthy("Shutting down — draining connections");

        var data = new Dictionary<string, object>();
        var status = HealthStatus.Healthy;

        // Check 1: Database connectivity
        try
        {
            await using var dbContext = new DavDatabaseContext();
            var count = await dbContext.Items.CountAsync(cancellationToken).ConfigureAwait(false);
            data["database"] = "connected";
            data["database_items"] = count;
        }
        catch (Exception ex)
        {
            status = HealthStatus.Unhealthy;
            data["database"] = $"unreachable: {ex.Message}";
        }

        // Check 2: Cache directory writable
        try
        {
            var testFile = Path.Combine(liveSegmentCache.CacheDirectory, ".health-check");
            await File.WriteAllTextAsync(testFile, "ok", cancellationToken).ConfigureAwait(false);
            File.Delete(testFile);
            data["cache_directory"] = "writable";
        }
        catch (Exception ex)
        {
            status = HealthStatus.Unhealthy;
            data["cache_directory"] = $"not writable: {ex.Message}";
        }

        // Check 3: Cache utilization — per failure model section 3
        var cacheStats = liveSegmentCache.GetStats();
        data["cache_segments"] = cacheStats.CachedSegmentCount;
        data["cache_bytes"] = cacheStats.CachedBytes;
        var maxBytes = liveSegmentCache.MaxCacheSizeBytes;
        var cacheUtilization = maxBytes > 0 ? (double)cacheStats.CachedBytes / maxBytes : 0;
        data["cache_utilization"] = cacheUtilization;
        data["cache_max_bytes"] = maxBytes;

        var totalLookups = cacheStats.Hits + cacheStats.Misses;
        data["cache_hit_rate"] = totalLookups > 0 ? (double)cacheStats.Hits / totalLookups : 0;

        // Hysteresis: enter Degraded at >0.90, exit at <0.80. Prevents
        // flap when utilization hovers near the 90% boundary.
        var wasCacheDegraded = _cacheDegradedLatch;
        if (!wasCacheDegraded && cacheUtilization > DegradedEnterThreshold)
            _cacheDegradedLatch = true;
        else if (wasCacheDegraded && cacheUtilization < DegradedExitThreshold)
            _cacheDegradedLatch = false;
        data["cache_degraded_latch"] = _cacheDegradedLatch;

        if (_cacheDegradedLatch)
            status = status == HealthStatus.Unhealthy ? HealthStatus.Unhealthy : HealthStatus.Degraded;

        // Check 4: NNTP pool utilization — per failure model section 1
        var poolStats = usenetClient.PoolStats;
        if (poolStats != null)
        {
            var utilization = poolStats.MaxPooled > 0
                ? (double)poolStats.TotalActive / poolStats.MaxPooled
                : 0;
            data["nntp_utilization"] = utilization;
            data["nntp_active"] = poolStats.TotalActive;
            data["nntp_max"] = poolStats.MaxPooled;

            // Same hysteresis pattern as the cache check above. Keeps
            // the /ready backpressure signal stable under normal load
            // oscillations near the 90% boundary.
            var wasNntpDegraded = _nntpDegradedLatch;
            if (!wasNntpDegraded && utilization > DegradedEnterThreshold)
                _nntpDegradedLatch = true;
            else if (wasNntpDegraded && utilization < DegradedExitThreshold)
                _nntpDegradedLatch = false;
            data["nntp_degraded_latch"] = _nntpDegradedLatch;

            if (_nntpDegradedLatch)
                status = status == HealthStatus.Unhealthy ? HealthStatus.Unhealthy : HealthStatus.Degraded;
        }
        else
        {
            data["nntp_pool"] = "not initialized";
        }

        data["nntp_healthy_providers"] = usenetClient.HealthyProviderCount;
        data["nntp_total_providers"] = usenetClient.TotalProviderCount;
        if (usenetClient.TotalProviderCount > 0 && !usenetClient.HasAvailableProvider)
            status = HealthStatus.Unhealthy;

        return new HealthCheckResult(status, "NZBDAV health check", data: data);
    }
}
