using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Minio.ApiEndpoints;
using Minio.DataModel;
using Minio.DataModel.Args;
using Minio.DataModel.Response;
using Minio.Exceptions;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Caching;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Metrics;
using NzbWebDAV.Models;
using NzbWebDAV.Services;
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
            () => 10_000L,
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

    [Fact]
    public async Task CollectMetrics_ExportsL2StateWhenEnabled()
    {
        var registry = Prometheus.Metrics.NewCustomRegistry();
        var factory = Prometheus.Metrics.WithCustomRegistry(registry);
        using var l2 = new L2Harness(startBackgroundWriter: true);
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
            () => 10_000L,
            () => poolStats,
            () => 1,
            () => 7,
            () => 1,
            () => l2.Cache,
            registry,
            factory
        );

        l2.Seed("segment-hit", Encoding.ASCII.GetBytes("segment-hit"));
        var hitResponse = await l2.Cache.TryReadAsync("segment-hit", CancellationToken.None);
        Assert.NotNull(hitResponse);
        await hitResponse!.DisposeAsync();

        var missResponse = await l2.Cache.TryReadAsync("segment-miss", CancellationToken.None);
        Assert.Null(missResponse);

        l2.Cache.EnqueueWrite(
            "segment-write",
            Encoding.ASCII.GetBytes("segment-write"),
            SegmentCategory.SmallFile,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "segment-write.bin");
        await l2.WriteObserved.WaitAsync(TimeSpan.FromSeconds(5));

        await using var output = new MemoryStream();
        await registry.CollectAndExportAsTextAsync(output);
        var metricsText = Encoding.UTF8.GetString(output.ToArray());

        Assert.Contains("nzbdav_l2_cache_enabled 1", metricsText);
        Assert.Contains("nzbdav_l2_cache_hits_total 1", metricsText);
        Assert.Contains("nzbdav_l2_cache_misses_total 1", metricsText);
        Assert.Contains("nzbdav_l2_cache_writes_total 1", metricsText);
    }

    [Fact]
    public async Task CollectMetrics_ExportsSharedHeaderCacheCounters()
    {
        var registry = Prometheus.Metrics.NewCustomRegistry();
        var factory = Prometheus.Metrics.WithCustomRegistry(registry);
        var poolStats = CreatePoolStats(maxConnections: 5, live: 4, idle: 1);
        var sharedHeaderCache = CreateSharedHeaderCache(hits: 3, misses: 5, writeFailures: 2);

        var collector = CreateCollector(
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
            () => null,
            registry,
            factory,
            sharedHeaderCache
        );

        await using var output = new MemoryStream();
        await registry.CollectAndExportAsTextAsync(output);
        var metricsText = Encoding.UTF8.GetString(output.ToArray());

        Assert.Contains("nzbdav_shared_header_cache_hits_total 3", metricsText);
        Assert.Contains("nzbdav_shared_header_cache_misses_total 5", metricsText);
        Assert.Contains("nzbdav_shared_header_cache_write_failures_total 2", metricsText);
    }

    [Fact]
    public async Task BlobCleanupService_DeletesL2Objects_ForQueuedOwner()
    {
        await using var tempConfig = new TempConfigScope();
        await using var setupContext = new DavDatabaseContext();
        await setupContext.Database.EnsureCreatedAsync();

        var ownerId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        setupContext.BlobCleanupItems.Add(new BlobCleanupItem { Id = ownerId });
        await setupContext.SaveChangesAsync();

        var service = new RecordingBlobCleanupService(new ConfigManager());
        var processed = await service.ProcessOneIterationAsync(CancellationToken.None);

        Assert.True(processed);
        Assert.Contains(ownerId, service.DeletedOwners);

        await using var verifyContext = new DavDatabaseContext();
        Assert.Empty(await verifyContext.BlobCleanupItems.ToListAsync());
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

    private sealed class L2Harness : IDisposable
    {
        private readonly ConcurrentDictionary<string, byte[]> _bodies = new(StringComparer.Ordinal);
        private readonly TaskCompletionSource _writeObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ObjectStorageSegmentCache _cache;

        public L2Harness(bool startBackgroundWriter)
        {
            _cache = new ObjectStorageSegmentCache(
                CreateMinioClientProxy(),
                "bucket",
                queueCapacity: 4,
                startBackgroundWriter: startBackgroundWriter);
        }

        public ObjectStorageSegmentCache Cache => _cache;
        public Task WriteObserved => _writeObserved.Task;

        public void Seed(string segmentId, byte[] body)
        {
            _bodies[ObjectStorageSegmentCache.GetObjectKey(segmentId)] = body;
        }

        public void Dispose()
        {
            _cache.Dispose();
        }

        private IObjectOperations CreateMinioClientProxy()
        {
            var createMethod = typeof(DispatchProxy)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Single(method =>
                    method.Name == "Create" &&
                    method.IsGenericMethodDefinition &&
                    method.GetGenericArguments().Length == 2 &&
                    method.GetParameters().Length == 0);

            var proxy = createMethod
                .MakeGenericMethod(typeof(IObjectOperations), typeof(RecordingProxy))
                .Invoke(null, null);

            ((RecordingProxy)proxy!).Handler = HandleCall;
            return (IObjectOperations)proxy!;
        }

        private object? HandleCall(MethodInfo targetMethod, object?[]? args)
        {
            switch (targetMethod.Name)
            {
                case nameof(IObjectOperations.GetObjectAsync):
                    return HandleGetObjectAsync(args);
                case nameof(IObjectOperations.PutObjectAsync):
                    return HandlePutObjectAsync(args);
                default:
                    throw new InvalidOperationException($"Unexpected call to {targetMethod.Name}");
            }
        }

        private object HandleGetObjectAsync(object?[]? args)
        {
            var getArgs = Assert.IsType<GetObjectArgs>(args![0]);
            var objectName = GetPropertyValue<string>(getArgs, "ObjectName");
            if (!_bodies.TryGetValue(objectName, out var body))
                return Task.FromException<ObjectStat>(CreateObjectNotFoundException());

            var callback = GetPropertyValue<Func<Stream, CancellationToken, Task>>(getArgs, "CallBack");
            Assert.NotNull(callback);
            var stream = new MemoryStream(body, writable: false);
            callback!(stream, CancellationToken.None).GetAwaiter().GetResult();
            return Task.FromResult((ObjectStat)Activator.CreateInstance(typeof(ObjectStat), nonPublic: true)!);
        }

        private object HandlePutObjectAsync(object?[]? args)
        {
            var putArgs = Assert.IsType<PutObjectArgs>(args![0]);
            var objectStreamData = GetPropertyValue<Stream>(putArgs, "ObjectStreamData");
            var objectName = GetPropertyValue<string>(putArgs, "ObjectName");
            using (var memoryStream = new MemoryStream())
            {
                objectStreamData.CopyToAsync(memoryStream).GetAwaiter().GetResult();
                _bodies[objectName] = memoryStream.ToArray();
            }

            _writeObserved.TrySetResult();
            return Task.FromResult<PutObjectResponse>(null!);
        }

        private static T GetPropertyValue<T>(object target, string propertyName)
        {
            var property = target.GetType().GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(property);
            return Assert.IsType<T>(property!.GetValue(target));
        }

        private static Exception CreateObjectNotFoundException()
        {
            return (Exception)Activator.CreateInstance(
                typeof(ObjectNotFoundException),
                "bucket",
                "object")!;
        }

        private sealed class RecordingProxy : DispatchProxy
        {
            public Func<MethodInfo, object?[]?, object?>? Handler { get; set; }

            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            {
                if (targetMethod == null)
                    throw new InvalidOperationException("Missing target method.");

                return Handler?.Invoke(targetMethod, args) ?? GetDefaultValue(targetMethod.ReturnType);
            }

            private static object? GetDefaultValue(Type returnType)
            {
                if (returnType == typeof(void) || returnType == typeof(Task))
                    return null;

                if (returnType.IsValueType)
                    return Activator.CreateInstance(returnType);

                return null;
            }
        }
    }

    private sealed class TempConfigScope : IAsyncDisposable
    {
        private readonly string? _previousConfigPath;
        private readonly string _configPath;

        public TempConfigScope()
        {
            _configPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_configPath);
            _previousConfigPath = Environment.GetEnvironmentVariable("CONFIG_PATH");
            Environment.SetEnvironmentVariable("CONFIG_PATH", _configPath);
        }

        public ValueTask DisposeAsync()
        {
            Environment.SetEnvironmentVariable("CONFIG_PATH", _previousConfigPath);

            if (Directory.Exists(_configPath))
                Directory.Delete(_configPath, recursive: true);

            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingBlobCleanupService(ConfigManager configManager) : BlobCleanupService(configManager)
    {
        public List<Guid> DeletedOwners { get; } = [];

        protected override Task DeleteLocalBlobAsync(Guid blobId, CancellationToken cancellationToken)
            => Task.CompletedTask;

        protected override Task DeleteL2ObjectsForOwnerAsync(Guid ownerId, CancellationToken cancellationToken)
        {
            DeletedOwners.Add(ownerId);
            return Task.CompletedTask;
        }
    }

    private static NzbdavMetricsCollector CreateCollector(
        Func<LiveSegmentCacheStats> getCacheStats,
        Func<long> getMaxCacheBytes,
        Func<ConnectionPoolStats?> getPoolStats,
        Func<int> getHealthyProviders,
        Func<int> getWarmingSessions,
        Func<int> getQueueProcessing,
        Func<ObjectStorageSegmentCache?> getL2Cache,
        CollectorRegistry registry,
        IMetricFactory factory,
        SharedHeaderCache? sharedHeaderCache)
    {
        return (NzbdavMetricsCollector)Activator.CreateInstance(
            typeof(NzbdavMetricsCollector),
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            args:
            [
                getCacheStats,
                getMaxCacheBytes,
                getPoolStats,
                getHealthyProviders,
                getWarmingSessions,
                getQueueProcessing,
                getL2Cache,
                registry,
                factory,
                sharedHeaderCache
            ],
            culture: null)!;
    }

    private static SharedHeaderCache CreateSharedHeaderCache(long hits, long misses, long writeFailures)
    {
        var cache = new SharedHeaderCache();
        SetCounter(cache, "_hits", hits);
        SetCounter(cache, "_misses", misses);
        SetCounter(cache, "_writeFailures", writeFailures);
        return cache;
    }

    private static void SetCounter(object instance, string fieldName, long value)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(instance, value);
    }
}

[CollectionDefinition(nameof(MetricsAndSweeperCollection), DisableParallelization = true)]
public sealed class MetricsAndSweeperCollection
{
}

[Collection(nameof(MetricsAndSweeperCollection))]
public sealed class YencHeaderCacheSweeperTests
{
    [Fact]
    public async Task SweepOnceAsync_RemovesExpiredRows_ButKeepsFreshRows()
    {
        await using var tempConfig = new TempConfigScope();
        await using var setupContext = new DavDatabaseContext();
        await setupContext.Database.EnsureCreatedAsync();

        setupContext.YencHeaderCache.AddRange(
            new YencHeaderCacheEntry
            {
                SegmentId = "old-segment",
                FileName = "old.bin",
                FileSize = 1,
                LineLength = 2,
                PartNumber = 3,
                TotalParts = 4,
                PartSize = 5,
                PartOffset = 6,
                CachedAt = DateTime.UtcNow.AddDays(-100)
            },
            new YencHeaderCacheEntry
            {
                SegmentId = "new-segment",
                FileName = "new.bin",
                FileSize = 10,
                LineLength = 20,
                PartNumber = 30,
                TotalParts = 40,
                PartSize = 50,
                PartOffset = 60,
                CachedAt = DateTime.UtcNow.AddDays(-10)
            });
        await setupContext.SaveChangesAsync();

        var configManager = new ConfigManager();
        var sweeper = CreateSweeper(configManager);

        await InvokeSweepOnceAsync(sweeper, CancellationToken.None);

        await using var verifyContext = new DavDatabaseContext();
        Assert.False(await verifyContext.YencHeaderCache.AnyAsync(x => x.SegmentId == "old-segment"));
        Assert.True(await verifyContext.YencHeaderCache.AnyAsync(x => x.SegmentId == "new-segment"));
    }

    private static object CreateSweeper(ConfigManager configManager)
    {
        var sweeperType = Type.GetType("NzbWebDAV.Services.YencHeaderCacheSweeper, NzbWebDAV");
        Assert.NotNull(sweeperType);

        return Activator.CreateInstance(
                   sweeperType!,
                   BindingFlags.Instance | BindingFlags.Public,
                   binder: null,
                   args: [configManager],
                   culture: null)
               ?? throw new InvalidOperationException("Failed to create YencHeaderCacheSweeper.");
    }

    private static async Task InvokeSweepOnceAsync(object sweeper, CancellationToken cancellationToken)
    {
        var method = sweeper.GetType().GetMethod(
            "SweepOnceAsync",
            BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(method);

        var task = (Task)method!.Invoke(sweeper, [cancellationToken])!;
        await task.ConfigureAwait(false);
    }

    private sealed class TempConfigScope : IAsyncDisposable
    {
        private readonly string? _previousConfigPath;
        private readonly string? _previousDatabaseUrl;
        private readonly string _configPath;

        public TempConfigScope()
        {
            _configPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_configPath);
            _previousConfigPath = Environment.GetEnvironmentVariable("CONFIG_PATH");
            _previousDatabaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
            Environment.SetEnvironmentVariable("CONFIG_PATH", _configPath);
            Environment.SetEnvironmentVariable("DATABASE_URL", null);
        }

        public ValueTask DisposeAsync()
        {
            Environment.SetEnvironmentVariable("CONFIG_PATH", _previousConfigPath);
            Environment.SetEnvironmentVariable("DATABASE_URL", _previousDatabaseUrl);

            if (Directory.Exists(_configPath))
                Directory.Delete(_configPath, recursive: true);

            return ValueTask.CompletedTask;
        }
    }
}
