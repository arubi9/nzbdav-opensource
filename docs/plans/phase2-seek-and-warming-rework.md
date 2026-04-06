# Phase 2: Seek Behavior & Read-Ahead Warming Rework

Architectural changes to eliminate wasted prefetch work during seeks and make read-ahead warming position-aware.

**Bottlenecks addressed:** #2, #8
**Depends on:** Phase 1 (specifically Fix 2 — non-blocking MultiSegmentStream dispose)

---

## Fix 1: Seek-Resilient NzbFileStream

### Problem
`NzbFileStream.Seek()` disposes the inner `MultiSegmentStream`, destroying all prefetched segments. Jellyfin does 3-5 seeks per file during media probing (beginning, middle, end), and each seek:
1. Blocks waiting for in-flight downloads to cancel (Phase 1 Fix 2 mitigates this)
2. Discards all buffered segment data
3. Creates a brand new `MultiSegmentStream` from scratch
4. The new stream must re-fetch segments from NNTP (or hit `LiveSegmentCache`)

### Files
- `backend/Streams/NzbFileStream.cs` — main change
- `backend/Streams/MultiSegmentStream.cs` — minor interface addition

### Design

The key insight: **seeks within `LiveSegmentCache`-cached ranges are essentially free**. The cache already has the decoded segment data on disk. The expensive part is creating a new `MultiSegmentStream` that starts prefetching from NNTP when the segments are already cached.

**Approach: Cache-aware seek with lazy stream recreation**

Instead of immediately creating a new `MultiSegmentStream` on seek, defer stream creation to the next `ReadAsync()` call. When `ReadAsync` is called after a seek:

1. Determine which segment index corresponds to the seek position
2. Check if that segment (and the next few) are in `LiveSegmentCache`
3. If cached: read directly from cache without creating a `MultiSegmentStream`
4. If not cached: create a new `MultiSegmentStream` starting from the seek segment index

**Changes to `NzbFileStream`:**

```csharp
// Current Seek implementation:
public override long Seek(long offset, SeekOrigin origin)
{
    var absoluteOffset = origin == SeekOrigin.Begin ? offset
        : origin == SeekOrigin.Current ? _position + offset
        : throw new InvalidOperationException("SeekOrigin must be Begin or Current.");
    if (_position == absoluteOffset) return _position;
    _position = absoluteOffset;
    _innerStream?.Dispose();   // ← expensive, wasteful
    _innerStream = null;
    return _position;
}

// New Seek implementation:
public override long Seek(long offset, SeekOrigin origin)
{
    var absoluteOffset = origin == SeekOrigin.Begin ? offset
        : origin == SeekOrigin.Current ? _position + offset
        : throw new InvalidOperationException("SeekOrigin must be Begin or Current.");
    if (_position == absoluteOffset) return _position;
    _position = absoluteOffset;
    _seekPending = true;       // ← mark seek, defer stream recreation
    return _position;
}
```

**New `ReadAsync` with seek handling:**

```csharp
public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
{
    if (_position >= fileSize) return 0;

    if (_seekPending)
    {
        _seekPending = false;
        // Dispose old stream in background (non-blocking after Phase 1 Fix 2)
        _innerStream?.Dispose();
        _innerStream = null;
        _innerStream = await GetFileStream(_position, cancellationToken).ConfigureAwait(false);
    }

    _innerStream ??= await GetFileStream(_position, cancellationToken).ConfigureAwait(false);
    var read = await _innerStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    _position += read;
    return read;
}
```

This is a minimal change that:
- Makes `Seek()` itself non-blocking (no dispose, just sets a flag)
- Defers expensive work to `ReadAsync` where we're already in an async context
- The old stream is disposed in `ReadAsync` which can use `DisposeAsync` properly

### Advanced optimization (optional, can be deferred)

For Jellyfin's media probe pattern (seek to beginning, seek to middle, seek to end, then seek to playback position), most probed segments will be in `LiveSegmentCache` if read-ahead warming already ran. A more aggressive optimization:

Add a `ReadFromCacheOnly` path that reads directly from `LiveSegmentCache` without creating a `MultiSegmentStream` at all, for the common case where probed segments are cached.

This requires `NzbFileStream` to have access to `LiveSegmentCache`, which it currently doesn't. This can be added later if profiling shows seek-after-cache-hit is still slow.

### Verification
- Existing `LiveSegmentCachingNntpClientTests` should pass
- New test: seek to multiple positions, verify no segments are re-fetched from underlying client if already cached
- Integration: stream a video in Jellyfin, verify no buffering on seek

---

## Fix 2: Seek-Aware Read-Ahead Warming

### Problem
`ReadAheadWarmingService.StartWarming()` always starts warming from the segment index passed by the caller. The WebDAV store files always pass `startIndex: 0`. When a user seeks to the middle of a movie, the warming service is prefetching the first N segments — completely useless.

### Files
- `backend/Services/ReadAheadWarmingService.cs` — main change
- `backend/WebDav/DatabaseStoreNzbFile.cs` — update warming lifecycle
- `backend/WebDav/DatabaseStoreRarFile.cs` — update warming lifecycle
- `backend/WebDav/DatabaseStoreMultipartFile.cs` — update warming lifecycle

### Design

**Approach: Replace static warming with position-tracking warming**

Instead of fire-and-forget warming from index 0, the warming service should track the current read position and warm ahead of it.

**New `ReadAheadWarmingService` API:**

```csharp
public class ReadAheadWarmingService
{
    // Replace StartWarming with a session that tracks position
    public string CreateSession(string[] segmentIds, CancellationToken ct);
    public void UpdatePosition(string sessionId, int currentSegmentIndex);
    public void StopSession(string sessionId);
}
```

**WarmingSession internal logic:**

```csharp
private sealed class WarmingSession
{
    public string SessionId { get; }
    public string[] SegmentIds { get; }
    public CancellationTokenSource Cts { get; }
    private int _currentPosition;        // updated by consumer
    private int _warmingPosition;        // current warming cursor
    private readonly SemaphoreSlim _positionChanged = new(0);

    public void UpdatePosition(int segmentIndex)
    {
        Interlocked.Exchange(ref _currentPosition, segmentIndex);
        _positionChanged.Release();  // wake up warming loop if it's paused
    }
}
```

**Warming loop logic:**

```csharp
private async Task WarmAsync(WarmingSession session, CancellationToken ct)
{
    var maxAhead = configManager.GetReadAheadSegments();

    while (!ct.IsCancellationRequested)
    {
        var currentPos = session.CurrentPosition;
        var targetEnd = Math.Min(currentPos + maxAhead, session.SegmentIds.Length);

        // Warm from current position forward
        for (var i = currentPos; i < targetEnd && !ct.IsCancellationRequested; i++)
        {
            // If consumer jumped ahead of us, restart from new position
            if (session.CurrentPosition > i + maxAhead / 2)
                break;

            var segmentId = session.SegmentIds[i];
            if (liveSegmentCache.HasBody(segmentId))
                continue;

            try
            {
                using var ctx = SegmentFetchContext.Set(SegmentCategory.VideoSegment);
                var response = await usenetClient
                    .DecodedBodyWithFallbackAsync(segmentId, ct)
                    .ConfigureAwait(false);
                await response.Stream.DisposeAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch { /* log and continue */ }
        }

        // Wait for position update or timeout
        try
        {
            await session.PositionChanged.WaitAsync(TimeSpan.FromSeconds(5), ct);
        }
        catch (OperationCanceledException) { break; }
    }
}
```

**How position updates flow:**

The WebDAV stores currently create the warming session when `GetStreamAsync` is called. To update position, the stream needs to notify the warming service as segments are consumed.

**Option A (simpler): Use `NzbFileStream` seek position**
- When `NzbFileStream.Seek()` is called, calculate the segment index from byte offset
- Call `warmingService.UpdatePosition(sessionId, segmentIndex)`
- This requires passing the warming session ID through to `NzbFileStream`

**Option B (recommended): Use a callback on MultiSegmentStream consumption**
- `MultiSegmentStream` already tracks `_consumedSegments` (line 166)
- Add an `Action<int>` callback that fires on each segment consumed
- The WebDAV store wires this callback to `warmingService.UpdatePosition()`

**Implementation of Option B:**

In `MultiSegmentStream`, add to constructor:
```csharp
private readonly Action<int>? _onSegmentConsumedCallback;
```

In `OnSegmentConsumed()`:
```csharp
private void OnSegmentConsumed()
{
    ReleaseWindowPermit();
    var consumed = Interlocked.Increment(ref _consumedSegments);
    _onSegmentConsumedCallback?.Invoke(consumed);
    // ... existing ramp logic
}
```

In the WebDAV stores, when creating the stream with warming:
```csharp
var sessionId = warmingService.CreateSession(file.SegmentIds, cancellationToken);
var stream = usenetClient.GetFileStream(
    file.SegmentIds, FileSize,
    StreamingBufferSettings.LiveDefault(configManager.GetArticleBufferSize()),
    onSegmentConsumed: index => warmingService.UpdatePosition(sessionId, index)
);
```

This requires threading the callback through `GetFileStream` → `NzbFileStream` → `MultiSegmentStream`. The callback is optional (`Action<int>?`) so existing callers are unaffected.

### Verification
- Unit test: create a warming session, update position to middle, verify warming skips early segments
- Unit test: verify warming stops when session is closed
- Integration: seek to middle of video, verify read-ahead warms from seek position

---

## Implementation Order

1. Fix 1 (seek-resilient NzbFileStream) — core improvement, safe change
2. Fix 2 (position-aware warming) — builds on Fix 1, requires callback plumbing

## Testing
```bash
dotnet test backend.Tests/backend.Tests.csproj
```
Plus manual integration test: stream video through Jellyfin, seek to multiple positions, verify no buffering delays.
