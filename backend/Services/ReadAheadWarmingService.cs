using System.Collections.Concurrent;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Caching;
using NzbWebDAV.Config;
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

                for (var i = currentPosition; i < targetEnd && !ct.IsCancellationRequested; i++)
                {
                    if (session.CurrentPosition > i + Math.Max(1, maxSegments / 2))
                        break;

                    var segmentId = session.SegmentIds[i];
                    if (_liveSegmentCache.HasBody(segmentId))
                        continue;

                    try
                    {
                        using var ctx = SegmentFetchContext.Set(SegmentCategory.VideoSegment);
                        var response = await _usenetClient
                            .DecodedBodyWithFallbackAsync(segmentId, ct)
                            .ConfigureAwait(false);
                        await response.Stream.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception e)
                    {
                        Log.Debug($"Read-ahead warming failed for segment: {e.Message}");
                    }
                }

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
