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
        // store the DavItem being accessed in the http context
        httpContext.Items["DavItem"] = davNzbFile;

        // Set segment fetch context for tiered eviction
        var category = SegmentCategoryClassifier.Classify(davNzbFile.Name);
        var ownerId = davNzbFile.ParentId ?? davNzbFile.Id;
        httpContext.Items[SegmentFetchContext.HttpContextItemKey] = SegmentFetchContext.Set(category, ownerId);

        // return the stream
        var id = davNzbFile.Id;
        var file = await dbClient.GetNzbFileAsync(id, cancellationToken).ConfigureAwait(false);
        if (file is null) throw new FileNotFoundException($"Could not find nzb file with id: {id}");
        // Start read-ahead warming for video files
        if (warmingService != null && FilenameUtil.IsVideoFile(davNzbFile.Name))
        {
            var sessionId = warmingService.CreateSession(file.SegmentIds, cancellationToken);
            var stream = usenetClient.GetFileStream(
                file.SegmentIds,
                FileSize,
                StreamingBufferSettings.LiveDefault(configManager.GetArticleBufferSize()),
                index => warmingService.UpdatePosition(sessionId, index)
            );
            return new DisposableCallbackStream(stream,
                onDispose: () => warmingService.StopSession(sessionId),
                onDisposeAsync: () =>
                {
                    warmingService.StopSession(sessionId);
                    return ValueTask.CompletedTask;
                });
        }

        return usenetClient.GetFileStream(
            file.SegmentIds,
            FileSize,
            StreamingBufferSettings.LiveDefault(configManager.GetArticleBufferSize())
        );
    }
}
