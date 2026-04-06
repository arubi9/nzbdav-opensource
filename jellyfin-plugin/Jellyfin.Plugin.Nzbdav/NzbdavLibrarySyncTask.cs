using Jellyfin.Plugin.Nzbdav.Api;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Nzbdav;

/// <summary>
/// Syncs NZBDAV content to .strm files in a local directory.
/// Jellyfin's native library scanner picks up the .strm files and handles
/// metadata, artwork, and playback — no custom library providers needed.
/// </summary>
public class NzbdavLibrarySyncTask : IScheduledTask
{
    private readonly ILogger<NzbdavLibrarySyncTask> _logger;

    public NzbdavLibrarySyncTask(ILogger<NzbdavLibrarySyncTask> logger)
    {
        _logger = logger;
    }

    public string Name => "NZBDAV Library Sync";
    public string Key => "NzbdavLibrarySync";
    public string Description => "Sync NZBDAV content to .strm files for Jellyfin library scanning.";
    public string Category => "NZBDAV";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromMinutes(15).Ticks
            }
        ];
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken ct)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || string.IsNullOrEmpty(config.NzbdavBaseUrl))
        {
            _logger.LogWarning("NZBDAV plugin not configured — skipping sync");
            return;
        }

        if (string.IsNullOrEmpty(config.LibraryPath))
        {
            _logger.LogWarning("NZBDAV LibraryPath not configured — skipping sync");
            return;
        }

        var client = new NzbdavApiClient(config);
        progress.Report(0);

        // Per failure model: if NZBDAV is unreachable, log and retry next cycle
        BrowseResponse? contentRoot;
        try
        {
            contentRoot = await client.BrowseAsync("content", ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "NZBDAV unreachable — will retry next cycle");
            progress.Report(100);
            return;
        }

        if (contentRoot is null || contentRoot.Items.Length == 0)
        {
            progress.Report(100);
            return;
        }

        var categories = contentRoot.Items.Where(i => i.Type == "directory").ToArray();
        var processed = 0;
        var total = 0;

        // Count mount folders for progress
        foreach (var category in categories)
        {
            try
            {
                var catContent = await client.BrowseAsync($"content/{category.Name}", ct)
                    .ConfigureAwait(false);
                if (catContent != null)
                    total += catContent.Items.Count(i => i.Type == "directory");
            }
            catch { /* skip on error */ }
        }

        if (total == 0) { progress.Report(100); return; }

        // Process each category/mount folder → create .strm files
        foreach (var category in categories)
        {
            BrowseResponse? catContent;
            try
            {
                catContent = await client.BrowseAsync($"content/{category.Name}", ct)
                    .ConfigureAwait(false);
            }
            catch { continue; }

            if (catContent is null) continue;

            foreach (var mountFolder in catContent.Items.Where(i => i.Type == "directory"))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await SyncMountFolder(client, config, category.Name, mountFolder, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to sync {Name}", mountFolder.Name);
                }

                processed++;
                progress.Report((double)processed / total * 100);
            }
        }

        _logger.LogInformation("NZBDAV sync complete: {Count} mount folders processed", processed);
        progress.Report(100);
    }

    private async Task SyncMountFolder(
        NzbdavApiClient client,
        Configuration.PluginConfiguration config,
        string categoryName,
        BrowseItem mountFolder,
        CancellationToken ct)
    {
        var contents = await client.BrowseAsync(
            $"content/{categoryName}/{mountFolder.Name}", ct).ConfigureAwait(false);
        if (contents is null) return;

        var videoFiles = contents.Items
            .Where(i => i.Type is "nzb_file" or "rar_file" or "multipart_file")
            .Where(i => IsVideoFile(i.Name))
            .ToArray();

        if (videoFiles.Length == 0) return;

        // Create directory: {LibraryPath}/{category}/{mountFolderName}/
        var folderPath = Path.Combine(config.LibraryPath, categoryName, mountFolder.Name);
        Directory.CreateDirectory(folderPath);

        foreach (var videoFile in videoFiles)
        {
            var strmPath = Path.Combine(folderPath, Path.ChangeExtension(videoFile.Name, ".strm"));

            // Skip if .strm already exists (idempotent)
            if (File.Exists(strmPath)) continue;

            // .strm file contains a direct stream URL.
            // Uses API key (not signed token) because .strm files are persistent
            // and signed tokens expire after 24h.
            var streamUrl = $"{config.NzbdavBaseUrl.TrimEnd('/')}/api/stream/{videoFile.Id}?apikey={config.ApiKey}";
            await File.WriteAllTextAsync(strmPath, streamUrl, ct).ConfigureAwait(false);

            _logger.LogDebug("Created .strm: {Path}", strmPath);
        }
    }

    private static bool IsVideoFile(string filename)
    {
        var ext = Path.GetExtension(filename)?.ToLowerInvariant();
        return ext is ".mkv" or ".mp4" or ".avi" or ".mov" or ".wmv" or ".flv"
            or ".m4v" or ".ts" or ".m2ts" or ".webm" or ".mpg" or ".mpeg";
    }
}
