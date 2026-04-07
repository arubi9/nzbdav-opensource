using System.Text;
using NzbWebDAV.Clients.Usenet.Caching;
using NzbWebDAV.Tests.TestDoubles;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class LiveSegmentCachingNntpClientTests
{
    [Fact]
    public async Task ConcurrentDecodedBodyCallsForSameSegmentHitUnderlyingClientOnce()
    {
        await using var cacheScope = new TempCacheScope();
        var fakeNntpClient = new FakeNntpClient
        {
            BodyFetchDelay = TimeSpan.FromMilliseconds(100)
        }.AddSegment("segment-a", Encoding.ASCII.GetBytes("segment-a"), partOffset: 0);

        using var liveCache = new LiveSegmentCache(cacheScope.Path);
        using var client = new LiveSegmentCachingNntpClient(fakeNntpClient, liveCache);

        var firstTask = client.DecodedBodyAsync("segment-a", CancellationToken.None);
        var secondTask = client.DecodedBodyAsync("segment-a", CancellationToken.None);

        var responses = await Task.WhenAll(firstTask, secondTask);
        await using var firstStream = responses[0].Stream;
        await using var secondStream = responses[1].Stream;

        Assert.Equal(1, fakeNntpClient.DecodedBodyCallCount);
        Assert.Equal("segment-a", Encoding.ASCII.GetString(await ReadAllBytesAsync(firstStream)));
        Assert.Equal("segment-a", Encoding.ASCII.GetString(await ReadAllBytesAsync(secondStream)));
    }

    [Fact]
    public async Task SecondRequestAfterCompletionIsServedFromDiskCache()
    {
        await using var cacheScope = new TempCacheScope();
        var fakeNntpClient = new FakeNntpClient()
            .AddSegment("segment-a", Encoding.ASCII.GetBytes("segment-a"), partOffset: 0);

        using var liveCache = new LiveSegmentCache(cacheScope.Path);
        using var client = new LiveSegmentCachingNntpClient(fakeNntpClient, liveCache);

        var firstResponse = await client.DecodedBodyAsync("segment-a", CancellationToken.None);
        await using (firstResponse.Stream)
        {
            await ReadAllBytesAsync(firstResponse.Stream);
        }

        var secondResponse = await client.DecodedBodyAsync("segment-a", CancellationToken.None);
        await using (secondResponse.Stream)
        {
            Assert.Equal("segment-a", Encoding.ASCII.GetString(await ReadAllBytesAsync(secondResponse.Stream)));
        }

        Assert.Equal(1, fakeNntpClient.DecodedBodyCallCount);
        Assert.Equal(1, liveCache.GetStats().CachedSegmentCount);
    }

    [Fact]
    public async Task GetYencHeadersAsyncIsMemoizedAcrossRequests()
    {
        await using var cacheScope = new TempCacheScope();
        var fakeNntpClient = new FakeNntpClient()
            .AddSegment("segment-a", Encoding.ASCII.GetBytes("segment-a"), partOffset: 123);

        using var liveCache = new LiveSegmentCache(cacheScope.Path);
        using var client = new LiveSegmentCachingNntpClient(fakeNntpClient, liveCache);

        var first = await client.GetYencHeadersAsync("segment-a", CancellationToken.None);
        var second = await client.GetYencHeadersAsync("segment-a", CancellationToken.None);

        Assert.Equal(first.PartOffset, second.PartOffset);
        Assert.Equal(1, fakeNntpClient.GetYencHeadersCallCount);
    }

    [Fact]
    public async Task DecodedArticleAsyncOnCachedBodyDoesNotRefetchBodyData()
    {
        await using var cacheScope = new TempCacheScope();
        var fakeNntpClient = new FakeNntpClient()
            .AddSegment("segment-a", Encoding.ASCII.GetBytes("segment-a"), partOffset: 0);

        using var liveCache = new LiveSegmentCache(cacheScope.Path);
        using var client = new LiveSegmentCachingNntpClient(fakeNntpClient, liveCache);

        var response = await client.DecodedBodyAsync("segment-a", CancellationToken.None);
        await using (response.Stream)
        {
            await ReadAllBytesAsync(response.Stream);
        }

        var article = await client.DecodedArticleAsync("segment-a", CancellationToken.None);
        await using (article.Stream)
        {
            Assert.Equal("segment-a", Encoding.ASCII.GetString(await ReadAllBytesAsync(article.Stream)));
        }
        Assert.Equal(1, fakeNntpClient.DecodedBodyCallCount);
        Assert.Equal(0, fakeNntpClient.DecodedArticleCallCount);
        Assert.Equal(1, fakeNntpClient.HeadCallCount);
    }

    [Fact]
    public async Task EvictionRemovesOnlyUnreferencedFiles()
    {
        await using var cacheScope = new TempCacheScope();
        var fakeNntpClient = new FakeNntpClient()
            .AddSegment("segment-a", Encoding.ASCII.GetBytes("AAAA"), partOffset: 0)
            .AddSegment("segment-b", Encoding.ASCII.GetBytes("BBBB"), partOffset: 4);

        using var liveCache = new LiveSegmentCache(cacheScope.Path, maxCacheSizeBytes: 6, maxAge: TimeSpan.FromHours(6));
        using var client = new LiveSegmentCachingNntpClient(fakeNntpClient, liveCache);

        var firstResponse = await client.DecodedBodyAsync("segment-a", CancellationToken.None);
        await ReadAllBytesAsync(firstResponse.Stream);

        var secondResponse = await client.DecodedBodyAsync("segment-b", CancellationToken.None);
        await using (secondResponse.Stream)
        {
            await ReadAllBytesAsync(secondResponse.Stream);
        }

        await liveCache.PruneAsync();

        var thirdResponse = await client.DecodedBodyAsync("segment-a", CancellationToken.None);
        await using (thirdResponse.Stream)
        {
            Assert.Equal("AAAA", Encoding.ASCII.GetString(await ReadAllBytesAsync(thirdResponse.Stream)));
        }

        Assert.Equal(2, fakeNntpClient.DecodedBodyCallCount);
        Assert.True(liveCache.GetStats().Evictions >= 1);

        await firstResponse.Stream.DisposeAsync();
    }

    [Fact]
    public async Task L1MissL2Hit_PromotesBodyToL1_WithoutCallingUnderlyingClient()
    {
        await using var cacheScope = new TempCacheScope();
        var fakeNntpClient = new FakeNntpClient();
        using var l2Cache = new ObjectStorageSegmentCache(
            bucketName: "bucket",
            queueCapacity: 4,
            ensureBucketExistsAsync: _ => Task.CompletedTask,
            tryReadAsync: (segmentId, _) => Task.FromResult<ObjectStorageSegmentCache.ReadResult?>(
                new ObjectStorageSegmentCache.ReadResult(
                    Encoding.ASCII.GetBytes("segment-a"),
                    new Dictionary<string, string>
                    {
                        ["x-amz-meta-yenc-header"] = System.Text.Json.JsonSerializer.Serialize(CreateHeader("segment.bin"))
                    })),
            writeAsync: (_, _) => Task.CompletedTask);

        using var liveCache = new LiveSegmentCache(cacheScope.Path, l2Cache: l2Cache);
        using var client = new LiveSegmentCachingNntpClient(fakeNntpClient, liveCache);

        var response = await client.DecodedBodyAsync("segment-a", CancellationToken.None);
        await using (response.Stream)
        {
            Assert.Equal("segment-a", Encoding.ASCII.GetString(await ReadAllBytesAsync(response.Stream)));
        }

        Assert.Equal(0, fakeNntpClient.DecodedBodyCallCount);
        Assert.True(liveCache.HasBody("segment-a"));
        Assert.Equal(1, l2Cache.L2Hits);
    }

    [Fact]
    public async Task L2Miss_FallsThroughToNntp_AndEnqueuesWriteBehind()
    {
        await using var cacheScope = new TempCacheScope();
        var fakeNntpClient = new FakeNntpClient()
            .AddSegment("segment-a", Encoding.ASCII.GetBytes("segment-a"), partOffset: 0);
        var writes = new List<ObjectStorageSegmentCache.WriteRequest>();
        using var l2Cache = new ObjectStorageSegmentCache(
            bucketName: "bucket",
            queueCapacity: 4,
            ensureBucketExistsAsync: _ => Task.CompletedTask,
            tryReadAsync: (_, _) => Task.FromResult<ObjectStorageSegmentCache.ReadResult?>(null),
            writeAsync: (request, _) =>
            {
                writes.Add(request);
                return Task.CompletedTask;
            });

        using var liveCache = new LiveSegmentCache(cacheScope.Path, l2Cache: l2Cache);
        using var client = new LiveSegmentCachingNntpClient(fakeNntpClient, liveCache);

        var response = await client.DecodedBodyAsync("segment-a", CancellationToken.None);
        await response.Stream.DisposeAsync();
        await Task.Delay(50);

        Assert.Equal(1, fakeNntpClient.DecodedBodyCallCount);
        Assert.Single(writes);
        Assert.Equal("segment-a", writes[0].SegmentId);
    }

    [Fact]
    public async Task InvalidL2Metadata_FallsThroughToNntp()
    {
        await using var cacheScope = new TempCacheScope();
        var fakeNntpClient = new FakeNntpClient()
            .AddSegment("segment-a", Encoding.ASCII.GetBytes("segment-a"), partOffset: 0);
        using var l2Cache = new ObjectStorageSegmentCache(
            bucketName: "bucket",
            queueCapacity: 4,
            ensureBucketExistsAsync: _ => Task.CompletedTask,
            tryReadAsync: (_, _) => Task.FromResult<ObjectStorageSegmentCache.ReadResult?>(
                new ObjectStorageSegmentCache.ReadResult(
                    Encoding.ASCII.GetBytes("segment-a"),
                    new Dictionary<string, string>())),
            writeAsync: (_, _) => Task.CompletedTask);

        using var liveCache = new LiveSegmentCache(cacheScope.Path, l2Cache: l2Cache);
        using var client = new LiveSegmentCachingNntpClient(fakeNntpClient, liveCache);

        var response = await client.DecodedBodyAsync("segment-a", CancellationToken.None);
        await using (response.Stream)
        {
            Assert.Equal("segment-a", Encoding.ASCII.GetString(await ReadAllBytesAsync(response.Stream)));
        }

        Assert.Equal(1, fakeNntpClient.DecodedBodyCallCount);
    }

    [Fact]
    public async Task L2Promotion_PreservesCategoryAndOwnerMetadata()
    {
        await using var cacheScope = new TempCacheScope();
        var ownerId = Guid.NewGuid();
        using var l2Cache = new ObjectStorageSegmentCache(
            bucketName: "bucket",
            queueCapacity: 4,
            ensureBucketExistsAsync: _ => Task.CompletedTask,
            tryReadAsync: (_, _) => Task.FromResult<ObjectStorageSegmentCache.ReadResult?>(
                new ObjectStorageSegmentCache.ReadResult(
                    Encoding.ASCII.GetBytes("segment-a"),
                    new Dictionary<string, string>
                    {
                        ["x-amz-meta-yenc-header"] = System.Text.Json.JsonSerializer.Serialize(CreateHeader("segment.bin")),
                        ["x-amz-meta-category"] = "small_file",
                        ["x-amz-meta-owner-nzb-id"] = ownerId.ToString()
                    })),
            writeAsync: (_, _) => Task.CompletedTask);

        using var liveCache = new LiveSegmentCache(cacheScope.Path, l2Cache: l2Cache);
        using var client = new LiveSegmentCachingNntpClient(new FakeNntpClient(), liveCache);

        var response = await client.DecodedBodyAsync("segment-a", CancellationToken.None);
        await response.Stream.DisposeAsync();

        Assert.Equal(1, liveCache.GetStats().SmallFileCount);
        liveCache.EvictByOwner(ownerId);
        Assert.False(liveCache.HasBody("segment-a"));
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream)
    {
        using var memoryStream = new MemoryStream();
        await stream.CopyToAsync(memoryStream);
        return memoryStream.ToArray();
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

            const int maxAttempts = 10;
            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                try
                {
                    Directory.Delete(Path, recursive: true);
                    return ValueTask.CompletedTask;
                }
                catch (IOException)
                {
                    if (attempt >= maxAttempts - 1) break;
                    Thread.Sleep(50);
                }
                catch (UnauthorizedAccessException)
                {
                    if (attempt >= maxAttempts - 1) break;
                    Thread.Sleep(50);
                }
            }

            // Best-effort temp cleanup; don't fail the test because Windows is
            // still holding a handle for a short period after disposal.
            return ValueTask.CompletedTask;
        }
    }
}
