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
using UsenetSharp.Models;

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
            }
            else
            {
                Log.Information("ProbeDataGenerator backfill: {Missing}/{Total} items need probe data", missing.Count, videoItems.Count);

                await ProcessBackfillBatchAsync(
                    missing,
                    (item, innerCt) => ProbeAndCacheFile(new DavDatabaseContext(), item, innerCt),
                    ct).ConfigureAwait(false);
            }

            // Phase 2 of the backfill (unconditional): ensure the first
            // segment of every video is present in L2 so a cold first-byte
            // read hits S3 (~50 ms) instead of NNTP (~500-1000 ms). Cheap -
            // one segment per file, no-op when L2 already has it.
            await WarmFirstSegmentsIntoL2Async(videoItems, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e)
        {
            Log.Warning(e, "ProbeDataGenerator backfill failed");
        }
    }

    /// <summary>
    /// For every video in the library, seed L2 with a small set of
    /// strategic segments (configurable policy — default warms first,
    /// middle, and last segment). Gives fast click-to-play AND fast
    /// mid-file scrub without pulling entire videos. Runs at Low NNTP
    /// priority so live streams are never blocked.
    /// Also populates the four yEnc layout metadata columns (YencPartSize /
    /// YencLastPartSize / YencSegmentCount / YencLayoutUniform) on each item
    /// so the O(1) fast path in NzbFileStream can skip InterpolationSearch.
    /// </summary>
    private async Task WarmFirstSegmentsIntoL2Async(IReadOnlyList<DavItem> videoItems, CancellationToken ct)
    {
        using var priorityScope = ct.SetContext(
            new DownloadPriorityContext { Priority = SemaphorePriority.Low });

        var policy = _configManager.GetL2PrewarmPolicy();
        var itemsProcessed = 0;
        var segmentsWarmed = 0;
        var metadataPopulated = 0;
        var metadataSkipped = 0;
        foreach (var item in videoItems)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var dbContext = new DavDatabaseContext();
                var segmentIds = await GetSegmentIds(dbContext, item, ct).ConfigureAwait(false);
                if (segmentIds is null || segmentIds.Length == 0) continue;

                var offsets = ResolvePrewarmOffsets(policy, segmentIds.Length);
                using var fetchCtx = SegmentFetchContext.Set(SegmentCategory.SmallFile, item.ParentId ?? item.Id);
                foreach (var offset in offsets)
                {
                    var segId = segmentIds[offset];
                    await TrySeedL2FirstSegmentAsync(segId, ct).ConfigureAwait(false);
                    segmentsWarmed++;
                }

                var populated = await PopulateYencLayoutMetadataAsync(
                    dbContext, item, segmentIds, ct).ConfigureAwait(false);
                if (populated) metadataPopulated++;
                else metadataSkipped++;

                itemsProcessed++;
                if (itemsProcessed % 100 == 0)
                    Log.Information(
                        "L2 prewarm progress ({Policy}): items={Items} segments={Segments} metaPopulated={MetaPopulated}",
                        policy, itemsProcessed, segmentsWarmed, metadataPopulated);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e)
            {
                Log.Debug("L2 prewarm failed for {Name}: {Error}", item.Name, e.Message);
            }
        }

        Log.Information(
            "L2 prewarm complete ({Policy}): items={Items} segments={Segments} metaPopulated={MetaPopulated} metaSkipped={MetaSkipped}",
            policy, itemsProcessed, segmentsWarmed, metadataPopulated, metadataSkipped);
    }

    /// <summary>
    /// Populates YencPartSize / YencLastPartSize / YencSegmentCount /
    /// YencLayoutUniform on the DavItem row if not yet set. Returns true
    /// when the row was updated, false when it was already populated (idempotent).
    /// Exceptions are caught and logged per-item so a single bad video does
    /// not abort the whole prewarm pass.
    /// </summary>
    private async Task<bool> PopulateYencLayoutMetadataAsync(
        DavDatabaseContext dbContext,
        DavItem item,
        string[] segmentIds,
        CancellationToken ct)
    {
        // Idempotent: skip if already populated.
        if (item.YencLayoutUniform != null)
            return false;

        if (segmentIds.Length <= 0)
            return false;

        try
        {
            Func<string, CancellationToken, Task<UsenetYencHeader>> headerFetcher =
                (segId, token) => _liveSegmentCache.GetOrAddHeaderAsync(
                    segId,
                    innerCt => _usenetClient.GetYencHeadersAsync(segId, innerCt),
                    token);

            var (partSize, lastPartSize, segmentCount, uniform) =
                await ComputeYencLayoutAsync(segmentIds, headerFetcher, ct).ConfigureAwait(false);

            // Re-query with tracking so SaveChanges can detect the change.
            await using var trackedCtx = new DavDatabaseContext();
            var tracked = await trackedCtx.Items
                .FirstOrDefaultAsync(x => x.Id == item.Id, ct)
                .ConfigureAwait(false);

            if (tracked is null) return false;

            // Double-check idempotency on the freshly-loaded row.
            if (tracked.YencLayoutUniform != null) return false;

            tracked.YencPartSize = partSize;
            tracked.YencLastPartSize = lastPartSize;
            tracked.YencSegmentCount = segmentCount;
            tracked.YencLayoutUniform = uniform;

            await trackedCtx.SaveChangesAsync(ct).ConfigureAwait(false);

            Log.Debug("YencLayout populated for {Name}: partSize={PartSize} lastPartSize={LastPartSize} segments={Segments} uniform={Uniform}",
                item.Name, partSize, lastPartSize, segmentCount, uniform);

            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            Log.Debug("YencLayout metadata population failed for {Name}: {Error}", item.Name, e.Message);
            return false;
        }
    }

    /// <summary>
    /// Pure (static) helper that samples the first, last, and (optionally)
    /// middle segment yEnc headers to determine the yEnc layout of a file.
    /// Accepts an injected <paramref name="headerFetcher"/> so it is fully
    /// testable without a live NNTP connection or database.
    /// </summary>
    internal static async Task<(long PartSize, long LastPartSize, int SegmentCount, bool Uniform)>
        ComputeYencLayoutAsync(
            string[] segmentIds,
            Func<string, CancellationToken, Task<UsenetYencHeader>> headerFetcher,
            CancellationToken ct)
    {
        var firstHeader = await headerFetcher(segmentIds[0], ct).ConfigureAwait(false);
        var lastHeader = await headerFetcher(segmentIds[^1], ct).ConfigureAwait(false);

        bool uniform;
        if (segmentIds.Length >= 3)
        {
            var middleHeader = await headerFetcher(segmentIds[segmentIds.Length / 2], ct).ConfigureAwait(false);
            uniform = middleHeader.PartSize == firstHeader.PartSize;
        }
        else
        {
            // 1 or 2 segments: trivially uniform.
            uniform = true;
        }

        return (firstHeader.PartSize, lastHeader.PartSize, segmentIds.Length, uniform);
    }

    /// <summary>
    /// Picks segment indices to prewarm for a given video based on the
    /// configured policy. For shorter videos the unique offsets collapse
    /// (e.g. a 2-segment video under first-middle-last returns indices
    /// 0 and 1, not three). Always returns distinct, sorted indices.
    /// </summary>
    internal static int[] ResolvePrewarmOffsets(string policy, int segmentCount)
    {
        if (segmentCount <= 0) return Array.Empty<int>();
        if (segmentCount == 1) return new[] { 0 };

        var offsets = policy switch
        {
            "first-only" => new[] { 0 },
            "first-and-last" => new[] { 0, segmentCount - 1 },
            "first-quartile-mid-threequartile-last" => new[]
            {
                0,
                segmentCount / 4,
                segmentCount / 2,
                segmentCount * 3 / 4,
                segmentCount - 1,
            },
            "dense" => GenerateEvenOffsets(segmentCount, 20),
            "ultra-dense" => GenerateEvenOffsets(segmentCount, 40),
            // default: first-middle-last
            _ => new[] { 0, segmentCount / 2, segmentCount - 1 },
        };

        // Deduplicate + sort (short videos can collapse offsets)
        return offsets.Distinct().OrderBy(x => x).ToArray();
    }

    private static int[] GenerateEvenOffsets(int segmentCount, int targetCount)
    {
        if (segmentCount <= targetCount)
        {
            // Short video: use every segment
            return Enumerable.Range(0, segmentCount).ToArray();
        }
        return Enumerable.Range(0, targetCount)
            .Select(i => (int)Math.Round(i * (segmentCount - 1.0) / (targetCount - 1)))
            .ToArray();
    }

    /// <summary>
    /// Returns true if an NNTP fetch + L2 write was kicked off, false if L2
    /// already had the segment or the fetch was short-circuited.
    /// </summary>
    private async Task<bool> TrySeedL2FirstSegmentAsync(string segmentId, CancellationToken ct)
    {
        await _liveSegmentCache.SeedL2Async(
            segmentId,
            async innerCt =>
            {
                var response = await _usenetClient
                    .DecodedArticleWithFallbackAsync(segmentId, onConnectionReadyAgain: null, innerCt)
                    .ConfigureAwait(false);
                var header = await response.Stream.GetYencHeadersAsync(innerCt).ConfigureAwait(false);
                if (header is null)
                    throw new InvalidOperationException($"Missing yEnc header for {segmentId}");
                return new LiveSegmentCache.BodyFetchSource(
                    response.Stream, header, response.ArticleHeaders);
            },
            ct,
            category: SegmentCategory.SmallFile).ConfigureAwait(false);
        return true;
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

        var concurrency = GetBackfillConcurrency();
        Log.Information("ProbeDataGenerator backfill starting with concurrency={Concurrency}", concurrency);

        var generated = 0;
        var generatedLock = new Lock();
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = concurrency,
            CancellationToken = ct,
        };

        await Parallel.ForEachAsync(items, options, async (item, innerCt) =>
        {
            try
            {
                await processItem(item, innerCt).ConfigureAwait(false);
                int done;
                lock (generatedLock) { done = ++generated; }
                if (done % 20 == 0)
                    Log.Information("ProbeDataGenerator backfill progress: {Done}/{Total}", done, items.Count);
            }
            catch (OperationCanceledException) when (innerCt.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                Log.Debug("Backfill probe failed for {Name}: {Error}", item.Name, e.Message);
            }
        }).ConfigureAwait(false);

        Log.Information("ProbeDataGenerator backfill complete: {Generated}/{Missing} probes created", generated, items.Count);
    }

    private static int GetBackfillConcurrency()
    {
        var env = Environment.GetEnvironmentVariable("NZBDAV_PROBE_CONCURRENCY");
        if (!string.IsNullOrEmpty(env) && int.TryParse(env, out var n) && n > 0)
            return Math.Min(n, 32);
        return 5;
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
