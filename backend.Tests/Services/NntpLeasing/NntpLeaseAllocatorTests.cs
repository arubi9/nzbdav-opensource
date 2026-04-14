using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Services.NntpLeasing;

namespace backend.Tests.Services.NntpLeasing;

[Collection(nameof(backend.Tests.Config.EnvironmentVariableCollection))]
public sealed class NntpLeaseAllocatorTests
{
    [Fact]
    public async Task AllocateOnce_WhenBothRolesHeartbeatForProvider_WritesSeventyThirtyLeases()
    {
        await using var harness = await SqliteAllocatorHarness.CreateAsync(CreateConfigManager((ProviderType.Pooled, 10)));
        await harness.SeedHeartbeatAsync("stream-1", providerIndex: 0, NodeRole.Streaming, hasDemand: true, heartbeatAt: harness.Now);
        await harness.SeedHeartbeatAsync("ingest-1", providerIndex: 0, NodeRole.Ingest, hasDemand: true, heartbeatAt: harness.Now);
        var allocator = harness.CreateAllocator(isLeader: true);

        await allocator.AllocateOnce(CancellationToken.None);

        var leases = await harness.ReadLeasesAsync();
        Assert.Collection(
            leases.OrderBy(x => x.NodeId, StringComparer.Ordinal),
            lease =>
            {
                Assert.Equal("ingest-1", lease.NodeId);
                Assert.Equal(0, lease.ProviderIndex);
                Assert.Equal(NodeRole.Ingest, lease.Role);
                Assert.Equal(3, lease.ReservedSlots);
                Assert.Equal(0, lease.BorrowedSlots);
                Assert.Equal(3, lease.GrantedSlots);
                Assert.Equal(1, lease.Epoch);
                Assert.Equal(harness.Now.Add(harness.LeaseTtl), lease.LeaseUntil);
                Assert.Equal(harness.Now, lease.UpdatedAt);
            },
            lease =>
            {
                Assert.Equal("stream-1", lease.NodeId);
                Assert.Equal(0, lease.ProviderIndex);
                Assert.Equal(NodeRole.Streaming, lease.Role);
                Assert.Equal(7, lease.ReservedSlots);
                Assert.Equal(0, lease.BorrowedSlots);
                Assert.Equal(7, lease.GrantedSlots);
                Assert.Equal(1, lease.Epoch);
                Assert.Equal(harness.Now.Add(harness.LeaseTtl), lease.LeaseUntil);
                Assert.Equal(harness.Now, lease.UpdatedAt);
            });
    }

    [Fact]
    public async Task AllocateOnce_IgnoresStaleHeartbeats()
    {
        await using var harness = await SqliteAllocatorHarness.CreateAsync(CreateConfigManager((ProviderType.Pooled, 10)));
        await harness.SeedHeartbeatAsync(
            "stream-stale",
            providerIndex: 0,
            NodeRole.Streaming,
            hasDemand: true,
            heartbeatAt: harness.Now.Subtract(harness.HeartbeatTtl).AddSeconds(-1));
        await harness.SeedHeartbeatAsync("ingest-fresh", providerIndex: 0, NodeRole.Ingest, hasDemand: true, heartbeatAt: harness.Now);
        var allocator = harness.CreateAllocator(isLeader: true);

        await allocator.AllocateOnce(CancellationToken.None);

        var leases = await harness.ReadLeasesAsync();
        Assert.Collection(
            leases,
            lease =>
            {
                Assert.Equal("ingest-fresh", lease.NodeId);
                Assert.Equal(NodeRole.Ingest, lease.Role);
                Assert.Equal(10, lease.GrantedSlots);
                Assert.Equal(1, lease.Epoch);
            });
    }

    [Fact]
    public async Task AllocateOnce_WhenNotLeader_DoesNothing()
    {
        await using var harness = await SqliteAllocatorHarness.CreateAsync(CreateConfigManager((ProviderType.Pooled, 10)));
        await harness.SeedHeartbeatAsync("stream-1", providerIndex: 0, NodeRole.Streaming, hasDemand: true, heartbeatAt: harness.Now);
        var allocator = harness.CreateAllocator(isLeader: false);

        await allocator.AllocateOnce(CancellationToken.None);

        Assert.Empty(await harness.ReadLeasesAsync());
    }

    [Fact]
    public async Task AllocateOnce_IncrementsEpochAcrossAllocationPasses()
    {
        await using var harness = await SqliteAllocatorHarness.CreateAsync(CreateConfigManager((ProviderType.Pooled, 10)));
        await harness.SeedHeartbeatAsync("stream-1", providerIndex: 0, NodeRole.Streaming, hasDemand: true, heartbeatAt: harness.Now);
        var allocator = harness.CreateAllocator(isLeader: true);

        await allocator.AllocateOnce(CancellationToken.None);
        harness.Advance(TimeSpan.FromSeconds(5));
        await harness.SeedHeartbeatAsync("stream-1", providerIndex: 0, NodeRole.Streaming, hasDemand: true, heartbeatAt: harness.Now);
        await allocator.AllocateOnce(CancellationToken.None);

        var lease = Assert.Single(await harness.ReadLeasesAsync());
        Assert.Equal(2, lease.Epoch);
        Assert.Equal(harness.Now, lease.UpdatedAt);
        Assert.Equal(harness.Now.Add(harness.LeaseTtl), lease.LeaseUntil);
    }

    [Fact]
    public async Task AllocateOnce_KeepsEpochMonotonicAcrossActiveDrainAndReactivation()
    {
        await using var harness = await SqliteAllocatorHarness.CreateAsync(CreateConfigManager((ProviderType.Pooled, 10)));
        await harness.SeedHeartbeatAsync("stream-1", providerIndex: 0, NodeRole.Streaming, hasDemand: true, heartbeatAt: harness.Now);
        var allocator = harness.CreateAllocator(isLeader: true);

        await allocator.AllocateOnce(CancellationToken.None);
        Assert.Equal(1, Assert.Single(await harness.ReadLeasesAsync()).Epoch);
        Assert.Equal(1, Assert.Single(await harness.ReadEpochsAsync()).Epoch);

        harness.Advance(TimeSpan.FromSeconds(5));
        await harness.SeedHeartbeatAsync(
            "stream-1",
            providerIndex: 0,
            NodeRole.Streaming,
            hasDemand: true,
            heartbeatAt: harness.Now.Subtract(harness.HeartbeatTtl).AddSeconds(-1));
        await allocator.AllocateOnce(CancellationToken.None);
        Assert.Empty(await harness.ReadLeasesAsync());

        harness.Advance(TimeSpan.FromSeconds(5));
        await harness.SeedHeartbeatAsync("stream-1", providerIndex: 0, NodeRole.Streaming, hasDemand: true, heartbeatAt: harness.Now);
        await allocator.AllocateOnce(CancellationToken.None);

        var reactivatedLease = Assert.Single(await harness.ReadLeasesAsync());
        Assert.Equal(2, reactivatedLease.Epoch);
        Assert.Equal(2, Assert.Single(await harness.ReadEpochsAsync()).Epoch);
    }

    [Fact]
    public async Task AllocateOnce_KeepsEpochMonotonicAcrossAllocatorRestartAfterDrain()
    {
        await using var harness = await SqliteAllocatorHarness.CreateAsync(CreateConfigManager((ProviderType.Pooled, 10)));
        await harness.SeedHeartbeatAsync("stream-1", providerIndex: 0, NodeRole.Streaming, hasDemand: true, heartbeatAt: harness.Now);
        var firstAllocator = harness.CreateAllocator(isLeader: true);

        await firstAllocator.AllocateOnce(CancellationToken.None);
        Assert.Equal(1, Assert.Single(await harness.ReadLeasesAsync()).Epoch);

        harness.Advance(TimeSpan.FromSeconds(5));
        await harness.SeedHeartbeatAsync(
            "stream-1",
            providerIndex: 0,
            NodeRole.Streaming,
            hasDemand: true,
            heartbeatAt: harness.Now.Subtract(harness.HeartbeatTtl).AddSeconds(-1));
        await firstAllocator.AllocateOnce(CancellationToken.None);
        Assert.Empty(await harness.ReadLeasesAsync());

        var secondAllocator = harness.CreateAllocator(isLeader: true);
        harness.Advance(TimeSpan.FromSeconds(5));
        await harness.SeedHeartbeatAsync("stream-1", providerIndex: 0, NodeRole.Streaming, hasDemand: true, heartbeatAt: harness.Now);
        await secondAllocator.AllocateOnce(CancellationToken.None);

        var reactivatedLease = Assert.Single(await harness.ReadLeasesAsync());
        Assert.Equal(2, reactivatedLease.Epoch);
    }

    [Fact]
    public async Task AllocateOnce_WritesDeterministicLeasesPerProvider()
    {
        await using var harness = await SqliteAllocatorHarness.CreateAsync(CreateConfigManager(
            (ProviderType.Pooled, 6),
            (ProviderType.BackupOnly, 99),
            (ProviderType.Pooled, 7)));
        await harness.SeedHeartbeatAsync("stream-b", providerIndex: 2, NodeRole.Streaming, hasDemand: true, heartbeatAt: harness.Now);
        await harness.SeedHeartbeatAsync("stream-a", providerIndex: 2, NodeRole.Streaming, hasDemand: true, heartbeatAt: harness.Now);
        await harness.SeedHeartbeatAsync("ingest-a", providerIndex: 2, NodeRole.Ingest, hasDemand: true, heartbeatAt: harness.Now);
        await harness.SeedHeartbeatAsync("node-b", providerIndex: 0, NodeRole.Streaming, hasDemand: true, heartbeatAt: harness.Now);
        await harness.SeedHeartbeatAsync("node-a", providerIndex: 0, NodeRole.Streaming, hasDemand: true, heartbeatAt: harness.Now);
        var allocator = harness.CreateAllocator(isLeader: true);

        await allocator.AllocateOnce(CancellationToken.None);

        var leases = await harness.ReadLeasesAsync();
        Assert.Equal(5, leases.Count);
        Assert.Equal(3, leases.Single(x => x.ProviderIndex == 0 && x.NodeId == "node-a").GrantedSlots);
        Assert.Equal(3, leases.Single(x => x.ProviderIndex == 0 && x.NodeId == "node-b").GrantedSlots);
        Assert.Equal(2, leases.Single(x => x.ProviderIndex == 2 && x.NodeId == "ingest-a").GrantedSlots);
        Assert.Equal(3, leases.Single(x => x.ProviderIndex == 2 && x.NodeId == "stream-a").GrantedSlots);
        Assert.Equal(2, leases.Single(x => x.ProviderIndex == 2 && x.NodeId == "stream-b").GrantedSlots);
        Assert.DoesNotContain(leases, x => x.ProviderIndex == 1);
    }

    [Fact]
    public async Task AllocateOnce_DeletesLeasesForProvidersThatAreNoLongerPooled()
    {
        await using var harness = await SqliteAllocatorHarness.CreateAsync(CreateConfigManager(
            (ProviderType.Pooled, 4),
            (ProviderType.Pooled, 6)));
        await harness.SeedHeartbeatAsync("node-a", providerIndex: 0, NodeRole.Streaming, hasDemand: true, heartbeatAt: harness.Now);
        await harness.SeedHeartbeatAsync("node-b", providerIndex: 1, NodeRole.Streaming, hasDemand: true, heartbeatAt: harness.Now);
        var allocator = harness.CreateAllocator(isLeader: true);

        await allocator.AllocateOnce(CancellationToken.None);
        Assert.Equal(2, (await harness.ReadLeasesAsync()).Count);

        harness.UpdateProviders((ProviderType.BackupOnly, 4), (ProviderType.Pooled, 6));
        await allocator.AllocateOnce(CancellationToken.None);

        var leases = await harness.ReadLeasesAsync();
        Assert.Single(leases);
        Assert.Equal(1, leases[0].ProviderIndex);
        Assert.Equal("node-b", leases[0].NodeId);
    }

    private static ConfigManager CreateConfigManager(params (ProviderType Type, int MaxConnections)[] providers)
    {
        var configManager = new ConfigManager();
        UpdateProviders(configManager, providers);
        return configManager;
    }

    private static void UpdateProviders(ConfigManager configManager, params (ProviderType Type, int MaxConnections)[] providers)
    {
        var providerConfig = new UsenetProviderConfig
        {
            Providers = providers
                .Select((provider, index) => new UsenetProviderConfig.ConnectionDetails
                {
                    Type = provider.Type,
                    Host = $"news-{index}.example.test",
                    Port = 563,
                    UseSsl = true,
                    User = "user",
                    Pass = "pass",
                    MaxConnections = provider.MaxConnections
                })
                .ToList()
        };

        configManager.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = "usenet.providers",
                ConfigValue = JsonSerializer.Serialize(providerConfig)
            }
        ]);
    }

    private sealed class SqliteAllocatorHarness : IAsyncDisposable
    {
        private readonly string _configPath;
        private readonly backend.Tests.Config.TemporaryEnvironment _environment;
        private DateTime _now;

        private SqliteAllocatorHarness(string configPath, backend.Tests.Config.TemporaryEnvironment environment, ConfigManager configManager, DateTime now)
        {
            _configPath = configPath;
            _environment = environment;
            ConfigManager = configManager;
            _now = now;
        }

        public ConfigManager ConfigManager { get; }
        public TimeSpan HeartbeatTtl { get; } = TimeSpan.FromSeconds(30);
        public TimeSpan LeaseTtl { get; } = TimeSpan.FromSeconds(30);
        public DateTime Now => _now;

        public static async Task<SqliteAllocatorHarness> CreateAsync(ConfigManager configManager)
        {
            var configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"nntp-allocator-{Guid.NewGuid():N}");
            Directory.CreateDirectory(configPath);

            var environment = new backend.Tests.Config.TemporaryEnvironment(
                ("DATABASE_URL", null),
                ("CONFIG_PATH", configPath));

            await using var dbContext = new DavDatabaseContext();
            await dbContext.Database.MigrateAsync();

            return new SqliteAllocatorHarness(
                configPath,
                environment,
                configManager,
                new DateTime(2026, 4, 14, 12, 0, 0, DateTimeKind.Utc));
        }

        public NntpLeaseAllocator CreateAllocator(bool isLeader)
        {
            return new NntpLeaseAllocator(
                ConfigManager,
                () => new DavDatabaseContext(),
                (_, _) => ValueTask.FromResult(isLeader),
                heartbeatTtl: HeartbeatTtl,
                leaseTtl: LeaseTtl,
                tickInterval: TimeSpan.FromMinutes(1),
                utcNow: () => _now);
        }

        public void Advance(TimeSpan amount)
        {
            _now = _now.Add(amount);
        }

        public void UpdateProviders(params (ProviderType Type, int MaxConnections)[] providers)
        {
            NntpLeaseAllocatorTests.UpdateProviders(ConfigManager, providers);
        }

        public async Task SeedHeartbeatAsync(
            string nodeId,
            int providerIndex,
            NodeRole role,
            bool hasDemand,
            DateTime heartbeatAt)
        {
            await using var dbContext = new DavDatabaseContext();
            var existing = await dbContext.NntpNodeHeartbeats.SingleOrDefaultAsync(x => x.NodeId == nodeId && x.ProviderIndex == providerIndex);
            if (existing == null)
            {
                dbContext.NntpNodeHeartbeats.Add(new NntpNodeHeartbeat
                {
                    NodeId = nodeId,
                    ProviderIndex = providerIndex,
                    Role = role,
                    Region = "test-region",
                    DesiredSlots = 0,
                    ActiveSlots = 0,
                    LiveSlots = 0,
                    HasDemand = hasDemand,
                    HeartbeatAt = heartbeatAt
                });
            }
            else
            {
                existing.Role = role;
                existing.HasDemand = hasDemand;
                existing.HeartbeatAt = heartbeatAt;
            }

            await dbContext.SaveChangesAsync();
        }

        public async Task<List<NntpConnectionLease>> ReadLeasesAsync()
        {
            await using var dbContext = new DavDatabaseContext();
            return (await dbContext.NntpConnectionLeases
                .AsNoTracking()
                .ToListAsync())
                .OrderBy(x => x.ProviderIndex)
                .ThenBy(x => x.NodeId, StringComparer.Ordinal)
                .ToList();
        }

        public async Task<List<NntpLeaseEpoch>> ReadEpochsAsync()
        {
            await using var dbContext = new DavDatabaseContext();
            return await dbContext.Set<NntpLeaseEpoch>()
                .AsNoTracking()
                .OrderBy(x => x.ProviderIndex)
                .ToListAsync();
        }

        public async ValueTask DisposeAsync()
        {
            _environment.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            try
            {
                File.Delete(DavDatabaseContext.DatabaseFilePath);
                File.Delete(DavDatabaseContext.DatabaseFilePath + "-wal");
                File.Delete(DavDatabaseContext.DatabaseFilePath + "-shm");
            }
            catch
            {
                // best effort test cleanup
            }

            if (Directory.Exists(_configPath))
                Directory.Delete(_configPath, recursive: true);

            await ValueTask.CompletedTask;
        }
    }
}
