using Jellyfin.Plugin.Nzbdav.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Nzbdav;

public class NzbdavLibrarySyncTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<NzbdavLibrarySyncTask> _logger;

    public NzbdavLibrarySyncTask(
        ILibraryManager libraryManager,
        ILogger<NzbdavLibrarySyncTask> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public string Name => "NZBDAV Library Sync";

    public string Key => "NzbdavLibrarySync";

    public string Description => "Synchronize Jellyfin library with NZBDAV content.";

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

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || string.IsNullOrEmpty(config.NzbdavBaseUrl))
        {
            _logger.LogWarning("NZBDAV plugin not configured, skipping library sync");
            return;
        }

        var client = new NzbdavApiClient(config);
        progress.Report(0);

        BrowseResponse? contentRoot;
        try
        {
            contentRoot = await client.BrowseAsync("content", cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "NZBDAV unreachable during library sync, will retry next cycle");
            progress.Report(100);
            return;
        }

        if (contentRoot is null || contentRoot.Items.Length == 0)
        {
            _logger.LogInformation("NZBDAV /content is empty, nothing to sync");
            progress.Report(100);
            return;
        }

        var categories = contentRoot.Items.Where(i => i.Type == "directory").ToArray();
        var totalItems = 0;
        var processedItems = 0;

        foreach (var category in categories)
        {
            var categoryContent = await client.BrowseAsync($"content/{category.Name}", cancellationToken)
                .ConfigureAwait(false);
            if (categoryContent != null)
                totalItems += categoryContent.Items.Length;
        }

        if (totalItems == 0)
        {
            progress.Report(100);
            return;
        }

        var existingNzbdavIds = GetExistingNzbdavIds();

        foreach (var category in categories)
        {
            var categoryContent = await client.BrowseAsync($"content/{category.Name}", cancellationToken)
                .ConfigureAwait(false);
            if (categoryContent is null) continue;

            foreach (var mountFolder in categoryContent.Items.Where(i => i.Type == "directory"))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    await ProcessMountFolder(client, category.Name, mountFolder, existingNzbdavIds, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to process mount folder {Name}", mountFolder.Name);
                }

                processedItems++;
                progress.Report((double)processedItems / totalItems * 100);
            }
        }

        _logger.LogInformation("NZBDAV library sync complete: processed {Count} mount folders", processedItems);
        progress.Report(100);
    }

    private async Task ProcessMountFolder(
        NzbdavApiClient client,
        string categoryName,
        BrowseItem mountFolder,
        HashSet<string> existingNzbdavIds,
        CancellationToken ct)
    {
        var contents = await client.BrowseAsync($"content/{categoryName}/{mountFolder.Name}", ct)
            .ConfigureAwait(false);
        if (contents is null) return;

        var mediaFiles = contents.Items
            .Where(i => i.Type is "nzb_file" or "rar_file" or "multipart_file")
            .Where(i => IsVideoFile(i.Name))
            .ToArray();

        if (mediaFiles.Length == 0) return;

        var primaryFile = mediaFiles.OrderByDescending(f => f.FileSize ?? 0).First();
        var nzbdavId = primaryFile.Id.ToString();
        if (existingNzbdavIds.Contains(nzbdavId))
            return;

        var isTvCategory = categoryName.Contains("tv", StringComparison.OrdinalIgnoreCase)
                           || categoryName.Contains("series", StringComparison.OrdinalIgnoreCase)
                           || categoryName.Contains("show", StringComparison.OrdinalIgnoreCase);

        if (isTvCategory)
        {
            _logger.LogDebug("TV content detected for {Name}, skipping auto-creation", mountFolder.Name);
            return;
        }

        var movie = new Movie
        {
            Name = mountFolder.Name,
            ProviderIds = new Dictionary<string, string>
            {
                ["NzbdavId"] = nzbdavId
            },
            IsVirtualItem = true
        };

        // Use a stable identifier as Path, not a signed URL (which expires in 24h).
        // NzbdavMediaSourceProvider generates fresh signed URLs on each playback request.
        movie.Path = $"nzbdav://{primaryFile.Id}";

        var meta = await client.GetMetaAsync(primaryFile.Id, ct).ConfigureAwait(false);
        if (meta != null)
            movie.Size = meta.FileSize ?? 0;

        _libraryManager.CreateItem(movie, null);
        existingNzbdavIds.Add(nzbdavId);
        _logger.LogInformation("Created Jellyfin movie: {Name} (NzbdavId: {Id})", mountFolder.Name, nzbdavId);
    }

    private HashSet<string> GetExistingNzbdavIds()
    {
        var query = new InternalItemsQuery
        {
            HasAnyProviderId = new Dictionary<string, string>
            {
                ["NzbdavId"] = string.Empty
            },
            Recursive = true
        };

        return _libraryManager.GetItemList(query)
            .Where(i => i.ProviderIds.ContainsKey("NzbdavId"))
            .Select(i => i.ProviderIds["NzbdavId"])
            .ToHashSet();
    }

    private static bool IsVideoFile(string filename)
    {
        var ext = Path.GetExtension(filename)?.ToLowerInvariant();
        return ext is ".mkv" or ".mp4" or ".avi" or ".mov" or ".wmv" or ".flv"
            or ".m4v" or ".ts" or ".m2ts" or ".webm" or ".mpg" or ".mpeg";
    }
}
