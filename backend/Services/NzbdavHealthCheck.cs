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

        if (cacheUtilization > 0.9)
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

            if (utilization > 0.9)
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
