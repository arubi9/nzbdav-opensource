# Spec: StreamClassifier + Probe Segment Stickiness

*Approved 2026-04-06*

---

## Problem

Every `/api/stream/{id}` request gets identical treatment: full warming session, high-priority NNTP connections, VideoSegment cache tier. This is correct for playback but destructive for FFmpeg probes — which read <1MB, seek to end-of-file, and close. During a Jellyfin library scan of 1000 movies, this creates 1000 wasted warming sessions, 2000 NNTP fetches competing with active playback, and 1.5GB of probe data in the evictable VideoSegment tier.

## Solution

A per-stream `StreamClassifier` struct that observes read/seek patterns and classifies the stream as `Probe` or `Playback`. The classification controls warming (suppressed for probes) and cache tier (SmallFile for probes, VideoSegment for playback).

---

## StreamClassifier

**Type:** `readonly struct` (value type, no heap allocation, owned by `NzbFileStream`)

**State:** Tracks read count, last read offset, whether a large seek has occurred.

**API:**
```
StreamClassifier(RequestHint initialHint, long fileSize)
void ObserveRead(long offset, int length)
void ObserveSeek(long fromOffset, long toOffset)
StreamClassification Classification { get; }
```

**Enum values:**
```
StreamClassification { Unknown, Probe, Playback }
RequestHint { Unknown, SuspectedProbe, SuspectedPlayback }
```

**Commit conditions:**
- **Commit to Probe:** A seek that jumps more than 50% of file length within the first 5 reads.
- **Commit to Playback:** 5 sequential reads (no seek > 50% of file length) completed.
- **Default fallback:** If still Unknown after 10 reads with no large seek, commit to Playback.
- **RequestHint effect:** `SuspectedProbe` lowers the observation window — one large seek confirms Probe immediately (no 5-read wait). `SuspectedPlayback` has no special effect (still requires 5 sequential reads to confirm).

**Immutability after commit:** Once classification is `Probe` or `Playback`, it never changes. The struct returns the committed value for all subsequent calls.

---

## RequestHint from HTTP layer

`StreamFileController` inspects the `Range` header before creating the stream:

- `Range: bytes=0-N` where N < 1MB → `RequestHint.SuspectedProbe`
- `Range: bytes=0-` (open-ended) or no Range header → `RequestHint.Unknown`
- Any other range → `RequestHint.Unknown`

The hint is passed through the stream creation chain: `StreamFileController` → `DatabaseStore` → `DatabaseStoreNzbFile` → `NzbFileStream` constructor.

For the WebDAV GET path (`GetAndHeadHandlerPatch`), the same logic applies using the `request.GetRange()` helper.

---

## NzbFileStream Integration

`NzbFileStream` owns a `StreamClassifier _classifier` field. It delegates:

- `ReadAsync` calls `_classifier.ObserveRead(_position, bytesRead)` after each successful read
- `Seek` calls `_classifier.ObserveSeek(oldPosition, newPosition)` before performing the seek
- Exposes `public StreamClassification Classification => _classifier.Classification`

---

## Warming Service Integration

Currently, warming starts immediately in `DatabaseStoreNzbFile.GetStreamAsync()`:
```csharp
var sessionId = warmingService.StartWarming(file.SegmentIds, 0, cancellationToken);
```

**Change:** Don't start warming at stream creation. Instead, start warming lazily from `NzbFileStream.ReadAsync` when classification commits to `Playback`:

```
On each ReadAsync:
  if classification just committed to Playback AND no warming session exists:
    start warming from current segment index
```

This means:
- Probes never start warming (classified before 5 sequential reads)
- Playback starts warming after ~5 segments (~3.75MB), with warming beginning from the current position (not segment 0)
- Unknown streams get no warming until committed

The warming session ID is stored on `NzbFileStream` and stopped on dispose (same as current behavior via `DisposableCallbackStream`).

---

## Cache Tier Integration

Currently, `SegmentFetchContext.Set(category, ownerId)` is called in the WebDAV store files before the stream is created. The category is determined by file extension via `SegmentCategoryClassifier.Classify()`.

**Change:** The cache tier should also consider the stream classification:

- `Classification == Probe` → `SegmentCategory.SmallFile` (sticky, evicted last)
- `Classification == Playback` → `SegmentCategory.VideoSegment` (evicted first)
- `Classification == Unknown` → `SegmentCategory.SmallFile` (conservative default — Option 1)

**Implementation:** `SegmentFetchContext` is `AsyncLocal`, set before the stream pipeline runs. The initial context is set by the WebDAV store based on file extension (existing behavior). When `StreamClassifier` commits to `Playback`, update the `AsyncLocal` context to `VideoSegment`. Since `AsyncLocal` flows with the async context, this affects all subsequent segment fetches for that stream.

For the Unknown → SmallFile default: the initial `SegmentFetchContext` is set to `SmallFile` for video files instead of the current `VideoSegment`. When classification commits to `Playback`, it's updated to `VideoSegment`. This means pre-classification segments are sticky (acceptable cost: 2-3 segments × 750KB = ~2.25MB per stream, and they're the first segments which are needed on every playback start anyway).

---

## What This Changes in Existing Code

| File | Change |
|------|--------|
| Create: `backend/Streams/StreamClassifier.cs` | New struct |
| Modify: `backend/Streams/NzbFileStream.cs` | Add `_classifier` field, observe reads/seeks, lazy warming |
| Modify: `backend/WebDav/DatabaseStoreNzbFile.cs` | Pass `RequestHint`, don't start warming immediately, set initial context to SmallFile for video |
| Modify: `backend/WebDav/DatabaseStoreRarFile.cs` | Same changes |
| Modify: `backend/WebDav/DatabaseStoreMultipartFile.cs` | Same changes |
| Modify: `backend/Api/Controllers/StreamFile/StreamFileController.cs` | Extract RequestHint from Range header, pass to store |
| Modify: `backend/WebDav/Base/GetAndHeadHandlerPatch.cs` | Extract RequestHint from Range header |
| Modify: `backend/Clients/Usenet/Caching/SegmentFetchContext.cs` | Add method to update category on committed AsyncLocal |
| Test: `backend.Tests/Streams/StreamClassifierTests.cs` | Synthetic read/seek sequences |

---

## Testing

`StreamClassifierTests` — unit tests with no stream pipeline dependency:

1. **5 sequential reads → Playback:** Create classifier, call ObserveRead 5 times with sequential offsets, assert Playback
2. **Read + large seek → Probe:** Create classifier, call ObserveRead once, call ObserveSeek with >50% jump, assert Probe
3. **SuspectedProbe + one large seek → immediate Probe:** Create with SuspectedProbe hint, one seek >50%, assert Probe without needing 5 reads
4. **10 reads no seek → Playback fallback:** Create classifier, call ObserveRead 10 times, assert Playback
5. **Classification is immutable after commit:** Commit to Probe, then call ObserveRead 100 times, assert still Probe
6. **Small seeks don't trigger Probe:** Create classifier, seek <50% of file, continue sequential reads, assert Playback
