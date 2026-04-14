using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Caching;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Services.NntpLeasing;
using NzbWebDAV.Websocket;

namespace backend.Tests.Services;

[Collection(nameof(backend.Tests.Config.EnvironmentVariableCollection))]
public sealed class NzbdavHealthCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_IncludesCurrentRoleAndLocalLeaseDetails()
    {
        await using var harness = await SqliteHealthCheckHarness.CreateAsync();
        await using var cacheScope = new TempCacheScope();
        using var liveCache = new LiveSegmentCache(cacheScope.Path);
        using var usenetClient = new UsenetStreamingClient(CreateConfigManager(), new WebsocketManager(), liveCache);
        var leaseState = new NntpLeaseState();
        leaseState.Apply(providerIndex: 0, grantedSlots: 4, epoch: 9, leaseUntil: harness.Now.AddSeconds(30), reservedSlots: 3, borrowedSlots: 1);

        var healthCheck = new NzbdavHealthCheck(
            liveCache,
            usenetClient,
            leaseState,
            new TestApplicationLifetime(),
            () => harness.Now,
            NodeRole.Streaming,
            isMultiNode: true);

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("Streaming", Assert.IsType<string>(result.Data["node_role"]));
        Assert.Equal("per_node", Assert.IsType<string>(result.Data["nntp_leasing_mode"]));
        Assert.Equal(4, Assert.IsType<int>(result.Data["nntp_local_lease_total_granted_slots"]));
        Assert.Equal(3, Assert.IsType<int>(result.Data["nntp_local_lease_total_reserved_slots"]));
        Assert.Equal(1, Assert.IsType<int>(result.Data["nntp_local_lease_total_borrowed_slots"]));

        var leases = Assert.IsType<NntpLeaseState.ProviderLeaseObservation[]>(result.Data["nntp_local_leases"]);
        var lease = Assert.Single(leases);
        Assert.Equal(0, lease.ProviderIndex);
        Assert.Equal(4, lease.GrantedSlots);
        Assert.Equal(3, lease.ReservedSlots);
        Assert.Equal(1, lease.BorrowedSlots);
        Assert.Equal(9, lease.Epoch);
        Assert.True(lease.IsFresh);
        Assert.Equal(30, lease.SecondsUntilExpiry);
    }

    private static ConfigManager CreateConfigManager()
    {
        var configManager = new ConfigManager();
        configManager.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = "usenet.providers",
                ConfigValue = """
                              {
                                "providers": [
                                  {
                                    "type": "Pooled",
                                    "host": "news.example.test",
                                    "port": 563,
                                    "useSsl": true,
                                    "user": "user",
                                    "pass": "pass",
                                    "maxConnections": 10
                                  }
                                ]
                              }
                              """
            }
        ]);
        return configManager;
    }

    private sealed class TestApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private sealed class SqliteHealthCheckHarness : IAsyncDisposable
    {
        private readonly string _configPath;
        private readonly backend.Tests.Config.TemporaryEnvironment _environment;

        private SqliteHealthCheckHarness(string configPath, backend.Tests.Config.TemporaryEnvironment environment)
        {
            _configPath = configPath;
            _environment = environment;
        }

        public DateTime Now { get; } = new(2026, 4, 14, 12, 0, 0, DateTimeKind.Utc);

        public static async Task<SqliteHealthCheckHarness> CreateAsync()
        {
            var configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"health-result-{Guid.NewGuid():N}");
            Directory.CreateDirectory(configPath);
            var environment = new backend.Tests.Config.TemporaryEnvironment(
                ("DATABASE_URL", null),
                ("CONFIG_PATH", configPath));

            await using var dbContext = new DavDatabaseContext();
            await dbContext.Database.MigrateAsync();

            return new SqliteHealthCheckHarness(configPath, environment);
        }

        public ValueTask DisposeAsync()
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
                // best effort
            }

            if (Directory.Exists(_configPath))
                Directory.Delete(_configPath, recursive: true);

            return ValueTask.CompletedTask;
        }
    }

    private sealed class TempCacheScope : IAsyncDisposable
    {
        public TempCacheScope()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public ValueTask DisposeAsync()
        {
            if (!Directory.Exists(Path))
                return ValueTask.CompletedTask;

            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // best effort
            }

            return ValueTask.CompletedTask;
        }
    }
}
