using System.Text;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Caching;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Tests.TestDoubles;

namespace backend.Tests.Services;

public sealed class ReadAheadWarmingServiceTests
{
    // Warming used to run one segment at a time, which pinned the prefetcher to a
    // single NNTP connection and capped streaming throughput far below the
    // available uplink no matter how many connections the pool allowed.
    // Eight segments at 200ms each take >=1.6s sequentially; concurrently they
    // finish in roughly one fetch delay.
    [Fact]
    public async Task WarmingFetchesSegmentsConcurrentlyRatherThanOneAtATime()
    {
        await using var cacheScope = new TempCacheScope();
        var configManager = new ConfigManager();
        configManager.UpdateValues(
        [
            new ConfigItem { ConfigName = "cache.read-ahead-enable", ConfigValue = "true" },
            new ConfigItem { ConfigName = "cache.read-ahead-segments", ConfigValue = "8" },
            new ConfigItem { ConfigName = "cache.read-ahead-concurrency", ConfigValue = "8" }
        ]);

        using var liveCache = new LiveSegmentCache(cacheScope.Path);
        using var fakeNntpClient = new FakeNntpClient
        {
            BodyFetchDelay = TimeSpan.FromMilliseconds(200)
        };

        var segmentIds = new string[8];
        for (var i = 0; i < segmentIds.Length; i++)
        {
            segmentIds[i] = $"segment-{i}";
            fakeNntpClient.AddSegment(segmentIds[i], Encoding.ASCII.GetBytes(i.ToString()), partOffset: i);
        }

        using var cachingClient = new LiveSegmentCachingNntpClient(fakeNntpClient, liveCache);
        using var warmingService = new ReadAheadWarmingService(cachingClient, liveCache, configManager);
        using var cts = new CancellationTokenSource();
        var sessionId = warmingService.CreateSession(segmentIds, cts.Token);

        var startedAt = DateTime.UtcNow;
        warmingService.UpdatePosition(sessionId, 0);
        await WaitForConditionAsync(() => segmentIds.All(liveCache.HasBody));
        var elapsed = DateTime.UtcNow - startedAt;

        Assert.True(
            elapsed < TimeSpan.FromMilliseconds(1200),
            $"Expected concurrent warming to finish well under the {8 * 200}ms sequential cost, took {elapsed.TotalMilliseconds:F0}ms.");
    }

    // The prefetch budget belongs to the shared NNTP connection pool, so it must
    // be global. When it was applied per session, N concurrent viewers demanded N
    // times the configured concurrency, overran the downloader's pending-queue
    // guard, and made *other* viewers' live reads throw mid-playback.
    [Fact]
    public async Task ReadAheadConcurrencyIsSharedAcrossSessionsNotPerSession()
    {
        const int budget = 4;
        const int sessions = 5;
        const int segmentsPerSession = 8;

        await using var cacheScope = new TempCacheScope();
        var configManager = new ConfigManager();
        configManager.UpdateValues(
        [
            new ConfigItem { ConfigName = "cache.read-ahead-enable", ConfigValue = "true" },
            new ConfigItem { ConfigName = "cache.read-ahead-segments", ConfigValue = segmentsPerSession.ToString() },
            new ConfigItem { ConfigName = "cache.read-ahead-concurrency", ConfigValue = budget.ToString() }
        ]);

        using var liveCache = new LiveSegmentCache(cacheScope.Path);
        using var fakeNntpClient = new FakeNntpClient
        {
            BodyFetchDelay = TimeSpan.FromMilliseconds(60)
        };

        var allSegments = new List<string[]>();
        for (var s = 0; s < sessions; s++)
        {
            var ids = new string[segmentsPerSession];
            for (var i = 0; i < segmentsPerSession; i++)
            {
                ids[i] = $"s{s}-segment-{i}";
                fakeNntpClient.AddSegment(ids[i], Encoding.ASCII.GetBytes($"{s}:{i}"), partOffset: i);
            }

            allSegments.Add(ids);
        }

        using var cachingClient = new LiveSegmentCachingNntpClient(fakeNntpClient, liveCache);
        using var warmingService = new ReadAheadWarmingService(cachingClient, liveCache, configManager);
        using var cts = new CancellationTokenSource();

        foreach (var ids in allSegments)
        {
            var sessionId = warmingService.CreateSession(ids, cts.Token);
            warmingService.UpdatePosition(sessionId, 0);
        }

        await WaitForConditionAsync(() => allSegments.All(ids => ids.All(liveCache.HasBody)));

        Assert.True(
            fakeNntpClient.PeakConcurrentBodyFetches <= budget,
            $"Read-ahead concurrency must be a global budget of {budget}, but {sessions} sessions " +
            $"drove {fakeNntpClient.PeakConcurrentBodyFetches} simultaneous fetches.");
    }

    [Fact]
    public async Task UpdatePositionWarmsFromLatestPosition()
    {
        await using var cacheScope = new TempCacheScope();
        var configManager = new ConfigManager();
        configManager.UpdateValues(
        [
            new ConfigItem { ConfigName = "cache.read-ahead-enable", ConfigValue = "true" },
            new ConfigItem { ConfigName = "cache.read-ahead-segments", ConfigValue = "2" }
        ]);

        using var liveCache = new LiveSegmentCache(cacheScope.Path);
        using var fakeNntpClient = new FakeNntpClient
        {
            BodyFetchDelay = TimeSpan.FromMilliseconds(200)
        };
        fakeNntpClient
            .AddSegment("segment-0", Encoding.ASCII.GetBytes("A"), partOffset: 0)
            .AddSegment("segment-1", Encoding.ASCII.GetBytes("B"), partOffset: 1)
            .AddSegment("segment-2", Encoding.ASCII.GetBytes("C"), partOffset: 2)
            .AddSegment("segment-3", Encoding.ASCII.GetBytes("D"), partOffset: 3);
        using var cachingClient = new LiveSegmentCachingNntpClient(fakeNntpClient, liveCache);
        using var warmingService = new ReadAheadWarmingService(cachingClient, liveCache, configManager);

        using var cts = new CancellationTokenSource();
        var sessionId = warmingService.CreateSession(
            ["segment-0", "segment-1", "segment-2", "segment-3"],
            cts.Token
        );

        warmingService.UpdatePosition(sessionId, 2);
        await WaitForConditionAsync(() => liveCache.HasBody("segment-2") && liveCache.HasBody("segment-3"));

        Assert.False(liveCache.HasBody("segment-0"));
        Assert.False(liveCache.HasBody("segment-1"));
    }

    [Fact]
    public async Task StopSessionPreventsFurtherWarming()
    {
        await using var cacheScope = new TempCacheScope();
        var configManager = new ConfigManager();
        configManager.UpdateValues(
        [
            new ConfigItem { ConfigName = "cache.read-ahead-enable", ConfigValue = "true" },
            new ConfigItem { ConfigName = "cache.read-ahead-segments", ConfigValue = "4" },
            // Warming is parallel by default, which would put the whole 3-segment
            // window in flight before StopSession could be observed. Pin it to one
            // at a time so this test measures stop semantics rather than fan-out.
            new ConfigItem { ConfigName = "cache.read-ahead-concurrency", ConfigValue = "1" }
        ]);

        using var liveCache = new LiveSegmentCache(cacheScope.Path);
        using var fakeNntpClient = new FakeNntpClient
        {
            BodyFetchDelay = TimeSpan.FromMilliseconds(200)
        };
        fakeNntpClient
            .AddSegment("segment-0", Encoding.ASCII.GetBytes("A"), partOffset: 0)
            .AddSegment("segment-1", Encoding.ASCII.GetBytes("B"), partOffset: 1)
            .AddSegment("segment-2", Encoding.ASCII.GetBytes("C"), partOffset: 2);

        using var cachingClient = new LiveSegmentCachingNntpClient(fakeNntpClient, liveCache);
        using var warmingService = new ReadAheadWarmingService(cachingClient, liveCache, configManager);
        using var cts = new CancellationTokenSource();
        var sessionId = warmingService.CreateSession(["segment-0", "segment-1", "segment-2"], cts.Token);

        warmingService.UpdatePosition(sessionId, 0);
        await WaitForConditionAsync(() => liveCache.HasBody("segment-0"));
        warmingService.StopSession(sessionId);
        await Task.Delay(350);

        Assert.False(liveCache.HasBody("segment-2"));
    }

    [Fact]
    public async Task DecodedBodyWithFallbackCachesSegmentOutsideWarmingLoop()
    {
        await using var cacheScope = new TempCacheScope();
        using var liveCache = new LiveSegmentCache(cacheScope.Path);
        using var fakeNntpClient = new FakeNntpClient()
            .AddSegment("segment-0", Encoding.ASCII.GetBytes("A"), partOffset: 0);
        using var cachingClient = new LiveSegmentCachingNntpClient(fakeNntpClient, liveCache);

        var response = await cachingClient.DecodedBodyWithFallbackAsync("segment-0", CancellationToken.None);
        await using (response.Stream)
        {
        }

        Assert.True(liveCache.HasBody("segment-0"));
        Assert.Equal(1, fakeNntpClient.DecodedBodyCallCount);
    }

    [Fact]
    public async Task CreateSessionBeginsFetchingSegments()
    {
        await using var cacheScope = new TempCacheScope();
        var configManager = new ConfigManager();
        configManager.UpdateValues(
        [
            new ConfigItem { ConfigName = "cache.read-ahead-enable", ConfigValue = "true" },
            new ConfigItem { ConfigName = "cache.read-ahead-segments", ConfigValue = "2" }
        ]);

        using var liveCache = new LiveSegmentCache(cacheScope.Path);
        using var fakeNntpClient = new FakeNntpClient
        {
            BodyFetchDelay = TimeSpan.FromMilliseconds(50)
        };
        fakeNntpClient
            .AddSegment("segment-0", Encoding.ASCII.GetBytes("A"), partOffset: 0)
            .AddSegment("segment-1", Encoding.ASCII.GetBytes("B"), partOffset: 1);
        using var cachingClient = new LiveSegmentCachingNntpClient(fakeNntpClient, liveCache);
        using var warmingService = new ReadAheadWarmingService(cachingClient, liveCache, configManager);
        using var cts = new CancellationTokenSource();

        warmingService.CreateSession(["segment-0", "segment-1"], cts.Token);
        await Task.Delay(300);

        Assert.True(fakeNntpClient.DecodedBodyCallCount > 0);
    }

    [Fact]
    public async Task DecodedBodyWithFallbackCachesSegmentWithVideoContext()
    {
        await using var cacheScope = new TempCacheScope();
        using var liveCache = new LiveSegmentCache(cacheScope.Path);
        using var fakeNntpClient = new FakeNntpClient()
            .AddSegment("segment-0", Encoding.ASCII.GetBytes("A"), partOffset: 0);
        using var cachingClient = new LiveSegmentCachingNntpClient(fakeNntpClient, liveCache);
        using var ctx = SegmentFetchContext.Set(SegmentCategory.VideoSegment);

        var response = await cachingClient.DecodedBodyWithFallbackAsync("segment-0", CancellationToken.None);
        await using (response.Stream)
        {
        }

        Assert.True(liveCache.HasBody("segment-0"));
    }

    [Fact]
    public async Task CreateSessionCachesInitialSegmentWithoutPositionUpdates()
    {
        await using var cacheScope = new TempCacheScope();
        var configManager = new ConfigManager();
        configManager.UpdateValues(
        [
            new ConfigItem { ConfigName = "cache.read-ahead-enable", ConfigValue = "true" },
            new ConfigItem { ConfigName = "cache.read-ahead-segments", ConfigValue = "2" }
        ]);

        using var liveCache = new LiveSegmentCache(cacheScope.Path);
        using var fakeNntpClient = new FakeNntpClient
        {
            BodyFetchDelay = TimeSpan.FromMilliseconds(50)
        };
        fakeNntpClient
            .AddSegment("segment-0", Encoding.ASCII.GetBytes("A"), partOffset: 0)
            .AddSegment("segment-1", Encoding.ASCII.GetBytes("B"), partOffset: 1);
        using var cachingClient = new LiveSegmentCachingNntpClient(fakeNntpClient, liveCache);
        using var warmingService = new ReadAheadWarmingService(cachingClient, liveCache, configManager);
        using var cts = new CancellationTokenSource();

        warmingService.CreateSession(["segment-0", "segment-1"], cts.Token);
        await Task.Delay(300);

        Assert.True(liveCache.HasBody("segment-0"));
    }

    [Fact]
    public void ActiveSessionCount_TracksOpenSessions()
    {
        var configManager = new ConfigManager();
        configManager.UpdateValues(
        [
            new ConfigItem { ConfigName = "cache.read-ahead-enable", ConfigValue = "true" }
        ]);

        using var liveCache = new LiveSegmentCache(System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        using var fakeNntpClient = new FakeNntpClient();
        using var cachingClient = new LiveSegmentCachingNntpClient(fakeNntpClient, liveCache);
        using var warmingService = new ReadAheadWarmingService(cachingClient, liveCache, configManager);
        using var cts = new CancellationTokenSource();

        var sessionId = warmingService.CreateSession(["segment-0"], cts.Token);
        Assert.Equal(1, warmingService.ActiveSessionCount);

        warmingService.StopSession(sessionId);
        Assert.Equal(0, warmingService.ActiveSessionCount);
    }

    [Fact]
    public async Task StopSession_DoesNotFaultBackgroundWarmingTask()
    {
        await using var cacheScope = new TempCacheScope();
        var configManager = new ConfigManager();
        configManager.UpdateValues(
        [
            new ConfigItem { ConfigName = "cache.read-ahead-enable", ConfigValue = "true" },
            new ConfigItem { ConfigName = "cache.read-ahead-segments", ConfigValue = "0" }
        ]);

        using var liveCache = new LiveSegmentCache(cacheScope.Path);
        using var fakeNntpClient = new FakeNntpClient();
        using var cachingClient = new LiveSegmentCachingNntpClient(fakeNntpClient, liveCache);
        using var warmingService = new ReadAheadWarmingService(cachingClient, liveCache, configManager);
        using var cts = new CancellationTokenSource();

        var sessionId = warmingService.CreateSession(["segment-0"], cts.Token);
        var warmingTask = GetWarmingTask(warmingService, sessionId);

        await Task.Delay(100);
        warmingService.StopSession(sessionId);
        await warmingTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(warmingTask.IsFaulted);
    }

    [Fact]
    public async Task StopSession_DuringFetchDoesNotFaultBackgroundWarmingTask()
    {
        await using var cacheScope = new TempCacheScope();
        var configManager = new ConfigManager();
        configManager.UpdateValues(
        [
            new ConfigItem { ConfigName = "cache.read-ahead-enable", ConfigValue = "true" },
            new ConfigItem { ConfigName = "cache.read-ahead-segments", ConfigValue = "1" }
        ]);

        using var liveCache = new LiveSegmentCache(cacheScope.Path);
        using var fakeNntpClient = new FakeNntpClient
        {
            BodyFetchDelay = TimeSpan.FromMilliseconds(500)
        };
        fakeNntpClient.AddSegment("segment-0", Encoding.ASCII.GetBytes("A"), partOffset: 0);
        using var cachingClient = new LiveSegmentCachingNntpClient(fakeNntpClient, liveCache);
        using var warmingService = new ReadAheadWarmingService(cachingClient, liveCache, configManager);
        using var cts = new CancellationTokenSource();

        var sessionId = warmingService.CreateSession(["segment-0"], cts.Token);
        var warmingTask = GetWarmingTask(warmingService, sessionId);

        await WaitForConditionAsync(() => fakeNntpClient.DecodedBodyCallCount > 0);
        warmingService.StopSession(sessionId);
        await warmingTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(warmingTask.IsFaulted);
    }

    private static async Task WaitForConditionAsync(Func<bool> condition)
    {
        var timeoutAt = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < timeoutAt)
        {
            if (condition()) return;
            await Task.Delay(25);
        }

        Assert.True(condition(), "Timed out waiting for condition.");
    }

    private static Task GetWarmingTask(ReadAheadWarmingService warmingService, string sessionId)
    {
        var sessionsField = typeof(ReadAheadWarmingService)
            .GetField("_sessions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var sessions = sessionsField?.GetValue(warmingService);
        if (sessions == null)
            throw new InvalidOperationException("Could not access warming session.");

        var tryGetValue = sessions.GetType().GetMethod("TryGetValue");
        if (tryGetValue == null)
            throw new InvalidOperationException("Could not inspect warming sessions.");

        var args = new object?[] { sessionId, null };
        var found = (bool)(tryGetValue.Invoke(sessions, args) ?? false);
        if (!found || args[1] == null)
            throw new InvalidOperationException("Could not access warming session.");

        var session = args[1]!;

        var taskProperty = session.GetType()
            .GetProperty("WarmingTask", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
        return taskProperty?.GetValue(session) as Task
               ?? throw new InvalidOperationException("Could not access warming task.");
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
                    // Best-effort temp cleanup.
                }
            }

            return ValueTask.CompletedTask;
        }
    }
}
