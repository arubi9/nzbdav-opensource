using NzbWebDAV.Exceptions;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Queue.DeobfuscationSteps._3.GetFileInfos;
using NzbWebDAV.Queue.FileProcessors;
using NzbWebDAV.Streams;
using UsenetSharp.Models;

namespace backend.Tests.Queue.FileProcessors;

public sealed class SevenZipProcessorTests
{
    [Fact]
    public async Task ProcessAsync_ThrowsRetryable_WhenMissingFileSizesRequireZeroConcurrencyFanout()
    {
        var fileInfo = new GetFileInfosStep.FileInfo
        {
            NzbFile = CreateNzbFile("<segment-1@example.test>"),
            FileName = "archive.7z.001",
            ReleaseDate = new DateTimeOffset(2026, 4, 14, 12, 0, 0, TimeSpan.Zero),
            FileSize = null,
            IsRar = false
        };

        using var client = new RecordingNntpClient();
        var processor = new SevenZipProcessor(
            [fileInfo],
            client,
            missingFileSizeConcurrency: 0,
            archivePassword: null,
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<CouldNotConnectToUsenetException>(() =>
            processor.ProcessAsync(new Progress<int>()));

        Assert.Contains("lease", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, client.GetFileSizeCallCount);
    }

    private static NzbFile CreateNzbFile(string messageId)
    {
        var nzbFile = new NzbFile { Subject = "\"archive.7z.001\"" };
        nzbFile.Segments.Add(new NzbSegment
        {
            Number = 1,
            Bytes = 10,
            MessageId = messageId
        });
        return nzbFile;
    }

    private sealed class RecordingNntpClient : NntpClient
    {
        private int _getFileSizeCallCount;

        public int GetFileSizeCallCount => Volatile.Read(ref _getFileSizeCallCount);

        public override Task<long> GetFileSizeAsync(NzbFile file, CancellationToken ct)
        {
            Interlocked.Increment(ref _getFileSizeCallCount);
            return Task.FromResult(123L);
        }

        public override Task ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override Task<UsenetResponse> AuthenticateAsync(string user, string pass, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override Task<UsenetHeadResponse> HeadAsync(SegmentId segmentId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(SegmentId segmentId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            Action<ArticleBodyResult>? onConnectionReadyAgain,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            Action<ArticleBodyResult>? onConnectionReadyAgain,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override void Dispose()
        {
        }
    }
}
