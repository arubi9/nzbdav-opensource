using System.Diagnostics;
using System.Reflection;
using System.Text;
using NzbWebDAV.Clients.Usenet.Caching;
using NzbWebDAV.Tests.TestDoubles;

namespace NzbWebDAV.Tests.Clients.Usenet.Caching;

/// <summary>
/// Tests for <see cref="LiveSegmentCache"/>'s background prune loop and hot-path behavior.
///
/// Context: <c>GetOrAddBodyAsync</c> historically awaited <c>PruneAsync</c> on the cache-miss
/// return path, which serialized concurrent misses through <c>_pruneLock</c> and paid an O(N)
/// scan on every miss. This file guards against regression of that behavior and confirms that
/// prune still runs regularly off a 30s background timer instead.
/// </summary>
public sealed class LiveSegmentCacheTests
{
    [Fact]
    public async Task GetOrAddBodyAsync_DoesNotBlockOnPruneLock()
    {
        await using var cacheScope = new TempCacheScope();
        var fakeNntpClient = new FakeNntpClient()
            .AddSegment("segment-a", Encoding.ASCII.GetBytes("AAAA"), partOffset: 0);

        using var liveCache = new LiveSegmentCache(cacheScope.Path);

        // Externally hold _pruneLock to simulate a concurrent prune being in flight.
        var pruneLockField = typeof(LiveSegmentCache).GetField(
            "_pruneLock",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(pruneLockField);
        var pruneLock = (SemaphoreSlim)pruneLockField!.GetValue(liveCache)!;

        await pruneLock.WaitAsync();
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var fetchResult = await liveCache.GetOrAddBodyAsync(
                "segment-a",
                async ct =>
                {
                    var response = await fakeNntpClient.DecodedBodyAsync("segment-a", ct);
                    var header = await response.Stream.GetYencHeadersAsync(ct);
                    return new LiveSegmentCache.BodyFetchSource(response.Stream, header!, null);
                },
                CancellationToken.None);
            stopwatch.Stop();

            await fetchResult.Response.Stream.DisposeAsync();

            // With the hot-path await PruneAsync removed, the call should return quickly even while
            // _pruneLock is held. Before the fix this would have blocked until the lock released.
            Assert.True(
                stopwatch.ElapsedMilliseconds < 500,
                $"GetOrAddBodyAsync blocked for {stopwatch.ElapsedMilliseconds}ms while _pruneLock was held");
        }
        finally
        {
            pruneLock.Release();
        }
    }

    [Fact]
    public async Task PruneLoop_RemovesExpiredEntries_WithoutExplicitCall()
    {
        await using var cacheScope = new TempCacheScope();
        var fakeNntpClient = new FakeNntpClient()
            .AddSegment("expiring", Encoding.ASCII.GetBytes("1111"), partOffset: 0);

        // Short max-age so entries expire almost immediately. The background loop ticks every 30s,
        // so the test must wait long enough to observe at least one tick.
        using var liveCache = new LiveSegmentCache(
            cacheScope.Path,
            maxCacheSizeBytes: 10L * 1024 * 1024 * 1024,
            maxAge: TimeSpan.FromMilliseconds(100));
        using var client = new LiveSegmentCachingNntpClient(fakeNntpClient, liveCache);

        await CacheSegmentAsync(client, "expiring", SegmentCategory.Unknown, Guid.NewGuid());
        Assert.True(liveCache.HasBody("expiring"));
        var baselineEvictions = liveCache.GetStats().Evictions;

        // Wait past the 30s loop interval to allow the background prune to run.
        // Poll with short delays; once the prune loop ticks we should see the eviction.
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            if (liveCache.GetStats().Evictions > baselineEvictions)
                break;
            await Task.Delay(500);
        }

        var stats = liveCache.GetStats();
        Assert.True(
            stats.Evictions > baselineEvictions,
            $"Expected background prune loop to evict expired entry within 45s; Evictions={stats.Evictions}, baseline={baselineEvictions}");
        Assert.False(liveCache.HasBody("expiring"));
    }

    private static async Task CacheSegmentAsync(
        LiveSegmentCachingNntpClient client,
        string segmentId,
        SegmentCategory? category = null,
        Guid? ownerNzbId = null)
    {
        IDisposable? contextScope = null;
        try
        {
            if (category.HasValue)
                contextScope = SegmentFetchContext.Set(category.Value, ownerNzbId);

            var response = await client.DecodedBodyAsync(segmentId, CancellationToken.None);
            await response.Stream.DisposeAsync();
        }
        finally
        {
            contextScope?.Dispose();
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

            return ValueTask.CompletedTask;
        }
    }
}
