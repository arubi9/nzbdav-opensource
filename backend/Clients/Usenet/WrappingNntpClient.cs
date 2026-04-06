using NzbWebDAV.Clients.Usenet.Caching;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Streams;
using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet;

public class WrappingNntpClient(INntpClient usenetClient) : NntpClient, ICachedSegmentReader
{
    private INntpClient _usenetClient = usenetClient;

    public override Task ConnectAsync(
        string host, int port, bool useSsl, CancellationToken cancellationToken) =>
        _usenetClient.ConnectAsync(host, port, useSsl, cancellationToken);

    public override Task<UsenetResponse> AuthenticateAsync(
        string user, string pass, CancellationToken cancellationToken) =>
        _usenetClient.AuthenticateAsync(user, pass, cancellationToken);

    public override Task<UsenetStatResponse> StatAsync(
        SegmentId segmentId, CancellationToken cancellationToken) =>
        _usenetClient.StatAsync(segmentId, cancellationToken);

    public override Task<UsenetHeadResponse> HeadAsync(
        SegmentId segmentId, CancellationToken cancellationToken) =>
        _usenetClient.HeadAsync(segmentId, cancellationToken);

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId, CancellationToken cancellationToken) =>
        _usenetClient.DecodedBodyAsync(segmentId, cancellationToken);

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
        SegmentId segmentId, CancellationToken cancellationToken) =>
        _usenetClient.DecodedArticleAsync(segmentId, cancellationToken);

    public override Task<UsenetDateResponse> DateAsync(
        CancellationToken cancellationToken) =>
        _usenetClient.DateAsync(cancellationToken);

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId, Action<ArticleBodyResult>? onConnectionReadyAgain, CancellationToken cancellationToken) =>
        _usenetClient.DecodedBodyAsync(segmentId, onConnectionReadyAgain, cancellationToken);

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
        SegmentId segmentId, Action<ArticleBodyResult>? onConnectionReadyAgain, CancellationToken cancellationToken) =>
        _usenetClient.DecodedArticleAsync(segmentId, onConnectionReadyAgain, cancellationToken);

    public override Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync(
        string segmentId, CancellationToken cancellationToken) =>
        _usenetClient.AcquireExclusiveConnectionAsync(segmentId, cancellationToken);

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId, UsenetExclusiveConnection exclusiveConnection, CancellationToken cancellationToken) =>
        _usenetClient.DecodedBodyAsync(segmentId, exclusiveConnection, cancellationToken);

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
        SegmentId segmentId, UsenetExclusiveConnection exclusiveConnection, CancellationToken cancellationToken) =>
        _usenetClient.DecodedArticleAsync(segmentId, exclusiveConnection, cancellationToken);

    public override Task<UsenetYencHeader> GetYencHeadersAsync(string segmentId, CancellationToken ct) =>
        _usenetClient.GetYencHeadersAsync(segmentId, ct);

    public virtual bool HasCachedBody(string segmentId) =>
        _usenetClient is ICachedSegmentReader cachedSegmentReader
        && cachedSegmentReader.HasCachedBody(segmentId);

    public virtual bool TryReadCachedBody(string segmentId, out UsenetDecodedBodyResponse response)
    {
        if (_usenetClient is ICachedSegmentReader cachedSegmentReader)
            return cachedSegmentReader.TryReadCachedBody(segmentId, out response);

        response = default!;
        return false;
    }

    public override NzbFileStream GetFileStream(
        NzbFile nzbFile,
        long fileSize,
        StreamingBufferSettings streamingBufferSettings,
        Action<int>? onSegmentIndexChanged
    ) => _usenetClient.GetFileStream(nzbFile, fileSize, streamingBufferSettings, onSegmentIndexChanged);

    public override NzbFileStream GetFileStream(
        string[] segmentIds,
        long fileSize,
        StreamingBufferSettings streamingBufferSettings,
        Action<int>? onSegmentIndexChanged
    ) => _usenetClient.GetFileStream(segmentIds, fileSize, streamingBufferSettings, onSegmentIndexChanged);

    public override NzbFileStream GetFileStream(
        string[] segmentIds,
        long fileSize,
        StreamingBufferSettings streamingBufferSettings,
        Action<int>? onSegmentIndexChanged,
        RequestHint requestHint
    ) => _usenetClient.GetFileStream(segmentIds, fileSize, streamingBufferSettings, onSegmentIndexChanged, requestHint);

    protected void ReplaceUnderlyingClient(INntpClient usenetClient)
    {
        var old = _usenetClient;
        _usenetClient = usenetClient;
        if (old is IDisposable disposable)
            disposable.Dispose();
    }

    public override void Dispose()
    {
        _usenetClient.Dispose();
        GC.SuppressFinalize(this);
    }
}
