using NzbWebDAV.Clients.Usenet.Models;
using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet.Caching;

public sealed class LiveSegmentCachingNntpClient(
    INntpClient usenetClient,
    LiveSegmentCache liveSegmentCache
) : WrappingNntpClient(usenetClient)
{
    public override Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync(
        string segmentId,
        CancellationToken cancellationToken
    )
    {
        return Task.FromResult(new UsenetExclusiveConnection(onConnectionReadyAgain: null));
    }

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId,
        CancellationToken cancellationToken
    )
    {
        return DecodedBodyAsync(segmentId, onConnectionReadyAgain: null, cancellationToken);
    }

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
        SegmentId segmentId,
        CancellationToken cancellationToken
    )
    {
        return DecodedArticleAsync(segmentId, onConnectionReadyAgain: null, cancellationToken);
    }

    public override async Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken
    )
    {
        if (liveSegmentCache.TryReadBody(segmentId, out var cachedResponse))
        {
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
            return cachedResponse;
        }

        var ctx = SegmentFetchContext.GetCurrent();
        var category = ctx?.Category ?? SegmentCategory.Unknown;
        var ownerNzbId = ctx?.OwnerNzbId;

        LiveSegmentCache.BodyFetchResult cacheResult;
        try
        {
            cacheResult = await liveSegmentCache.GetOrAddBodyAsync(
                segmentId,
                async ct =>
                {
                    var response = await base.DecodedBodyAsync(segmentId, onConnectionReadyAgain, ct).ConfigureAwait(false);
                    var header = await response.Stream.GetYencHeadersAsync(ct).ConfigureAwait(false);
                    if (header is null)
                        throw new InvalidOperationException($"Failed to read yEnc headers for segment {segmentId}.");

                    return new LiveSegmentCache.BodyFetchSource(response.Stream, header, ArticleHeaders: null);
                },
                cancellationToken,
                category,
                ownerNzbId
            ).ConfigureAwait(false);
        }
        catch
        {
            if (onConnectionReadyAgain != null)
                onConnectionReadyAgain(ArticleBodyResult.NotRetrieved);
            throw;
        }

        if (cacheResult.UsedExistingFetch)
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);

        return cacheResult.Response;
    }

    public override async Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken
    )
    {
        if (liveSegmentCache.HasBody(segmentId))
        {
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
            return await liveSegmentCache.CreateArticleResponseAsync(
                segmentId,
                async ct => (await base.HeadAsync(segmentId, ct).ConfigureAwait(false)).ArticleHeaders,
                cancellationToken
            ).ConfigureAwait(false);
        }

        var ctx = SegmentFetchContext.GetCurrent();
        var category = ctx?.Category ?? SegmentCategory.Unknown;
        var ownerNzbId = ctx?.OwnerNzbId;

        LiveSegmentCache.BodyFetchResult cacheResult;
        try
        {
            cacheResult = await liveSegmentCache.GetOrAddBodyAsync(
                segmentId,
                async ct =>
                {
                    var response = await base.DecodedArticleAsync(segmentId, onConnectionReadyAgain, ct).ConfigureAwait(false);
                    var header = await response.Stream.GetYencHeadersAsync(ct).ConfigureAwait(false);
                    if (header is null)
                        throw new InvalidOperationException($"Failed to read yEnc headers for segment {segmentId}.");

                    return new LiveSegmentCache.BodyFetchSource(response.Stream, header, response.ArticleHeaders);
                },
                cancellationToken,
                category,
                ownerNzbId
            ).ConfigureAwait(false);
        }
        catch
        {
            if (onConnectionReadyAgain != null)
                onConnectionReadyAgain(ArticleBodyResult.NotRetrieved);
            throw;
        }

        if (cacheResult.UsedExistingFetch)
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);

        await cacheResult.Response.Stream.DisposeAsync().ConfigureAwait(false);
        return await liveSegmentCache.CreateArticleResponseAsync(
            segmentId,
            async ct => (await base.HeadAsync(segmentId, ct).ConfigureAwait(false)).ArticleHeaders,
            cancellationToken
        ).ConfigureAwait(false);
    }

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId,
        UsenetExclusiveConnection connection,
        CancellationToken cancellationToken
    )
    {
        return DecodedBodyAsync(segmentId, cancellationToken);
    }

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
        SegmentId segmentId,
        UsenetExclusiveConnection connection,
        CancellationToken cancellationToken
    )
    {
        return DecodedArticleAsync(segmentId, cancellationToken);
    }

    public override Task<UsenetYencHeader> GetYencHeadersAsync(string segmentId, CancellationToken ct)
    {
        return liveSegmentCache.GetOrAddHeaderAsync(segmentId, token => base.GetYencHeadersAsync(segmentId, token), ct);
    }
}
