using System.Collections.Concurrent;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Caching;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Config;
using NzbWebDAV.Extensions;
using Serilog;

namespace NzbWebDAV.Services;

public class ReadAheadWarmingService : IDisposable
{
    private static readonly TimeSpan InitialPositionGracePeriod = TimeSpan.FromMilliseconds(50);

    private readonly INntpClient _usenetClient;
    private readonly LiveSegmentCache _liveSegmentCache;
    private readonly ConfigManager _configManager;
    private readonly ConcurrentDictionary<string, WarmingSession> _sessions = new();

    public int ActiveSessionCount => _sessions.Count;

    public ReadAheadWarmingService(
        UsenetStreamingClient usenetClient,
        LiveSegmentCache liveSegmentCache,
        ConfigManager configManager
    ) : this((INntpClient)usenetClient, liveSegmentCache, configManager)
    {
    }

    public ReadAheadWarmingService(
        INntpClient usenetClient,
        LiveSegmentCache liveSegmentCache,
        ConfigManager configManager
    )
    {
        _usenetClient = usenetClient;
        _liveSegmentCache = liveSegmentCache;
        _configManager = configManager;
    }

    public string CreateSession(string[] segmentIds, CancellationToken ct)
    {
        if (!_configManager.IsReadAheadEnabled())
            return string.Empty;

        var sessionId = Guid.NewGuid().ToString("N");
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var session = new WarmingSession(sessionId, segmentIds, cts);
        session.WarmingTask = Task.Run(() => WarmSegmentsAsync(session, cts.Token), cts.Token);
        _sessions.TryAdd(sessionId, session);
        return sessionId;
    }

    public void UpdatePosition(string sessionId, int currentSegmentIndex)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        if (!_sessions.TryGetValue(sessionId, out var session)) return;
        session.UpdatePosition(currentSegmentIndex);
    }

    public void StopSession(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        if (!_sessions.TryRemove(sessionId, out var session)) return;

        try
        {
            session.CancellationTokenSource.Cancel();
            _ = Task.Run(() => CleanupSessionAsync(session), CancellationToken.None);
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    private static async Task CleanupSessionAsync(WarmingSession session)
    {
        try
        {
            if (session.WarmingTask != null)
                await session.WarmingTask.ConfigureAwait(false);
        }
        catch
        {
            // Best effort cleanup; cancellation/faults should not leak resources.
        }
        finally
        {
            session.CancellationTokenSource.Dispose();
            session.PositionChanged.Dispose();
        }
    }

    private async Task WarmSegmentsAsync(WarmingSession session, CancellationToken ct)
    {
        var maxSegments = _configManager.GetReadAheadSegments();
        var warmConcurrency = _configManager.GetReadAheadConcurrency();

        try
        {
            try
            {
                await session.PositionChanged
                    .WaitAsync(InitialPositionGracePeriod, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            while (!ct.IsCancellationRequested)
            {
                var currentPosition = session.CurrentPosition;
                var targetEnd = Math.Min(currentPosition + maxSegments, session.SegmentIds.Length);

                // Warm the window in parallel. A strictly sequential loop here
                // held exactly one NNTP connection open no matter how many the
                // pool allowed, so the prefetcher could never outrun the reader
                // and the connection pool sat mostly idle during playback.
                // SegmentFetchContext is an ambient scope, so it must be set
                // inside the body: a scope opened outside Parallel.ForEachAsync
                // does not flow into the per-item execution contexts.
                var warmFailures = 0;
                try
                {
                    await Parallel.ForEachAsync(
                        Enumerable.Range(currentPosition, Math.Max(0, targetEnd - currentPosition)),
                        new ParallelOptions
                        {
                            MaxDegreeOfParallelism = warmConcurrency,
                            CancellationToken = ct,
                        },
                        async (i, innerCt) =>
                        {
                            // Honour a stopped session promptly: queued items
                            // must not start a fetch after cancellation.
                            if (innerCt.IsCancellationRequested)
                                return;

                            // Playback has already moved past this segment, so
                            // warming it is wasted bandwidth.
                            if (session.CurrentPosition > i + Math.Max(1, maxSegments / 2))
                                return;

                            var segmentId = session.SegmentIds[i];
                            if (_liveSegmentCache.HasBody(segmentId))
                                return;

                            try
                            {
                                using var ctx = SegmentFetchContext.Set(SegmentCategory.VideoSegment);

                                // Prefetch is speculative, so it must never
                                // outrank somebody's actual playback. An unset
                                // context defaults to High (DownloadingNntpClient
                                // line 108), which let one viewer's read-ahead
                                // crowd another viewer's live reads out of the
                                // shared connection pool.
                                using var priority = innerCt.SetContext(
                                    new DownloadPriorityContext { Priority = SemaphorePriority.Low });

                                var response = await _usenetClient
                                    .DecodedBodyWithFallbackAsync(segmentId, innerCt)
                                    .ConfigureAwait(false);
                                await response.Stream.DisposeAsync().ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                            }
                            catch (Exception e)
                            {
                                Interlocked.Increment(ref warmFailures);
                                Log.Debug($"Read-ahead warming failed for segment: {e.Message}");
                            }
                        }).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }

                // A read-ahead that fails for every segment silently degrades
                // playback to synchronous NNTP fetches, so surface it above the
                // production log level instead of only at Debug.
                if (warmFailures > 0)
                    Log.Warning("Read-ahead warming failed for {Failures} segment(s) in session {Session}",
                        warmFailures, session.SessionId);

                try
                {
                    await session.PositionChanged.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation on stream close.
        }
    }

    public void Dispose()
    {
        foreach (var sessionId in _sessions.Keys.ToArray())
            StopSession(sessionId);

        _sessions.Clear();
        GC.SuppressFinalize(this);
    }

    private sealed class WarmingSession
    {
        private int _currentPosition;

        public WarmingSession(
            string sessionId,
            string[] segmentIds,
            CancellationTokenSource cancellationTokenSource
        )
        {
            SessionId = sessionId;
            SegmentIds = segmentIds;
            CancellationTokenSource = cancellationTokenSource;
            PositionChanged = new SemaphoreSlim(0, 1);
        }

        public string SessionId { get; }
        public string[] SegmentIds { get; }
        public CancellationTokenSource CancellationTokenSource { get; }
        public SemaphoreSlim PositionChanged { get; }
        public Task? WarmingTask { get; set; }
        public int CurrentPosition => Volatile.Read(ref _currentPosition);

        public void UpdatePosition(int currentSegmentIndex)
        {
            Interlocked.Exchange(ref _currentPosition, currentSegmentIndex);
            try
            {
                PositionChanged.Release();
            }
            catch (SemaphoreFullException)
            {
                // Another position update is already pending.
            }
        }
    }
}
