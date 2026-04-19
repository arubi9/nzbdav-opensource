using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Api.Controllers.GetWebdavItem;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;
using NzbWebDAV.WebDav;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Tasks;

public class BackfillStrmFilesTask(
    ConfigManager configManager,
    DavDatabaseClient dbClient,
    WebsocketManager websocketManager,
    bool forceOverwrite = false
) : BaseTask
{
    protected override async Task ExecuteInternal()
    {
        try
        {
            var ct = SigtermUtil.GetCancellationToken();
            await BackfillAllStrmFiles(ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Report($"Failed: {e.Message}");
            Log.Error(e, "Failed to backfill strm files.");
        }
    }

    private async Task BackfillAllStrmFiles(CancellationToken token)
    {
        var completedDownloadDir = configManager.GetStrmCompletedDownloadDir();
        var baseUrl = configManager.GetBaseUrl().TrimEnd('/');
        var strmKey = configManager.GetStrmKey();

        var videoItems = await dbClient.Ctx.Items
            .AsNoTracking()
            .Where(x => x.Path.StartsWith("/content/"))
            .Where(x => x.Type != DavItem.ItemType.Directory)
            .ToListAsync(token)
            .ConfigureAwait(false);

        var eligibleItems = videoItems
            .Where(x => FilenameUtil.IsVideoFile(x.Name))
            .ToList();

        var written = 0;
        var skipped = 0;
        var updated = 0;
        var debounce = DebounceUtil.CreateDebounce(TimeSpan.FromMilliseconds(500));

        foreach (var item in eligibleItems)
        {
            token.ThrowIfCancellationRequested();

            var strmPath = GetStrmFilePath(item, completedDownloadDir);
            var pathUrl = DatabaseStoreSymlinkFile.GetTargetPath(item.Id, "", '/').TrimStart('/');
            var downloadKey = GetWebdavItemRequest.GenerateDownloadKey(strmKey, pathUrl);
            var extension = Path.GetExtension(item.Name).ToLower().TrimStart('.');
            var targetUrl = $"{baseUrl}/view/{pathUrl}?downloadKey={downloadKey}&extension={extension}";

            if (File.Exists(strmPath))
            {
                if (!forceOverwrite)
                {
                    skipped++;
                    continue;
                }

                var existing = await File.ReadAllTextAsync(strmPath, token).ConfigureAwait(false);
                if (existing.Trim() == targetUrl)
                {
                    skipped++;
                    continue;
                }

                await File.WriteAllTextAsync(strmPath, targetUrl, token).ConfigureAwait(false);
                updated++;
            }
            else
            {
                var dir = Path.GetDirectoryName(strmPath);
                if (dir != null)
                    Directory.CreateDirectory(dir);

                await File.WriteAllTextAsync(strmPath, targetUrl, token).ConfigureAwait(false);
                written++;
            }

            debounce(() => ReportProgress(written, updated, skipped, eligibleItems.Count));
        }

        ReportProgress(written, updated, skipped, eligibleItems.Count);
        Report($"Done! New: {written}, Updated: {updated}, Unchanged: {skipped}, Total eligible: {eligibleItems.Count}");
    }

    private static string GetStrmFilePath(DavItem davItem, string completedDownloadDir)
    {
        var path = davItem.Path + ".strm";
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.Join(completedDownloadDir, Path.Join(parts[2..]));
    }

    private void Report(string message)
    {
        _ = websocketManager.SendMessage(WebsocketTopic.StrmToSymlinksTaskProgress, message);
    }

    private void ReportProgress(int written, int updated, int skipped, int total)
    {
        Report($"Backfilling strm files... New: {written}, Updated: {updated}, Unchanged: {skipped}, Total: {total}");
    }
}
