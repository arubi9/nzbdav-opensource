using System.Text;
using System.Text.Json;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Caching;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Metrics;
using NzbWebDAV.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Services.NntpLeasing;
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
        var now = new DateTime(2026, 4, 14, 12, 0, 0, DateTimeKind.Utc);
        var leaseState = new NntpLeaseState();
        leaseState.Apply(providerIndex: 0, grantedSlots: 4, epoch: 9, leaseUntil: now.AddSeconds(30), reservedSlots: 3, borrowedSlots: 1);
        leaseState.Apply(providerIndex: 1, grantedSlots: 2, epoch: 5, leaseUntil: now.AddSeconds(-5), reservedSlots: 2, borrowedSlots: 0);

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
            () => 10_000L,
            () => poolStats,
            () => 1,
            () => 7,
            () => 1,
            () => leaseState.GetProviderLeaseObservations(now),
            () => null,
            () => null,
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
        Assert.Contains("nzbdav_nntp_lease_slots{kind=\"granted\",provider_index=\"0\"} 4", metricsText);
        Assert.Contains("nzbdav_nntp_lease_slots{kind=\"reserved\",provider_index=\"0\"} 3", metricsText);
        Assert.Contains("nzbdav_nntp_lease_slots{kind=\"borrowed\",provider_index=\"0\"} 1", metricsText);
        Assert.Contains("nzbdav_nntp_lease_epoch{provider_index=\"0\"} 9", metricsText);
        Assert.Contains("nzbdav_nntp_lease_fresh{provider_index=\"0\"} 1", metricsText);
        Assert.Contains("nzbdav_nntp_lease_expires_in_seconds{provider_index=\"0\"} 30", metricsText);
        Assert.Contains("nzbdav_nntp_lease_fresh{provider_index=\"1\"} 0", metricsText);
        Assert.Contains("nzbdav_nntp_lease_expires_in_seconds{provider_index=\"1\"} 0", metricsText);
        Assert.Contains("nzbdav_nntp_lease_slots_total{kind=\"granted\"} 6", metricsText);
        Assert.Contains("nzbdav_nntp_lease_slots_total{kind=\"reserved\"} 5", metricsText);
        Assert.Contains("nzbdav_nntp_lease_slots_total{kind=\"borrowed\"} 1", metricsText);
        Assert.Contains("nzbdav_warming_sessions_active 7", metricsText);
        Assert.Contains("nzbdav_queue_processing 1", metricsText);
        Assert.Contains("nzbdav_l2_cache_enabled 0", metricsText);
    }

    [Fact]
    public async Task CollectMetrics_ExportsL2State_WhenEnabled()
    {
        var registry = Prometheus.Metrics.NewCustomRegistry();
        var factory = Prometheus.Metrics.WithCustomRegistry(registry);
        using var l2 = new ObjectStorageSegmentCache(
            bucketName: "bucket",
            queueCapacity: 4,
            ensureBucketExistsAsync: _ => Task.CompletedTask,
            tryReadAsync: (_, _) => Task.FromResult<ObjectStorageSegmentCache.ReadResult?>(null),
            writeAsync: (_, _) => Task.CompletedTask,
            startWriter: false);

        await l2.TryReadAsync("segment-a", CancellationToken.None);
        l2.EnqueueWrite("segment-a", [1], SegmentCategory.Unknown, null, new UsenetSharp.Models.UsenetYencHeader
        {
            FileName = "segment.bin",
            FileSize = 1,
            LineLength = 128,
            PartNumber = 1,
            TotalParts = 1,
            PartSize = 1,
            PartOffset = 0
        });

        var collector = new NzbdavMetricsCollector(
            () => new LiveSegmentCacheStats(0, 0, 0, 0, 0, 0, 0, 0, 0),
            () => 0L,
            () => null,
            () => 0,
            () => 0,
            () => 0,
            () => [],
            () => l2,
            () => null,
            registry,
            factory
        );

        await using var output = new MemoryStream();
        await registry.CollectAndExportAsTextAsync(output);
        var metricsText = Encoding.UTF8.GetString(output.ToArray());

        Assert.Contains("nzbdav_l2_cache_enabled 1", metricsText);
        Assert.Contains("nzbdav_l2_cache_misses_total 1", metricsText);
    }

    [Fact]
    public async Task CollectMetrics_ExportsSharedHeaderCacheState()
    {
        var registry = Prometheus.Metrics.NewCustomRegistry();
        var factory = Prometheus.Metrics.WithCustomRegistry(registry);
        var poolStats = CreatePoolStats(maxConnections: 5, live: 4, idle: 1);
        var sharedHeaderCache = new SharedHeaderCache();

        SetPrivateField(sharedHeaderCache, "_hits", 1L);
        SetPrivateField(sharedHeaderCache, "_misses", 1L);
        SetPrivateField(sharedHeaderCache, "_writeFailures", 1L);

        var collector = new NzbdavMetricsCollector(
            () => new LiveSegmentCacheStats(0, 0, 0, 0, 0, 0, 0, 0, 0),
            () => 0L,
            () => poolStats,
            () => 1,
            () => 0,
            () => 0,
            () => [],
            () => null,
            () => sharedHeaderCache,
            registry,
            factory
        );

        await using var output = new MemoryStream();
        await registry.CollectAndExportAsTextAsync(output);
        var metricsText = Encoding.UTF8.GetString(output.ToArray());

        Assert.Contains("nzbdav_shared_header_cache_hits_total 1", metricsText);
        Assert.Contains("nzbdav_shared_header_cache_misses_total 1", metricsText);
        Assert.Contains("nzbdav_shared_header_cache_write_failures_total 1", metricsText);
    }

    [Fact]
    public async Task SweepOnce_RemovesExpiredRows_ButKeepsFreshRows()
    {
        var fixture = new NzbWebDAV.Tests.Clients.Usenet.Caching.PostgresHeaderCacheFixture();
        if (!fixture.IsAvailable) return;

        await fixture.InitializeAsync();
        try
        {
            await fixture.ResetAsync();

            await using (var dbContext = new DavDatabaseContext())
            {
                dbContext.YencHeaderCache.Add(new YencHeaderCacheEntry
                {
                    SegmentId = "old-segment",
                    FileName = "old.bin",
                    FileSize = 100,
                    LineLength = 128,
                    PartNumber = 1,
                    TotalParts = 1,
                    PartSize = 100,
                    PartOffset = 0,
                    CachedAt = DateTime.UtcNow.AddDays(-100)
                });
                dbContext.YencHeaderCache.Add(new YencHeaderCacheEntry
                {
                    SegmentId = "new-segment",
                    FileName = "new.bin",
                    FileSize = 100,
                    LineLength = 128,
                    PartNumber = 1,
                    TotalParts = 1,
                    PartSize = 100,
                    PartOffset = 0,
                    CachedAt = DateTime.UtcNow.AddDays(-10)
                });
                await dbContext.SaveChangesAsync();
            }

            var configManager = new ConfigManager();
            configManager.UpdateValues(
            [
                new ConfigItem { ConfigName = "cache.metadata-retention-days", ConfigValue = "90" }
            ]);

            var sweeper = new YencHeaderCacheSweeper(configManager);
            await sweeper.SweepOnce(CancellationToken.None);

            await using var verifyContext = new DavDatabaseContext();
            Assert.False(await verifyContext.YencHeaderCache.AnyAsync(x => x.SegmentId == "old-segment"));
            Assert.True(await verifyContext.YencHeaderCache.AnyAsync(x => x.SegmentId == "new-segment"));
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static UsenetSharp.Models.UsenetYencHeader CreateHeader(string fileName, long partOffset)
    {
        return new UsenetSharp.Models.UsenetYencHeader
        {
            FileName = fileName,
            FileSize = partOffset + 100,
            LineLength = 128,
            PartNumber = 1,
            TotalParts = 1,
            PartSize = 100,
            PartOffset = partOffset
        };
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

    private static void SetPrivateField(object target, string fieldName, long value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        field!.SetValue(target, value);
    }
}
