using System.Text;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.TestDoubles;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Streams;

public class NzbFileStreamTests
{
    [Fact]
    public async Task RepeatedSeeksWithinResolvedSegmentsDoNotRecallHeaders()
    {
        var fakeNntpClient = new FakeNntpClient()
            .AddSegment("segment-1", Encoding.ASCII.GetBytes("AAAA"), partOffset: 0)
            .AddSegment("segment-2", Encoding.ASCII.GetBytes("BBBB"), partOffset: 4)
            .AddSegment("segment-3", Encoding.ASCII.GetBytes("CCCC"), partOffset: 8);

        await using var stream = new NzbFileStream(
            ["segment-1", "segment-2", "segment-3"],
            fileSize: 12,
            fakeNntpClient,
            StreamingBufferSettings.Fixed(0)
        );

        var buffer = new byte[1];

        stream.Seek(5, SeekOrigin.Begin);
        await stream.ReadExactlyAsync(buffer);
        var headerCallsAfterFirstSeek = fakeNntpClient.GetYencHeadersCallCount;

        stream.Seek(6, SeekOrigin.Begin);
        await stream.ReadExactlyAsync(buffer);

        Assert.Equal(headerCallsAfterFirstSeek, fakeNntpClient.GetYencHeadersCallCount);
    }

    [Fact]
    public async Task SeekStillRebuildsTheInnerStreamCorrectly()
    {
        var fakeNntpClient = new FakeNntpClient()
            .AddSegment("segment-1", Encoding.ASCII.GetBytes("AAAA"), partOffset: 0)
            .AddSegment("segment-2", Encoding.ASCII.GetBytes("BBBB"), partOffset: 4)
            .AddSegment("segment-3", Encoding.ASCII.GetBytes("CCCC"), partOffset: 8);

        await using var stream = new NzbFileStream(
            ["segment-1", "segment-2", "segment-3"],
            fileSize: 12,
            fakeNntpClient,
            StreamingBufferSettings.Fixed(0)
        );

        stream.Seek(6, SeekOrigin.Begin);
        var bytes = await ReadExactlyAsync(stream, 4);

        Assert.Equal("BBCC", Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public async Task SeekDoesNotSynchronouslyDisposeInflightInnerStream()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var stream = new NzbFileStream(
            ["segment-1", "segment-2", "segment-3"],
            fileSize: 12,
            new NonCancellingSeekClient(gate.Task),
            StreamingBufferSettings.LiveDefault(articleBufferSize: 3)
        );

        var pendingRead = stream.ReadAsync(new byte[1].AsMemory()).AsTask();
        await Task.Delay(50);
        var startedAt = DateTime.UtcNow;
        stream.Seek(6, SeekOrigin.Begin);
        var elapsed = DateTime.UtcNow - startedAt;
        gate.TrySetResult();
        await pendingRead;

        Assert.True(elapsed < TimeSpan.FromMilliseconds(250), $"Seek blocked for {elapsed.TotalMilliseconds:F0} ms.");
    }

    [Fact]
    public async Task MissingPreferredDuplicateSegmentFallsBackToAlternate()
    {
        var fakeNntpClient = new FakeNntpClient()
            .AddSegment("segment-1b", Encoding.ASCII.GetBytes("AAAA"), partOffset: 0)
            .AddSegment("segment-2", Encoding.ASCII.GetBytes("BBBB"), partOffset: 4);
        var nzbFile = new NzbFile
        {
            Subject = "example.mkv"
        };
        nzbFile.Segments.Add(new NzbSegment { Number = 1, Bytes = 4, MessageId = "segment-1a" });
        nzbFile.Segments.Add(new NzbSegment { Number = 1, Bytes = 4, MessageId = "segment-1b" });
        nzbFile.Segments.Add(new NzbSegment { Number = 2, Bytes = 4, MessageId = "segment-2" });

        await using var stream = await fakeNntpClient
            .GetFileStream(nzbFile, StreamingBufferSettings.Fixed(0), CancellationToken.None);

        var bytes = await ReadExactlyAsync(stream, 8);

        Assert.Equal("AAAABBBB", Encoding.ASCII.GetString(bytes));
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int byteCount)
    {
        var buffer = new byte[byteCount];
        var offset = 0;

        while (offset < byteCount)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, byteCount - offset));
            if (read == 0) break;
            offset += read;
        }

        return buffer[..offset];
    }

    private sealed class NonCancellingSeekClient(Task gateTask) : NntpClient
    {
        private readonly Task _gateTask = gateTask;

        public override async Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            Action<ArticleBodyResult>? onConnectionReadyAgain,
            CancellationToken cancellationToken
        )
        {
            await _gateTask.ConfigureAwait(false);
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
            return new UsenetDecodedBodyResponse
            {
                SegmentId = segmentId,
                ResponseCode = 222,
                ResponseMessage = "Body retrieved",
                Stream = new CachedYencStream(
                    new UsenetSharp.Models.UsenetYencHeader
                    {
                        FileName = "segment.bin",
                        FileSize = 4,
                        LineLength = 128,
                        PartNumber = 1,
                        TotalParts = 1,
                        PartSize = 4,
                        PartOffset = segmentId.ToString() switch
                        {
                            "segment-1" => 0,
                            "segment-2" => 4,
                            _ => 8
                        }
                    },
                    new MemoryStream(Encoding.ASCII.GetBytes("AAAA"), writable: false)
                )
            };
        }

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            CancellationToken cancellationToken
        ) => DecodedBodyAsync(segmentId, onConnectionReadyAgain: null, cancellationToken);

        public override Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync(
            string segmentId,
            CancellationToken cancellationToken
        ) => Task.FromResult(new UsenetExclusiveConnection(onConnectionReadyAgain: null));

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            UsenetExclusiveConnection exclusiveConnection,
            CancellationToken cancellationToken
        ) => DecodedBodyAsync(segmentId, exclusiveConnection.OnConnectionReadyAgain, cancellationToken);

        public override Task<UsenetSharp.Models.UsenetYencHeader> GetYencHeadersAsync(string segmentId, CancellationToken ct)
        {
            var header = new UsenetSharp.Models.UsenetYencHeader
            {
                FileName = "segment.bin",
                FileSize = 12,
                LineLength = 128,
                PartNumber = 1,
                TotalParts = 1,
                PartSize = 4,
                PartOffset = segmentId.ToString() switch
                {
                    "segment-1" => 0,
                    "segment-2" => 4,
                    _ => 8
                }
            };

            return Task.FromResult(header);
        }

        public override Task ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override Task<UsenetSharp.Models.UsenetResponse> AuthenticateAsync(string user, string pass, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override Task<UsenetHeadResponse> HeadAsync(SegmentId segmentId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(SegmentId segmentId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            Action<ArticleBodyResult>? onConnectionReadyAgain,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override void Dispose()
        {
        }
    }
}
