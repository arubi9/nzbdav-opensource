using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
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

public class DatabaseStoreRarFile(
    DavItem davRarFile,
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    UsenetStreamingClient usenetClient,
    ConfigManager configManager,
    ReadAheadWarmingService? warmingService = null
) : BaseStoreStreamFile(httpContext)
{
    public DavItem DavItem => davRarFile;
    public override string Name => davRarFile.Name;
    public override string UniqueKey => davRarFile.Id.ToString();
    public override long FileSize => davRarFile.FileSize!.Value;
    public override DateTime CreatedAt => davRarFile.CreatedAt;

    protected override async Task<Stream> GetStreamAsync(CancellationToken ct)
    {
        // store the DavItem being accessed in the http context
        httpContext.Items["DavItem"] = davRarFile;

        // Set segment fetch context for tiered eviction
        var category = SegmentCategoryClassifier.Classify(davRarFile.Name);
        var ownerId = davRarFile.ParentId ?? davRarFile.Id;
        httpContext.Items[SegmentFetchContext.HttpContextItemKey] = SegmentFetchContext.Set(category, ownerId);

        // return the stream
        var id = davRarFile.Id;
        var rarFile = await dbClient.Ctx.RarFiles.Where(x => x.Id == id).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (rarFile is null) throw new FileNotFoundException($"Could not find nzb file with id: {id}");
        var fileParts = rarFile.ToDavMultipartFileMeta().FileParts;
        var stream = (Stream)new DavMultipartFileStream(
            fileParts,
            usenetClient,
            StreamingBufferSettings.LiveDefault(configManager.GetArticleBufferSize())
        );

        // Start read-ahead warming for video files
        if (warmingService != null && FilenameUtil.IsVideoFile(davRarFile.Name))
        {
            var segmentIds = rarFile.ToDavMultipartFileMeta().FileParts
                .SelectMany(p => p.SegmentIds)
                .ToArray();
            var sessionId = warmingService.CreateSession(segmentIds, ct);
            stream = new DavMultipartFileStream(
                fileParts,
                usenetClient,
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

        return stream;
    }
}
