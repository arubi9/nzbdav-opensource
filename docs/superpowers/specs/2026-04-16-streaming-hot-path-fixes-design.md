# Streaming Hot-Path Fixes — Design

**Date:** 2026-04-16
**Status:** Approved
**Scope:** Two targeted perf fixes that reduce contention during concurrent streaming.

## Problem

Post-audit of recent streaming-reliability commits identified two remaining hot-path issues:

### Issue 1 — `PruneAsync` serializes concurrent cache misses

`LiveSegmentCache.GetOrAddBodyAsync` awaits `PruneAsync` before returning the
`BodyFetchResult` to the caller (`backend/Clients/Usenet/Caching/LiveSegmentCache.cs:285`).
`PruneAsync` acquires `_pruneLock = new SemaphoreSlim(1, 1)` and always runs at
least one O(N) scan of `_cachedSegments.ToArray()` to remove expired entries
(lines 341–346) regardless of byte pressure. Under concurrent miss load (multiple
streams, read-ahead warming), every miss pays the scan cost and serializes
through the single lock.

### Issue 2 — Probe backfill contends at `High` priority

`MediaProbeService.BackfillMissingProbes`
(`backend/Services/MediaProbeService.cs:82`) runs once per node startup and
issues `DecodedBodyWithFallbackAsync` calls (lines 187, 204) without setting a
`DownloadPriorityContext` on the cancellation token. `DownloadingNntpClient.cs:108`
defaults missing context to `SemaphorePriority.High`, so backfill competes with
live streams for the same high-priority NNTP slots for the duration of the
first-time backfill (hours on large libraries).

Live streams set `High` in `BaseStoreStreamFile.cs:14`. Ingest correctly sets
`Low` in `QueueItemProcessor.cs:108`. Backfill should match ingest.

## Goals

- Remove `PruneAsync` from the cache-miss synchronous return path.
- Run `PruneAsync` on a 30s background timer (best-effort; cache size is a soft
  limit on disk).
- Make probe backfill run at `SemaphorePriority.Low` so the existing
  80/20 priority-odds starvation guard in `PrioritizedSemaphore` keeps live
  streams responsive during backfill.

## Non-Goals

- No change to `PruneAsync`'s four-pass eviction logic.
- No pause/resume coordination for backfill based on active streams. 80/20
  odds already prevent starvation.
- No change to the ffprobe-over-HTTP segment reads inside
  `ProbeAndCacheFile`. Those requests flow through `BaseStoreStreamFile`
  which sets `High`; we only lower the direct first/last-segment prefetch
  calls in `BackfillMissingProbes`. Prefetch is the bulk of backfill
  contention; ffprobe's own reads remain `High` and are rare per item.

## Design

### Fix 1 — Background prune loop in `LiveSegmentCache`

Add a background prune loop that mirrors the existing `ConnectionPool.SweepLoop`
pattern.

**New fields:**
```csharp
private readonly CancellationTokenSource _pruneCts = new();
private readonly Task _pruneLoopTask;
```

**Constructor:** after `InitializeAndRehydrate()`, start the loop:
```csharp
_pruneLoopTask = Task.Run(PruneLoopAsync);
```

**Loop body:**
```csharp
private async Task PruneLoopAsync()
{
    try
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(_pruneCts.Token).ConfigureAwait(false))
        {
            try
            {
                await PruneAsync(_pruneCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_pruneCts.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                Log.Warning(e, "LiveSegmentCache prune loop iteration failed");
            }
        }
    }
    catch (OperationCanceledException) { }
}
```

**Hot path change — `GetOrAddBodyAsync`:**
Delete line 285:
```csharp
await PruneAsync(cancellationToken).ConfigureAwait(false);
```

The miss path returns `BodyFetchResult` immediately after `OpenBodyResponse`.

**Dispose:**
```csharp
public void Dispose()
{
    if (_disposed) return;
    _disposed = true;
    _pruneCts.Cancel();
    try { _pruneLoopTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
    _pruneCts.Dispose();
    _headerCache.Dispose();
    _pruneLock.Dispose();
    GC.SuppressFinalize(this);
}
```

**Keep unchanged:** the two existing `_ = Task.Run(() => PruneAsync())` calls
(lines 153, 730) that fire on config-size change and post-rehydration.
`_pruneLock` serializes them against the background loop safely.

**Behavior:** misses return in O(1) work after body fetch. Cache can overshoot
budget by ≤30s worth of misses. For a 20 GB cache at 100 misses/s (each ~700 KB
= 70 MB/s), 30s overshoot = ~2 GB = 10% of budget. Acceptable for a disk cache.

### Fix 2 — `DownloadPriorityContext { Priority = Low }` on backfill

Wrap the backfill cancellation token with a `DownloadPriorityContext` before
entering the foreach, using the same `SetContext`/scope pattern as
`BaseStoreStreamFile.cs:14-18`.

**Change in `MediaProbeService.BackfillMissingProbes`**, at the top of the
try block after acquiring `missing`:
```csharp
var priorityContext = new DownloadPriorityContext { Priority = SemaphorePriority.Low };
using var priorityScope = ct.SetContext(priorityContext);
var priorityCt = priorityScope.Token;

foreach (var item in missing)
{
    priorityCt.ThrowIfCancellationRequested();
    try
    {
        await ProbeAndCacheFile(new DavDatabaseContext(), item, priorityCt).ConfigureAwait(false);
        // ...
    }
    // ...
}
```

(Exact API name — `SetContext` vs `CancellationTokenContext.SetContext` — to be
confirmed during implementation by reading `BaseStoreStreamFile.cs`.)

Context flows via `AsyncLocal` through
`DownloadingNntpClient.AcquireExclusiveConnectionAsync`
(`backend/Clients/Usenet/DownloadingNntpClient.cs:107-108`), which reads
`cancellationToken.GetContext<DownloadPriorityContext>()`.

Known limitation (accepted): the ffprobe subprocess inside `ProbeAndCacheFile`
hits `/api/stream/{id}` over HTTP; those segment fetches flow through
`BaseStoreStreamFile.cs:14` which resets priority to `High`. Only the direct
first/last-segment prefetch calls (lines 187, 204 in `MediaProbeService`) are
affected by this fix. That still eliminates the bulk of backfill segment
pressure because prefetch fires for every backfilled item whereas ffprobe
reads are shorter and rarer.

## Testing

### Fix 1

- **`LiveSegmentCacheTests.PruneLoop_RemovesExpiredEntries_WithoutExplicitCall`**
  Construct cache, add entries, advance time past `_maxAge`, sleep ~35 seconds
  (real clock; no injected clock today), assert `_cachedSegments` count drops
  and `Evictions` counter increases.
- **`LiveSegmentCacheTests.GetOrAddBodyAsync_DoesNotBlockOnPruneLock`**
  Externally acquire `_pruneLock` (via reflection or by starting a long
  `PruneAsync` with a blocking fetch factory), then call `GetOrAddBodyAsync`
  for a new segment and assert it completes in <100 ms. Prior behavior would
  have blocked until the prune released.
- Existing `PruneAsync` direct-invocation tests remain green (behavior of
  `PruneAsync` itself unchanged).

### Fix 2

- **`MediaProbeServiceTests.BackfillMissingProbes_UsesLowPriority`**
  Wire a `MediaProbeService` with a recording fake `UsenetStreamingClient`
  that captures the `DownloadPriorityContext.Priority` visible inside
  `DecodedBodyWithFallbackAsync` (via
  `ct.GetContext<DownloadPriorityContext>()`). Run the backfill loop with
  one missing item and assert the captured priority is
  `SemaphorePriority.Low`.
- **Regression guard — `BaseStoreStreamFileTests` (if present) or new test:**
  Live-stream read observes `SemaphorePriority.High`. This guards against a
  future change that accidentally lowers live streams.

## Files Changed

- `backend/Clients/Usenet/Caching/LiveSegmentCache.cs` — add prune loop, remove
  hot-path prune, update `Dispose`.
- `backend/Services/MediaProbeService.cs` — wrap backfill ct with Low priority
  context.
- `backend.Tests/Clients/Usenet/Caching/LiveSegmentCacheTests.cs` — 2 new tests.
- `backend.Tests/Services/MediaProbeServiceTests.cs` — 1 new test (create file
  if absent).
- `backend.Tests/WebDav/BaseStoreStreamFileTests.cs` — 1 regression test
  (create file if absent; if too invasive, replaced by assertion on
  `DownloadPriorityContext` flow inside an existing streaming test).

## Risk & Rollout

- Both changes are behavior-preserving under single-user load.
- Fix 1 risk: prune loop exception swallowed → silent unbounded growth. Mitigated
  by `Log.Warning` on loop-iteration failures and by the config-change/
  rehydration `Task.Run(() => PruneAsync())` paths continuing to work.
- Fix 2 risk: ingest/backfill starvation if live streams saturate at `High`.
  Existing 80/20 priority odds (`HighPriorityOdds = 80`) guarantee `Low`
  waiters receive ~20% of releases — same guarantee ingest already relies on.
- No DB, schema, or API contract changes. No config keys added.
- Single deployable commit.
