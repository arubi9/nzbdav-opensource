# Dense L2 prewarm policy — design

## Problem

The existing prewarm policies (`first-only`, `first-and-last`,
`first-middle-last` default, `first-quartile-mid-threequartile-last`)
cover only 1-5 segments per video. Between warmed offsets, scrub-bar
seeks still trigger cold NNTP fetches + InterpolationSearch, adding
300-1500 ms latency.

For libraries where storage is plentiful (Cloudflare R2 at $0.015/GB
is effectively free), we want the option to prewarm enough segments
that any scrub lands within a few percent of a warmed island.

## Goal

Add two new policies to `MediaProbeService.ResolvePrewarmOffsets`:

- **`dense`** — 20 evenly-spaced segments per video (every ~5% of file)
- **`ultra-dense`** — 40 evenly-spaced segments per video (every ~2.5%)

Keep existing policies unchanged. Default stays `first-middle-last`.

## Storage cost math

At library size 752 videos with typical segment size 750 KB:

| Policy | Segments/video | Total L2 | Cost (R2 @ $0.015/GB/mo) |
|---|---|---|---|
| first-only | 1 | 0.56 GB | $0.008 |
| first-and-last | 2 | 1.1 GB | $0.017 |
| first-middle-last (default) | 3 | 1.7 GB | $0.025 |
| first-quartile-mid-threequartile-last | 5 | 2.8 GB | $0.042 |
| **dense** | **20** | **~11.3 GB** | **~$0.17/mo** |
| **ultra-dense** | **40** | **~22.5 GB** | **~$0.34/mo** |

At $0.17/mo the dense policy is strictly additive capacity for
negligible cost. No reason to avoid it on an R2 / Cloudflare backend.

## Runtime cost math

Prewarm runs at Low NNTP priority, yielding to live streams. At
Newshosting's 100-conn cap and ~250 ms per NNTP BODY:
- `first-middle-last`: 3 × 752 = 2256 fetches ≈ 6 minutes
- `dense`: 20 × 752 = 15040 fetches ≈ **40 minutes** one-time
- `ultra-dense`: 40 × 752 = 30080 fetches ≈ 80 minutes

All happen in background after startup. Already-cached segments
short-circuit (the `SeedL2Async` helper checks L2 presence first and
returns without fetch if the key exists), so restarts after a warmed
library cost zero fetches.

## Implementation

### File: `backend/Services/MediaProbeService.cs`

Extend `ResolvePrewarmOffsets` switch with two new arms. Use
deterministic rounding so short-video collapse through the existing
`.Distinct().OrderBy()` guard stays correct.

```csharp
"dense" => GenerateEvenOffsets(segmentCount, 20),
"ultra-dense" => GenerateEvenOffsets(segmentCount, 40),
```

Helper:

```csharp
private static int[] GenerateEvenOffsets(int segmentCount, int targetCount)
{
    if (segmentCount <= targetCount)
    {
        // Short video: use every segment
        return Enumerable.Range(0, segmentCount).ToArray();
    }
    return Enumerable.Range(0, targetCount)
        .Select(i => (int)Math.Round(i * (segmentCount - 1.0) / (targetCount - 1)))
        .ToArray();
}
```

### File: `backend/Config/ConfigManager.cs`

No change. `GetL2PrewarmPolicy()` already reads `cache.l2.prewarm-policy`
as a free-form string. The switch in `ResolvePrewarmOffsets` interprets
it; unknown values fall back to the `first-middle-last` default, which
remains the safe behaviour.

### Config documentation (no code)

Valid values for `cache.l2.prewarm-policy`:
- `first-only`
- `first-and-last`
- `first-middle-last` (default)
- `first-quartile-mid-threequartile-last`
- `dense` (new — 20 evenly-spaced offsets)
- `ultra-dense` (new — 40 evenly-spaced offsets)

## Verification

### Unit tests (file: `backend.Tests/Services/MediaProbeServiceTests.cs`)

Add the following test cases to the existing `MediaProbeServiceTests`
class:

```csharp
[Fact]
public void ResolvePrewarmOffsets_DenseTwentySegmentsOnLargeFile()
{
    // 1000-segment file under dense policy returns exactly 20 offsets,
    // first = 0, last = 999, evenly spaced.
    var offsets = MediaProbeService.ResolvePrewarmOffsets("dense", 1000);
    Assert.Equal(20, offsets.Length);
    Assert.Equal(0, offsets[0]);
    Assert.Equal(999, offsets[^1]);
    // Expected values: Math.Round(i * 999/19) for i in 0..19
    // Verify monotonic + roughly evenly spaced.
    for (int i = 1; i < offsets.Length; i++)
        Assert.True(offsets[i] > offsets[i - 1]);
}

[Fact]
public void ResolvePrewarmOffsets_UltraDenseFortySegmentsOnLargeFile()
{
    var offsets = MediaProbeService.ResolvePrewarmOffsets("ultra-dense", 1000);
    Assert.Equal(40, offsets.Length);
    Assert.Equal(0, offsets[0]);
    Assert.Equal(999, offsets[^1]);
}

[Fact]
public void ResolvePrewarmOffsets_DenseCollapsesOnShortVideo()
{
    // 10-segment file under dense (asking for 20) returns all 10.
    var offsets = MediaProbeService.ResolvePrewarmOffsets("dense", 10);
    Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, offsets);
}

[Fact]
public void ResolvePrewarmOffsets_DenseOnExactlyTwentySegments()
{
    // Boundary: segmentCount == targetCount returns every index.
    var offsets = MediaProbeService.ResolvePrewarmOffsets("dense", 20);
    Assert.Equal(Enumerable.Range(0, 20).ToArray(), offsets);
}
```

### Build verification

```
cd backend && dotnet build --nologo -v q
```

Must finish with `0 Error(s)` and no new warnings in the modified
files.

### Test verification

```
dotnet test backend.Tests --nologo --filter 'FullyQualifiedName~MediaProbeServiceTests'
```

Must report all tests passing, including 4 new ones.

### Full suite

```
dotnet test backend.Tests --nologo
```

Must report zero failures across the full suite.

## Rollback

Single config change reverts behaviour:
```
cache.l2.prewarm-policy = first-middle-last
```
No code-level rollback needed.

## Non-goals

- Smart policy that adapts per video (hot items get dense, cold items
  get first-only). Future work.
- Content-aware keyframe-aligned offsets from ffprobe data. Future work.
- Removing existing policies. All legacy values stay valid.
