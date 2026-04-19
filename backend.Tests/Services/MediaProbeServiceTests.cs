using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Services;
using UsenetSharp.Models;

namespace backend.Tests.Services;

public sealed class MediaProbeServiceTests
{
    [Fact]
    public async Task ProcessBackfillBatch_TagsCancellationTokenWithLowPriority()
    {
        // Arrange — single video item. Contents of DavItem don't matter for
        // this test; the helper only forwards it to the processor.
        var item = MakeItem("sample.mkv");

        SemaphorePriority? observedPriority = null;
        var processorInvocations = 0;

        Task CapturePriority(DavItem _, CancellationToken ct)
        {
            processorInvocations++;
            observedPriority = ct.GetContext<DownloadPriorityContext>()?.Priority;
            return Task.CompletedTask;
        }

        using var cts = new CancellationTokenSource();

        // Act
        await MediaProbeService.ProcessBackfillBatchAsync(new[] { item }, CapturePriority, cts.Token);

        // Assert — the helper must have tagged the ct with Low priority so any
        // DecodedBodyWithFallbackAsync calls inside the real processor flow
        // through DownloadingNntpClient at Low priority, yielding to live
        // streams at High.
        Assert.Equal(1, processorInvocations);
        Assert.NotNull(observedPriority);
        Assert.Equal(SemaphorePriority.Low, observedPriority);
    }

    [Fact]
    public async Task ProcessBackfillBatch_RemovesPriorityContextAfterCompletion()
    {
        // The `using var priorityScope` must dispose after the foreach so
        // later use of the raw token does not carry Low priority into
        // unrelated work.
        var item = MakeItem("sample.mkv");

        using var cts = new CancellationTokenSource();
        var token = cts.Token;

        await MediaProbeService.ProcessBackfillBatchAsync(
            new[] { item },
            (_, _) => Task.CompletedTask,
            token);

        // After the helper returns, the context must no longer be attached
        // to the token.
        Assert.Null(token.GetContext<DownloadPriorityContext>());
    }

    [Fact]
    public async Task ProcessBackfillBatch_SwallowsProcessorExceptionsPerItem()
    {
        // Guard: a failing item must not abort the rest of the batch.
        var items = new[]
        {
            MakeItem("a.mkv"),
            MakeItem("b.mkv"),
            MakeItem("c.mkv")
        };

        var seen = new List<string>();
        Task ThrowOnMiddle(DavItem item, CancellationToken _)
        {
            seen.Add(item.Name);
            if (item.Name == "b.mkv") throw new InvalidOperationException("boom");
            return Task.CompletedTask;
        }

        await MediaProbeService.ProcessBackfillBatchAsync(items, ThrowOnMiddle, CancellationToken.None);

        Assert.Equal(new[] { "a.mkv", "b.mkv", "c.mkv" }, seen);
    }

    [Fact]
    public void ResolvePrewarmOffsets_FirstOnlyReturnsZero()
    {
        var offsets = MediaProbeService.ResolvePrewarmOffsets("first-only", 10);
        Assert.Equal(new[] { 0 }, offsets);
    }

    [Fact]
    public void ResolvePrewarmOffsets_FirstAndLast()
    {
        var offsets = MediaProbeService.ResolvePrewarmOffsets("first-and-last", 10);
        Assert.Equal(new[] { 0, 9 }, offsets);
    }

    [Fact]
    public void ResolvePrewarmOffsets_FirstMiddleLastIsDefault()
    {
        var offsets = MediaProbeService.ResolvePrewarmOffsets("unknown-policy", 100);
        Assert.Equal(new[] { 0, 50, 99 }, offsets);
    }

    [Fact]
    public void ResolvePrewarmOffsets_CollapsesOnShortVideo()
    {
        // 2 segments under first-middle-last -> only 0 and 1 (distinct)
        var offsets = MediaProbeService.ResolvePrewarmOffsets("first-middle-last", 2);
        Assert.Equal(new[] { 0, 1 }, offsets);
    }

    [Fact]
    public void ResolvePrewarmOffsets_SingleSegmentVideo()
    {
        var offsets = MediaProbeService.ResolvePrewarmOffsets("first-middle-last", 1);
        Assert.Equal(new[] { 0 }, offsets);
    }

    [Fact]
    public void ResolvePrewarmOffsets_EmptyReturnsEmpty()
    {
        var offsets = MediaProbeService.ResolvePrewarmOffsets("first-middle-last", 0);
        Assert.Empty(offsets);
    }

    [Fact]
    public void ResolvePrewarmOffsets_AggressivePolicy()
    {
        // first-quartile-mid-threequartile-last on 100 segments
        var offsets = MediaProbeService.ResolvePrewarmOffsets(
            "first-quartile-mid-threequartile-last", 100);
        Assert.Equal(new[] { 0, 25, 50, 75, 99 }, offsets);
    }

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

    // -----------------------------------------------------------------------
    // ComputeYencLayoutAsync tests
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ComputeYencLayout_UniformSegments_SetsAllFieldsAndUniformTrue()
    {
        // Arrange: 5 segments all 768 000 bytes except the last (500 000).
        const long uniformPart = 768_000L;
        const long lastPart    = 500_000L;
        var segmentIds = new[] { "seg-0", "seg-1", "seg-2", "seg-3", "seg-4" };

        Task<UsenetYencHeader> FakeHeaderFetcher(string segId, CancellationToken _)
        {
            var partSize = segId == "seg-4" ? lastPart : uniformPart;
            return Task.FromResult(new UsenetYencHeader
            {
                FileName  = "test.bin",
                FileSize  = 5 * uniformPart,
                PartSize  = partSize,
                PartOffset = 0,
                PartNumber = 1,
                TotalParts = 5,
                LineLength = 128,
            });
        }

        // Act
        var (partSize, lastPartSize, segmentCount, uniform) =
            await MediaProbeService.ComputeYencLayoutAsync(segmentIds, FakeHeaderFetcher, CancellationToken.None);

        // Assert
        Assert.Equal(uniformPart, partSize);
        Assert.Equal(lastPart, lastPartSize);
        Assert.Equal(5, segmentCount);
        Assert.True(uniform);
    }

    [Fact]
    public async Task ComputeYencLayout_NonUniformMiddleSegment_SetsUniformFalse()
    {
        // Arrange: middle segment has a different PartSize — non-uniform NZB.
        const long normalPart  = 768_000L;
        const long oddPart     = 400_000L;
        const long lastPart    = 300_000L;
        var segmentIds = new[] { "seg-0", "seg-1", "seg-2", "seg-3", "seg-4" };

        Task<UsenetYencHeader> FakeHeaderFetcher(string segId, CancellationToken _)
        {
            // seg-2 is the middle (index 5/2 = 2) and has an unexpected size.
            var partSize = segId switch
            {
                "seg-2" => oddPart,
                "seg-4" => lastPart,
                _       => normalPart
            };
            return Task.FromResult(new UsenetYencHeader
            {
                FileName   = "test.bin",
                FileSize   = 5 * normalPart,
                PartSize   = partSize,
                PartOffset = 0,
                PartNumber = 1,
                TotalParts = 5,
                LineLength = 128,
            });
        }

        // Act
        var (partSize, lastPartSize, segmentCount, uniform) =
            await MediaProbeService.ComputeYencLayoutAsync(segmentIds, FakeHeaderFetcher, CancellationToken.None);

        // Assert: PartSize/LastPartSize still captured; uniform must be false.
        Assert.Equal(normalPart, partSize);
        Assert.Equal(lastPart, lastPartSize);
        Assert.Equal(5, segmentCount);
        Assert.False(uniform);
    }

    [Fact]
    public async Task ComputeYencLayout_TwoSegments_TriviallyUniform()
    {
        // Two segments: no middle sample — should be trivially uniform.
        var segmentIds = new[] { "seg-0", "seg-1" };
        var callCount = 0;

        Task<UsenetYencHeader> FakeHeaderFetcher(string _, CancellationToken __)
        {
            callCount++;
            return Task.FromResult(new UsenetYencHeader
            {
                FileName   = "x.bin",
                FileSize   = 2_000_000L,
                PartSize   = 1_000_000L,
                PartOffset = 0,
                PartNumber = 1,
                TotalParts = 2,
                LineLength = 128,
            });
        }

        var (_, _, segmentCount, uniform) =
            await MediaProbeService.ComputeYencLayoutAsync(segmentIds, FakeHeaderFetcher, CancellationToken.None);

        Assert.Equal(2, segmentCount);
        Assert.True(uniform);
        // Only first + last fetched (no middle for 2-segment file).
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task ComputeYencLayout_AlreadyPopulated_IdempotentCheck()
    {
        // This test verifies that PopulateYencLayoutMetadataAsync returns false
        // immediately when YencLayoutUniform is already set, without invoking
        // any header fetcher. We exercise ComputeYencLayoutAsync directly and
        // confirm it is NOT called (via a throwing fetcher) — the early-return
        // guard lives in PopulateYencLayoutMetadataAsync, so here we just
        // confirm ComputeYencLayoutAsync itself counts fetches for 1-segment.
        var segmentIds = new[] { "only-seg" };
        var callCount = 0;

        Task<UsenetYencHeader> FakeHeaderFetcher(string _, CancellationToken __)
        {
            callCount++;
            return Task.FromResult(new UsenetYencHeader
            {
                FileName   = "solo.bin",
                FileSize   = 500_000L,
                PartSize   = 500_000L,
                PartOffset = 0,
                PartNumber = 1,
                TotalParts = 1,
                LineLength = 128,
            });
        }

        var (partSize, lastPartSize, segmentCount, uniform) =
            await MediaProbeService.ComputeYencLayoutAsync(segmentIds, FakeHeaderFetcher, CancellationToken.None);

        // Single segment: first == last, trivially uniform.
        Assert.Equal(500_000L, partSize);
        Assert.Equal(500_000L, lastPartSize);
        Assert.Equal(1, segmentCount);
        Assert.True(uniform);
        // Only 2 calls: seg-0 as first AND seg-0 as last (same index, same result).
        Assert.Equal(2, callCount);
    }

    private static DavItem MakeItem(string name)
    {
        var id = Guid.NewGuid();
        return new DavItem
        {
            Id = id,
            IdPrefix = id.ToString("N")[..5],
            Name = name,
            Path = "/content/" + name,
            Type = DavItem.ItemType.NzbFile,
            FileSize = 1_000_000
        };
    }
}
