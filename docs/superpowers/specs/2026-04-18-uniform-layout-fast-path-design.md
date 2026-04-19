# Uniform yEnc layout fast path — design

## Problem

`NzbFileStream.FetchSegmentRangeAsync` (and the shared
`InterpolationSearch` helper it invokes) finds the segment containing
a given byte offset by fetching yEnc headers of candidate segments
in a speculative parallel window. Even with radius-2 (5 concurrent
probes) optimisations, cold-seek latency includes **300-800 ms** of
header-fetch overhead before the actual segment body fetch can start.

Observed in the 2026-04-18 stress test: max TTFB 4-5 s on mid-file
cold seeks. InterpolationSearch probes account for ~30-40% of that
tail.

## Key insight

yEnc NZB postings use a **uniform** `part_size` for all segments except
the last. Modern posting tools (SABnzbd, NZBGet, newsup, etc.) always
use a constant chunk size (typically 768000 or 720000 bytes). The
variability is:
- `PartSize` (constant for indices 0..N-2)
- `LastPartSize` (index N-1, shorter)
- `SegmentCount` (N)

If we persist these three numbers per-file, the offset → segment
mapping is O(1) arithmetic with zero NNTP round-trips:

```csharp
int SegmentForByteOffset(long offset, long partSize, int segmentCount, long lastPartSize)
{
    long regularBytes = (long)(segmentCount - 1) * partSize;
    if (offset < regularBytes) return (int)(offset / partSize);
    return segmentCount - 1;
}
```

## Scope

**In scope**
1. Persist per-file yEnc layout metadata at probe time.
2. Stream path uses the metadata for O(1) offset → segment lookup.
3. Fallback to existing InterpolationSearch when layout is non-uniform
   or metadata not yet populated.
4. Metric to track how often the fast path vs fallback is used.

**Out of scope**
- Replacing `InterpolationSearch` entirely (stays as fallback).
- Populating metadata for videos that were ingested before this change
  ships — they get populated on the next probe pass.
- Rewriting the yEnc header layer.

## Data model

### DB migration

New columns on the `DavItems` table (or a dedicated sidecar table if
the team prefers — keep the data close to where it's used):

- `YencPartSize` BIGINT NULL — sampled `part_size` of segment 0
- `YencLastPartSize` BIGINT NULL — sampled `part_size` of segment N-1
- `YencSegmentCount` INTEGER NULL — `N`
- `YencLayoutUniform` BOOLEAN NULL — `true` if sampled middle segment
  matches the uniform prediction; `false` or NULL means use fallback

All four nullable so existing rows stay valid (NULL → fallback path).

### Model change

Add properties to `DavItem`:

```csharp
public long? YencPartSize { get; set; }
public long? YencLastPartSize { get; set; }
public int? YencSegmentCount { get; set; }
public bool? YencLayoutUniform { get; set; }
```

Update `DavDatabaseContext` `OnModelCreating` if configuration is needed.
Generate EF Core migration with `dotnet ef migrations add AddYencLayoutMetadata`.

## Probe-time population

`MediaProbeService.BackfillMissingProbes` already fetches segment 0's
yEnc header for `.mediainfo.json` sidecar creation and L2 prewarm.
Extend that flow to:

1. Capture `segment_0.PartSize` (already fetched).
2. Fetch `segment_{N-1}`'s yEnc header — one extra NNTP HEAD per video.
3. Fetch a middle segment (index `N/2`)'s yEnc header — one more NNTP
   HEAD. Confirms `PartSize` matches segment 0 → layout is uniform.
4. Persist the four values on the `DavItems` row.

Overhead: 2 extra NNTP HEAD calls per video during probe backfill.
At 100 conns and ~50 ms per HEAD: 752 × 2 = 1504 calls / 100 conns ×
50 ms ≈ 1 s additional probe wall time for the whole library. Trivial.

## Stream path fast path

### File: `backend/Streams/NzbFileStream.cs` (and/or wherever
`InterpolationSearch` is invoked for offset → segment lookup)

Before calling `InterpolationSearch.FindAsync(...)`, check the fast
path:

```csharp
private async Task<FetchedSegment> FetchSegmentForOffsetAsync(
    long offset, CancellationToken ct)
{
    // Fast path: uniform yEnc layout, zero probes needed.
    if (_davItem.YencLayoutUniform == true
        && _davItem.YencPartSize is { } partSize && partSize > 0
        && _davItem.YencSegmentCount is { } segCount && segCount > 0
        && _davItem.YencLastPartSize is { } lastSize)
    {
        var segmentIndex = SegmentForByteOffset(offset, partSize, segCount, lastSize);
        NzbdavMetricsCollector.IncrementYencFastPathHits();
        return await FetchSegmentByIndexAsync(segmentIndex, ct).ConfigureAwait(false);
    }

    // Fallback: speculative parallel InterpolationSearch
    NzbdavMetricsCollector.IncrementYencFastPathMisses();
    return await InterpolationSearch.FindAsync(...).ConfigureAwait(false);
}

internal static int SegmentForByteOffset(
    long offset, long partSize, int segmentCount, long lastPartSize)
{
    if (segmentCount <= 0) return 0;
    if (segmentCount == 1) return 0;
    var regularBytes = (long)(segmentCount - 1) * partSize;
    if (offset < regularBytes) return (int)(offset / partSize);
    return segmentCount - 1;
}
```

Important: `SegmentForByteOffset` must be internal / testable. Add it
as a static helper on whichever class makes sense (likely the
NzbFileStream class or a `YencLayout` helper class).

## Observability

Add two counters in `NzbdavMetricsCollector`:

- `nzbdav_yenc_fast_path_hits_total` — incremented when uniform layout
  is used for offset lookup
- `nzbdav_yenc_fast_path_misses_total` — incremented when fallback
  InterpolationSearch is required (non-uniform or metadata absent)

Dashboard panel: fast_path_hit_rate = hits / (hits + misses). Should
trend to >95% once probe backfill has populated metadata for the
library.

## Verification

### Unit tests

Create `backend.Tests/Streams/YencLayoutTests.cs` (or add to existing
test class covering this area) with cases:

```csharp
[Fact]
public void SegmentForByteOffset_FirstSegmentStart()
{
    Assert.Equal(0, YencLayout.SegmentForByteOffset(0, 768000, 10, 500000));
}

[Fact]
public void SegmentForByteOffset_LastByteOfFirstSegment()
{
    Assert.Equal(0, YencLayout.SegmentForByteOffset(767999, 768000, 10, 500000));
}

[Fact]
public void SegmentForByteOffset_FirstByteOfSecondSegment()
{
    Assert.Equal(1, YencLayout.SegmentForByteOffset(768000, 768000, 10, 500000));
}

[Fact]
public void SegmentForByteOffset_MiddleOfFile()
{
    // 10 segments, segment 4 covers bytes 3072000..3839999
    Assert.Equal(4, YencLayout.SegmentForByteOffset(3500000, 768000, 10, 500000));
}

[Fact]
public void SegmentForByteOffset_LastSegmentAnywhere()
{
    // N=10, segment 9 is last, covers bytes 9*768000=6912000..end
    Assert.Equal(9, YencLayout.SegmentForByteOffset(7000000, 768000, 10, 500000));
    Assert.Equal(9, YencLayout.SegmentForByteOffset(7411999, 768000, 10, 500000));
}

[Fact]
public void SegmentForByteOffset_SingleSegmentFile()
{
    Assert.Equal(0, YencLayout.SegmentForByteOffset(0, 1_000_000, 1, 1_000_000));
    Assert.Equal(0, YencLayout.SegmentForByteOffset(999_999, 1_000_000, 1, 1_000_000));
}

[Fact]
public void SegmentForByteOffset_EmptyOrInvalid()
{
    Assert.Equal(0, YencLayout.SegmentForByteOffset(0, 0, 0, 0));
}
```

### Integration test (end-to-end fast path triggers)

Add a test to the existing `NzbFileStream` or stream-level test suite
that constructs a `DavItem` with populated YencLayout metadata,
requests a mid-file offset, and asserts:
- No InterpolationSearch probes issued (mock the NNTP client).
- Fast-path hit metric incremented by 1.
- Correct segment fetched (assert on returned segment index).

And a companion test with metadata absent (`YencLayoutUniform = null`)
that falls back to `InterpolationSearch` path, asserts miss counter
incremented.

### Live verification

After deploy:
- Run a probe backfill sweep (existing background task). Confirm
  `DavItems` rows populate the four columns.
- Trigger a mid-file cold seek via `curl -r 18000000000-18001048575`
  on a populated video. TTFB should drop from ~850 ms to <250 ms
  (only cost remaining is cold NNTP BODY fetch; no header probes).
- Query metrics: `nzbdav_yenc_fast_path_hit_rate` should be >90%
  once backfill is complete.

## Non-uniform edge cases

Some old NZBs have variable `part_size` across segments (rare in
modern postings, common in ancient ones from 2011-2015). Handle:
- When sampled middle segment's `PartSize` doesn't match segment 0 →
  set `YencLayoutUniform = false`. Stream path uses fallback.
- No data loss; just slower seek on those specific files.

## Rollback

Disable the fast path via config flag (optional addition):
- New config `cache.yenc-fast-path-enabled` default `true`. Setting
  `false` forces all lookups through `InterpolationSearch`.

Or ship without the flag — rollback is just reverting the commit.
The DB migration can stay; nullable columns don't affect anything if
ignored.

## Expected impact

| Scenario | Current (InterpolationSearch) | After fast path |
|---|---|---|
| Cold mid-file seek | 300-800 ms (probes) + 500-1500 ms (body fetch) = 800-2300 ms | 1-3 ms (math) + 500-1500 ms (body fetch) = **500-1500 ms** |
| Repeat seek to warmed offset | 50 ms (L1 hit, no probes) | unchanged |
| NNTP conn pressure from speculative probes | ~5 probes per cold seek | **0** |

Combined with the `dense` prewarm policy (sister spec), the
worst-case P99 TTFB during the stress test should drop from 5 s to
~1-1.5 s.
