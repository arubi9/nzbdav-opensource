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

public class DatabaseStoreMultipartFile(
    DavItem davMultipartFile,
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    UsenetStreamingClient usenetClient,
    ConfigManager configManager,
    ReadAheadWarmingService? warmingService = null
) : BaseStoreStreamFile(httpContext)
{
    public DavItem DavItem => davMultipartFile;
    public override string Name => davMultipartFile.Name;
    public override string UniqueKey => davMultipartFile.Id.ToString();
    public override long FileSize => davMultipartFile.FileSize!.Value;
    public override DateTime CreatedAt => davMultipartFile.CreatedAt;

    protected override async Task<Stream> GetStreamAsync(CancellationToken ct)
    {
        // store the DavItem being accessed in the http context
        httpContext.Items["DavItem"] = davMultipartFile;

        // Set segment fetch context for tiered eviction
        var category = SegmentCategoryClassifier.Classify(davMultipartFile.Name);
        var ownerId = davMultipartFile.ParentId ?? davMultipartFile.Id;
        httpContext.Items[SegmentFetchContext.HttpContextItemKey] = SegmentFetchContext.Set(category, ownerId);

        // return the stream
        var id = davMultipartFile.Id;
        var multipartFile = await dbClient.Ctx.MultipartFiles.Where(x => x.Id == id).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (multipartFile is null) throw new FileNotFoundException($"Could not find nzb file with id: {id}");
        var packedStream = new DavMultipartFileStream(
            multipartFile.Metadata.FileParts,
            usenetClient,
            StreamingBufferSettings.LiveDefault(configManager.GetArticleBufferSize())
        );
        Stream stream = multipartFile.Metadata.AesParams != null
            ? new AesDecoderStream(packedStream, multipartFile.Metadata.AesParams)
            : packedStream;

        // Start read-ahead warming for video files
        if (warmingService != null && FilenameUtil.IsVideoFile(davMultipartFile.Name))
        {
            var segmentIds = multipartFile.Metadata.FileParts
                .SelectMany(p => p.SegmentIds)
                .ToArray();
            var sessionId = warmingService.CreateSession(segmentIds, ct);
            packedStream = new DavMultipartFileStream(
                multipartFile.Metadata.FileParts,
                usenetClient,
                StreamingBufferSettings.LiveDefault(configManager.GetArticleBufferSize()),
                index => warmingService.UpdatePosition(sessionId, index)
            );
            stream = multipartFile.Metadata.AesParams != null
                ? new AesDecoderStream(packedStream, multipartFile.Metadata.AesParams)
                : packedStream;
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
