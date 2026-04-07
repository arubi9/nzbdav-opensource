using System.Text;
using NzbWebDAV.Clients.Usenet.Caching;

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
                return Task.FromResult<byte[]?>(body);
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
            tryReadAsync: (_, _) => Task.FromResult<byte[]?>(null),
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
            tryReadAsync: (_, _) => Task.FromResult<byte[]?>(null),
            writeAsync: async (_, _) => await gate.Task.ConfigureAwait(false),
            startWriter: false);

        cache.EnqueueWrite("segment-a", [1, 2, 3], SegmentCategory.SmallFile, null, "a.bin");
        cache.EnqueueWrite("segment-b", [4, 5, 6], SegmentCategory.SmallFile, null, "b.bin");

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
            tryReadAsync: (_, _) => Task.FromResult<byte[]?>(null),
            writeAsync: (_, _) =>
            {
                wrote.TrySetResult();
                return Task.CompletedTask;
            });

        cache.EnqueueWrite("segment-a", [1, 2, 3], SegmentCategory.SmallFile, null, "a.bin");
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
            tryReadAsync: (_, _) => Task.FromResult<byte[]?>(null),
            writeAsync: async (_, _) =>
            {
                await Task.Delay(50);
                Interlocked.Increment(ref writes);
            });

        cache.EnqueueWrite("segment-a", [1], SegmentCategory.Unknown, null, "a.bin");
        cache.EnqueueWrite("segment-b", [2], SegmentCategory.Unknown, null, "b.bin");
        cache.EnqueueWrite("segment-c", [3], SegmentCategory.Unknown, null, "c.bin");

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
            tryReadAsync: (_, _) => Task.FromResult<byte[]?>(null),
            writeAsync: (_, _) => Task.CompletedTask);

        cache.Dispose();
        cache.Dispose();
    }
}
