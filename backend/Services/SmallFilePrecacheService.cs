using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Caching;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Queue;
using Serilog;

namespace NzbWebDAV.Services;

public class SmallFilePrecacheService(
    QueueManager queueManager,
    UsenetStreamingClient usenetClient,
    LiveSegmentCache liveSegmentCache,
    ConfigManager configManager
) : BackgroundService
{
    private readonly Channel<QueueManager.NzbProcessedEventArgs> _channel =
        Channel.CreateBounded<QueueManager.NzbProcessedEventArgs>(100);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        queueManager.OnNzbProcessed += (_, args) =>
        {
            if (configManager.IsPrecacheEnabled())
                _channel.Writer.TryWrite(args);
        };

        await foreach (var args in _channel.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await PrecacheSmallFilesForJob(args, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                Log.Debug($"Pre-cache error for job {args.JobName}: {e.Message}");
            }
        }
    }

    private async Task PrecacheSmallFilesForJob(
        QueueManager.NzbProcessedEventArgs args,
        CancellationToken ct
    )
    {
        // Look up the history item to find the mount folder
        await using var dbContext = new DavDatabaseContext();
        var historyItem = await dbContext.HistoryItems
            .FirstOrDefaultAsync(h => h.Id == args.QueueItemId, ct)
            .ConfigureAwait(false);

        if (historyItem?.DownloadDirId is null) return;
        var mountFolderId = historyItem.DownloadDirId.Value;

        // Get children of the mount folder
        var children = await dbContext.Items
            .Where(x => x.ParentId == mountFolderId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var maxFileSize = configManager.GetPrecacheMaxFileSize();
        var smallFiles = children
            .Where(x => x.FileSize.HasValue && x.FileSize.Value <= maxFileSize)
            .Where(x => SegmentCategoryClassifier.Classify(x.Name) == SegmentCategory.SmallFile)
            .ToList();

        if (smallFiles.Count == 0) return;

        Log.Information($"Pre-caching {smallFiles.Count} small files for {args.JobName}");

        foreach (var file in smallFiles)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var segmentIds = await GetSegmentIdsForFile(dbContext, file, ct).ConfigureAwait(false);
                if (segmentIds is null) continue;

                var ownerId = file.ParentId ?? file.Id;
                using var fetchCtx = SegmentFetchContext.Set(SegmentCategory.SmallFile, ownerId);

                foreach (var segmentId in segmentIds)
                {
                    if (ct.IsCancellationRequested) break;
                    if (liveSegmentCache.HasBody(segmentId)) continue;

                    try
                    {
                        var response = await usenetClient
                            .DecodedBodyWithFallbackAsync(segmentId, ct)
                            .ConfigureAwait(false);
                        await response.Stream.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception e) when (!ct.IsCancellationRequested)
                    {
                        Log.Debug($"Pre-cache segment fetch failed: {e.Message}");
                    }
                }
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                Log.Debug($"Pre-cache failed for file {file.Name}: {e.Message}");
            }
        }
    }

    private static async Task<string[]?> GetSegmentIdsForFile(
        DavDatabaseContext dbContext,
        DavItem file,
        CancellationToken ct
    )
    {
        return file.Type switch
        {
            DavItem.ItemType.NzbFile =>
                (await dbContext.NzbFiles.FirstOrDefaultAsync(x => x.Id == file.Id, ct).ConfigureAwait(false))
                    ?.SegmentIds,
            DavItem.ItemType.RarFile =>
                (await dbContext.RarFiles.FirstOrDefaultAsync(x => x.Id == file.Id, ct).ConfigureAwait(false))
                    ?.ToDavMultipartFileMeta().FileParts.SelectMany(p => p.SegmentIds).ToArray(),
            DavItem.ItemType.MultipartFile =>
                (await dbContext.MultipartFiles.FirstOrDefaultAsync(x => x.Id == file.Id, ct).ConfigureAwait(false))
                    ?.Metadata.FileParts.SelectMany(p => p.SegmentIds).ToArray(),
            _ => null
        };
    }
}
