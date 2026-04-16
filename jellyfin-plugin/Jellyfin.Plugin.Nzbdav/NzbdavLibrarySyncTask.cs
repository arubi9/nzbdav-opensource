using Jellyfin.Plugin.Nzbdav.Api;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace Jellyfin.Plugin.Nzbdav;

/// <summary>
/// Syncs NZBDAV content to .strm files using a single manifest HTTP request.
/// The manifest endpoint returns the entire /content tree as one JSON document,
/// ETag-versioned so subsequent syncs that find no changes make zero NZBDAV API calls.
/// </summary>
public class NzbdavLibrarySyncTask : IScheduledTask
{
    private readonly ILogger<NzbdavLibrarySyncTask> _logger;
    private string? _cachedETag;

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

        // Single HTTP request for entire content tree, ETag-cached
        ManifestResponse? manifest;
        string? newETag;
        try
        {
            (manifest, newETag) = await client.GetManifestAsync(_cachedETag, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "NZBDAV unreachable — will retry next cycle");
            progress.Report(100);
            return;
        }

        if (manifest is null)
        {
            // 304 Not Modified — nothing changed since last sync
            _logger.LogDebug("NZBDAV manifest unchanged (ETag match) — skipping sync");
            _cachedETag = newETag;
            progress.Report(100);
            return;
        }

        _cachedETag = newETag;

        if (manifest.Items.Length == 0)
        {
            ReconcileStaleFiles(config, expectedRelativePaths: [], runId: CreateRunId());
            progress.Report(100);
            return;
        }

        var allItems = manifest.Items.ToDictionary(i => i.Id);
        var expectedRelativePaths = BuildExpectedStrmRelativePaths(manifest.Items, allItems);
        var runId = CreateRunId();

        // Find all video files
        var videoFiles = manifest.Items
            .Where(i => i.Type is "nzb_file" or "rar_file" or "multipart_file")
            .Where(i => IsVideoFile(i.Name))
            .ToArray();

        var processed = 0;
        foreach (var videoFile in videoFiles)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await SyncVideoFile(config, client, videoFile, allItems, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to sync {Name}", videoFile.Name);
            }

            processed++;
            progress.Report((double)processed / videoFiles.Length * 100);
        }

        ReconcileStaleFiles(config, expectedRelativePaths, runId);
        _logger.LogInformation("NZBDAV sync complete: {Count} video files processed from manifest", processed);
        progress.Report(100);
    }

    private async Task SyncVideoFile(
        Configuration.PluginConfiguration config,
        NzbdavApiClient client,
        ManifestItem videoFile,
        Dictionary<Guid, ManifestItem> allItems,
        CancellationToken ct)
    {
        // Build the filesystem path from the manifest path
        // e.g., /content/movies/MovieName/movie.mkv → {LibraryPath}/movies/MovieName/movie.strm
        var relativePath = BuildStrmRelativePath(videoFile, allItems);

        var strmRelativePath = Path.ChangeExtension(relativePath, ".strm");
        var strmPath = Path.Combine(config.LibraryPath, strmRelativePath);

        // Ensure parent directory exists
        var strmDir = Path.GetDirectoryName(strmPath);
        if (strmDir != null) Directory.CreateDirectory(strmDir);

        var streamUrl = $"{config.NzbdavBaseUrl.TrimEnd('/')}/api/stream/{videoFile.Id}?apikey={config.ApiKey}";
        File.WriteAllText(strmPath, streamUrl);

        // If probe data is available, write it alongside the .strm so Jellyfin
        // can read media info without probing the stream via NNTP.
        if (videoFile.HasProbeData)
        {
            var probePath = Path.ChangeExtension(strmPath, ".mediainfo.json");
            if (!File.Exists(probePath))
            {
                var probeData = await client.GetProbeDataAsync(videoFile.Id, ct).ConfigureAwait(false);
                if (probeData != null)
                {
                    File.WriteAllText(probePath, probeData);
                    _logger.LogDebug("Wrote probe data: {Path}", probePath);
                }
            }
        }

        _logger.LogDebug("Created/refreshed .strm: {Path}", strmPath);
    }

    private void ReconcileStaleFiles(
        Configuration.PluginConfiguration config,
        IReadOnlyCollection<string> expectedRelativePaths,
        string runId)
    {
        var libraryRoot = config.LibraryPath;
        if (!Directory.Exists(libraryRoot))
            return;

        var expected = expectedRelativePaths
            .Select(NormalizeRelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var quarantineRoot = Path.Combine(libraryRoot, ".quarantine", runId);
        EnsureQuarantineRoot(libraryRoot);
        var quarantinedCount = 0;

        foreach (var strmPath in Directory.EnumerateFiles(libraryRoot, "*.strm", SearchOption.AllDirectories))
        {
            try
            {
                var relativePath = Path.GetRelativePath(libraryRoot, strmPath);
                if (IsUnderQuarantine(relativePath))
                    continue;

                var strmContent = File.ReadLines(strmPath).FirstOrDefault();
                if (!IsNzbdavManagedStrmContent(strmContent ?? string.Empty, config.NzbdavBaseUrl))
                    continue;

                var normalizedRelativePath = NormalizeRelativePath(relativePath);
                if (expected.Contains(normalizedRelativePath))
                    continue;

                var quarantineRelativePath = GetQuarantineRelativePath(relativePath, runId);
                var quarantineStrmPath = Path.Combine(libraryRoot, quarantineRelativePath);
                MoveFilePreservingStructure(strmPath, quarantineStrmPath);

                var probePath = Path.ChangeExtension(strmPath, ".mediainfo.json");
                if (File.Exists(probePath))
                {
                    var quarantineProbePath = Path.Combine(
                        libraryRoot,
                        GetQuarantineRelativePath(Path.ChangeExtension(relativePath, ".mediainfo.json"), runId));
                    MoveFilePreservingStructure(probePath, quarantineProbePath);
                }

                quarantinedCount++;
                _logger.LogInformation("Quarantined stale NZBDAV mirror file: {Path}", normalizedRelativePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to quarantine stale NZBDAV mirror file: {Path}", strmPath);
            }
        }

        if (quarantinedCount > 0)
        {
            _logger.LogInformation(
                "Quarantined {Count} stale NZBDAV mirror file(s) into {QuarantineRoot}. Retention is manual.",
                quarantinedCount,
                quarantineRoot);
        }
    }

    private static string[] BuildExpectedStrmRelativePaths(
        ManifestItem[] items,
        IReadOnlyDictionary<Guid, ManifestItem> allItems)
    {
        return items
            .Where(i => i.Type is "nzb_file" or "rar_file" or "multipart_file")
            .Where(i => IsVideoFile(i.Name))
            .Select(i => Path.ChangeExtension(BuildStrmRelativePath(i, allItems), ".strm"))
            .Select(NormalizeRelativePath)
            .ToArray();
    }

    private static bool IsNzbdavManagedStrmContent(string content, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(baseUrl))
            return false;

        if (!Uri.TryCreate(content.Trim(), UriKind.Absolute, out var contentUri))
            return false;

        var trimmedBaseUrl = baseUrl.TrimEnd('/');
        if (!Uri.TryCreate(trimmedBaseUrl, UriKind.Absolute, out var baseUri))
            return false;

        return string.Equals(contentUri.Scheme, baseUri.Scheme, StringComparison.OrdinalIgnoreCase)
               && string.Equals(contentUri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase)
               && contentUri.Port == baseUri.Port
               && contentUri.AbsolutePath.StartsWith("/api/stream/", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetQuarantineRelativePath(string relativePath, string runId)
    {
        return Path.Combine(".quarantine", runId, relativePath + ".quarantined");
    }

    private static bool IsUnderQuarantine(string relativePath)
    {
        return NormalizeRelativePath(relativePath).StartsWith(".quarantine/", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRelativePath(string path)
    {
        return path.Replace('\\', '/');
    }

    private static void MoveFilePreservingStructure(string sourcePath, string destinationPath)
    {
        var destinationDir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(destinationDir))
            Directory.CreateDirectory(destinationDir);

        if (File.Exists(destinationPath))
            File.Delete(destinationPath);

        File.Move(sourcePath, destinationPath);
    }

    private static void EnsureQuarantineRoot(string libraryRoot)
    {
        var quarantineRoot = Path.Combine(libraryRoot, ".quarantine");
        Directory.CreateDirectory(quarantineRoot);
        var noMediaPath = Path.Combine(quarantineRoot, ".nomedia");
        if (!File.Exists(noMediaPath))
            File.WriteAllText(noMediaPath, "NZBDAV quarantine - do not scan");
    }

    private static string CreateRunId()
    {
        return DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff'Z'", CultureInfo.InvariantCulture)
               + "-"
               + Guid.NewGuid().ToString("N")[..8];
    }

    private static bool IsVideoFile(string filename)
    {
        var ext = Path.GetExtension(filename)?.ToLowerInvariant();
        return ext is ".mkv" or ".mp4" or ".avi" or ".mov" or ".wmv" or ".flv"
            or ".m4v" or ".ts" or ".m2ts" or ".webm" or ".mpg" or ".mpeg";
    }

    private static string BuildStrmRelativePath(ManifestItem videoFile, IReadOnlyDictionary<Guid, ManifestItem> allItems)
    {
        var relativePath = videoFile.Path;
        if (relativePath.StartsWith("/content/", StringComparison.OrdinalIgnoreCase))
            relativePath = relativePath["/content/".Length..];

        var fileName = Path.GetFileNameWithoutExtension(relativePath);
        var ext = Path.GetExtension(videoFile.Name);
        if (LooksObfuscated(fileName) && videoFile.ParentId.HasValue
            && allItems.TryGetValue(videoFile.ParentId.Value, out var parent)
            && parent.Type == "directory")
        {
            var parentDir = Path.GetDirectoryName(relativePath);
            relativePath = Path.Combine(parentDir ?? "", parent.Name + ext);
        }

        return relativePath;
    }

    /// <summary>
    /// Detects obfuscated filenames — random alphanumeric strings that Usenet uploaders
    /// use to evade DMCA bots. Real release names contain dots or hyphens as separators
    /// (e.g., "Family.Guy.S24E07.1080p"); obfuscated names are a single run of letters/digits
    /// (e.g., "W6Ss3ROn1dPrVxlU916rJLYwTk6QbtDe").
    /// </summary>
    private static bool LooksObfuscated(string name)
    {
        if (name.Length < 12) return false;
        // Real release names have dots, hyphens, spaces, or underscores as word separators
        if (name.IndexOfAny(['.', '-', ' ', '_']) >= 0) return false;

        var hasDigit = false;
        var hasUpper = false;
        var hasLower = false;
        foreach (var c in name)
        {
            if (!char.IsLetterOrDigit(c))
                return false;
            if (char.IsDigit(c)) hasDigit = true;
            if (char.IsUpper(c)) hasUpper = true;
            if (char.IsLower(c)) hasLower = true;
        }

        return hasDigit && hasUpper && hasLower;
    }
}
