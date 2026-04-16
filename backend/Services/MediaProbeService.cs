using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Caching;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Queue;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// After NZB processing completes, probes video files with FFmpeg to generate
/// MediaInfo that Jellyfin can read from .nfo sidecars — eliminating the need
/// for Jellyfin to probe streams via NNTP during library scans.
///
/// FFmpeg is optional. If not found on PATH, the service logs a warning and
/// does nothing. The system falls back to Approach B (probe segments cached
/// as SmallFile tier on first Jellyfin scan).
/// </summary>
public class MediaProbeService : BackgroundService
{
    private readonly QueueManager _queueManager;
    private readonly UsenetStreamingClient _usenetClient;
    private readonly LiveSegmentCache _liveSegmentCache;
    private readonly ConfigManager _configManager;
    private readonly Channel<QueueManager.NzbProcessedEventArgs> _channel;
    private readonly string? _ffprobePath;

    public MediaProbeService(
        QueueManager queueManager,
        UsenetStreamingClient usenetClient,
        LiveSegmentCache liveSegmentCache,
        ConfigManager configManager)
    {
        _queueManager = queueManager;
        _usenetClient = usenetClient;
        _liveSegmentCache = liveSegmentCache;
        _configManager = configManager;
        _channel = Channel.CreateBounded<QueueManager.NzbProcessedEventArgs>(100);
        _ffprobePath = FindFfprobe();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_ffprobePath is null)
        {
            Log.Information("FFmpeg/ffprobe not found on PATH — media pre-probing disabled. " +
                            "Jellyfin will probe streams on first scan (cached after first access).");
            return;
        }

        Log.Information("FFmpeg pre-probing enabled via {Path}", _ffprobePath);

        // Backfill probe data for items processed before ffprobe was available
        _ = Task.Run(() => BackfillMissingProbes(stoppingToken), stoppingToken);

        _queueManager.OnNzbProcessed += (_, args) => _channel.Writer.TryWrite(args);

        await foreach (var args in _channel.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await ProbeFilesForJob(args, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                Log.Debug("Media probe error for job {JobName}: {Error}", args.JobName, e.Message);
            }
        }
    }

    private async Task BackfillMissingProbes(CancellationToken ct)
    {
        try
        {
            // Wait for services to stabilize
            await Task.Delay(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);

            await using var dbContext = new DavDatabaseContext();
            var videoItems = await dbContext.Items
                .AsNoTracking()
                .Where(x => x.Path.StartsWith("/content/") &&
                            x.Type != DavItem.ItemType.Directory &&
                            x.FileSize != null && x.FileSize > 0)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            var cacheDir = _liveSegmentCache.CacheDirectory;
            var missing = videoItems
                .Where(x => FilenameUtil.IsVideoFile(x.Name))
                .Where(x => !File.Exists(Path.Combine(cacheDir, $"probe-{x.Id:N}.json")))
                .ToList();

            if (missing.Count == 0)
            {
                Log.Information("ProbeDataGenerator backfill: all {Total} items already have probe data", videoItems.Count);
                return;
            }

            Log.Information("ProbeDataGenerator backfill: {Missing}/{Total} items need probe data", missing.Count, videoItems.Count);

            await ProcessBackfillBatchAsync(
                missing,
                (item, innerCt) => ProbeAndCacheFile(new DavDatabaseContext(), item, innerCt),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e)
        {
            Log.Warning(e, "ProbeDataGenerator backfill failed");
        }
    }

    /// <summary>
    /// Iterates the backfill batch under a Low-priority download scope so that
    /// NNTP connections acquired by the processor (e.g. first/last-segment
    /// prefetch) yield to live streams. The 80/20 priority-odds guard in
    /// <c>PrioritizedSemaphore</c> still prevents starvation of the backfill.
    /// </summary>
    internal static async Task ProcessBackfillBatchAsync(
        IReadOnlyList<DavItem> items,
        Func<DavItem, CancellationToken, Task> processItem,
        CancellationToken ct)
    {
        using var priorityScope = ct.SetContext(
            new DownloadPriorityContext { Priority = SemaphorePriority.Low });

        var generated = 0;
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await processItem(item, ct).ConfigureAwait(false);
                generated++;
                if (generated % 20 == 0)
                    Log.Information("ProbeDataGenerator backfill progress: {Done}/{Total}", generated, items.Count);
            }
            catch (Exception e)
            {
                Log.Debug("Backfill probe failed for {Name}: {Error}", item.Name, e.Message);
            }
        }

        Log.Information("ProbeDataGenerator backfill complete: {Generated}/{Missing} probes created", generated, items.Count);
    }

    private async Task ProbeFilesForJob(QueueManager.NzbProcessedEventArgs args, CancellationToken ct)
    {
        await using var dbContext = new DavDatabaseContext();
        var historyItem = await dbContext.HistoryItems
            .FirstOrDefaultAsync(h => h.Id == args.QueueItemId, ct)
            .ConfigureAwait(false);

        if (historyItem?.DownloadDirId is null) return;

        var children = await dbContext.Items
            .Where(x => x.ParentId == historyItem.DownloadDirId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var videoFiles = children
            .Where(x => FilenameUtil.IsVideoFile(x.Name))
            .ToList();

        if (videoFiles.Count == 0) return;

        foreach (var videoFile in videoFiles)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await ProbeAndCacheFile(dbContext, videoFile, ct).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Log.Debug("Failed to probe {Name}: {Error}", videoFile.Name, e.Message);
            }
        }
    }

    private async Task ProbeAndCacheFile(DavDatabaseContext dbContext, DavItem videoFile, CancellationToken ct)
    {
        // Get segment IDs for this file
        var segmentIds = await GetSegmentIds(dbContext, videoFile, ct).ConfigureAwait(false);
        if (segmentIds is null || segmentIds.Length == 0) return;

        // Fetch first and last segments into cache (SmallFile tier via SegmentFetchContext)
        using var fetchCtx = SegmentFetchContext.Set(SegmentCategory.SmallFile, videoFile.ParentId ?? videoFile.Id);

        // First segment
        if (!_liveSegmentCache.HasBody(segmentIds[0]))
        {
            try
            {
                var response = await _usenetClient
                    .DecodedBodyWithFallbackAsync(segmentIds[0], ct)
                    .ConfigureAwait(false);
                await response.Stream.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Log.Debug("Failed to cache first segment of {Name}: {Error}", videoFile.Name, e.Message);
            }
        }

        // Last segment
        var lastSegmentId = segmentIds[^1];
        if (!_liveSegmentCache.HasBody(lastSegmentId))
        {
            try
            {
                var response = await _usenetClient
                    .DecodedBodyWithFallbackAsync(lastSegmentId, ct)
                    .ConfigureAwait(false);
                await response.Stream.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Log.Debug("Failed to cache last segment of {Name}: {Error}", videoFile.Name, e.Message);
            }
        }

        // Run ffprobe against the NZBDAV stream URL (which now serves from cache)
        var baseUrl = _configManager.GetBaseUrl().TrimEnd('/');
        var apiKey = _configManager.GetApiKey();
        var streamUrl = $"{baseUrl}/api/stream/{videoFile.Id}?apikey={apiKey}";

        var probeResult = await RunFfprobe(streamUrl, ct).ConfigureAwait(false);
        if (probeResult is null) return;

        // Write .mediainfo.json sidecar to cache directory
        var probeFilePath = Path.Combine(_liveSegmentCache.CacheDirectory, $"probe-{videoFile.Id:N}.json");
        await File.WriteAllTextAsync(probeFilePath, probeResult, ct).ConfigureAwait(false);

        Log.Debug("Pre-probed {Name} — mediainfo cached", videoFile.Name);
    }

    private async Task<string?> RunFfprobe(string url, CancellationToken ct)
    {
        if (_ffprobePath is null) return null;

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = _ffprobePath,
                Arguments = $"-v quiet -print_format json -show_format -show_streams \"{url}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process.Start();

            // I5 fix: drain stdout + stderr concurrently with WaitForExit. The
            // previous sequential read-then-wait could deadlock when ffprobe
            // filled its stderr buffer (e.g. on a stream it can't decode) —
            // WaitForExit would hang forever because stderr was never drained.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            var exitTask = process.WaitForExitAsync(ct);
            await Task.WhenAll(stdoutTask, stderrTask, exitTask).ConfigureAwait(false);

            var output = await stdoutTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                var error = await stderrTask.ConfigureAwait(false);
                Log.Debug("ffprobe failed (exit {Code}): {Error}", process.ExitCode, error);
                return null;
            }

            // Validate it's valid JSON
            JsonDocument.Parse(output).Dispose();
            return output;
        }
        catch (Exception e)
        {
            Log.Debug("ffprobe execution failed: {Error}", e.Message);
            return null;
        }
    }

    private static async Task<string[]?> GetSegmentIds(
        DavDatabaseContext dbContext, DavItem file, CancellationToken ct)
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

    private static string? FindFfprobe()
    {
        // Check common locations
        var candidates = new[]
        {
            "ffprobe",
            "/usr/bin/ffprobe",
            "/usr/local/bin/ffprobe",
            "/opt/ffmpeg/bin/ffprobe"
        };

        foreach (var candidate in candidates)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = candidate,
                    Arguments = "-version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                if (process != null)
                {
                    process.WaitForExit(5000);
                    if (process.ExitCode == 0)
                        return candidate;
                }
            }
            catch
            {
                // Not found at this path, try next
            }
        }

        return null;
    }
}
