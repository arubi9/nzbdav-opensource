using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Caching;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Services.NntpLeasing;
using NzbWebDAV.Websocket;
using UsenetSharp.Models;

namespace backend.Tests.Services;

[Collection(nameof(backend.Tests.Config.EnvironmentVariableCollection))]
public sealed class HealthCheckServiceLeaseTests
{
    [Fact]
    public async Task ExecuteAsync_DefersWhenPerNodeLeasingHasZeroFreshGrants()
    {
        await using var harness = await SqliteHealthCheckHarness.CreateAsync();
        var configManager = CreateRepairEnabledConfig(totalPooledConnections: 12, maxDownloadConnections: 2);
        await harness.SeedHealthCheckFileAsync("segment-1@example.test");
        await using var cacheScope = new TempCacheScope();
        using var liveCache = new LiveSegmentCache(cacheScope.Path);
        using var usenetClient = new RecordingUsenetStreamingClient(configManager, new WebsocketManager(), liveCache);
        var service = new TestHealthCheckService(
            configManager,
            usenetClient,
            new WebsocketManager(),
            new NntpLeaseState());
        SetPrivateField(service, "_usePerNodeLeasing", true);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunAsync(cts.Token));

        Assert.Equal(0, usenetClient.CheckAllSegmentsCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_UsesLegacyPooledConcurrencyWhenPerNodeLeasingIsDisabled()
    {
        await using var harness = await SqliteHealthCheckHarness.CreateAsync();
        var configManager = CreateRepairEnabledConfig(totalPooledConnections: 12, maxDownloadConnections: 2);
        await harness.SeedHealthCheckFileAsync("segment-1@example.test");
        await using var cacheScope = new TempCacheScope();
        using var liveCache = new LiveSegmentCache(cacheScope.Path);
        using var usenetClient = new RecordingUsenetStreamingClient(configManager, new WebsocketManager(), liveCache);
        var service = new TestHealthCheckService(
            configManager,
            usenetClient,
            new WebsocketManager(),
            new NntpLeaseState());
        SetPrivateField(service, "_usePerNodeLeasing", false);
        usenetClient.OnCheckAllSegments = _ => { };

        using var cts = new CancellationTokenSource();
        usenetClient.OnCheckAllSegments = _ => cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunAsync(cts.Token));

        Assert.Equal(1, usenetClient.CheckAllSegmentsCallCount);
        Assert.Equal(12, usenetClient.LastConcurrency);
    }

    [Fact]
    public async Task ConfigChange_UsenetProviders_ClearsMissingSegmentIds()
    {
        await using var harness = await SqliteHealthCheckHarness.CreateAsync();
        await harness.SeedMissingSegmentIdAsync("segment-a@example.test");

        var configManager = CreateRepairEnabledConfig(totalPooledConnections: 12, maxDownloadConnections: 2);
        await using var cacheScope = new TempCacheScope();
        using var liveCache = new LiveSegmentCache(cacheScope.Path);
        using var usenetClient = new RecordingUsenetStreamingClient(configManager, new WebsocketManager(), liveCache);
        _ = new HealthCheckService(configManager, usenetClient, new WebsocketManager(), new NntpLeaseState());

        configManager.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = "usenet.providers",
                ConfigValue = JsonSerializer.Serialize(new UsenetProviderConfig())
            }
        ]);

        await harness.AssertMissingSegmentIdsClearedAsync();
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        var field = target.GetType().BaseType?.GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Could not find field {fieldName}.");

        field.SetValue(target, value);
    }

    private static ConfigManager CreateRepairEnabledConfig(int totalPooledConnections, int maxDownloadConnections)
    {
        var configManager = new ConfigManager();
        var providerConfig = new UsenetProviderConfig
        {
            Providers =
            [
                new UsenetProviderConfig.ConnectionDetails
                {
                    Type = ProviderType.Pooled,
                    Host = "news.example.test",
                    Port = 563,
                    UseSsl = true,
                    User = "user",
                    Pass = "pass",
                    MaxConnections = totalPooledConnections
                }
            ]
        };
        var arrConfig = new ArrConfig
        {
            RadarrInstances =
            [
                new ArrConfig.ConnectionDetails
                {
                    Host = "http://radarr.example.test",
                    ApiKey = "api-key"
                }
            ]
        };

        configManager.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = "usenet.providers",
                ConfigValue = JsonSerializer.Serialize(providerConfig)
            },
            new ConfigItem
            {
                ConfigName = "usenet.max-download-connections",
                ConfigValue = maxDownloadConnections.ToString()
            },
            new ConfigItem
            {
                ConfigName = "repair.enable",
                ConfigValue = "true"
            },
            new ConfigItem
            {
                ConfigName = "media.library-dir",
                ConfigValue = "/library"
            },
            new ConfigItem
            {
                ConfigName = "arr.instances",
                ConfigValue = JsonSerializer.Serialize(arrConfig)
            }
        ]);

        return configManager;
    }

    private sealed class TestHealthCheckService : HealthCheckService
    {
        public TestHealthCheckService(
            ConfigManager configManager,
            UsenetStreamingClient usenetClient,
            WebsocketManager websocketManager,
            NntpLeaseState leaseState)
            : base(configManager, usenetClient, websocketManager, leaseState)
        {
        }

        public Task RunAsync(CancellationToken cancellationToken) => base.ExecuteAsync(cancellationToken);
    }

    private sealed class RecordingUsenetStreamingClient : UsenetStreamingClient
    {
        private int _checkAllSegmentsCallCount;

        public RecordingUsenetStreamingClient(
            ConfigManager configManager,
            WebsocketManager websocketManager,
            LiveSegmentCache liveSegmentCache)
            : base(configManager, websocketManager, liveSegmentCache)
        {
        }

        public int CheckAllSegmentsCallCount => Volatile.Read(ref _checkAllSegmentsCallCount);
        public int? LastConcurrency { get; private set; }
        public Action<int>? OnCheckAllSegments { get; set; }

        public override Task CheckAllSegmentsAsync(
            IEnumerable<string> segmentIds,
            int concurrency,
            IProgress<int>? progress,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _checkAllSegmentsCallCount);
            LastConcurrency = concurrency;
            OnCheckAllSegments?.Invoke(concurrency);
            return Task.CompletedTask;
        }
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

        public static async Task<SqliteHealthCheckHarness> CreateAsync()
        {
            var configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"health-{Guid.NewGuid():N}");
            Directory.CreateDirectory(configPath);
            var environment = new backend.Tests.Config.TemporaryEnvironment(
                ("DATABASE_URL", null),
                ("CONFIG_PATH", configPath));

            await using var dbContext = new DavDatabaseContext();
            await dbContext.Database.MigrateAsync();

            return new SqliteHealthCheckHarness(configPath, environment);
        }

        public async Task SeedHealthCheckFileAsync(string segmentId)
        {
            await using var dbContext = new DavDatabaseContext();
            var id = Guid.NewGuid();
            dbContext.Items.Add(new DavItem
            {
                Id = id,
                IdPrefix = id.ToString("N")[..DavItem.IdPrefixLength],
                CreatedAt = DateTime.UtcNow,
                ParentId = DavItem.ContentFolder.Id,
                Name = "movie.mkv",
                FileSize = 123,
                Type = DavItem.ItemType.NzbFile,
                Path = "/content/movies/movie.mkv",
                ReleaseDate = DateTimeOffset.UtcNow.AddDays(-10),
                LastHealthCheck = null,
                NextHealthCheck = DateTimeOffset.UtcNow.AddMinutes(-1)
            });
            dbContext.NzbFiles.Add(new DavNzbFile
            {
                Id = id,
                SegmentIds = [segmentId]
            });
            await dbContext.SaveChangesAsync();
        }

        public async Task SeedMissingSegmentIdAsync(string segmentId)
        {
            await using var dbContext = new DavDatabaseContext();
            dbContext.MissingSegmentIds.Add(new MissingSegmentId
            {
                SegmentId = segmentId,
                DetectedAt = DateTimeOffset.UtcNow
            });
            await dbContext.SaveChangesAsync();
        }

        public async Task AssertMissingSegmentIdsClearedAsync()
        {
            var timeoutAt = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < timeoutAt)
            {
                await using var dbContext = new DavDatabaseContext();
                var count = await dbContext.MissingSegmentIds.CountAsync();
                if (count == 0)
                    return;

                await Task.Delay(50);
            }

            await using var verifyContext = new DavDatabaseContext();
            Assert.Equal(0, await verifyContext.MissingSegmentIds.CountAsync());
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
            if (Directory.Exists(Path))
            {
                try
                {
                    Directory.Delete(Path, recursive: true);
                }
                catch
                {
                    // best effort
                }
            }

            return ValueTask.CompletedTask;
        }
    }
}
