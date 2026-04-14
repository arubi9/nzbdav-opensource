using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Services.NntpLeasing;
using NzbWebDAV.Tests.TestDoubles;
using UsenetSharp.Models;

namespace backend.Tests.Services.NntpLeasing;

[Collection(nameof(backend.Tests.Config.EnvironmentVariableCollection))]
public sealed class NntpLeaseAgentTests
{
    [Fact]
    public void Apply_TracksProviderGrantEpochFreshnessAndTotal()
    {
        var leaseState = new NntpLeaseState();
        var leaseUntil = new DateTime(2026, 4, 14, 12, 0, 30, DateTimeKind.Utc);

        leaseState.Apply(providerIndex: 0, grantedSlots: 4, epoch: 7, leaseUntil);
        leaseState.Apply(providerIndex: 1, grantedSlots: 2, epoch: 3, leaseUntil.AddSeconds(10));

        Assert.Equal(4, leaseState.GetProviderGrant(0));
        Assert.Equal(2, leaseState.GetProviderGrant(1));
        Assert.Equal(6, leaseState.GetTotalGrantedSlots());
        Assert.Equal(7, leaseState.GetProviderEpoch(0));
        Assert.True(leaseState.IsLeaseFresh(0, leaseUntil.AddSeconds(-1)));

        leaseState.Apply(providerIndex: 0, grantedSlots: 1, epoch: 8, leaseUntil.AddSeconds(20));

        Assert.Equal(1, leaseState.GetProviderGrant(0));
        Assert.Equal(3, leaseState.GetTotalGrantedSlots());
        Assert.Equal(8, leaseState.GetProviderEpoch(0));
        Assert.False(leaseState.IsLeaseFresh(1, leaseUntil.AddSeconds(11)));
    }

    [Fact]
    public async Task RunOnce_RoundTripsLeaseIntoLocalStateAndAppliesLimits()
    {
        await using var harness = await SqliteLeaseAgentHarness.CreateAsync(CreateConfigManager(
            (ProviderType.Pooled, 10),
            (ProviderType.Pooled, 8)));
        await harness.SeedLeaseAsync("stream-node", providerIndex: 0, NodeRole.Streaming, grantedSlots: 4, epoch: 9);
        await harness.SeedLeaseAsync("stream-node", providerIndex: 1, NodeRole.Streaming, grantedSlots: 2, epoch: 5);

        var leaseState = new NntpLeaseState();
        var appliedProviderLimits = new Dictionary<int, int>();
        var appliedDownloadLimits = new List<int>();
        var agent = new NntpLeaseAgent(
            harness.ConfigManager,
            leaseState,
            (providerIndex, grantedSlots) => appliedProviderLimits[providerIndex] = grantedSlots,
            grantedSlots => appliedDownloadLimits.Add(grantedSlots),
            () => new DavDatabaseContext(),
            nodeIdFactory: () => "stream-node",
            nodeRole: NodeRole.Streaming,
            region: "test-region",
            tickInterval: TimeSpan.FromMinutes(1),
            utcNow: () => harness.Now);

        await agent.RunOnce(CancellationToken.None);

        var heartbeats = await harness.ReadHeartbeatsAsync();
        Assert.Equal(2, heartbeats.Count);
        Assert.All(heartbeats, heartbeat =>
        {
            Assert.Equal("stream-node", heartbeat.NodeId);
            Assert.Equal(NodeRole.Streaming, heartbeat.Role);
            Assert.Equal("test-region", heartbeat.Region);
            Assert.True(heartbeat.HasDemand);
            Assert.Equal(harness.Now, heartbeat.HeartbeatAt);
        });

        Assert.Equal(4, leaseState.GetProviderGrant(0));
        Assert.Equal(2, leaseState.GetProviderGrant(1));
        Assert.Equal(6, leaseState.GetTotalGrantedSlots());
        Assert.Equal(9, leaseState.GetProviderEpoch(0));
        Assert.Equal(5, leaseState.GetProviderEpoch(1));
        Assert.Equal(4, appliedProviderLimits[0]);
        Assert.Equal(2, appliedProviderLimits[1]);
        Assert.Equal([6], appliedDownloadLimits);
    }

    [Fact]
    public async Task UpdateMaxDownloadConnections_AllowsAdditionalWaitersWithoutConfigReload()
    {
        var configManager = new ConfigManager();
        configManager.UpdateValues(
        [
            new ConfigItem { ConfigName = "usenet.max-download-connections", ConfigValue = "1" },
            new ConfigItem { ConfigName = "usenet.streaming-priority", ConfigValue = "100" }
        ]);

        using var client = new DownloadingNntpClient(new FakeNntpClient(), configManager);
        var holder = await client.AcquireExclusiveConnectionAsync("holder", CancellationToken.None);
        var waiter = client.AcquireExclusiveConnectionAsync("waiter", CancellationToken.None);

        Assert.False(waiter.IsCompleted);

        client.UpdateMaxDownloadConnections(2);

        var acquired = await waiter.WaitAsync(TimeSpan.FromSeconds(2));
        acquired.OnConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
        holder.OnConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
    }

    [Theory]
    [InlineData(false, NodeRole.Streaming, false)]
    [InlineData(true, NodeRole.Combined, false)]
    [InlineData(true, NodeRole.Streaming, true)]
    [InlineData(true, NodeRole.Ingest, true)]
    public void ShouldUsePerNodeLeasing_OnlyEnablesExplicitRoleMultiNodeNodes(
        bool isMultiNode,
        NodeRole nodeRole,
        bool expected)
    {
        Assert.Equal(expected, NntpLeaseAgent.ShouldUsePerNodeLeasing(isMultiNode, nodeRole));
    }

    private static ConfigManager CreateConfigManager(params (ProviderType Type, int MaxConnections)[] providers)
    {
        var configManager = new ConfigManager();
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
        return configManager;
    }

    private sealed class SqliteLeaseAgentHarness : IAsyncDisposable
    {
        private readonly string _configPath;
        private readonly backend.Tests.Config.TemporaryEnvironment _environment;

        private SqliteLeaseAgentHarness(string configPath, backend.Tests.Config.TemporaryEnvironment environment, ConfigManager configManager)
        {
            _configPath = configPath;
            _environment = environment;
            ConfigManager = configManager;
        }

        public ConfigManager ConfigManager { get; }
        public DateTime Now { get; } = new(2026, 4, 14, 12, 0, 0, DateTimeKind.Utc);

        public static async Task<SqliteLeaseAgentHarness> CreateAsync(ConfigManager configManager)
        {
            var configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"nntp-agent-{Guid.NewGuid():N}");
            Directory.CreateDirectory(configPath);

            var environment = new backend.Tests.Config.TemporaryEnvironment(
                ("DATABASE_URL", null),
                ("CONFIG_PATH", configPath));

            await using var dbContext = new DavDatabaseContext();
            await dbContext.Database.MigrateAsync();

            return new SqliteLeaseAgentHarness(configPath, environment, configManager);
        }

        public async Task SeedLeaseAsync(string nodeId, int providerIndex, NodeRole role, int grantedSlots, long epoch)
        {
            await using var dbContext = new DavDatabaseContext();
            dbContext.NntpConnectionLeases.Add(new NntpConnectionLease
            {
                NodeId = nodeId,
                ProviderIndex = providerIndex,
                Role = role,
                ReservedSlots = grantedSlots,
                BorrowedSlots = 0,
                GrantedSlots = grantedSlots,
                Epoch = epoch,
                LeaseUntil = Now.AddSeconds(30),
                UpdatedAt = Now
            });
            await dbContext.SaveChangesAsync();
        }

        public async Task<List<NntpNodeHeartbeat>> ReadHeartbeatsAsync()
        {
            await using var dbContext = new DavDatabaseContext();
            return await dbContext.NntpNodeHeartbeats
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
