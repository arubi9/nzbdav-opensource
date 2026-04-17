using NzbWebDAV.Exceptions;
using NzbWebDAV.Models;
using NzbWebDAV.Utils;

namespace backend.Tests.Utils;

public sealed class InterpolationSearchTests
{
    [Fact]
    public async Task Find_UniformSegments_LocatesMatchInFirstRoundTripBatch()
    {
        const int segmentCount = 100;
        const long segmentSize = 10;
        var totalBytes = segmentCount * segmentSize;
        var fetchCount = 0;

        var result = await InterpolationSearch.Find(
            searchByte: 507,
            indexRangeToSearch: new LongRange(0, segmentCount),
            byteRangeToSearch: new LongRange(0, totalBytes),
            getByteRangeOfGuessedIndex: i =>
            {
                Interlocked.Increment(ref fetchCount);
                return new ValueTask<LongRange>(new LongRange(i * segmentSize, (i + 1) * segmentSize));
            },
            cancellationToken: CancellationToken.None);

        Assert.Equal(50, result.FoundIndex);
        Assert.Equal(new LongRange(500, 510), result.FoundByteRange);
        // Parallel window fires 5 fetches in a single batch for a uniform layout.
        Assert.True(fetchCount <= 5, $"Expected at most one batch of fetches, got {fetchCount}");
    }

    [Fact]
    public async Task Find_NonUniformSegments_ConvergesAcrossMultipleBatches()
    {
        // Simulate segments with varying sizes so the primary interpolation
        // guess is wrong and bounds-narrowing must drive convergence.
        var ranges = new List<LongRange>();
        long offset = 0;
        for (var i = 0; i < 50; i++)
        {
            var size = (i % 3 == 0) ? 5L : 20L;
            ranges.Add(new LongRange(offset, offset + size));
            offset += size;
        }

        var total = offset;
        var searchByte = ranges[37].StartInclusive + 3;
        var batches = 0;

        var result = await InterpolationSearch.Find(
            searchByte: searchByte,
            indexRangeToSearch: new LongRange(0, ranges.Count),
            byteRangeToSearch: new LongRange(0, total),
            getByteRangeOfGuessedIndex: i =>
            {
                Interlocked.Increment(ref batches);
                return new ValueTask<LongRange>(ranges[i]);
            },
            cancellationToken: CancellationToken.None);

        Assert.Equal(37, result.FoundIndex);
        Assert.Equal(ranges[37], result.FoundByteRange);
    }

    [Fact]
    public async Task Find_SearchByteOutsideRange_Throws()
    {
        await Assert.ThrowsAsync<SeekPositionNotFoundException>(async () =>
        {
            await InterpolationSearch.Find(
                searchByte: 1_000_000,
                indexRangeToSearch: new LongRange(0, 10),
                byteRangeToSearch: new LongRange(0, 100),
                getByteRangeOfGuessedIndex: i =>
                    new ValueTask<LongRange>(new LongRange(i * 10, (i + 1) * 10)),
                cancellationToken: CancellationToken.None);
        });
    }

    [Fact]
    public async Task Find_FirstSegment_HandledWhenPrimaryGuessIsAtStartOfRange()
    {
        var fetches = 0;
        var result = await InterpolationSearch.Find(
            searchByte: 3,
            indexRangeToSearch: new LongRange(0, 1000),
            byteRangeToSearch: new LongRange(0, 100_000),
            getByteRangeOfGuessedIndex: i =>
            {
                Interlocked.Increment(ref fetches);
                return new ValueTask<LongRange>(new LongRange(i * 100, (i + 1) * 100));
            },
            cancellationToken: CancellationToken.None);

        Assert.Equal(0, result.FoundIndex);
        Assert.Equal(new LongRange(0, 100), result.FoundByteRange);
    }

    [Fact]
    public async Task Find_LastSegment_HandledWhenPrimaryGuessIsAtEndOfRange()
    {
        var result = await InterpolationSearch.Find(
            searchByte: 99_995,
            indexRangeToSearch: new LongRange(0, 1000),
            byteRangeToSearch: new LongRange(0, 100_000),
            getByteRangeOfGuessedIndex: i =>
                new ValueTask<LongRange>(new LongRange(i * 100, (i + 1) * 100)),
            cancellationToken: CancellationToken.None);

        Assert.Equal(999, result.FoundIndex);
        Assert.Equal(new LongRange(99_900, 100_000), result.FoundByteRange);
    }
}
