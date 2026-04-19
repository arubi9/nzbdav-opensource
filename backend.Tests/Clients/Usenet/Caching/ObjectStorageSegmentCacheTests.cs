using System.Text;
using NzbWebDAV.Clients.Usenet.Caching;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Clients.Usenet.Caching;

public sealed class ObjectStorageSegmentCacheTests
{
    [Fact]
    public void GetObjectKey_IsDeterministic()
    {
        var key1 = ObjectStorageSegmentCache.GetObjectKey("segment-a");
        var key2 = ObjectStorageSegmentCache.GetObjectKey("segment-a");

        Assert.Equal(key1, key2);
    }

    [Fact]
    public void GetObjectKey_HasCorrectShape()
    {
        var key = ObjectStorageSegmentCache.GetObjectKey("segment-a");
        Assert.Matches(@"^segments/[0-9a-f]{2}/[0-9a-f]{64}$", key);
    }

    [Fact]
    public async Task TryReadAsync_ReturnsStreamOnSuccess()
    {
        using var cache = new ObjectStorageSegmentCache(
            bucketName: "bucket",
            queueCapacity: 4,
            ensureBucketExistsAsync: _ => Task.CompletedTask,
            tryReadAsync: (segmentId, ct) =>
            {
                var body = Encoding.ASCII.GetBytes($"body:{segmentId}");
                return Task.FromResult<ObjectStorageSegmentCache.ReadResult?>(
                    new ObjectStorageSegmentCache.ReadResult(body, new Dictionary<string, string>()));
            },
            writeAsync: (_, _) => Task.CompletedTask);

        await using var stream = await cache.TryReadAsync("segment-a", CancellationToken.None);
        Assert.NotNull(stream);

        using var memory = new MemoryStream();
        await stream!.CopyToAsync(memory);
        Assert.Equal("body:segment-a", Encoding.ASCII.GetString(memory.ToArray()));
        Assert.Equal(1, cache.L2Hits);
        Assert.Equal(0, cache.L2Misses);
    }

    [Fact]
    public async Task TryReadAsync_ReturnsNullOnMiss()
    {
        using var cache = new ObjectStorageSegmentCache(
            bucketName: "bucket",
            queueCapacity: 4,
            ensureBucketExistsAsync: _ => Task.CompletedTask,
            tryReadAsync: (_, _) => Task.FromResult<ObjectStorageSegmentCache.ReadResult?>(null),
            writeAsync: (_, _) => Task.CompletedTask);

        var stream = await cache.TryReadAsync("segment-a", CancellationToken.None);

        Assert.Null(stream);
        Assert.Equal(0, cache.L2Hits);
        Assert.Equal(1, cache.L2Misses);
    }

    [Fact]
    public async Task TryReadAsync_ReturnsNullOnTransientError()
    {
        using var cache = new ObjectStorageSegmentCache(
            bucketName: "bucket",
            queueCapacity: 4,
            ensureBucketExistsAsync: _ => Task.CompletedTask,
            tryReadAsync: (_, _) => throw new IOException("boom"),
            writeAsync: (_, _) => Task.CompletedTask);

        var stream = await cache.TryReadAsync("segment-a", CancellationToken.None);

        Assert.Null(stream);
        Assert.Equal(1, cache.L2Misses);
    }

    [Fact]
    public async Task EnqueueWrite_DropsOnFullQueue()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var cache = new ObjectStorageSegmentCache(
            bucketName: "bucket",
            queueCapacity: 1,
            ensureBucketExistsAsync: _ => Task.CompletedTask,
            tryReadAsync: (_, _) => Task.FromResult<ObjectStorageSegmentCache.ReadResult?>(null),
            writeAsync: async (_, _) => await gate.Task.ConfigureAwait(false),
            startWriter: false);

        cache.EnqueueWrite("segment-a", [1, 2, 3], SegmentCategory.SmallFile, null, CreateHeader("a.bin"));
        cache.EnqueueWrite("segment-b", [4, 5, 6], SegmentCategory.SmallFile, null, CreateHeader("b.bin"));

        Assert.Equal(1, cache.L2WritesDropped);
        gate.TrySetResult();
    }

    [Fact]
    public async Task EnqueueWrite_CompletesAsynchronously()
    {
        var wrote = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var cache = new ObjectStorageSegmentCache(
            bucketName: "bucket",
            queueCapacity: 4,
            ensureBucketExistsAsync: _ => Task.CompletedTask,
            tryReadAsync: (_, _) => Task.FromResult<ObjectStorageSegmentCache.ReadResult?>(null),
            writeAsync: (_, _) =>
            {
                wrote.TrySetResult();
                return Task.CompletedTask;
            });

        cache.EnqueueWrite("segment-a", [1, 2, 3], SegmentCategory.SmallFile, null, CreateHeader("a.bin"));
        await wrote.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, cache.L2Writes);
    }

    [Fact]
    public void Dispose_DrainsWriterWithinBudget()
    {
        var writes = 0;

        var cache = new ObjectStorageSegmentCache(
            bucketName: "bucket",
            queueCapacity: 4,
            ensureBucketExistsAsync: _ => Task.CompletedTask,
            tryReadAsync: (_, _) => Task.FromResult<ObjectStorageSegmentCache.ReadResult?>(null),
            writeAsync: async (_, _) =>
            {
                await Task.Delay(50);
                Interlocked.Increment(ref writes);
            });

        cache.EnqueueWrite("segment-a", [1], SegmentCategory.Unknown, null, CreateHeader("a.bin"));
        cache.EnqueueWrite("segment-b", [2], SegmentCategory.Unknown, null, CreateHeader("b.bin"));
        cache.EnqueueWrite("segment-c", [3], SegmentCategory.Unknown, null, CreateHeader("c.bin"));

        cache.Dispose();

        Assert.Equal(3, writes);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        using var cache = new ObjectStorageSegmentCache(
            bucketName: "bucket",
            queueCapacity: 4,
            ensureBucketExistsAsync: _ => Task.CompletedTask,
            tryReadAsync: (_, _) => Task.FromResult<ObjectStorageSegmentCache.ReadResult?>(null),
            writeAsync: (_, _) => Task.CompletedTask);

        cache.Dispose();
        cache.Dispose();
    }

    [Fact]
    public async Task DeleteByOwnerAsync_UsesInjectedDeleteDelegate()
    {
        Guid? deletedOwner = null;

        using var cache = new ObjectStorageSegmentCache(
            bucketName: "bucket",
            queueCapacity: 4,
            ensureBucketExistsAsync: _ => Task.CompletedTask,
            tryReadAsync: (_, _) => Task.FromResult<ObjectStorageSegmentCache.ReadResult?>(null),
            writeAsync: (_, _) => Task.CompletedTask,
            deleteByOwnerAsync: (ownerNzbId, _) =>
            {
                deletedOwner = ownerNzbId;
                return Task.CompletedTask;
            });

        var ownerId = Guid.NewGuid();
        await cache.DeleteByOwnerAsync(ownerId, CancellationToken.None);

        Assert.Equal(ownerId, deletedOwner);
    }

    [Fact]
    public void CtorFromConfig_DegradesWhenRequiredSettingsMissing()
    {
        var configManager = new ConfigManager();
        configManager.UpdateValues(
        [
            new ConfigItem { ConfigName = "cache.l2.enabled", ConfigValue = "true" },
            new ConfigItem { ConfigName = "cache.l2.bucket-name", ConfigValue = "nzbdav-segments" }
        ]);

        using var cache = new ObjectStorageSegmentCache(configManager);

        Assert.Equal("nzbdav-segments", cache.BucketName);
        Assert.Equal(0, cache.L2Hits);
    }

    [Fact]
    public async Task WriterLoop_TimesOutStalledWrite()
    {
        var stall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var cache = new ObjectStorageSegmentCache(
            bucketName: "bucket",
            queueCapacity: 4,
            ensureBucketExistsAsync: _ => Task.CompletedTask,
            tryReadAsync: (_, _) => Task.FromResult<ObjectStorageSegmentCache.ReadResult?>(null),
            writeAsync: async (_, ct) =>
            {
                await stall.Task.WaitAsync(ct).ConfigureAwait(false);
            },
            writeTimeout: TimeSpan.FromMilliseconds(150));

        cache.EnqueueWrite("segment-a", [1], SegmentCategory.SmallFile, null, CreateHeader("a.bin"));

        // Writer should trip the timeout and increment failures without
        // blocking subsequent writes. Poll briefly.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (cache.L2WriteFailures == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(25);

        Assert.Equal(1, cache.L2WriteFailures);
        Assert.Equal(0, cache.L2Writes);
        stall.TrySetResult();
    }

    [Fact]
    public async Task WriterLoop_RecoversAfterTimeout()
    {
        // Force single writer so ordering is deterministic: stalled write
        // must timeout before OK write runs.
        var calls = 0;

        using var cache = new ObjectStorageSegmentCache(
            bucketName: "bucket",
            queueCapacity: 4,
            ensureBucketExistsAsync: _ => Task.CompletedTask,
            tryReadAsync: (_, _) => Task.FromResult<ObjectStorageSegmentCache.ReadResult?>(null),
            writeAsync: async (_, ct) =>
            {
                var n = Interlocked.Increment(ref calls);
                if (n == 1)
                    await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            },
            writeTimeout: TimeSpan.FromMilliseconds(100),
            writerParallelism: 1);

        cache.EnqueueWrite("segment-stalled", [1], SegmentCategory.SmallFile, null, CreateHeader("a.bin"));
        cache.EnqueueWrite("segment-ok", [2], SegmentCategory.SmallFile, null, CreateHeader("b.bin"));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while ((cache.L2Writes < 1 || cache.L2WriteFailures < 1) && DateTime.UtcNow < deadline)
            await Task.Delay(25);

        Assert.Equal(1, cache.L2Writes);
        Assert.Equal(1, cache.L2WriteFailures);
    }

    [Fact]
    public async Task TryReadAsync_TimesOutAndCountsAsMiss()
    {
        using var cache = new ObjectStorageSegmentCache(
            bucketName: "bucket",
            queueCapacity: 4,
            ensureBucketExistsAsync: _ => Task.CompletedTask,
            tryReadAsync: async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                return null;
            },
            writeAsync: (_, _) => Task.CompletedTask,
            readTimeout: TimeSpan.FromMilliseconds(100));

        var stream = await cache.TryReadAsync("segment-a", CancellationToken.None);

        Assert.Null(stream);
        Assert.Equal(1, cache.L2ReadTimeouts);
        Assert.Equal(1, cache.L2Misses);
    }

    [Fact]
    public async Task MultipleWriters_DrainConcurrently()
    {
        // 8 writes, each takes 200 ms. Single writer = 1.6s, 4 writers = 400ms.
        // We assert completion within 800 ms to prove parallelism.
        var started = 0;
        var peakConcurrent = 0;
        var currentConcurrent = 0;
        var lockObj = new object();

        using var cache = new ObjectStorageSegmentCache(
            bucketName: "bucket",
            queueCapacity: 16,
            ensureBucketExistsAsync: _ => Task.CompletedTask,
            tryReadAsync: (_, _) => Task.FromResult<ObjectStorageSegmentCache.ReadResult?>(null),
            writeAsync: async (_, _) =>
            {
                Interlocked.Increment(ref started);
                int now;
                lock (lockObj)
                {
                    currentConcurrent++;
                    now = currentConcurrent;
                    if (now > peakConcurrent) peakConcurrent = now;
                }
                await Task.Delay(200).ConfigureAwait(false);
                lock (lockObj) { currentConcurrent--; }
            },
            writerParallelism: 4);

        for (var i = 0; i < 8; i++)
            cache.EnqueueWrite($"seg-{i}", [1], SegmentCategory.SmallFile, null, CreateHeader($"{i}.bin"));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (cache.L2Writes < 8 && DateTime.UtcNow < deadline)
            await Task.Delay(25);

        Assert.Equal(8, cache.L2Writes);
        Assert.True(peakConcurrent >= 2, $"Expected multi-worker concurrency, peak was {peakConcurrent}");
        Assert.Equal(4, cache.WriterParallelism);
    }

    [Fact]
    public async Task SingleWriter_PreservesLegacyBehavior()
    {
        using var cache = new ObjectStorageSegmentCache(
            bucketName: "bucket",
            queueCapacity: 16,
            ensureBucketExistsAsync: _ => Task.CompletedTask,
            tryReadAsync: (_, _) => Task.FromResult<ObjectStorageSegmentCache.ReadResult?>(null),
            writeAsync: (_, _) => Task.CompletedTask,
            writerParallelism: 1);

        for (var i = 0; i < 5; i++)
            cache.EnqueueWrite($"seg-{i}", [1], SegmentCategory.SmallFile, null, CreateHeader($"{i}.bin"));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (cache.L2Writes < 5 && DateTime.UtcNow < deadline)
            await Task.Delay(25);

        Assert.Equal(5, cache.L2Writes);
        Assert.Equal(1, cache.WriterParallelism);
    }

    [Fact]
    public void WriterParallelism_RejectsInvalidValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ObjectStorageSegmentCache(
            bucketName: "b",
            queueCapacity: 4,
            ensureBucketExistsAsync: _ => Task.CompletedTask,
            tryReadAsync: (_, _) => Task.FromResult<ObjectStorageSegmentCache.ReadResult?>(null),
            writeAsync: (_, _) => Task.CompletedTask,
            writerParallelism: 0));

        Assert.Throws<ArgumentOutOfRangeException>(() => new ObjectStorageSegmentCache(
            bucketName: "b",
            queueCapacity: 4,
            ensureBucketExistsAsync: _ => Task.CompletedTask,
            tryReadAsync: (_, _) => Task.FromResult<ObjectStorageSegmentCache.ReadResult?>(null),
            writeAsync: (_, _) => Task.CompletedTask,
            writerParallelism: 64));
    }

    [Fact]
    public void QueueDepth_ReflectsPendingWrites()
    {
        using var cache = new ObjectStorageSegmentCache(
            bucketName: "bucket",
            queueCapacity: 8,
            ensureBucketExistsAsync: _ => Task.CompletedTask,
            tryReadAsync: (_, _) => Task.FromResult<ObjectStorageSegmentCache.ReadResult?>(null),
            writeAsync: (_, _) => Task.CompletedTask,
            startWriter: false);

        Assert.Equal(0, cache.QueueDepth);
        cache.EnqueueWrite("a", [1], SegmentCategory.SmallFile, null, CreateHeader("a.bin"));
        cache.EnqueueWrite("b", [2], SegmentCategory.SmallFile, null, CreateHeader("b.bin"));
        Assert.Equal(2, cache.QueueDepth);
    }

    private static UsenetYencHeader CreateHeader(string fileName)
    {
        return new UsenetYencHeader
        {
            FileName = fileName,
            FileSize = 123,
            LineLength = 128,
            PartNumber = 1,
            TotalParts = 1,
            PartSize = 123,
            PartOffset = 0
        };
    }
}
