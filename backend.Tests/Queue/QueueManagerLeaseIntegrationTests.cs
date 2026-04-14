using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Caching;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Queue;
using NzbWebDAV.Models;
using NzbWebDAV.Services.NntpLeasing;
using NzbWebDAV.Websocket;

namespace backend.Tests.Queue;

[Collection(nameof(backend.Tests.Config.EnvironmentVariableCollection))]
public sealed class QueueManagerLeaseIntegrationTests
{
    [Fact]
    public async Task BeginProcessingQueueItem_RequeuesQueueItem_WhenPerNodeLeasingHasZeroFreshGrants()
    {
        await using var harness = await SqliteQueueHarness.CreateAsync();
        var configManager = CreateConfigManager(maxDownloadConnections: 2, pooledConnections: 12);
        var websocketManager = new WebsocketManager();
        var leaseState = new NntpLeaseState();
        await using var cacheScope = new TempCacheScope();
        using var liveCache = new LiveSegmentCache(cacheScope.Path);
        using var streamingClient = new UsenetStreamingClient(configManager, websocketManager, liveCache);
        using var queueManager = new QueueManager(streamingClient, configManager, websocketManager, leaseState);

        CancelBackgroundLoop(queueManager);
        SetPrivateField(queueManager, "_usePerNodeLeasing", true);

        var queueItemId = Guid.NewGuid();
        await using (var seedContext = new DavDatabaseContext())
        {
            seedContext.QueueItems.Add(new QueueItem
            {
                Id = queueItemId,
                CreatedAt = DateTime.UtcNow,
                FileName = "release.nzb",
                JobName = "release",
                NzbFileSize = 123,
                TotalSegmentBytes = 123,
                Category = "movies",
                Priority = QueueItem.PriorityOption.Normal,
                PostProcessing = QueueItem.PostProcessingOption.Default,
                PauseUntil = null
            });
            await seedContext.SaveChangesAsync();
        }

        await using (var processingContext = new DavDatabaseContext())
        {
            var dbClient = new DavDatabaseClient(processingContext);
            var queueItem = await processingContext.QueueItems.SingleAsync(x => x.Id == queueItemId);
            using var cachingClient = new ArticleCachingNntpClient(streamingClient);
            await using var nzbStream = new MemoryStream(Encoding.UTF8.GetBytes(MinimalNzb));
            using var cts = new CancellationTokenSource();

            var inProgress = InvokeBeginProcessingQueueItem(
                queueManager,
                dbClient,
                cachingClient,
                queueItem,
                nzbStream,
                cts);
            var processingTask = (Task<bool>)GetPropertyValue(inProgress, "ProcessingTask");
            var processedSuccessfully = await processingTask;

            Assert.False(processedSuccessfully);
        }

        await using var assertContext = new DavDatabaseContext();
        var pausedQueueItem = await assertContext.QueueItems.SingleAsync(x => x.Id == queueItemId);
        var historyItem = await assertContext.HistoryItems.SingleOrDefaultAsync(x => x.Id == queueItemId);

        Assert.NotNull(pausedQueueItem.PauseUntil);
        Assert.True(pausedQueueItem.PauseUntil > DateTime.Now.AddSeconds(30));
        Assert.Null(historyItem);
    }

    private static object InvokeBeginProcessingQueueItem(
        QueueManager queueManager,
        DavDatabaseClient dbClient,
        INntpClient usenetClient,
        QueueItem queueItem,
        Stream queueNzbStream,
        CancellationTokenSource cts)
    {
        var method = typeof(QueueManager).GetMethod(
            "BeginProcessingQueueItem",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Could not find BeginProcessingQueueItem.");

        return method.Invoke(queueManager, [dbClient, usenetClient, queueItem, queueNzbStream, cts])
               ?? throw new InvalidOperationException("BeginProcessingQueueItem returned null.");
    }

    private static object GetPropertyValue(object target, string propertyName)
    {
        var property = target.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Could not find property {propertyName}.");

        return property.GetValue(target)
               ?? throw new InvalidOperationException($"Property {propertyName} was null.");
    }

    private static void CancelBackgroundLoop(QueueManager queueManager)
    {
        var field = typeof(QueueManager).GetField(
            "_cancellationTokenSource",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Could not find queue cancellation token source.");

        if (field.GetValue(queueManager) is CancellationTokenSource cts)
            cts.Cancel();
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Could not find field {fieldName}.");

        field.SetValue(target, value);
    }

    private static ConfigManager CreateConfigManager(int maxDownloadConnections, int pooledConnections)
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
                    MaxConnections = pooledConnections
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
            }
        ]);
        return configManager;
    }

    private const string MinimalNzb = """
                                      <?xml version="1.0" encoding="utf-8"?>
                                      <nzb>
                                        <file subject="release.mkv">
                                          <segments>
                                            <segment bytes="123" number="1">segment-1@example.test</segment>
                                          </segments>
                                        </file>
                                      </nzb>
                                      """;

    private sealed class SqliteQueueHarness : IAsyncDisposable
    {
        private readonly string _configPath;
        private readonly backend.Tests.Config.TemporaryEnvironment _environment;

        private SqliteQueueHarness(string configPath, backend.Tests.Config.TemporaryEnvironment environment)
        {
            _configPath = configPath;
            _environment = environment;
        }

        public static async Task<SqliteQueueHarness> CreateAsync()
        {
            var configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"queue-{Guid.NewGuid():N}");
            Directory.CreateDirectory(configPath);
            var environment = new backend.Tests.Config.TemporaryEnvironment(
                ("DATABASE_URL", null),
                ("CONFIG_PATH", configPath));

            await using var dbContext = new DavDatabaseContext();
            await dbContext.Database.MigrateAsync();

            return new SqliteQueueHarness(configPath, environment);
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
