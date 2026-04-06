using NzbWebDAV.Exceptions;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Tests.TestDoubles;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class DuplicateSegmentFallbackTests
{
    [Fact]
    public async Task CheckAllSegmentsAsyncAcceptsAnyAvailableDuplicateCandidate()
    {
        using var client = new FakeNntpClient()
            .AddSegment("segment-1b", [1, 2, 3])
            .AddSegment("segment-2", [4, 5, 6]);

        await client.CheckAllSegmentsAsync(
            [NzbSegmentIdSet.Encode(["segment-1a", "segment-1b"]), "segment-2"],
            concurrency: 2,
            progress: null,
            CancellationToken.None
        );

        Assert.Equal(3, client.StatCallCount);
    }

    [Fact]
    public async Task CheckAllSegmentsAsyncThrowsWhenAllDuplicateCandidatesAreMissing()
    {
        using var client = new FakeNntpClient();

        var exception = await Assert.ThrowsAsync<UsenetArticleNotFoundException>(() => client.CheckAllSegmentsAsync(
            [NzbSegmentIdSet.Encode(["segment-1a", "segment-1b"])],
            concurrency: 1,
            progress: null,
            CancellationToken.None
        ));

        Assert.Equal("segment-1b", exception.SegmentId);
    }
}
