using System.Text;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Models;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Clients.Usenet;

public sealed class WrappingNntpClientTests
{
    [Fact]
    public async Task GetFileStream_UsesOuterWrapper_ForExclusiveConnectionPath()
    {
        var inner = new NonExclusiveInnerClient();
        using var outer = new TrackingWrappingClient(inner);

        await using var stream = outer.GetFileStream(
            ["segment-1"],
            fileSize: 4,
            StreamingBufferSettings.LiveDefault(articleBufferSize: 1)
        );

        var buffer = new byte[1];
        var read = await stream.ReadAsync(buffer, CancellationToken.None);

        Assert.Equal(1, read);
        Assert.Equal(1, outer.AcquireExclusiveConnectionCallCount);
        Assert.Equal("A", Encoding.ASCII.GetString(buffer));
    }

    private sealed class TrackingWrappingClient(INntpClient inner) : WrappingNntpClient(inner)
    {
        public int AcquireExclusiveConnectionCallCount { get; private set; }

        public override Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync(
            string segmentId,
            CancellationToken cancellationToken
        )
        {
            AcquireExclusiveConnectionCallCount++;
            return Task.FromResult(new UsenetExclusiveConnection(onConnectionReadyAgain: null));
        }

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            UsenetExclusiveConnection exclusiveConnection,
            CancellationToken cancellationToken
        )
        {
            return base.DecodedBodyAsync(segmentId, cancellationToken);
        }
    }

    private sealed class NonExclusiveInnerClient : NntpClient
    {
        public override Task ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override Task<UsenetResponse> AuthenticateAsync(string user, string pass, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override Task<UsenetHeadResponse> HeadAsync(SegmentId segmentId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            CancellationToken cancellationToken
        )
            => DecodedBodyAsync(segmentId, onConnectionReadyAgain: null, cancellationToken);

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            Action<ArticleBodyResult>? onConnectionReadyAgain,
            CancellationToken cancellationToken
        )
        {
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
            return Task.FromResult(new UsenetDecodedBodyResponse
            {
                SegmentId = segmentId,
                ResponseCode = 222,
                ResponseMessage = "Body retrieved",
                Stream = new NzbWebDAV.Streams.CachedYencStream(
                    new UsenetYencHeader
                    {
                        FileName = "segment.bin",
                        FileSize = 4,
                        LineLength = 128,
                        PartNumber = 1,
                        TotalParts = 1,
                        PartSize = 4,
                        PartOffset = 0
                    },
                    new MemoryStream(Encoding.ASCII.GetBytes("AAAA"), writable: false)
                )
            });
        }

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            CancellationToken cancellationToken
        )
            => throw new NotSupportedException();

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            Action<ArticleBodyResult>? onConnectionReadyAgain,
            CancellationToken cancellationToken
        )
            => throw new NotSupportedException();

        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override void Dispose()
        {
        }
    }
}
