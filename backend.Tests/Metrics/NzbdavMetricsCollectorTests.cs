using System.Text;
using System.Text.Json;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Caching;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Metrics;
using NzbWebDAV.Models;
using NzbWebDAV.Websocket;
using Prometheus;

namespace backend.Tests.Metrics;

public sealed class NzbdavMetricsCollectorTests
{
    [Fact]
    public async Task CollectMetrics_ExportsCurrentStateOnScrape()
    {
        var registry = Prometheus.Metrics.NewCustomRegistry();
        var factory = Prometheus.Metrics.WithCustomRegistry(registry);
        var poolStats = CreatePoolStats(maxConnections: 5, live: 4, idle: 1);

        var collector = new NzbdavMetricsCollector(
            () => new LiveSegmentCacheStats(
                CachedSegmentCount: 6,
                CachedBytes: 1234,
                Hits: 10,
                Misses: 2,
                Dedupes: 3,
                Evictions: 1,
                SmallFileCount: 2,
                VideoSegmentCount: 3,
                UnknownCount: 1),
            () => poolStats,
            () => 1,
            () => 7,
            () => 1,
            registry,
            factory
        );

        await using var output = new MemoryStream();
        await registry.CollectAndExportAsTextAsync(output);
        var metricsText = Encoding.UTF8.GetString(output.ToArray());

        Assert.Contains("nzbdav_cache_bytes 1234", metricsText);
        Assert.Contains("nzbdav_cache_segments{category=\"video\"} 3", metricsText);
        Assert.Contains("nzbdav_cache_segments{category=\"small_file\"} 2", metricsText);
        Assert.Contains("nzbdav_cache_segments{category=\"unknown\"} 1", metricsText);
        Assert.Contains("nzbdav_cache_hit_rate 0.8333333333333334", metricsText);
        Assert.Contains("nzbdav_cache_hits_total 10", metricsText);
        Assert.Contains("nzbdav_cache_misses_total 2", metricsText);
        Assert.Contains("nzbdav_cache_evictions_total 1", metricsText);
        Assert.Contains("nzbdav_cache_dedupes_total 3", metricsText);
        Assert.Contains("nzbdav_nntp_connections_live 4", metricsText);
        Assert.Contains("nzbdav_nntp_connections_idle 1", metricsText);
        Assert.Contains("nzbdav_nntp_connections_active 3", metricsText);
        Assert.Contains("nzbdav_nntp_connections_max 5", metricsText);
        Assert.Contains("nzbdav_nntp_providers_healthy 1", metricsText);
        Assert.Contains("nzbdav_warming_sessions_active 7", metricsText);
        Assert.Contains("nzbdav_queue_processing 1", metricsText);
    }

    private static ConnectionPoolStats CreatePoolStats(int maxConnections, int live, int idle)
    {
        var providerConfig = new UsenetProviderConfig
        {
            Providers =
            [
                new UsenetProviderConfig.ConnectionDetails
                {
                    Type = ProviderType.Pooled,
                    Host = "example.test",
                    Port = 563,
                    UseSsl = true,
                    User = "user",
                    Pass = "pass",
                    MaxConnections = maxConnections
                }
            ]
        };

        var poolStats = new ConnectionPoolStats(providerConfig, new WebsocketManager());
        var onChanged = poolStats.GetOnConnectionPoolChanged(0);
        onChanged.Invoke(
            sender: null,
            new ConnectionPoolStats.ConnectionPoolChangedEventArgs(live, idle, maxConnections)
        );
        return poolStats;
    }
}
