using System.Text;
using NzbWebDAV.Clients.Usenet.Caching;
using NzbWebDAV.Tests.TestDoubles;

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

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream)
    {
        using var memoryStream = new MemoryStream();
        await stream.CopyToAsync(memoryStream);
        return memoryStream.ToArray();
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
