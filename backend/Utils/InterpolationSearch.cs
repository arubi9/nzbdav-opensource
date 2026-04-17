using NzbWebDAV.Exceptions;
using NzbWebDAV.Models;

namespace NzbWebDAV.Utils;

public static class InterpolationSearch
{
    // When fetching candidate ranges is expensive (e.g. each fetch is an NNTP
    // round-trip to retrieve a yEnc header), a strict serial binary search
    // pays log2(N) round-trips on a cold cache. For uniform-size segments the
    // primary interpolation guess is almost always within ±1 of the true
    // index, so we speculatively fire the primary guess plus a small window
    // of its neighbours in parallel. On uniform-segment files this usually
    // finds the answer in one round-trip. On non-uniform files it still
    // tightens the search bounds with more information per iteration.
    private const int ParallelWindowRadius = 2;

    public static Result Find
    (
        long searchByte,
        LongRange indexRangeToSearch,
        LongRange byteRangeToSearch,
        Func<int, LongRange> getByteRangeOfGuessedIndex
    )
    {
        return Find(
            searchByte,
            indexRangeToSearch,
            byteRangeToSearch,
            guess => new ValueTask<LongRange>(getByteRangeOfGuessedIndex(guess)),
            SigtermUtil.GetCancellationToken()
        ).GetAwaiter().GetResult();
    }

    public static async Task<Result> Find
    (
        long searchByte,
        LongRange indexRangeToSearch,
        LongRange byteRangeToSearch,
        Func<int, ValueTask<LongRange>> getByteRangeOfGuessedIndex,
        CancellationToken cancellationToken
    )
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // make sure our search is even possible.
            if (!byteRangeToSearch.Contains(searchByte) || indexRangeToSearch.Count <= 0)
                throw new SeekPositionNotFoundException($"Corrupt file. Cannot find byte position {searchByte}.");

            // interpolate the primary guess
            var searchByteFromStart = searchByte - byteRangeToSearch.StartInclusive;
            var bytesPerIndex = (double)byteRangeToSearch.Count / indexRangeToSearch.Count;
            var guessFromStart = (long)Math.Floor(searchByteFromStart / bytesPerIndex);
            var primaryGuess = (int)(indexRangeToSearch.StartInclusive + guessFromStart);

            // clamp primary into [start, end) so large extrapolations near the
            // tail don't overshoot on files whose segments are smaller than the
            // uniform average.
            if (primaryGuess < indexRangeToSearch.StartInclusive)
                primaryGuess = (int)indexRangeToSearch.StartInclusive;
            var lastValid = (int)(indexRangeToSearch.EndExclusive - 1);
            if (primaryGuess > lastValid)
                primaryGuess = lastValid;

            // assemble a contiguous window of candidate indices centred on
            // primaryGuess and inside the current search range.
            var windowStart = Math.Max((int)indexRangeToSearch.StartInclusive, primaryGuess - ParallelWindowRadius);
            var windowEnd = Math.Min(lastValid, primaryGuess + ParallelWindowRadius);
            var windowSize = windowEnd - windowStart + 1;
            var candidates = new int[windowSize];
            var tasks = new Task<LongRange>[windowSize];
            for (var i = 0; i < windowSize; i++)
            {
                var idx = windowStart + i;
                candidates[i] = idx;
                tasks[i] = getByteRangeOfGuessedIndex(idx).AsTask();
            }

            var results = await Task.WhenAll(tasks).ConfigureAwait(false);

            // scan the window for a direct hit and to compute the tightest
            // possible bounds update in a single iteration.
            LongRange? anchorBelow = null;
            var anchorBelowIdx = -1;
            LongRange? anchorAbove = null;
            var anchorAboveIdx = -1;

            for (var i = 0; i < windowSize; i++)
            {
                var range = results[i];
                if (!range.IsContainedWithin(byteRangeToSearch))
                    throw new SeekPositionNotFoundException($"Corrupt file. Cannot find byte position {searchByte}.");

                if (range.Contains(searchByte))
                    return new Result(candidates[i], range);

                if (range.EndExclusive <= searchByte)
                {
                    // candidate sits entirely below the search byte; keep the
                    // highest such range so we can advance lower bound as far
                    // as possible.
                    if (anchorBelow is null || range.EndExclusive > anchorBelow.EndExclusive)
                    {
                        anchorBelow = range;
                        anchorBelowIdx = candidates[i];
                    }
                }
                else if (range.StartInclusive > searchByte)
                {
                    // candidate sits entirely above; keep the lowest such
                    // range so we can pull upper bound as far down as
                    // possible.
                    if (anchorAbove is null || range.StartInclusive < anchorAbove.StartInclusive)
                    {
                        anchorAbove = range;
                        anchorAboveIdx = candidates[i];
                    }
                }
            }

            var newStartIndex = anchorBelow is not null
                ? anchorBelowIdx + 1
                : indexRangeToSearch.StartInclusive;
            var newEndIndexExclusive = anchorAbove is not null
                ? anchorAboveIdx
                : indexRangeToSearch.EndExclusive;

            var newStartByte = anchorBelow is not null
                ? anchorBelow.EndExclusive
                : byteRangeToSearch.StartInclusive;
            var newEndByteExclusive = anchorAbove is not null
                ? anchorAbove.StartInclusive
                : byteRangeToSearch.EndExclusive;

            indexRangeToSearch = indexRangeToSearch with
            {
                StartInclusive = newStartIndex,
                EndExclusive = newEndIndexExclusive
            };
            byteRangeToSearch = byteRangeToSearch with
            {
                StartInclusive = newStartByte,
                EndExclusive = newEndByteExclusive
            };
        }
    }

    public record Result(int FoundIndex, LongRange FoundByteRange);
}