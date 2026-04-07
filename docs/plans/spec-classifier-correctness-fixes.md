# Spec: Classifier Correctness Fixes

*Approved 2026-04-06*

The previous classifier implementation didn't actually wire warming/cache to classification — it used closures and AsyncLocal mutation that don't propagate correctly. This spec restructures classification to live inside `NzbFileStream` where it has direct access to all I/O signals.

---

## Root Cause

Both C1 (warming not classification-driven) and C2 (AsyncLocal mutation fragile) share one cause: classification was observed in `NzbFileStream` but the side effects (warming start, cache tier upgrade) lived in `DatabaseStoreNzbFile` closures that ran *before* `NzbFileStream` could classify anything. Classification, warming, and cache tier all need to live in the same place.

## Approach

Move warming session ownership and cache tier control INTO `NzbFileStream`. The store file passes a `WarmingHook` and a `SegmentFetchContext` reference; `NzbFileStream` calls them when classification commits. This eliminates the closure timing problem and the AsyncLocal upward-flow problem.

---

## Fixes

### C1 + C2 + I10: Move warming and tier upgrade into NzbFileStream

**Change:** `NzbFileStream` accepts:
- `Action? onClassifiedAsPlayback` — invoked exactly once when classification commits to Playback
- `SegmentFetchContextHolder?` — a mutable holder reference whose `Category` field is mutated when classification commits to Playback

`SegmentFetchContext` becomes a class with mutable `Category` instead of a record. The `AsyncLocal<SegmentFetchContext>` value is the SAME instance across the call chain, so mutating its field is visible everywhere without re-setting `_current.Value`. This is the fix the reviewer suggested.

`DatabaseStoreNzbFile.GetStreamAsync` creates the stream with:
- `onClassifiedAsPlayback` callback that calls `warmingService.CreateSession(...)` and stores the session ID in a closure-captured variable
- The `SegmentFetchContext` instance is the one set via `SegmentFetchContext.Set(SmallFile, ownerId)` at the top of the method

When `NzbFileStream._classifier` commits to Playback:
1. Call `onClassifiedAsPlayback` once → starts warming
2. Mutate the SegmentFetchContext instance's Category from SmallFile → VideoSegment

### C2 fix detail: SegmentFetchContext becomes mutable

```csharp
public sealed class SegmentFetchContext
{
    public SegmentCategory Category { get; private set; }
    public Guid? OwnerNzbId { get; }

    public SegmentFetchContext(SegmentCategory category, Guid? ownerNzbId)
    {
        Category = category;
        OwnerNzbId = ownerNzbId;
    }

    public void UpgradeCategory(SegmentCategory newCategory) => Category = newCategory;
}
```

Existing `Set()` returns `IDisposable` and stores via `_current.Value = new SegmentFetchContext(...)`. The instance reference is what flows via AsyncLocal — we mutate the instance, never reassign `_current.Value` after Set().

### I7: Track sequentiality in StreamClassifier

`ObserveRead` checks if the new offset equals `_lastReadOffset` (the end of the previous read). If not sequential, decrement (or reset) the sequential counter. Commit to Playback requires 5 *sequential* reads, not just 5 reads.

### I8: Observe reads in seek-overlay path too

`NzbFileStream.ReadAsync` currently returns from the seek-overlay path without calling `_classifier.ObserveRead`. Add the call so cache-served reads are observed.

### I1: Fix manifest ETag collisions

Compute ETag from `count` + `XOR(item.Id.GetHashCode())` + `XOR(item.HasProbeData ? id : 0)`. This catches:
- Item count changes (count component)
- Item set changes via add/delete (XOR of IDs)
- Probe generation events (XOR of probed IDs)

Renames are still missed but they're rare and not catastrophic — operator can force-refresh.

### I5: Drain ffprobe stderr concurrently

Replace sequential read-then-wait with `Task.WhenAll(stdoutTask, stderrTask, waitTask)`. Prevents deadlock on stderr buffer overflow.

---

## Files Modified

| File | Change |
|------|--------|
| `backend/Streams/StreamClassifier.cs` | Track sequentiality (I7) |
| `backend/Streams/NzbFileStream.cs` | Accept warming/tier callbacks, observe overlay reads (I8), invoke callbacks on commit |
| `backend/Clients/Usenet/Caching/SegmentFetchContext.cs` | Mutable Category field, UpgradeCategory method |
| `backend/WebDav/DatabaseStoreNzbFile.cs` | Pass warming/tier callbacks to stream |
| `backend/WebDav/DatabaseStoreRarFile.cs` | *Deferred* — see note below |
| `backend/WebDav/DatabaseStoreMultipartFile.cs` | *Deferred* — see note below |
| `backend/Api/Controllers/Manifest/ManifestController.cs` | XOR-based ETag (I1) |
| `backend/Services/MediaProbeService.cs` | Concurrent stream draining (I5) |
| `backend.Tests/Streams/StreamClassifierTests.cs` | Add test for non-sequential read pattern |

**RAR/multipart deferral.** `DavMultipartFileStream` wraps multiple inner `NzbFileStream` instances that are recreated on every seek. Threading classifier callbacks through it cleanly requires idempotency tracking across inner streams. Since these stores already classify by filename (returning `VideoSegment` for video files), the cache-tier correctness concern is much smaller — everything is already cached at playback tier. The probe-window bandwidth cost is a separate concern tracked in follow-up work.
