using Microsoft.AspNetCore.Http;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Caching;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Streams;
using NzbWebDAV.Utils;
using NzbWebDAV.WebDav.Base;

namespace NzbWebDAV.WebDav;

public class DatabaseStoreNzbFile(
    DavItem davNzbFile,
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    INntpClient usenetClient,
    ConfigManager configManager,
    ReadAheadWarmingService? warmingService = null
) : BaseStoreStreamFile(httpContext)
{
    public DavItem DavItem => davNzbFile;
    public override string Name => davNzbFile.Name;
    public override string UniqueKey => davNzbFile.Id.ToString();
    public override long FileSize => davNzbFile.FileSize!.Value;
    public override DateTime CreatedAt => davNzbFile.CreatedAt;

    protected override async Task<Stream> GetStreamAsync(CancellationToken cancellationToken)
    {
        httpContext.Items["DavItem"] = davNzbFile;

        // Initial cache tier: videos start as SmallFile (cheap to keep around during
        // the probe window). When the StreamClassifier commits to Playback, the
        // NzbFileStream mutates this SAME instance's Category → VideoSegment via
        // the reference we hand it below. This is the C2 fix — we mutate one
        // AsyncLocal instance instead of re-assigning AsyncLocal.Value, which
        // does not flow upward out of a child async frame.
        var isVideo = FilenameUtil.IsVideoFile(davNzbFile.Name);
        var initialCategory = isVideo
            ? SegmentCategory.SmallFile
            : SegmentCategoryClassifier.Classify(davNzbFile.Name);
        var ownerId = davNzbFile.ParentId ?? davNzbFile.Id;
        var segmentContext = SegmentFetchContext.SetReturningContext(
            initialCategory, ownerId, out var contextScope);
        httpContext.Items[SegmentFetchContext.HttpContextItemKey] = contextScope;

        var id = davNzbFile.Id;
        var file = await dbClient.GetNzbFileAsync(id, cancellationToken).ConfigureAwait(false);
        if (file is null) throw new FileNotFoundException($"Could not find nzb file with id: {id}");

        // Extract RequestHint from Range header
        var hint = GetRequestHint();

        // warmingSessionId is captured by both the onClassifiedAsPlayback callback
        // (which writes it) and the onSegmentIndexChanged callback (which reads it
        // to forward progress). Both run on the stream's read loop so we don't
        // need thread-safety primitives.
        string? warmingSessionId = null;

        var stream = usenetClient.GetFileStream(
            file.SegmentIds,
            FileSize,
            StreamingBufferSettings.LiveDefault(configManager.GetArticleBufferSize()),
            onSegmentIndexChanged: index =>
            {
                if (warmingSessionId != null)
                    warmingService?.UpdatePosition(warmingSessionId, index);
            },
            requestHint: hint
        );

        // C1 fix: warming is now classifier-driven — starts exactly when
        // NzbFileStream's classifier commits to Playback, not on the first
        // segment read (which fires for probes too).
        if (warmingService != null && isVideo)
        {
            stream.BindClassifierCallbacks(
                segmentContext: segmentContext,
                onClassifiedAsPlayback: () =>
                {
                    warmingSessionId = warmingService.CreateSession(file.SegmentIds, cancellationToken);
                });

            return new DisposableCallbackStream(stream,
                onDispose: () =>
                {
                    if (warmingSessionId != null) warmingService.StopSession(warmingSessionId);
                },
                onDisposeAsync: () =>
                {
                    if (warmingSessionId != null) warmingService.StopSession(warmingSessionId);
                    return ValueTask.CompletedTask;
                });
        }

        // Non-video file: still bind the context so the classifier can upgrade
        // tier if classification unexpectedly commits to Playback on, say, an
        // image file that FFmpeg decides to fully decode.
        stream.BindClassifierCallbacks(segmentContext, onClassifiedAsPlayback: null);
        return stream;
    }

    private RequestHint GetRequestHint()
    {
        var rangeHeader = httpContext.Request.Headers.Range.FirstOrDefault();
        if (string.IsNullOrEmpty(rangeHeader)) return RequestHint.Unknown;

        // bytes=0-65535 or bytes=0-1048575 (< 1MB explicit end) → SuspectedProbe
        if (rangeHeader.StartsWith("bytes=0-", StringComparison.OrdinalIgnoreCase))
        {
            var endStr = rangeHeader["bytes=0-".Length..];
            if (long.TryParse(endStr, out var end) && end < 1_048_576)
                return RequestHint.SuspectedProbe;
        }

        return RequestHint.Unknown;
    }
}
