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

        // Set initial cache context — SmallFile for video files (pre-classification default).
        // When StreamClassifier commits to Playback, the context updates to VideoSegment.
        var isVideo = FilenameUtil.IsVideoFile(davNzbFile.Name);
        var category = isVideo ? SegmentCategory.SmallFile : SegmentCategoryClassifier.Classify(davNzbFile.Name);
        var ownerId = davNzbFile.ParentId ?? davNzbFile.Id;
        httpContext.Items[SegmentFetchContext.HttpContextItemKey] = SegmentFetchContext.Set(category, ownerId);

        var id = davNzbFile.Id;
        var file = await dbClient.GetNzbFileAsync(id, cancellationToken).ConfigureAwait(false);
        if (file is null) throw new FileNotFoundException($"Could not find nzb file with id: {id}");

        // Extract RequestHint from Range header
        var hint = GetRequestHint();

        // Create stream with classifier — warming starts lazily when classified as Playback
        string? warmingSessionId = null;
        var stream = usenetClient.GetFileStream(
            file.SegmentIds,
            FileSize,
            StreamingBufferSettings.LiveDefault(configManager.GetArticleBufferSize()),
            onSegmentIndexChanged: index =>
            {
                // Lazy warming: start when classifier commits to Playback
                if (warmingService != null && isVideo && warmingSessionId == null)
                {
                    warmingSessionId = warmingService.CreateSession(file.SegmentIds, cancellationToken);
                }

                if (warmingSessionId != null)
                    warmingService!.UpdatePosition(warmingSessionId, index);

                // Update cache context when past the probe window
                if (index >= 5 && isVideo)
                    SegmentFetchContext.UpdateCurrentCategory(SegmentCategory.VideoSegment);
            },
            requestHint: hint
        );

        // Clean up warming on dispose
        if (warmingService != null && isVideo)
        {
            return new DisposableCallbackStream(stream,
                onDispose: () => { if (warmingSessionId != null) warmingService.StopSession(warmingSessionId); },
                onDisposeAsync: () =>
                {
                    if (warmingSessionId != null) warmingService.StopSession(warmingSessionId);
                    return ValueTask.CompletedTask;
                });
        }

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
