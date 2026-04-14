using NzbWebDAV.Exceptions;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Queue.DeobfuscationSteps._1.FetchFirstSegment;
using NzbWebDAV.Tests.TestDoubles;

namespace backend.Tests.Queue.DeobfuscationSteps;

public sealed class FetchFirstSegmentsStepTests
{
    [Fact]
    public async Task FetchFirstSegments_ThrowsRetryable_WhenExplicitConcurrencyIsZero()
    {
        using var client = new FakeNntpClient();
        var nzbFile = new NzbFile { Subject = "\"movie.mkv\"" };
        nzbFile.Segments.Add(new NzbSegment
        {
            Number = 1,
            Bytes = 123,
            MessageId = "<segment-1@example.test>"
        });

        var exception = await Assert.ThrowsAsync<CouldNotConnectToUsenetException>(() =>
            FetchFirstSegmentsStep.FetchFirstSegments(
                [nzbFile],
                client,
                concurrency: 0,
                CancellationToken.None));

        Assert.Contains("lease", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, client.DecodedArticleCallCount);
    }
}
