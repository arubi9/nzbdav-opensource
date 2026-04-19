# Expanded L2 prewarm policy — design

## Problem

`MediaProbeService.WarmFirstSegmentsIntoL2Async` currently warms only
**segment 0** of every video. This gives fast click-to-first-byte but
leaves all other offsets cold.

Measured impact (2026-04-17 audit):
- Click first byte: 48 ms (L1 hit on warm segment 0)
- Mid-file seek (18 GB offset): 851 ms (NNTP fetch + InterpolationSearch)
- Tail seek: 280 ms (warming helped via read-ahead)

At hundreds of concurrent viewers, any seek into uncached territory
triggers NNTP fetch, pressures the 100-conn Newshosting cap, and adds
~1 s to the scrub experience.

## Goal

Extend prewarm to cover the positions most likely to be hit:
1. **Segment 0** (current) — click-to-play
2. **Segment at ~50% offset** — scrub-to-middle
3. **Last segment** — some containers (MKV index) need tail; some codecs
   (certain HEVC profiles) read the moov atom late

## Math

- Current: 752 videos × 1 segment × 750 KB = **~560 MB** L2 footprint
- Proposed: 752 videos × 3 segments × 750 KB = **~1.65 GB** L2 footprint

L2 bucket capacity has headroom for 3× expansion. R2 / OVH S3 storage
pricing negligible at this scale ($0.015 / GB / month). Cost delta:
<$0.02 /month.

## Implementation

Refactor `WarmFirstSegmentsIntoL2Async` → `WarmVideoSegmentsIntoL2Async`
that accepts a `segmentPolicy` enum / list:

```csharp
private enum WarmOffsets
{
    FirstOnly,            // current
    FirstAndLast,         // adds tail
    FirstMiddleLast,      // proposed default
    FirstQuarterMidThreeQuarterLast,  // aggressive
}
```

Per-video logic:

```csharp
var segmentIds = await GetSegmentIds(dbContext, item, ct);
if (segmentIds.Length == 0) continue;

var offsets = policy switch
{
    FirstOnly => new[] { 0 },
    FirstAndLast when segmentIds.Length == 1 => new[] { 0 },
    FirstAndLast => new[] { 0, segmentIds.Length - 1 },
    FirstMiddleLast when segmentIds.Length <= 2 => Enumerable.Range(0, segmentIds.Length).ToArray(),
    FirstMiddleLast => new[] { 0, segmentIds.Length / 2, segmentIds.Length - 1 },
    _ => ...
};

foreach (var offset in offsets)
{
    var segId = segmentIds[offset];
    var seeded = await TrySeedL2FirstSegmentAsync(segId, ct);
    ...
}
```

## Config

New config key `cache.l2.prewarm-policy`:
- `first-only` (legacy)
- `first-and-last`
- `first-middle-last` (**new default**)
- `first-quartile-mid-threequartile-last` (opt-in, 5 segments/video = ~2.8 GB)

## Prewarm ordering

Run in deterministic pass order so a single video gets all warm targets
together — improves viewer experience if they start playing a
half-warmed video mid-backfill.

```csharp
foreach (var item in videoItems)
{
    foreach (var segId in segmentsForPolicy(item))
    {
        await TrySeedL2FirstSegmentAsync(segId, ct);
    }
}
```

Not parallel: keeps Low-priority NNTP yield behaviour intact.

## Interaction with ReadAheadWarmingService

`ReadAheadWarmingService` already prefetches the next segment during
active playback. The prewarm targets are snapshots at rest; read-ahead
handles dynamic forward motion. No conflict.

## Verification

- **Unit test:** video with 10 segments, policy `first-middle-last`
  → confirms warm-seeds segments 0, 5, 9.
- **Unit test:** video with 1 segment → warms segment 0 only, no errors.
- **Live:** backfill a library, count L2 objects per owner NZB, assert
  3 per video (or fewer for short content).
- **Perf measurement:** cold-seek latency p50 should drop from ~850 ms
  to <200 ms for scrub positions within 10% of the warmed offsets.

## Follow-up ideas (not in this spec)

- Adaptive policy based on item popularity (hot items get more warm
  points)
- Keyframe-aligned warm points using Jellyfin's ffprobe data
- Trending-item full-file prewarm on new releases
