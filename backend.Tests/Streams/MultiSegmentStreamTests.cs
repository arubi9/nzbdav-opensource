using System.Text;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.TestDoubles;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Streams;

public class MultiSegmentStreamTests
{
    [Fact]
    public async Task StartupPrefetchIsCappedToTwoSegmentsInitially()
    {
        var fakeNntpClient = CreateSegmentClient(5);
        await using var stream = MultiSegmentStream.Create(
            CreateSegmentIds(5),
            fakeNntpClient,
            new StreamingBufferSettings(maxBufferedSegments: 5, startupBufferedSegments: 2, rampAfterConsumedSegments: 2),
            CancellationToken.None
        );

        await WaitForConditionAsync(() => fakeNntpClient.DecodedBodyCallCount >= 2);
        await Task.Delay(50);

        Assert.Equal(2, fakeNntpClient.DecodedBodyCallCount);
    }

    [Fact]
    public async Task PrefetchWindowExpandsAfterTwoConsumedSegments()
    {
        var fakeNntpClient = CreateSegmentClient(5);
        await using var stream = MultiSegmentStream.Create(
            CreateSegmentIds(5),
            fakeNntpClient,
            new StreamingBufferSettings(maxBufferedSegments: 5, startupBufferedSegments: 2, rampAfterConsumedSegments: 2),
            CancellationToken.None
        );

        await WaitForConditionAsync(() => fakeNntpClient.DecodedBodyCallCount == 2);

        var buffer = new byte[1];
        await stream.ReadExactlyAsync(buffer);
        await stream.ReadExactlyAsync(buffer);
        await WaitForConditionAsync(() => fakeNntpClient.DecodedBodyCallCount == 3);

        await stream.ReadExactlyAsync(buffer);
        await WaitForConditionAsync(() => fakeNntpClient.DecodedBodyCallCount == 5);
    }

    [Fact]
    public async Task DisposeAsyncDrainsUnreadPendingStreams()
    {
        var fakeNntpClient = CreateSegmentClient(3);
        var stream = MultiSegmentStream.Create(
            CreateSegmentIds(3),
            fakeNntpClient,
            new StreamingBufferSettings(maxBufferedSegments: 3, startupBufferedSegments: 3, rampAfterConsumedSegments: 0),
            CancellationToken.None
        );

        await WaitForConditionAsync(() => fakeNntpClient.DecodedBodyCallCount == 3);
        await stream.DisposeAsync();

        Assert.Equal(fakeNntpClient.CreatedStreamCount, fakeNntpClient.DisposedStreamCount);
    }

    [Fact]
    public async Task SyncDisposeReturnsWithoutWaitingForInflightDownloads()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fakeNntpClient = new NonCancellingNntpClient(gate.Task);
        var stream = MultiSegmentStream.Create(
            CreateSegmentIds(3),
            fakeNntpClient,
            new StreamingBufferSettings(maxBufferedSegments: 3, startupBufferedSegments: 3, rampAfterConsumedSegments: 0),
            CancellationToken.None
        );

        await WaitForConditionAsync(() => fakeNntpClient.DecodedBodyCallCount == 3);
        _ = Task.Run(async () =>
        {
            await Task.Delay(750);
            gate.TrySetResult();
        });

        var startedAt = DateTime.UtcNow;
        stream.Dispose();
        var elapsed = DateTime.UtcNow - startedAt;

        Assert.True(elapsed < TimeSpan.FromMilliseconds(250), $"Dispose blocked for {elapsed.TotalMilliseconds:F0} ms.");
    }

    private static FakeNntpClient CreateSegmentClient(int segmentCount)
    {
        var fakeNntpClient = new FakeNntpClient();
        for (var i = 0; i < segmentCount; i++)
            fakeNntpClient.AddSegment($"segment-{i}", Encoding.ASCII.GetBytes(((char)('A' + i)).ToString()), i);
        return fakeNntpClient;
    }

    private static string[] CreateSegmentIds(int count)
    {
        return Enumerable.Range(0, count)
            .Select(i => $"segment-{i}")
            .ToArray();
    }

    private static async Task WaitForConditionAsync(Func<bool> condition)
    {
        var timeoutAt = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < timeoutAt)
        {
            if (condition()) return;
            await Task.Delay(25);
        }

        Assert.True(condition(), "Timed out waiting for condition.");
    }

    private sealed class NonCancellingNntpClient(Task gateTask) : NntpClient
    {
        private readonly Task _gateTask = gateTask;
        private int _decodedBodyCallCount;

        public int DecodedBodyCallCount => Volatile.Read(ref _decodedBodyCallCount);

        public override async Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            Action<ArticleBodyResult>? onConnectionReadyAgain,
            CancellationToken cancellationToken
        )
        {
            Interlocked.Increment(ref _decodedBodyCallCount);
            await _gateTask.ConfigureAwait(false);
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
            return new UsenetDecodedBodyResponse
            {
                SegmentId = segmentId,
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedBodyFollows,
                ResponseMessage = "222 - Body retrieved",
                Stream = new CachedYencStream(
                    new UsenetYencHeader
                    {
                        FileName = "segment.bin",
                        FileSize = 1,
                        LineLength = 128,
                        PartNumber = 1,
                        TotalParts = 1,
                        PartSize = 1,
                        PartOffset = 0
                    },
                    new MemoryStream([1], writable: false)
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

        public override Task ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override Task<UsenetResponse> AuthenticateAsync(string user, string pass, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override Task<UsenetHeadResponse> HeadAsync(SegmentId segmentId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

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
