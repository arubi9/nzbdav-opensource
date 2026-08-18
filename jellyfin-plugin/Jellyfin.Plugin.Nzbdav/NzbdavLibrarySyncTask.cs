using Jellyfin.Plugin.Nzbdav.Api;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

[assembly: InternalsVisibleTo("jellyfin-plugin.Tests")]

namespace Jellyfin.Plugin.Nzbdav;

/// <summary>
/// Syncs NZBDAV content to .strm files using a single manifest HTTP request.
/// The manifest endpoint returns the entire /content tree as one JSON document,
/// ETag-versioned so subsequent syncs that find no changes make zero NZBDAV API calls.
/// </summary>
public class NzbdavLibrarySyncTask : IScheduledTask
{
    private const long StreamTokenLifetimeSeconds = 7 * 24 * 60 * 60;
    private const long StreamTokenRefreshAgeSeconds = 3 * 24 * 60 * 60;

    private const int MaxDecimalExpiryLength = 19;
    private const int MaxManifestPathLength = 1_024;
    private const int MaxManifestPathSegments = 128;
    private const int MaxManifestNameLength = 255;
    private const int MaxManifestParentNameLength = 255;
    // Ownership files are untrusted input. Read only a bounded UTF-8 first line
    // before constructing strings or parsing URLs/marker fields.
    private const int MaxManagedStrmBytes = 8 * 1024;
    private const int MaxManagedProbeBytes = 8 * 1024 * 1024;
    private const int MaxManagedMarkerBytes = 8 * 1024;
    private const string ManagedStrmMarkerSuffix = ".nzbdav.managed";
    private const string ManagedIntentMarkerSuffix = ".nzbdav.managed.intent";
    private const string ManagedMarkerHeader = "nzbdav.managed";
    private const int ManagedMarkerFormatVersion = 4;
    private const string ManagedMarkerFileName = ".nzbdav.managed";
    private const string LogicalTombstoneSuffix = ".nzbdav.tombstone";
    private const string RecoveryDirectoryName = ".nzbdav-recovery";
    // There is one in-place writer per process (all callers share ExecuteGate),
    // so a fixed transaction slot makes synchronous recovery O(1) in directory
    // entries. It never searches for a matching suffix in an attacker-sized
    // namespace.
    // Recovery is deliberately a two-slot state machine.  A slot is reused
    // only after the other slot contains a fully valid state, so a torn write
    // can damage at most the inactive slot.  There are no operation names,
    // pointers, or retirement/deletion records.
    private const string RecoverySlot0FileName = "slot0";
    private const string RecoverySlot1FileName = "slot1";
    private const string PreparedSlotPrefix = ".nzbdav.prepare-";
    private const int PreparedSlotCount = 3;
    private const int MaxRecoveryJournalBytes = 64 * 1024;

    private static readonly string[] ReservedRelativePathSegments =
    [
        ".quarantine",
        "quarantine",
        ".temp",
        "temp",
        ".marker",
        "marker",
        ManagedMarkerFileName,
        RecoveryDirectoryName
    ];

    // The manifest is a logical filesystem contract, not an OS-native one.  In
    // particular, accepting case-distinct names on Linux makes a library behave
    // differently after it is moved to Windows.
    private static StringComparer PathComparer
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static readonly string[] WindowsDeviceNames =
    [
        "CON",
        "PRN",
        "AUX",
        "NUL",
        "COM1",
        "COM2",
        "COM3",
        "COM4",
        "COM5",
        "COM6",
        "COM7",
        "COM8",
        "COM9",
        "LPT1",
        "LPT2",
        "LPT3",
        "LPT4",
        "LPT5",
        "LPT6",
        "LPT7",
        "LPT8",
        "LPT9"
    ];

    private readonly ILogger<NzbdavLibrarySyncTask> _logger;
    private readonly TimeProvider _timeProvider;
    // A race fixture belongs to this task instance. Production callers leave it
    // null; tests can inject one without sharing mutable process-wide state.
    private readonly Action<string>? _pathMutationHook;
    private readonly Func<NzbdavOperationConfiguration?> _configurationAccessor;
    private static readonly SemaphoreSlim ExecuteGate = new(1, 1);
    private string? _cachedETag;

    public NzbdavLibrarySyncTask(
        ILogger<NzbdavLibrarySyncTask> logger,
        TimeProvider? timeProvider = null,
        Action<string>? pathMutationHook = null)
        : this(logger, timeProvider, pathMutationHook, NzbdavOperationConfigurationAccessor.CaptureFromPlugin)
    {
    }

    internal NzbdavLibrarySyncTask(
        ILogger<NzbdavLibrarySyncTask> logger,
        TimeProvider? timeProvider,
        Action<string>? pathMutationHook,
        Func<NzbdavOperationConfiguration?> configurationAccessor)
    {
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _pathMutationHook = pathMutationHook;
        _configurationAccessor = configurationAccessor;
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
        // Capture before waiting for the process-wide writer gate. A queued run
        // must not acquire a different root, endpoint, or credential after an
        // administrator edits configuration while another run is in progress.
        var operationConfiguration = _configurationAccessor();
        await ExecuteGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ExecuteCoreAsync(progress, operationConfiguration, ct).ConfigureAwait(false);
        }
        finally
        {
            ExecuteGate.Release();
        }
    }

    private async Task ExecuteCoreAsync(
        IProgress<double> progress,
        NzbdavOperationConfiguration? operationConfiguration,
        CancellationToken ct)
    {
        if (operationConfiguration is null || !operationConfiguration.IsValid)
        {
            _logger.LogWarning("NZBDAV plugin not configured — skipping sync");
            return;
        }

        // The legacy helpers and API client accept PluginConfiguration, but it
        // is a private operation-local copy, never Jellyfin's mutable instance.
        var config = operationConfiguration.ToPluginConfiguration();

        // In-place updates are journaled before their first byte changes. A
        // previous process may have died after that point, so recovery must run
        // before the manifest can cause another mutation.
        if (!RecoverPendingInPlaceUpdates(operationConfiguration.LibraryPath, ct))
        {
            _logger.LogWarning("Pending NZBDAV recovery could not be resolved safely; skipping normal sync");
            progress.Report(100);
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A null/typed client failure is not a successful manifest.  Most
            // importantly, do not advance the ETag in this path.
            _logger.LogWarning(ex, "NZBDAV unreachable or manifest request failed — will retry next cycle");
            progress.Report(100);
            return;
        }

        if (manifest is null)
        {
            // 304 Not Modified — content paths did not change, but signed stream
            // URLs can still be close to expiry and must be rotated independently.
            _logger.LogDebug("NZBDAV manifest unchanged (ETag match) — checking stream token freshness");
            var refreshSucceeded = false;
            if (TryParseBackendBaseUri(config.NzbdavBaseUrl, out var existingBaseUri))
                refreshSucceeded = await RefreshExistingTokens(config, client, existingBaseUri, ct).ConfigureAwait(false);
            else
                _logger.LogWarning("NZBDAV base URL was invalid ({BaseUrl}); skipping token rotation", config.NzbdavBaseUrl);

            if (refreshSucceeded)
                _cachedETag = newETag ?? _cachedETag;
            progress.Report(100);
            return;
        }

        if (!TryParseBackendBaseUri(config.NzbdavBaseUrl, out var baseUri))
        {
            _logger.LogWarning("NZBDAV base URL was invalid ({BaseUrl}); skipping sync", config.NzbdavBaseUrl);
            progress.Report(100);
            return;
        }

        // A single release with a name the local filesystem cannot represent
        // (e.g. a directory ending in '.') must not abort the whole sync: skip
        // it and its descendants, and sync everything else. Descendants are
        // excluded automatically because they carry the bad segment in their
        // own path. Namespace collisions still abort below — those indicate
        // manifest corruption, not one bad release name.
        var (representableItems, skippedPaths) = PartitionRepresentableManifestItems(manifest.Items);
        if (skippedPaths.Length > 0)
        {
            _logger.LogWarning(
                "Skipping {Count} manifest item(s) whose names the filesystem cannot represent (e.g. {Examples}); the rest of the library will sync.",
                skippedPaths.Length,
                string.Join(", ", skippedPaths.OrderBy(p => p.Length).Take(3)));
        }

        string[] expectedRelativePaths;
        var anyFailures = false;
        try
        {
            var allItems = representableItems.ToDictionary(i => i.Id);
            expectedRelativePaths = BuildExpectedStrmRelativePaths(representableItems, allItems);

            // Find all video files
            var videoFiles = representableItems
                .Where(i => i.Type is "nzb_file" or "rar_file" or "multipart_file")
                .Where(i => IsVideoFile(i.Name))
                .ToArray();

            var processed = 0;
            foreach (var videoFile in videoFiles)
            {
                ct.ThrowIfCancellationRequested();

                if (!await SyncVideoFile(config, client, baseUri, videoFile, allItems, ct).ConfigureAwait(false))
                    anyFailures = true;

                processed++;
                progress.Report((double)processed / videoFiles.Length * 100);
            }

            bool didReconcile = false;
            if (!anyFailures)
            {
                try
                {
                    didReconcile = ReconcileStaleFiles(config, expectedRelativePaths, runId: CreateRunId(), ct: ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to reconcile stale files for library run");
                    anyFailures = true;
                }
            }
            else
            {
                _logger.LogWarning("Manifest sync contained item failures; skipping stale-file reconciliation");
            }

            if (!anyFailures && didReconcile)
                _cachedETag = newETag;

            _logger.LogInformation("NZBDAV sync complete: {Count} video files processed from manifest", processed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to sync manifest");
        }

        progress.Report(100);
    }

    private async Task<bool> SyncVideoFile(
        Configuration.PluginConfiguration config,
        NzbdavApiClient client,
        Uri baseUri,
        ManifestItem videoFile,
        Dictionary<Guid, ManifestItem> allItems,
        CancellationToken ct)
    {
        var committedOutputs = new List<CommittedOutput>();
        var durableFreshIntent = false;
        try
        {
            // A marker is the ownership proof.  Never adopt an unmarked sidecar:
            // doing so would let a failed/foreign file become quarantinable plugin
            // content on the next run.
            var strmPath = BuildSafeStrmPath(config.LibraryPath, videoFile, allItems);
            var probePath = BuildSafeProbePath(strmPath);
            var markerPath = BuildManagedMarkerPath(strmPath);
            var intentPath = BuildManagedIntentMarkerPath(strmPath);
            var canonicalRoot = GetCanonicalLibraryRoot(config.LibraryPath);
            // Reject existing unsafe path components before requesting metadata.
            // This is an early fail-closed optimization only: the later reads
            // and writes still obtain their capabilities descriptor-rooted, so
            // a replacement after this check cannot redirect a mutation.
            if (!ValidateSyncOutputPathsBeforeNetwork(
                    canonicalRoot, strmPath, probePath, markerPath, intentPath))
            {
                _logger.LogWarning("Refusing to operate on reparse-point output path for {Id}", videoFile.Id);
                return false;
            }

            // Reconciliation retains stale Linux source pathnames as logical
            // tombstones. The source, marker, and probe are one managed unit;
            // never refresh or recreate any part while the tombstone still
            // names this exact source inode. A pathname replacement is not
            // tombstoned and remains visible to foreign-safe handling.
            if (IsLogicallyTombstoned(config.LibraryPath, strmPath))
                return true;

            var markerExists = TryPathExistsAnchored(config.LibraryPath, markerPath, out _);
            var suppressedProbeReplacement = IsSuppressedProbeReplacement(config.LibraryPath, probePath);
            var completedOwnership = TryGetManagedOwnership(
                config.LibraryPath, markerPath, strmPath, baseUri, videoFile.Id, out var ownership,
                completedOnly: true, allowSuppressedProbe: suppressedProbeReplacement,
                suppressedProbeRoot: config.LibraryPath);
            var ownsOutputs = completedOwnership;
            // v2 is a migration input for the writer only. It is upgraded to a
            // strict completion proof before any provider/reconcile caller can
            // observe it.
            if (!ownsOutputs)
                ownsOutputs = TryGetManagedOwnership(
                    config.LibraryPath, markerPath, strmPath, baseUri, videoFile.Id, out ownership,
                    completedOnly: false, allowSuppressedProbe: suppressedProbeReplacement,
                    suppressedProbeRoot: config.LibraryPath);
            if (!ownsOutputs && TryPathExistsAnchored(config.LibraryPath, intentPath, out _)
                && TryReadManagedIntent(config.LibraryPath, intentPath, videoFile.Id, out var intent))
            {
                return await CompleteManagedIntentAsync(
                    config, client, baseUri, videoFile, strmPath, probePath, intent, ct).ConfigureAwait(false);
            }
            if (markerExists && !ownsOutputs)
            {
                _logger.LogWarning("Refusing to adopt foreign or malformed marker for {Id}", videoFile.Id);
                return false;
            }

            var strmRead = ownsOutputs
                ? ownership.Strm
                : TryReadBoundedFirstLine(config.LibraryPath, strmPath, MaxManagedStrmBytes);
            var strmExists = strmRead is not null;
            var probeRead = TryReadBoundedFileForPath(config.LibraryPath, probePath, MaxManagedProbeBytes, out _, out _);
            var probeExists = probeRead;
            if (ownsOutputs && probeExists && ownership.Probe is null)
            {
                // A known foreign replacement is logically suppressed by the
                // fixed recovery slot. Keep the old valid stream+marker pair
                // visible without touching that sidecar.
                if (suppressedProbeReplacement)
                    return true;

                // A v1 marker (or a v2 stream-only marker) never proves a
                // sidecar. Do not copy, refresh, or silently adopt it.
                _logger.LogWarning("Refusing to operate on unproven probe sidecar for {Id}", videoFile.Id);
                return false;
            }
            if (!ownsOutputs && (strmExists || probeExists))
            {
                _logger.LogWarning("Refusing to adopt existing unowned mirror output for {Id}", videoFile.Id);
                return false;
            }

            var existingUrl = strmRead?.Content;
            var shouldRefresh = string.IsNullOrEmpty(existingUrl)
                               || ShouldRefreshStreamUrl(existingUrl, baseUri, videoFile.Id, _timeProvider.GetUtcNow());
            string streamUrl;

            if (!shouldRefresh)
            {
                streamUrl = existingUrl!;
            }
            else
            {
                // Fetch every required output before creating the first output. A
                // null probe is an item failure, not a successful partial adoption.
                var meta = await client.GetMetaAsync(videoFile.Id, ct).ConfigureAwait(false);
                if (!TryValidateMetadataStreamToken(meta?.StreamToken, _timeProvider.GetUtcNow()))
                {
                    _logger.LogWarning("NZBDAV returned an invalid or unusable stream token for {Id}; retaining existing output", videoFile.Id);
                    return false;
                }

                streamUrl = client.GetSignedStreamUrl(videoFile.Id, meta!.StreamToken!);
            }

            string? probeData = null;
            if (videoFile.HasProbeData && !probeExists)
            {
                probeData = await client.GetProbeDataAsync(videoFile.Id, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(probeData))
                {
                    _logger.LogWarning("NZBDAV did not return probe data for {Id}; item will be retried", videoFile.Id);
                    return false;
                }
            }

            if (!ownsOutputs && !strmExists && !probeExists)
            {
                // Prepare both anonymous inodes first. Their fstat identity and
                // complete content hash are then bound into the durable intent;
                // only after that marker is durable are the inodes linked.
                using var preparedStrm = PreparedOutput.Create(GetCanonicalLibraryRoot(config.LibraryPath), strmPath, streamUrl, ct, _pathMutationHook);
                using var preparedProbe = probeData is null ? (PreparedOutput?)null
                    : PreparedOutput.Create(GetCanonicalLibraryRoot(config.LibraryPath), probePath, probeData, ct, _pathMutationHook);
                var operationId = Guid.NewGuid().ToString("N");
                ProbeOwnership? freshProbe = preparedProbe is null ? null : new ProbeOwnership(preparedProbe.Identity, preparedProbe.Sha256);
                var intentContent = BuildManagedIntentContent(videoFile.Id, operationId, streamUrl,
                    preparedStrm.Identity, preparedStrm.Sha256, freshProbe);
                await CommitTextOutputAsync(config.LibraryPath, intentPath, intentContent, true, false, ct,
                    committedOutputs, mutationHook: _pathMutationHook).ConfigureAwait(false);
                durableFreshIntent = true;
                ct.ThrowIfCancellationRequested();
                preparedStrm.Publish(GetCanonicalLibraryRoot(config.LibraryPath), false, _pathMutationHook);
                ct.ThrowIfCancellationRequested();
                preparedProbe?.Publish(GetCanonicalLibraryRoot(config.LibraryPath), false, _pathMutationHook);
                var completion = BuildManagedStateContent("complete", operationId, videoFile.Id, streamUrl,
                    preparedStrm.Identity, preparedStrm.Sha256, freshProbe);
                await CommitTextOutputAsync(config.LibraryPath, markerPath, completion, true, false, ct,
                    committedOutputs, mutationHook: _pathMutationHook).ConfigureAwait(false);
                _logger.LogDebug("Created completed managed output set: {Path}", strmPath);
                ct.ThrowIfCancellationRequested();
                return true;
            }

            var tokenChanged = !string.Equals(existingUrl, streamUrl, StringComparison.Ordinal);
            if (probeData is not null)
            {
                // A probe added to an already-owned output is a two-member
                // transaction. The marker's old `none` claim remains durable
                // until the probe and its new binding can be recovered together.
                if (!ownsOutputs || ownership.Strm.Identity is null || ownership.Marker.Identity is null
                    || !TryReadBoundedFileForPath(config.LibraryPath, markerPath, MaxManagedMarkerBytes,
                        out var probeOldMarkerBytes, out var probeOldMarkerIdentity)
                    || probeOldMarkerIdentity is null
                    || probeOldMarkerIdentity.Value != ownership.Marker.Identity.Value)
                    throw new IOException("Cannot bind probe addition pair identities.");

                using var preparedProbe = PreparedOutput.Create(
                    GetCanonicalLibraryRoot(config.LibraryPath), probePath, probeData, ct, _pathMutationHook);
                var probeCompletion = BuildManagedStateContent(
                    "complete", ownership.OperationId.ToString("N"), ownership.ItemId,
                    ownership.Strm.Content, ownership.Strm.Identity.Value, ownership.Strm.Sha256,
                    new ProbeOwnership(preparedProbe.Identity, preparedProbe.Sha256));
                var probeCompletionBytes = new UTF8Encoding(false).GetBytes(probeCompletion);
                if (!CommitProbeAddition(config, preparedProbe, probePath, markerPath,
                        probeOldMarkerIdentity.Value, probeOldMarkerBytes, probeCompletionBytes, ct,
                        out var committedProbe))
                    return false;

                // Carry the descriptor-verified probe ownership and committed
                // marker bytes forward. A post-commit pathname read can observe
                // the pre-probe marker on filesystems with delayed cache
                // invalidation; using that stale `none` claim would regress the
                // marker during the immediately-following token rotation.
                ownership = ownership with
                {
                    Marker = new BoundedFirstLine(
                        probeCompletion, ownership.Marker.Identity, probeCompletionBytes),
                    Probe = committedProbe
                };
                probeData = null;
                completedOwnership = true;
            }

            if (tokenChanged && ownsOutputs)
            {
                if (ownership.Strm.Identity is not { } rotationStreamIdentity
                    || ownership.Marker.Identity is not { } rotationMarkerIdentity)
                    throw new IOException("Cannot bind token rotation pair identities.");

                // Use the ownership snapshot that was descriptor-verified before
                // publication. In particular, CommitProbeAddition has replaced
                // this with its exact committed marker bytes and probe binding;
                // rereading the marker here could observe a stale pre-probe
                // `none` record and carry it into the rotation journal.
                var oldMarkerBytes = ownership.Marker.Bytes;
                ProbeOwnership? rotatedProbe = ownership.Probe;

                var completion = BuildManagedStateContent(
                    "complete", ownership.OperationId.ToString("N"), ownership.ItemId,
                    streamUrl, rotationStreamIdentity,
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(streamUrl))), rotatedProbe);
                var newMarkerBytes = new UTF8Encoding(false).GetBytes(completion);
                if (!CommitTokenRotation(
                        config, strmPath, markerPath, rotationStreamIdentity,
                        ownership.Strm.Bytes, new UTF8Encoding(false).GetBytes(streamUrl),
                        rotationMarkerIdentity, oldMarkerBytes, newMarkerBytes, ct))
                    return false;
            }
            else if (!completedOwnership || probeData is not null)
            {
                // Marker creation is deliberately last, after all output writes
                // have completed and been fsynced. This branch is not token
                // rotation; rotations above always use the paired journal.
                await CommitTextOutputAsync(
                    config.LibraryPath,
                    markerPath,
                    BuildManagedMarkerContent(config.LibraryPath, videoFile.Id, strmPath, probePath),
                    noReplace: !ownsOutputs,
                    rollbackOnFailure: !ownsOutputs,
                    ct,
                    committedOutputs,
                    ownsOutputs ? ownership.Marker.Identity : null,
                    mutationHook: _pathMutationHook).ConfigureAwait(false);
            }

            _logger.LogDebug("Created/refreshed .strm: {Path}", strmPath);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            CleanupUnownedOutputs(config.LibraryPath, committedOutputs, _pathMutationHook);
            throw;
        }
        catch (WriteCommitException ex) when (ex.InnerException is OperationCanceledException && ct.IsCancellationRequested)
        {
            CleanupUnownedOutputs(config.LibraryPath, committedOutputs, _pathMutationHook);
            throw ex.InnerException;
        }
        catch (Exception ex)
        {
            // Once an intention is durable, ordinary partial failures leave it
            // and any exact outputs in place for the next run. Cancellation is
            // the explicit caller-requested rollback path used by the legacy
            // synchronous task contract.
            if (!durableFreshIntent)
                CleanupUnownedOutputs(config.LibraryPath, committedOutputs, _pathMutationHook);
            _logger.LogWarning(ex, "Failed to sync {Id}", videoFile.Id);
            return false;
        }
    }

    private sealed record CommittedOutput(string Path, FileIdentity? Identity, string Sha256);

    private sealed class PreparedOutput : IDisposable
    {
        private FileStream? _anonymous;
        private FileStream? _named;
        private readonly string? _temporaryPath;
        private readonly string _destination;
        private readonly int _rootFd;
        private readonly int _directoryFd;
        private bool _published;

        public FileIdentity Identity { get; }
        public string Sha256 { get; }
        public string Destination => _destination;

        private PreparedOutput(string destination, FileIdentity identity, string sha256,
            FileStream? anonymous, FileStream? named, string? temporaryPath, int rootFd, int directoryFd)
        {
            _destination = destination; Identity = identity; Sha256 = sha256;
            _anonymous = anonymous; _named = named; _temporaryPath = temporaryPath;
            _rootFd = rootFd; _directoryFd = directoryFd;
        }

        public static PreparedOutput Create(
            string canonicalRoot, string destination, string content, CancellationToken ct,
            Action<string>? mutationHook = null, FileIdentity? durableIdentity = null)
        {
            ct.ThrowIfCancellationRequested();
            var bytes = new UTF8Encoding(false).GetBytes(content);
            var maxBytes = destination.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)
                ? MaxManagedStrmBytes : MaxManagedProbeBytes;
            if (bytes.Length > maxBytes) throw new IOException("Output exceeds its fixed bound.");
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            if (!OperatingSystem.IsLinux())
            {
                var directory = Path.GetDirectoryName(destination) ?? throw new IOException("Output has no directory.");
                Directory.CreateDirectory(directory);

                // Named preparation uses bounded fixed slots rather than an
                // operation-id suffix. A pre-existing slot is always a
                // collision; only an inode created and held by this operation
                // can be published. Foreign occupants are left untouched and
                // the bounded set is exhausted rather than growing artifacts.
                for (var slot = 0; slot < PreparedSlotCount; slot++)
                {
                    var temporary = Path.Combine(directory, PreparedSlotPrefix + slot.ToString(CultureInfo.InvariantCulture));
                    FileStream? preparedFile = null;
                    try
                    {
                        preparedFile = OpenWindowsOwnedFile(temporary, createNew: true);
                        preparedFile.Write(bytes);
                        preparedFile.Flush(true);
                        var identity = GetFileIdentityWindows(preparedFile.SafeFileHandle);
                        SyncFile(temporary);

                        // Deterministic watcher seam: any pathname replacement
                        // after the handle proof is detected before publication.
                        mutationHook?.Invoke(temporary);
                        if (CaptureFileIdentity(directory, temporary) != identity)
                            throw new IOException($"Prepared output identity changed: '{temporary}'.");

                        var prepared = new PreparedOutput(
                            destination, identity, hash, null, preparedFile, temporary, -1, -1);
                        preparedFile = null; // ownership transferred
                        return prepared;
                    }
                    catch (IOException) when (preparedFile is null && File.Exists(temporary))
                    {
                        // A fixed preparation name is not an ownership record.
                        // A pre-existing slot is adoptable only when the
                        // canonical recovery intent already records this exact
                        // inode identity. Without that durable proof, even
                        // byte-identical content remains foreign.
                        if (durableIdentity is null)
                            continue;
                        try
                        {
                            using var existing = OpenWindowsOwnedFile(temporary, createNew: false);
                            var existingIdentity = GetFileIdentityWindows(existing.SafeFileHandle);
                            var existingBytes = ReadBoundedBytes(existing, maxBytes);
                            if (existingIdentity != durableIdentity.Value
                                || existingBytes is null
                                || !existingBytes.AsSpan().SequenceEqual(bytes)
                                || CaptureFileIdentity(directory, temporary) != existingIdentity)
                                continue;

                            mutationHook?.Invoke(temporary);
                            if (CaptureFileIdentity(directory, temporary) != existingIdentity)
                                continue;

                            // Transfer a fresh handle, rather than the proof
                            // handle disposed by the using scope, to the output.
                            var adopted = OpenWindowsOwnedFile(temporary, createNew: false);
                            if (GetFileIdentityWindows(adopted.SafeFileHandle) != durableIdentity.Value
                                || CaptureFileIdentity(directory, temporary) != durableIdentity.Value)
                            {
                                adopted.Dispose();
                                continue;
                            }
                            return new PreparedOutput(
                                destination, existingIdentity, hash, null, adopted, temporary, -1, -1);
                        }
                        catch
                        {
                            // A foreign/replaced slot remains visible. Never
                            // turn a failed proof into pathname deletion.
                            continue;
                        }
                    }
                    catch
                    {
                        if (preparedFile is not null)
                        {
                            try
                            {
                                DeleteOpenedWindowsFile(
                                    temporary, maxBytes,
                                    GetFileIdentityWindows(preparedFile.SafeFileHandle),
                                    hash, mutationHook: null);
                            }
                            catch { /* retain this bounded owned slot */ }
                            preparedFile.Dispose();
                        }
                        throw;
                    }
                }

                throw new IOException("No safe fixed preparation slot is available.");
            }

            var relative = Path.GetRelativePath(canonicalRoot, destination);
            var directoryPart = Path.GetDirectoryName(relative);
            var rootFd = OpenDirectoryForMutation(canonicalRoot);
            var directoryFd = rootFd;
            FileStream? stream = null;
            global::Microsoft.Win32.SafeHandles.SafeFileHandle? preparedHandle = null;
            try
            {
                var device = ValidateOpenedDirectoryDescriptor(rootFd, canonicalRoot, expectedDevice: null);
                directoryFd = string.IsNullOrWhiteSpace(directoryPart) ? rootFd
                    : OpenDirectoryChain(canonicalRoot, rootFd, device, directoryPart, createDirectories: true);
                var directoryPath = string.IsNullOrWhiteSpace(directoryPart) ? canonicalRoot : Path.Combine(canonicalRoot, directoryPart);
                var fd = OpenAnonymousWritableFileAt(directoryFd, device, directoryPath);
                if (fd < 0) throw new PlatformNotSupportedException("Fresh Linux output requires O_TMPFILE.");
                preparedHandle = new global::Microsoft.Win32.SafeHandles.SafeFileHandle((IntPtr)fd, true);
                stream = new FileStream(preparedHandle, FileAccess.ReadWrite, 16 * 1024, false);
                preparedHandle = null; // FileStream owns the handle now.
                stream.Write(bytes); stream.Flush(true);
                if (SyncFd(checked((int)stream.SafeFileHandle.DangerousGetHandle())) != 0)
                    throw new IOException("Failed to fsync prepared output.");
                var prepared = new PreparedOutput(destination,
                    GetFileIdentity(checked((int)stream.SafeFileHandle.DangerousGetHandle())),
                    hash, stream, null, null, rootFd, directoryFd);
                stream = null; // ownership transferred to PreparedOutput
                return prepared;
            }
            catch
            {
                stream?.Dispose();
                preparedHandle?.Dispose();
                if (directoryFd != rootFd) _ = CloseDirectoryHandle(directoryFd);
                _ = CloseDirectoryHandle(rootFd);
                throw;
            }
        }

        public void Publish(
            string canonicalRoot,
            bool replace = false,
            Action<string>? mutationHook = null,
            FileStream? expectedDestination = null,
            FileIdentity? expectedDestinationIdentity = null)
        {
            if (_published) return;
            _ = canonicalRoot;
            if (OperatingSystem.IsLinux())
            {
                var name = Path.GetFileName(_destination);
                if (_anonymous is null || LinkFileAtNoReplace(checked((int)_anonymous.SafeFileHandle.DangerousGetHandle()), _directoryFd, name) != 0)
                    throw new IOException("Prepared output destination already exists or cannot be linked.");
                if (!PathHasIdentity(_directoryFd, GetFileDevice(_directoryFd), name, _destination, Identity)
                    || SyncDirectoryFd(_directoryFd) != 0) throw new IOException("Prepared output publication was not durable.");
                // Invoke the seam only after the link is durable. A hook that
                // cancels or fails now leaves the exact held inode and durable
                // intent available for recovery, never an uncommitted path.
                mutationHook?.Invoke(_destination);
            }
            else
            {
                if (_temporaryPath is null || _named is null) throw new IOException("Prepared output temporary handle is missing.");
                if (GetFileIdentityWindows(_named.SafeFileHandle) != Identity
                    || CaptureFileIdentity(Path.GetDirectoryName(_temporaryPath)!, _temporaryPath) != Identity)
                    throw new IOException("Prepared output identity changed before publication.");
                if (replace)
                    throw new IOException("Existing Windows destinations must use the held-handle transaction path.");

                MoveWindowsOwnedFileNoReplace(_named, _temporaryPath, _destination, Identity, mutationHook);
                SyncFile(_destination);
                if (CaptureFileIdentity(Path.GetDirectoryName(_destination)!, _destination) != Identity)
                    throw new IOException("Prepared output identity changed during publication.");
                SyncDirectory(Path.GetDirectoryName(_destination)!);
                // The destination seam is post-publication. A failure here
                // leaves the durable intended inode visible for recovery,
                // while the source seam above still proves the held inode
                // before rename.
                mutationHook?.Invoke(_destination);
            }
            _published = true;
        }

        public void Dispose()
        {
            _anonymous?.Dispose(); _anonymous = null;
            if (!_published && _named is not null)
            {
                try
                {
                    DeleteOpenedWindowsFile(
                        _temporaryPath ?? _destination,
                        _destination.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)
                            ? MaxManagedStrmBytes : MaxManagedProbeBytes,
                        Identity, Sha256, mutationHook: null);
                    if (_temporaryPath is not null)
                        SyncDirectory(Path.GetDirectoryName(_temporaryPath)!);
                }
                catch { /* retain the bounded slot for proof-based recovery */ }
            }
            _named?.Dispose(); _named = null;
            if (_directoryFd >= 0 && _directoryFd != _rootFd) _ = CloseDirectoryHandle(_directoryFd);
            if (_rootFd >= 0) _ = CloseDirectoryHandle(_rootFd);
        }
    }

    private async Task<bool> CompleteManagedIntentAsync(
        Configuration.PluginConfiguration config,
        NzbdavApiClient client,
        Uri baseUri,
        ManifestItem videoFile,
        string strmPath,
        string probePath,
        ManagedIntent intent,
        CancellationToken ct)
    {
        PreparedOutput? preparedStrm = null;
        PreparedOutput? preparedProbe = null;
        try
        {
            if (intent.ItemId != videoFile.Id
                || !Uri.TryCreate(intent.StreamUrl, UriKind.Absolute, out var streamUri)
                || !IsCanonicalStreamRequest(streamUri, baseUri, intent.ItemId)
                || !string.Equals(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(intent.StreamUrl))), intent.StreamSha256, StringComparison.OrdinalIgnoreCase))
                return false;
            var markerIdentity = intent.Marker.Identity;
            if (markerIdentity is null) return false;

            var strm = TryReadBoundedFirstLine(config.LibraryPath, strmPath, MaxManagedStrmBytes);
            FileIdentity streamIdentity;
            if (strm is not null)
            {
                if (strm.Value.Identity != intent.StreamIdentity
                    || !string.Equals(strm.Value.Content.Trim(), intent.StreamUrl, StringComparison.Ordinal)
                    || !string.Equals(strm.Value.Sha256, intent.StreamSha256, StringComparison.OrdinalIgnoreCase)) return false;
                streamIdentity = strm.Value.Identity ?? throw new IOException("Stream identity disappeared.");
            }
            else
            {
                // A missing output is a safe supersession case. Prepare a new
                // inode, bind its identity into a replacement intent, then link.
                preparedStrm = PreparedOutput.Create(
                    GetCanonicalLibraryRoot(config.LibraryPath), strmPath, intent.StreamUrl, ct,
                    _pathMutationHook, intent.StreamIdentity);
                streamIdentity = preparedStrm.Identity;
            }

            ProbeOwnership? probe = intent.Probe;
            if (probe is null)
            {
                if (TryPathExistsAnchored(config.LibraryPath, probePath, out _)) return false;
            }
            else if (TryReadBoundedFileForPath(config.LibraryPath, probePath, MaxManagedProbeBytes, out var probeBytes, out var probeIdentity))
            {
                if (probeIdentity is null || probeIdentity.Value != probe.Value.Identity
                    || !string.Equals(Convert.ToHexString(SHA256.HashData(probeBytes)), probe.Value.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
            }
            else
            {
                var probeData = await client.GetProbeDataAsync(videoFile.Id, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(probeData)) return false;
                preparedProbe = PreparedOutput.Create(
                    GetCanonicalLibraryRoot(config.LibraryPath), probePath, probeData, ct,
                    _pathMutationHook, probe.Value.Identity);
                if (!string.Equals(preparedProbe.Sha256, probe.Value.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
                probe = new ProbeOwnership(preparedProbe.Identity, preparedProbe.Sha256);
            }

            if (preparedStrm is not null || preparedProbe is not null)
            {
                var updated = BuildManagedIntentContent(intent.ItemId, intent.OperationId.ToString("N"), intent.StreamUrl,
                    streamIdentity, intent.StreamSha256, probe);
                await CommitTextOutputAsync(config.LibraryPath, intent.MarkerPath, updated, false, false, ct, [],
                    expectedIdentity: markerIdentity, mutationHook: null).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                preparedStrm?.Publish(GetCanonicalLibraryRoot(config.LibraryPath), false, _pathMutationHook);
                ct.ThrowIfCancellationRequested();
                preparedProbe?.Publish(GetCanonicalLibraryRoot(config.LibraryPath), false, _pathMutationHook);
            }

            var completion = BuildManagedStateContent("complete", intent.OperationId.ToString("N"), intent.ItemId,
                intent.StreamUrl, streamIdentity, intent.StreamSha256, probe);
            await CommitTextOutputAsync(config.LibraryPath, BuildManagedMarkerPath(strmPath), completion,
                noReplace: true, rollbackOnFailure: false, ct, [], mutationHook: null).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to complete managed output intention for {Id}", videoFile.Id);
            return false;
        }
        finally
        {
            // Ownership remains with this method until the whole intent has
            // completed. This covers every early return, failed publication,
            // and cancellation on both O_TMPFILE and named-temp platforms.
            preparedProbe?.Dispose();
            preparedStrm?.Dispose();
        }
    }

    private static async Task CommitTextOutputAsync(
        string libraryRoot,
        string destinationPath,
        string content,
        bool noReplace,
        bool rollbackOnFailure,
        CancellationToken ct,
        ICollection<CommittedOutput> journal,
        FileIdentity? expectedIdentity = null,
        string? expectedGuardPath = null,
        FileIdentity? expectedGuardIdentity = null,
        Action<string>? mutationHook = null)
    {
        try
        {
            var result = noReplace
                ? await WriteTextAtomicallyAsyncNoReplace(
                    libraryRoot, destinationPath, content, ct,
                    expectedGuardPath, expectedGuardIdentity, mutationHook).ConfigureAwait(false)
                : expectedIdentity is null
                    ? await WriteTextAtomicallyAsync(
                        libraryRoot, destinationPath, content, ct,
                        expectedGuardPath, expectedGuardIdentity, mutationHook).ConfigureAwait(false)
                    : await WriteTextAtomicallyWithExpectedIdentityAsync(
                        libraryRoot, destinationPath, content, ct,
                        expectedIdentity.Value, expectedGuardPath, expectedGuardIdentity, mutationHook).ConfigureAwait(false);
            if (rollbackOnFailure && result.DestinationCommitted)
                journal.Add(new CommittedOutput(destinationPath, result.Identity, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))));
        }
        catch (WriteCommitException ex)
        {
            if (rollbackOnFailure && ex.Result.DestinationCommitted)
                journal.Add(new CommittedOutput(destinationPath, ex.Result.Identity, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))));
            throw;
        }
    }

    private static void CleanupUnownedOutputs(string libraryRoot, IReadOnlyList<CommittedOutput> outputs, Action<string>? mutationHook)
    {
        // The marker is the ownership proof, so remove it before its sidecars.
        List<Exception>? failures = null;
        foreach (var output in outputs.Reverse())
        {
            try
            {
                if (OperatingSystem.IsLinux())
                    CleanupOutputLinux(libraryRoot, output, mutationHook);
                else if (OperatingSystem.IsWindows() && File.Exists(output.Path))
                {
                    if (output.Identity is null || !IsSha256(output.Sha256))
                        throw new IOException($"Cannot prove cleanup identity for '{output.Path}'.");
                    DeleteOpenedWindowsFile(
                        output.Path,
                        MaxManagedProbeBytes,
                        output.Identity.Value,
                        output.Sha256,
                        mutationHook);
                    var directory = Path.GetDirectoryName(output.Path);
                    if (!string.IsNullOrEmpty(directory))
                        SyncDirectory(directory);
                }
                else if (!OperatingSystem.IsLinux() && File.Exists(output.Path))
                {
                    throw new IOException($"Cannot prove cleanup semantics for '{output.Path}'.");
                }
            }
            catch (Exception ex)
            {
                // Identity mismatch and unproven cleanup are never converted into
                // a best-effort delete. Report them explicitly after all safe
                // cleanup attempts have completed.
                (failures ??= []).Add(ex);
            }
        }

        if (failures is not null)
            throw new IOException("Failed to prove cleanup of one or more NZBDAV outputs.", failures[0]);
    }

    private static FileIdentity? CaptureFileIdentity(string canonicalRoot, string fullPath)
    {
        if (!OperatingSystem.IsLinux())
        {
            if (!File.Exists(fullPath) || ContainsSymlinkInPath(canonicalRoot, fullPath))
                return null;
            try
            {
                using var file = new FileStream(fullPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                return GetFileIdentityWindows(file.SafeFileHandle);
            }
            catch
            {
                return null;
            }
        }

        var relative = Path.GetRelativePath(canonicalRoot, fullPath);
        var directory = Path.GetDirectoryName(relative);
        var name = Path.GetFileName(relative);
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var rootFd = OpenDirectoryForMutation(canonicalRoot);
        try
        {
            var rootDevice = ValidateOpenedDirectoryDescriptor(rootFd, canonicalRoot, expectedDevice: null);
            var dirFd = string.IsNullOrWhiteSpace(directory)
                ? rootFd
                : OpenDirectoryChain(canonicalRoot, rootFd, rootDevice, directory, createDirectories: false, invokeMutationHook: false);
            try
            {
                var fd = OpenPathFileAt(dirFd, rootDevice, name, fullPath, invokeMutationHook: false);
                if (fd < 0)
                    return null;
                try { return GetFileIdentity(fd); }
                finally { _ = CloseDirectoryHandle(fd); }
            }
            finally
            {
                if (dirFd != rootFd)
                    _ = CloseDirectoryHandle(dirFd);
            }
        }
        finally
        {
            _ = CloseDirectoryHandle(rootFd);
        }
    }

    private static void CleanupOutputLinux(string libraryRoot, CommittedOutput output, Action<string>? mutationHook = null)
    {
        _ = libraryRoot;
        if (output.Identity is null || !IsSha256(output.Sha256))
            throw new IOException($"Cannot prove retained cleanup identity for '{output.Path}'.");

        // Linux has no unlink-by-open-fd/CAS-by-inode primitive. Verify the
        // retained inode and bytes from one descriptor, then retain it as a
        // logically failed output. A pathname replacement between proofs is
        // foreign-visible and causes item failure; it is never unlinked.
        if (!TryReadBoundedFileForPath(libraryRoot, output.Path, MaxManagedProbeBytes, out var bytes, out var identity)
            || identity is null
            || identity.Value != output.Identity.Value
            || !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), output.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"Refusing to clean replaced output '{output.Path}'.");
        mutationHook?.Invoke(output.Path);
        if (!TryReadBoundedFileForPath(libraryRoot, output.Path, MaxManagedProbeBytes, out var unchanged, out var unchangedIdentity)
            || unchangedIdentity is null
            || unchangedIdentity.Value != output.Identity.Value
            || !string.Equals(Convert.ToHexString(SHA256.HashData(unchanged)), output.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"Output changed during safe Linux cleanup '{output.Path}'.");
        // The owned inode is intentionally retained. Its transaction/journal
        // state is recoverable and no unverified pathname is touched.
    }
    private async Task<bool> RefreshExistingTokens(
        Configuration.PluginConfiguration config,
        NzbdavApiClient client,
        Uri baseUri,
        CancellationToken ct)
    {
        if (!LibraryRootExists(config.LibraryPath))
            return true;

        var succeeded = true;
        var canonicalRoot = GetCanonicalLibraryRoot(config.LibraryPath);
        foreach (var strmPath in EnumerateAnchoredStrmFiles(canonicalRoot))
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var canonicalStrmPath = Path.GetFullPath(strmPath);
                if (!IsPathWithinRoot(canonicalRoot, canonicalStrmPath) || ContainsSymlinkInPath(canonicalRoot, canonicalStrmPath))
                    continue;
                var relativePath = Path.GetRelativePath(canonicalRoot, canonicalStrmPath);
                if (IsUnderQuarantine(relativePath) || IsUnderRecovery(relativePath))
                    continue;
                if (IsLogicallyTombstoned(canonicalRoot, canonicalStrmPath))
                    continue;

                var markerPath = BuildManagedMarkerPath(canonicalStrmPath);
                if (!TryGetManagedOwnership(canonicalRoot, markerPath, canonicalStrmPath, baseUri, null, out var ownership, completedOnly: true))
                    continue;

                var existingUrl = ownership.Strm.Content;
                if (!TryGetStreamId(existingUrl, out var id))
                    continue;

                if (!ShouldRefreshExistingStream(existingUrl, baseUri, id, _timeProvider.GetUtcNow()))
                    continue;

                var meta = await client.GetMetaAsync(id, ct).ConfigureAwait(false);
                if (!TryValidateMetadataStreamToken(meta?.StreamToken, _timeProvider.GetUtcNow()))
                {
                    succeeded = false;
                    _logger.LogWarning("NZBDAV returned an invalid or unusable stream token for {Id}; retaining {Path}", id, canonicalStrmPath);
                    continue;
                }

                var streamUrl = client.GetSignedStreamUrl(id, meta!.StreamToken!);
                if (!string.Equals(existingUrl?.TrimEnd('\r', '\n'), streamUrl, StringComparison.Ordinal))
                {
                    if (ownership.Strm.Identity is null)
                        throw new IOException($"Cannot bind ownership identity for '{canonicalStrmPath}'.");

                    var completion = BuildManagedStateContent(
                        "complete", ownership.OperationId.ToString("N"), ownership.ItemId,
                        streamUrl, ownership.Strm.Identity.Value,
                        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(streamUrl))), ownership.Probe);
                    // Ownership was proven before the metadata await. Keep
                    // both original byte snapshots: rereading either member
                    // here would bless a same-inode operator edit as our CAS
                    // predecessor. Publication rechecks these exact bytes from
                    // rooted descriptors immediately before each truncation.
                    if (ownership.Marker.Identity is null)
                        throw new IOException("Cannot bind ownership marker bytes.");
                    var oldStreamBytes = ownership.Strm.Bytes;
                    var oldMarkerBytes = ownership.Marker.Bytes;
                    // Bind both members again after the await, before creating
                    // a journal or changing either pathname. This rejects an
                    // in-place foreign edit just as strictly as a replacement.
                    if (!TryReadBoundedFileForPath(canonicalRoot, canonicalStrmPath, MaxManagedStrmBytes,
                            out var liveStream, out var liveStreamIdentity)
                        || liveStreamIdentity != ownership.Strm.Identity.Value
                        || !liveStream.AsSpan().SequenceEqual(oldStreamBytes)
                        || !TryReadBoundedFileForPath(canonicalRoot, markerPath, MaxManagedMarkerBytes,
                            out var liveMarker, out var liveMarkerIdentity)
                        || liveMarkerIdentity != ownership.Marker.Identity.Value
                        || !liveMarker.AsSpan().SequenceEqual(oldMarkerBytes))
                    {
                        succeeded = false;
                        _logger.LogWarning("NZBDAV stream or ownership marker changed during token metadata lookup; retaining {Path}", canonicalStrmPath);
                        continue;
                    }
                    var newStreamBytes = new UTF8Encoding(false).GetBytes(streamUrl);
                    var newMarkerBytes = new UTF8Encoding(false).GetBytes(completion);
                    if (!CommitTokenRotation(
                            config, canonicalStrmPath, markerPath,
                            ownership.Strm.Identity.Value, oldStreamBytes, newStreamBytes,
                            ownership.Marker.Identity.Value, oldMarkerBytes, newMarkerBytes, ct))
                        succeeded = false;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A failed rotation is retryable. Leave the prior URL in place so
                // an active stream is not disrupted and the next cycle can retry.
                succeeded = false;
                _logger.LogWarning(ex, "Failed to refresh NZBDAV stream token for {Path}", strmPath);
            }
        }

        return succeeded;
    }

    private static bool ShouldRefreshStreamUrl(string? url, Uri backendBase, Guid expectedId, DateTimeOffset now)
    {
        var managedState = ClassifyManagedStreamUrl(url, backendBase, expectedId, out var canonicalToken);
        return managedState switch
        {
            ManagedStreamUrlType.CanonicalToken => TokenNeedsRefresh(canonicalToken, now),
            ManagedStreamUrlType.MalformedCanonicalToken => true,
            ManagedStreamUrlType.LegacyApiKey => true,
            _ => false,
        };
    }

    private static bool ShouldRefreshExistingStream(string? url, Uri backendBase, Guid expectedId, DateTimeOffset now)
        => ShouldRefreshStreamUrl(url, backendBase, expectedId, now);

    private static bool TryValidateMetadataStreamToken(string? token, DateTimeOffset now)
    {
        if (!TryParseStreamTokenParts(token, out var expiry))
            return false;

        var nowUnix = now.ToUnixTimeSeconds();
        // Metadata is a new publication input. Unlike an already-owned URL,
        // it may not be expired (the backend's grace window is for serving
        // old URLs during rotation), and checked arithmetic prevents a forged
        // near-MaxValue expiry from bypassing the future bound.
        return expiry > nowUnix
            && nowUnix <= long.MaxValue - StreamTokenLifetimeSeconds
            && expiry <= nowUnix + StreamTokenLifetimeSeconds;
    }

    private static bool TokenNeedsRefresh(string? token, DateTimeOffset now)
    {
        if (!TryParseStreamTokenParts(token, out var expiry))
            return true;

        var nowUnix = now.ToUnixTimeSeconds();
        if (expiry <= nowUnix)
            return true;

        if (nowUnix > long.MaxValue - StreamTokenLifetimeSeconds
            || expiry > nowUnix + StreamTokenLifetimeSeconds)
            return true;

        return expiry <= nowUnix + StreamTokenLifetimeSeconds - StreamTokenRefreshAgeSeconds;
    }

    private static ManagedStreamUrlType ClassifyManagedStreamUrl(
        string? url,
        Uri backendBase,
        Guid expectedId,
        out string? canonicalToken)
    {
        canonicalToken = null;

        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)
            || !TryGetStreamId(uri, out var id)
            || id != expectedId)
        {
            return ManagedStreamUrlType.NotManaged;
        }

        if (!IsCanonicalStreamRequest(uri, backendBase, expectedId))
            return ManagedStreamUrlType.NotManaged;

        return ClassifyManagedStreamUrl(uri, backendBase, out canonicalToken);
    }

    private static bool IsCanonicalStreamRequest(Uri streamUri, Uri backendBase, Guid expectedId)
    {
        if (!TryGetCanonicalStreamId(streamUri, backendBase, out var streamIdText))
            return false;

        return Guid.TryParseExact(streamIdText, "D", out var streamId)
               && streamId == expectedId
               && IsBackendStreamAuthorityMatch(streamUri, backendBase);
    }

    private static bool TryParseBackendBaseUri(string? value, out Uri baseUri)
    {
        baseUri = null!;
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var parsed)
            || parsed.Scheme is not ("http" or "https")
            || string.IsNullOrEmpty(parsed.Host)
            || !string.IsNullOrEmpty(parsed.UserInfo)
            || !string.IsNullOrEmpty(parsed.Fragment)
            || !string.IsNullOrEmpty(parsed.Query))
            return false;

        // A base URL is an authority and path prefix, never a URL containing
        // credentials or a fragment/query whose interpretation could differ
        // between the marker parser and HttpClient.
        baseUri = parsed;
        return true;
    }

    private static int EffectivePort(Uri uri)
    {
        if (uri.Port >= 0)
            return uri.Port;
        return uri.Scheme.ToLowerInvariant() switch
        {
            "http" => 80,
            "https" => 443,
            _ => -1
        };
    }

    private static ManagedStreamUrlType ClassifyManagedStreamUrl(
        Uri streamUri,
        Uri backendBase,
        out string? canonicalToken)
    {
        canonicalToken = null;

        if (!TryGetCanonicalStreamId(streamUri, backendBase, out _)
            || !IsBackendStreamAuthorityMatch(streamUri, backendBase))
            return ManagedStreamUrlType.NotManaged;

        if (!TryGetSingleQueryParameter(streamUri.Query, out var key, out var value))
            return ManagedStreamUrlType.NotManaged;

        if (string.Equals(key, "token", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseStreamTokenParts(value, out _))
                return ManagedStreamUrlType.MalformedCanonicalToken;

            canonicalToken = value;
            return ManagedStreamUrlType.CanonicalToken;
        }

        if (string.Equals(key, "apikey", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
            return ManagedStreamUrlType.LegacyApiKey;

        return ManagedStreamUrlType.NotManaged;
    }

    private static bool IsCanonicalStreamRequest(Uri streamUri, Uri backendBase)
    {
        return TryGetCanonicalStreamId(streamUri, backendBase, out _);
    }

    private static bool TryGetCanonicalStreamId(Uri streamUri, Uri backendBase, out string streamId)
    {
        streamId = string.Empty;
        var basePath = backendBase.AbsolutePath.TrimEnd('/');
        if (basePath == "/")
            basePath = string.Empty;

        // Require a complete base-path segment before appending the route. A
        // textual prefix check alone is unsafe for /media vs /mediaplus.
        var routePrefix = string.IsNullOrEmpty(basePath)
            ? "/api/stream/"
            : basePath + "/api/stream/";
        if (!streamUri.AbsolutePath.StartsWith(routePrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        streamId = streamUri.AbsolutePath[routePrefix.Length..];
        return !string.IsNullOrEmpty(streamId) && !streamId.Contains('/');
    }

    private static bool IsBackendStreamAuthorityMatch(Uri streamUri, Uri backendBase)
    {
        // Userinfo and fragments are never part of an NZBDAV stream URL.  In
        // addition to being ambiguous, accepting them would make a marker a
        // forgeable claim of ownership of an otherwise foreign sidecar.
        return TryParseBackendBaseUri(backendBase.AbsoluteUri, out var safeBase)
               && string.IsNullOrEmpty(streamUri.UserInfo)
               && string.IsNullOrEmpty(streamUri.Fragment)
               && string.Equals(streamUri.Scheme, safeBase.Scheme, StringComparison.OrdinalIgnoreCase)
               && string.Equals(streamUri.Host, safeBase.Host, StringComparison.OrdinalIgnoreCase)
               && EffectivePort(streamUri) == EffectivePort(safeBase);
    }

    private static bool TryGetSingleQueryParameter(string query, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;

        var trimmedQuery = query.TrimStart('?');
        if (string.IsNullOrEmpty(trimmedQuery))
            return false;

        var parts = trimmedQuery.Split('&', StringSplitOptions.None);
        if (parts.Length != 1)
            return false;

        var part = parts[0];
        if (string.IsNullOrEmpty(part))
            return false;

        var separator = part.IndexOf('=', StringComparison.Ordinal);
        if (separator <= 0)
            return false;

        key = Uri.UnescapeDataString(part[..separator]);
        value = Uri.UnescapeDataString(part[(separator + 1)..]);
        return !string.IsNullOrWhiteSpace(key);
    }

    private static bool TryParseStreamTokenParts(string? token, out long expiry)
    {
        expiry = 0;

        if (string.IsNullOrWhiteSpace(token))
            return false;

        var separator = token.IndexOf('.');
        if (separator <= 0 || token.IndexOf('.', separator + 1) >= 0)
            return false;

        if (separator == token.Length - 1)
            return false;

        var expiryText = token[..separator];
        var signature = token[(separator + 1)..];

        if (expiryText.Length > MaxDecimalExpiryLength
            || (expiryText.Length > 1 && expiryText[0] == '0'))
            return false;
        foreach (var character in expiryText)
            if (character is < '0' or > '9')
                return false;

        if (!long.TryParse(expiryText, NumberStyles.None, CultureInfo.InvariantCulture, out expiry)
            || expiry <= 0)
            return false;

        return IsValidCanonicalStreamSignature(signature);
    }

    private static bool IsValidCanonicalStreamSignature(string signature)
    {
        if (signature.Length != 43)
            return false;

        var values = new int[43];
        for (var i = 0; i < 43; i++)
        {
            var value = FromBase64UrlChar(signature[i]);
            if (value < 0)
                return false;

            values[i] = value;
        }

        // Canonical 256-bit base64url signatures are 43 chars with clear
        // trailing bits in the final sextet.
        if ((values[^1] & 0b11) != 0)
            return false;

        return true;
    }

    private static int FromBase64UrlChar(char value)
        => value is >= 'A' and <= 'Z' ? value - 'A'
            : value is >= 'a' and <= 'z' ? value - 'a' + 26
            : value is >= '0' and <= '9' ? value - '0' + 52
            : value is '-' ? 62
            : value is '_' ? 63
            : -1;

    private static bool TryGetStreamId(string? url, out Guid id)
    {
        id = default;
        return Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && TryGetStreamId(uri, out id);
    }

    private static bool TryGetStreamId(Uri uri, out Guid id)
    {
        id = default;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
            return false;

        if (!string.Equals(segments[^2], "stream", StringComparison.OrdinalIgnoreCase))
            return false;

        return Guid.TryParseExact(segments[^1], "D", out id);
    }

    private enum ManagedStreamUrlType
    {
        NotManaged,
        CanonicalToken,
        MalformedCanonicalToken,
        LegacyApiKey,
    }

    private bool ReconcileStaleFiles(
        Configuration.PluginConfiguration config,
        IReadOnlyCollection<string> expectedRelativePaths,
        string runId,
        CancellationToken ct)
    {
        var libraryRoot = config.LibraryPath;
        if (!LibraryRootExists(libraryRoot))
            return true;

        if (!TryParseBackendBaseUri(config.NzbdavBaseUrl, out var baseUri))
        {
            _logger.LogWarning("NZBDAV base URL was invalid; refusing stale-file ownership checks");
            return false;
        }

        var expected = expectedRelativePaths
            .Select(NormalizeRelativePathForComparison)
            .ToHashSet(PathComparer);
        var canonicalRoot = GetCanonicalLibraryRoot(libraryRoot);
        // The destination is a function of the logical source pathname, not a
        // run nonce. A copy interrupted before its tombstone is therefore
        // reused on every retry instead of multiplying quarantine artifacts.
        var quarantineRoot = Path.Combine(canonicalRoot, ".quarantine");
        EnsureQuarantineRoot(canonicalRoot);

        // Threat boundary: once a source pathname is observed, an adversarial
        // writer may unlink it and create a foreign file before any later
        // pathname operation. Linux has no unlink-by-fd/CAS-by-inode primitive,
        // so the source-absent postcondition is intentionally not claimed. The
        // safe guarantee is narrower: copy from the held inode, never replace
        // an existing quarantine destination, retain foreign originals, and
        // fail closed when either identity post-check differs.
        var plannedCopies = new List<(string Source, string Destination, FileIdentity? Identity, string Sha256)>();
        var plannedTombstones = new List<(string Source, string Tombstone, FileIdentity Identity, string Sha256)>();
        foreach (var strmPath in EnumerateAnchoredStrmFiles(canonicalRoot))
        {
            ct.ThrowIfCancellationRequested();

            var canonicalStrmPath = Path.GetFullPath(strmPath);
            if (!IsPathWithinRoot(canonicalRoot, canonicalStrmPath))
                continue;

            if (ContainsSymlinkInPath(canonicalRoot, canonicalStrmPath))
                continue;

            var relativeStrmPath = Path.GetRelativePath(canonicalRoot, canonicalStrmPath);
            if (IsUnderQuarantine(relativeStrmPath))
                continue;

            var markerPath = BuildManagedMarkerPath(canonicalStrmPath);
            if (!TryReadBoundedFileForPath(canonicalRoot, canonicalStrmPath, MaxManagedStrmBytes, out var sourceBytes, out var sourceIdentity)
                || sourceIdentity is null)
                continue;
            var sourceHash = Convert.ToHexString(SHA256.HashData(sourceBytes));

            // A tombstone is a logical quarantine record. It intentionally
            // leaves the source pathname in place because Linux cannot safely
            // unlink a pathname by the identity held in an fd. If the source
            // identity no longer matches, fail closed rather than treating a
            // replacement as owned content.
            var tombstonePath = BuildLogicalTombstonePath(canonicalStrmPath);
            if (TryPathExistsAnchored(canonicalRoot, tombstonePath, out _))
            {
                if (TryGetLogicalTombstone(canonicalRoot, tombstonePath, out var tombstoneIdentity, out var tombstoneHash)
                    && tombstoneIdentity == sourceIdentity.Value
                    && string.Equals(sourceHash, tombstoneHash, StringComparison.OrdinalIgnoreCase))
                    continue;

                // A pathname replacement, including an inode-reuse case with a
                // different content hash, is foreign-visible and untouched.
                _logger.LogWarning("Refusing stale-file mutation after logical tombstone identity changed: {Path}", canonicalStrmPath);
                continue;
            }

            if (!TryGetManagedOwnership(canonicalRoot, markerPath, canonicalStrmPath, baseUri, null, out var ownership, completedOnly: true))
                continue;

            var normalizedRelativePath = NormalizeRelativePathForComparison(relativeStrmPath);
            if (expected.Contains(normalizedRelativePath))
                continue;

            plannedCopies.Add((canonicalStrmPath, Path.Combine(canonicalRoot, GetQuarantineRelativePath(relativeStrmPath, runId)), sourceIdentity, sourceHash));
            plannedTombstones.Add((canonicalStrmPath, tombstonePath, sourceIdentity.Value, sourceHash));

            // A probe is quarantinable only when the marker independently
            // records its identity and full-content hash. A legacy marker or a
            // missing/mismatched proof leaves the probe at its original name.
            var probePath = Path.ChangeExtension(canonicalStrmPath, ".mediainfo.json");
            if (ownership.Probe is { } probeProof
                && TryReadBoundedFileForPath(canonicalRoot, probePath, MaxManagedProbeBytes, out var probeBytes, out var probeIdentity)
                && probeIdentity is not null
                && probeIdentity.Value == probeProof.Identity
                && string.Equals(Convert.ToHexString(SHA256.HashData(probeBytes)), probeProof.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                var relativeProbePath = Path.GetRelativePath(canonicalRoot, probePath);
                plannedCopies.Add((probePath, Path.Combine(canonicalRoot, GetQuarantineRelativePath(relativeProbePath, runId)), probeIdentity, probeProof.Sha256));
            }

            if (!TryReadBoundedFileForPath(canonicalRoot, markerPath, MaxManagedMarkerBytes, out var markerBytes, out var markerIdentity)
                || markerIdentity is null)
                continue;
            plannedCopies.Add((markerPath, Path.Combine(canonicalRoot, GetQuarantineRelativePath(
                Path.GetRelativePath(canonicalRoot, markerPath), runId)), markerIdentity, Convert.ToHexString(SHA256.HashData(markerBytes))));
        }

        if (plannedCopies.Count == 0)
            return true;

        // Reject every case-insensitive destination collision up front,
        // including an older quarantine run or a foreign file. The commit below
        // uses NOREPLACE again because this preflight can race.
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matchingDestinationProofs = new HashSet<string>(PathComparer);
        foreach (var copy in plannedCopies)
        {
            EnsureCaseInsensitivePathComponents(canonicalRoot, copy.Destination, allowExistingFinal: true);
            var destinationExists = TryPathExistsAnchored(canonicalRoot, copy.Destination, out var destinationIsDirectory);
            if (destinationExists && destinationIsDirectory)
                throw new IOException($"Quarantine destination is a foreign directory: '{copy.Destination}'.");
            if (destinationExists)
            {
                if (!TryReadBoundedFileForPath(canonicalRoot, copy.Destination, MaxManagedProbeBytes, out var existingCopy, out _)
                    || !string.Equals(Convert.ToHexString(SHA256.HashData(existingCopy)), copy.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"Quarantine destination is foreign or mismatched: '{copy.Destination}'.");

                // Bytes alone are not an ownership proof. On a restart the
                // source copy may already be durable, but an attacker can put
                // identical bytes at the deterministic destination. Only the
                // copied managed marker for this stale unit authorizes treating
                // that exact destination as the prior operation's result.
                if (!IsProvenQuarantineDestination(copy, plannedCopies, canonicalRoot))
                    throw new IOException($"Matching quarantine destination has no managed marker proof: '{copy.Destination}'.");
                matchingDestinationProofs.Add(copy.Destination);
            }

            if (!destinations.Add(copy.Destination))
                throw new IOException($"Quarantine destination collision: '{copy.Destination}'.");
        }

        foreach (var tombstone in plannedTombstones)
            EnsureCaseInsensitivePathComponents(canonicalRoot, tombstone.Tombstone, allowExistingFinal: true);
        try
        {
            foreach (var move in plannedCopies)
            {
                ct.ThrowIfCancellationRequested();

                // A copy never mutates the source pathname. Keep any durable
                // quarantine copy for manual retention/recovery if a later
                // durability check fails.
                CopyFileToQuarantineWithProof(canonicalRoot, move.Source, move.Destination, ct,
                    move.Identity, move.Sha256, _pathMutationHook,
                    matchingDestinationProofs.Contains(move.Destination));
            }

            foreach (var tombstone in plannedTombstones)
            {
                ct.ThrowIfCancellationRequested();
                WriteLogicalTombstone(
                    canonicalRoot,
                    tombstone.Source,
                    tombstone.Tombstone,
                    tombstone.Identity,
                    tombstone.Sha256,
                    runId,
                    ct,
                    _pathMutationHook);
            }

            _logger.LogInformation(
                "Quarantined {Count} stale NZBDAV mirror file(s) into {QuarantineRoot}. Retention is manual.",
                plannedCopies.Count,
                quarantineRoot);

            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancellation preserves source and foreign pathnames. Any durable
            // copies are left in quarantine for explicit operator cleanup.
            throw;
        }
        catch (QuarantineCopyDurabilityException ex)
        {
            if (ex.InnerException is OperationCanceledException)
                throw ex.InnerException;
            _logger.LogWarning(ex, "Failed to durably reconcile stale NZBDAV mirror files; source pathnames were retained.");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to reconcile stale NZBDAV mirror files; source pathnames were retained.");
            return false;
        }
    }

    private sealed class QuarantineCopyDurabilityException(string message, Exception inner, bool commitOccurred)
        : IOException(message, inner)
    {
        public bool CommitOccurred { get; } = commitOccurred;
    }

    internal static (ManifestItem[] Representable, string[] SkippedPaths) PartitionRepresentableManifestItems(
        ManifestItem[] items)
    {
        var representable = new List<ManifestItem>(items.Length);
        var skipped = new List<string>();
        foreach (var item in items)
        {
            try
            {
                ValidateManifestPathInternal(item.Path, isManifestPath: true);
                representable.Add(item);
            }
            catch (ArgumentException)
            {
                skipped.Add(item.Path);
            }
        }

        return (representable.ToArray(), skipped.ToArray());
    }

    private static string[] BuildExpectedStrmRelativePaths(
        ManifestItem[] items,
        IReadOnlyDictionary<Guid, ManifestItem> allItems)
    {
        // Validate the complete manifest namespace before any HTTP-dependent
        // output mutation. This catches case-folded duplicates and file/directory
        // prefixes even when one of the colliding entries is not a video.
        ValidateManifestNamespace(items);

        var expected = new HashSet<string>(PathComparer);
        var results = new List<string>(items.Length);
        foreach (var item in items)
        {
            if (item.Type is not ("nzb_file" or "rar_file" or "multipart_file") || !IsVideoFile(item.Name))
                continue;

            var relativePath = BuildStrmRelativePath(item, allItems);
            var managedRelativePath = Path.ChangeExtension(relativePath, ".strm");
            var normalized = NormalizeRelativePathForComparison(managedRelativePath);

            if (!expected.Add(normalized))
                throw new InvalidOperationException($"Duplicate destination path '{normalized}' in manifest.");

            results.Add(normalized);
        }

        ValidatePathNamespace(results);
        return results.ToArray();
    }

    private static void ValidateManifestNamespace(IEnumerable<ManifestItem> items)
    {
        var entries = items
            .Select(item => new
            {
                RawPath = ValidateManifestPathInternal(item.Path, isManifestPath: true),
                item.Type
            })
            .Select(entry => new
            {
                // Keep the validated spelling as well as the canonical key.
                // NFC aliases must be rejected at file/directory boundaries,
                // not silently erased before that namespace check.
                entry.RawPath,
                Path = NormalizeRelativePathForComparison(entry.RawPath),
                entry.Type
            })
            .ToArray();

        ValidateCaseInsensitiveSegmentSpellings(entries.Select(static entry => entry.Path));

        var seen = new HashSet<string>(PathComparer);
        foreach (var entry in entries)
        {
            if (!seen.Add(entry.Path))
                throw new InvalidOperationException($"Manifest path collision: '{entry.Path}'.");
        }

        // A directory may legitimately prefix its children. Any other prefix
        // is a file-vs-directory collision (foo.strm vs foo.strm/bar).
        foreach (var entry in entries)
        {
            var prefix = entry.Path + "/";
            if (entries.Any(other => other.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                && !string.Equals(entry.Type, "directory", StringComparison.Ordinal))
                throw new InvalidOperationException($"Manifest file/directory prefix collision: '{entry.Path}'.");
        }

        static bool HasCanonicalAliasCollision(string[] left, string[] right)
        {
            if (left.Length != right.Length)
                return false;

            var hasAliasDiff = false;
            for (var i = 0; i < left.Length; i++)
            {
                var leftSegment = left[i];
                var rightSegment = right[i];
                var leftNormalized = NormalizeRelativePathForComparison(leftSegment);
                var rightNormalized = NormalizeRelativePathForComparison(rightSegment);

                if (!string.Equals(leftNormalized, rightNormalized, StringComparison.OrdinalIgnoreCase))
                    return false;

                if (!string.Equals(leftSegment, rightSegment, StringComparison.Ordinal))
                    hasAliasDiff = true;
            }

            return hasAliasDiff;
        }

        var directoryPaths = entries
            .Where(static e => string.Equals(e.Type, "directory", StringComparison.Ordinal))
            .Select(static e => e.RawPath)
            .ToArray();

        foreach (var entry in entries.Where(static e => !string.Equals(e.Type, "directory", StringComparison.Ordinal)))
        {
            var fileSegments = entry.RawPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (fileSegments.Length < 2)
                continue;

            var fileParentSegments = fileSegments[..^1];
            foreach (var directoryPath in directoryPaths)
            {
                var directorySegments = directoryPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (directorySegments.Length != fileParentSegments.Length + 1)
                    continue;

                var directoryParentSegments = directorySegments[..^1];
                if (HasCanonicalAliasCollision(
                        fileParentSegments,
                        directoryParentSegments))
                {
                    throw new InvalidOperationException($"Manifest file/directory prefix collision: '{entry.Path}'.");
                }
            }
        }
    }

    private static void ValidatePathNamespace(IEnumerable<string> paths)
    {
        var normalized = paths
            .Select(path => path.Normalize(NormalizationForm.FormC).Replace('\\', '/'))
            .ToArray();
        ValidateCaseInsensitiveSegmentSpellings(normalized);

        var seen = new HashSet<string>(PathComparer);
        foreach (var path in normalized)
        {
            if (!seen.Add(path))
                throw new InvalidOperationException($"Manifest path collision: '{path}'.");
        }

        var ordered = normalized
            .Select(path => path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            .OrderBy(parts => parts.Length)
            .ToArray();
        foreach (var parts in ordered)
        {
            foreach (var prefix in ordered)
            {
                if (prefix.Length >= parts.Length)
                    break;
                if (parts.Take(prefix.Length).SequenceEqual(prefix, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Manifest file/directory prefix collision: '{string.Join('/', prefix)}'.");
            }
        }
    }

    private static void ValidateCaseInsensitiveSegmentSpellings(IEnumerable<string> paths)
    {
        // A spelling collision is a sibling collision, not a depth collision.
        // Keeping the logical parent path in the key permits Foo/a and foo/b
        // when Foo and foo belong to different parents, while still rejecting
        // A/Foo and A/foo.
        var spellings = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var segments = path.Normalize(NormalizationForm.FormC)
                .Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries);
            var parent = string.Empty;
            for (var index = 0; index < segments.Length; index++)
            {
                var byName = spellings.TryGetValue(parent, out var names)
                    ? names
                    : spellings[parent] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var segment = segments[index];
                if (byName.TryGetValue(segment, out var original)
                    && !string.Equals(original, segment, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Case-insensitive directory spelling collision: '{original}' vs '{segment}'.");
                byName.TryAdd(segment, segment);
                parent = string.IsNullOrEmpty(parent) ? segment : parent + "/" + segment;
            }
        }
    }

    private static bool IsNzbdavManagedStrmContent(string content, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(baseUrl))
            return false;

        if (!Uri.TryCreate(content.Trim(), UriKind.Absolute, out var contentUri))
            return false;

        if (!TryParseBackendBaseUri(baseUrl, out var baseUri))
            return false;

        if (!IsCanonicalStreamRequest(contentUri, baseUri))
            return false;

        return ClassifyManagedStreamUrl(contentUri, baseUri, out _) != ManagedStreamUrlType.NotManaged;
    }

    private static string GetQuarantineRelativePath(string relativePath, string runId)
    {
        _ = runId;
        return Path.Combine(".quarantine", relativePath + ".quarantined");
    }

    private readonly record struct QuarantineMarkerProof(
        Guid OperationId, Guid ItemId, string StreamSha256, string ProbeSha256);

    private static bool TryReadQuarantineMarkerProof(
        string canonicalRoot,
        string markerPath,
        out QuarantineMarkerProof proof,
        out byte[] markerBytes)
    {
        proof = default;
        markerBytes = [];
        if (!TryReadBoundedFileForPath(canonicalRoot, markerPath, MaxManagedMarkerBytes,
                out markerBytes, out var markerIdentity)
            || markerIdentity is null)
            return false;

        try
        {
            var text = new UTF8Encoding(false, true).GetString(markerBytes);
            if (text.Contains('\r') || text.Contains('\n'))
                return false;
            var parts = text.Split('/', StringSplitOptions.None);
            if (parts.Length != 11 || parts[0] != ManagedMarkerHeader || parts[1] != "4"
                || !Guid.TryParseExact(parts[2], "N", out var operationId)
                || !Guid.TryParseExact(parts[3], "D", out var itemId)
                || !TryParseCanonicalUInt64(parts[4], out var streamDevice)
                || !TryParseCanonicalUInt64(parts[5], out var streamInode)
                || !IsSha256(parts[6])
                || !TryParseCanonicalUInt64(parts[7], out var probeDevice)
                || !TryParseCanonicalUInt64(parts[8], out var probeInode)
                || !(parts[9] == "none" || IsSha256(parts[9]))
                || !TryDecodeManagedUrl(parts[10], out var url)
                || !string.Equals(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))),
                    parts[6], StringComparison.OrdinalIgnoreCase)
                || !TryGetStreamId(url, out var urlId) || urlId != itemId
                || !TryValidateRecoveryStreamUrl(url)
                || !string.Equals(
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(url)).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
                    parts[10], StringComparison.Ordinal)
                || (parts[9] == "none"
                    ? probeDevice != 0 || probeInode != 0
                    : probeDevice == 0 || probeInode == 0))
                return false;

            _ = streamDevice;
            _ = streamInode;
            proof = new QuarantineMarkerProof(operationId, itemId, parts[6], parts[9]);
            return true;
        }
        catch
        {
            proof = default;
            markerBytes = [];
            return false;
        }
    }

    private static bool IsQuarantineMarkerSource(string sourcePath)
        => sourcePath.EndsWith(ManagedStrmMarkerSuffix, StringComparison.OrdinalIgnoreCase);

    private static string? GetQuarantineOwnerStrmSource(string sourcePath)
    {
        if (sourcePath.EndsWith(ManagedStrmMarkerSuffix, StringComparison.OrdinalIgnoreCase))
            return sourcePath[..^ManagedStrmMarkerSuffix.Length];
        if (sourcePath.EndsWith(".mediainfo.json", StringComparison.OrdinalIgnoreCase))
            return Path.ChangeExtension(sourcePath, ".strm");
        if (sourcePath.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
            return sourcePath;
        return null;
    }

    private static bool IsProvenQuarantineDestination(
        (string Source, string Destination, FileIdentity? Identity, string Sha256) copy,
        IReadOnlyList<(string Source, string Destination, FileIdentity? Identity, string Sha256)> plannedCopies,
        string canonicalRoot)
    {
        var ownerStrm = GetQuarantineOwnerStrmSource(copy.Source);
        if (ownerStrm is null)
            return false;

        var markerSource = BuildManagedMarkerPath(ownerStrm);
        var markerCopy = plannedCopies.FirstOrDefault(candidate =>
            PathComparer.Equals(candidate.Source, markerSource));
        var streamCopy = plannedCopies.FirstOrDefault(candidate =>
            PathComparer.Equals(candidate.Source, ownerStrm));
        if (string.IsNullOrEmpty(markerCopy.Destination)
            || string.IsNullOrEmpty(streamCopy.Destination)
            || markerCopy.Identity is null
            || streamCopy.Identity is null)
            return false;

        // A matching byte sequence is not an ownership proof. Re-read the
        // source marker and require its durable v4 item/operation/hash claim to
        // be exactly the marker copied at this deterministic destination. This
        // makes restart convergence safe only for the same managed unit; an
        // absent, malformed, or foreign marker fails closed even if the stream
        // bytes happen to match.
        if (!TryReadQuarantineMarkerProof(canonicalRoot, markerSource, out var sourceProof, out var sourceMarkerBytes)
            || CaptureFileIdentity(canonicalRoot, markerSource) != markerCopy.Identity
            || !string.Equals(Convert.ToHexString(SHA256.HashData(sourceMarkerBytes)), markerCopy.Sha256,
                StringComparison.OrdinalIgnoreCase)
            || !TryReadQuarantineMarkerProof(canonicalRoot, markerCopy.Destination, out var copiedProof, out var copiedMarkerBytes)
            || !sourceMarkerBytes.AsSpan().SequenceEqual(copiedMarkerBytes)
            || sourceProof != copiedProof
            || !string.Equals(sourceProof.StreamSha256, streamCopy.Sha256, StringComparison.OrdinalIgnoreCase))
            return false;

        var streamRead = TryReadBoundedFileForPath(canonicalRoot, streamCopy.Destination,
            MaxManagedStrmBytes, out var streamBytes, out _);
        if (!streamRead
            || !string.Equals(Convert.ToHexString(SHA256.HashData(streamBytes)), streamCopy.Sha256,
                StringComparison.OrdinalIgnoreCase))
            return false;

        if (sourceProof.ProbeSha256 != "none")
        {
            var probeSource = Path.ChangeExtension(ownerStrm, ".mediainfo.json");
            var probeCopy = plannedCopies.FirstOrDefault(candidate =>
                PathComparer.Equals(candidate.Source, probeSource));
            if (string.IsNullOrEmpty(probeCopy.Destination)
                || !string.Equals(probeCopy.Sha256, sourceProof.ProbeSha256, StringComparison.OrdinalIgnoreCase)
                || !TryReadBoundedFileForPath(canonicalRoot, probeCopy.Destination,
                    MaxManagedProbeBytes, out var probeBytes, out _)
                || !string.Equals(Convert.ToHexString(SHA256.HashData(probeBytes)), probeCopy.Sha256,
                    StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // The stream and marker copies jointly prove every source member. A
        // probe copy is additionally bound above when the marker claims one.
        return true;
    }


    private static bool IsUnderQuarantine(string relativePath)
    {
        const StringComparison comparison = StringComparison.OrdinalIgnoreCase;

        var normalized = NormalizeRelativePathForComparison(relativePath);
        return normalized.StartsWith(".quarantine/", comparison);
    }

    private static bool IsUnderRecovery(string relativePath)
    {
        var normalized = NormalizeRelativePathForComparison(relativePath);
        return string.Equals(normalized, RecoveryDirectoryName, StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(RecoveryDirectoryName + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRelativePathForComparison(string path)
    {
        return path.Normalize(NormalizationForm.FormC).Replace('\\', '/');
    }

    private static string NormalizeRelativePath(string path)
    {
        return NormalizeRelativePathForComparison(path);
    }

    private readonly record struct FileIdentity(ulong Device, ulong Inode);
    private readonly record struct WriteCommitResult(bool DestinationCommitted, FileIdentity? Identity);
    private readonly record struct RecoveryArtifacts(
        string JournalPath, string OldContentPath, FileIdentity OldIdentity,
        string OldSha256, string OperationId);

    // A slot selection is a capability, not just a pathname. The writer may
    // reuse an existing inactive slot only when its held inode, exact bytes,
    // parsed state, and raw bytes hash are all still the selected values.
    private sealed record RecoverySlotExpectation(
        int Slot, bool Exists, FileIdentity? Identity, byte[] Bytes, string RawSha256,
        RecoveryStateDocument? State, string StateSha256);

    private sealed record RecoveryStateDocument(
        int Version, ulong Generation, string OperationId, string Phase,
        string PreviousStateSha256, string Destination, ulong DestinationDevice,
        ulong DestinationInode, string DestinationOldSha256, string DestinationNewSha256,
        string OldBytesBase64, string OldBytesSha256, string StateSha256,
        string? DestinationNewBytesBase64 = null,
        // Paired transaction fields. The first member is the stream for token
        // rotation, or a prepared probe for sidecar addition; a non-null pair
        // destination makes this one bounded two-output transaction. The single-
        // destination fields above remain the existing in-place recovery format.
        string? StreamDestination = null, ulong StreamOldDevice = 0, ulong StreamOldInode = 0,
        ulong StreamNewDevice = 0, ulong StreamNewInode = 0,
        string? StreamOldSha256 = null, string? StreamNewSha256 = null,
        string? StreamOldBytesBase64 = null, string? StreamNewBytesBase64 = null,
        string? MarkerDestination = null, ulong MarkerOldDevice = 0, ulong MarkerOldInode = 0,
        ulong MarkerNewDevice = 0, ulong MarkerNewInode = 0,
        string? MarkerOldSha256 = null, string? MarkerNewSha256 = null,
        string? MarkerOldBytesBase64 = null, string? MarkerNewBytesBase64 = null,
        // A probe addition has no old pathname/inode. The first paired member
        // is therefore journaled by its prepared new identity and hash only.
        bool? StreamOldPresent = null);

    private static readonly JsonSerializerOptions RecoveryJsonOptions = new(JsonSerializerDefaults.General)
    {
        WriteIndented = false, MaxDepth = 4, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed class HeldLinuxFile(int rootFd, int directoryFd, int fileFd, FileIdentity identity) : IDisposable
    {
        public int RootFd { get; } = rootFd;
        public int DirectoryFd { get; } = directoryFd;
        public int FileFd { get; } = fileFd;
        public FileIdentity Identity { get; } = identity;
        public void Dispose()
        {
            _ = CloseDirectoryHandle(FileFd);
            if (DirectoryFd != RootFd) _ = CloseDirectoryHandle(DirectoryFd);
            _ = CloseDirectoryHandle(RootFd);
        }
    }

    private sealed class WriteCommitException(string message, Exception inner, WriteCommitResult result)
        : IOException(message, inner)
    { public WriteCommitResult Result { get; } = result; }

    internal static async ValueTask<IDisposable> EnterMediaGateAsync(CancellationToken ct)
    {
        await ExecuteGate.WaitAsync(ct).ConfigureAwait(false);
        return new GateLease();
    }

    private sealed class GateLease : IDisposable
    {
        private int _released;
        public void Dispose() { if (Interlocked.Exchange(ref _released, 1) == 0) ExecuteGate.Release(); }
    }

    internal static async Task<bool> RecoverForMediaAsync(
        string libraryRoot, string mediaPath, CancellationToken ct)
    {
        using var gate = await EnterMediaGateAsync(ct).ConfigureAwait(false);
        return RecoverForMediaUnderGate(libraryRoot, mediaPath, ct);
    }

    internal static bool RecoverForMediaUnderGate(string libraryRoot, string mediaPath, CancellationToken ct)
    {
        try
        {
            var root = GetCanonicalLibraryRoot(libraryRoot);
            var path = Path.GetFullPath(mediaPath);
            return IsPathWithinRoot(root, path) && !ContainsSymlinkInPath(root, path)
                && RecoverPendingInPlaceUpdates(root, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return false; }
    }

    private static string RecoverySlotPath(string recoveryRoot, int slot)
        => Path.Combine(recoveryRoot, slot == 0 ? RecoverySlot0FileName : RecoverySlot1FileName);

    private static bool IsValidRecoveryGeneration(ulong generation) => generation != 0;

    // Serial arithmetic keeps ordering meaningful when the unsigned counter wraps.
    private static bool IsRecoveryGenerationNewer(ulong candidate, ulong current)
        => candidate != 0 && current != 0 && candidate != current
            && unchecked(candidate - current) < (1UL << 63);

    private static ulong NextRecoveryGeneration(ulong generation)
        => generation == ulong.MaxValue ? 1UL : generation + 1UL;

    private static RecoveryStateDocument CreateRecoveryState(
        ulong generation, string operationId, string phase, string previousStateSha256,
        string destination, FileIdentity destinationIdentity, byte[] oldBytes, byte[] newBytes)
    {
        var oldHash = Convert.ToHexString(SHA256.HashData(oldBytes));
        var state = new RecoveryStateDocument(
            1, generation, operationId, phase, previousStateSha256, destination,
            destinationIdentity.Device, destinationIdentity.Inode, oldHash,
            Convert.ToHexString(SHA256.HashData(newBytes)), Convert.ToBase64String(oldBytes), oldHash, string.Empty,
            Convert.ToBase64String(newBytes));
        return state with { StateSha256 = ComputeRecoveryStateSha256(state) };
    }

    private static RecoveryStateDocument CreatePairedRecoveryState(
        ulong generation, string operationId, string phase, string previousStateSha256,
        string streamDestination, FileIdentity streamOldIdentity, FileIdentity streamNewIdentity,
        byte[] streamOldBytes, byte[] streamNewBytes,
        string markerDestination, FileIdentity markerOldIdentity, FileIdentity markerNewIdentity,
        byte[] markerOldBytes, byte[] markerNewBytes,
        bool? streamOldPresent = null, bool includeStreamNewBytes = true,
        string? streamNewSha256Override = null)
    {
        var streamOldHash = Convert.ToHexString(SHA256.HashData(streamOldBytes));
        var streamNewHash = streamNewSha256Override ?? Convert.ToHexString(SHA256.HashData(streamNewBytes));
        var markerOldHash = Convert.ToHexString(SHA256.HashData(markerOldBytes));
        var markerNewHash = Convert.ToHexString(SHA256.HashData(markerNewBytes));
        var state = new RecoveryStateDocument(
            1, generation, operationId, phase, previousStateSha256, streamDestination,
            streamOldIdentity.Device, streamOldIdentity.Inode, streamOldHash, streamNewHash,
            Convert.ToBase64String(streamOldBytes), streamOldHash, string.Empty, null,
            streamDestination, streamOldIdentity.Device, streamOldIdentity.Inode,
            streamNewIdentity.Device, streamNewIdentity.Inode, streamOldHash, streamNewHash,
            Convert.ToBase64String(streamOldBytes), includeStreamNewBytes ? Convert.ToBase64String(streamNewBytes) : null,
            markerDestination, markerOldIdentity.Device, markerOldIdentity.Inode,
            markerNewIdentity.Device, markerNewIdentity.Inode, markerOldHash, markerNewHash,
            Convert.ToBase64String(markerOldBytes), Convert.ToBase64String(markerNewBytes),
            streamOldPresent);
        return state with { StateSha256 = ComputeRecoveryStateSha256(state) };
    }

    private static bool IsPairedRecoveryState(RecoveryStateDocument state)
        => !string.IsNullOrEmpty(state.StreamDestination)
           || !string.IsNullOrEmpty(state.MarkerDestination);

    private static string ComputeRecoveryStateSha256(RecoveryStateDocument state)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            state with { StateSha256 = string.Empty }, RecoveryJsonOptions)));


    private static bool TryReadRecoveryStateBytes(
        byte[] bytes, out RecoveryStateDocument state, out string hash)
    {
        state = null!; hash = string.Empty;
        try
        {
            var value = JsonSerializer.Deserialize<RecoveryStateDocument>(bytes, RecoveryJsonOptions);
            if (value is null || value.Version != 1 || !IsValidRecoveryGeneration(value.Generation)
                || !Guid.TryParseExact(value.OperationId, "N", out _)
                || value.Phase is not ("intent" or "ready" or "publication" or "complete" or "suppressed")
                || (value.PreviousStateSha256 != "none" && !IsSha256(value.PreviousStateSha256))
                || string.IsNullOrWhiteSpace(value.Destination)
                || Path.IsPathRooted(value.Destination)
                || value.Destination.Contains('\u0000')
                || value.DestinationDevice == 0 || value.DestinationInode == 0
                || !IsSha256(value.DestinationOldSha256) || !IsSha256(value.DestinationNewSha256)
                || !IsSha256(value.OldBytesSha256) || !IsSha256(value.StateSha256)
                || string.Equals(value.PreviousStateSha256, value.StateSha256, StringComparison.OrdinalIgnoreCase)
                || (value.DestinationNewBytesBase64 is not null
                    && !TryDecodeRecoveryBytes(value.DestinationNewBytesBase64, MaxManagedMarkerBytes, out _))
                || !string.Equals(value.StateSha256, ComputeRecoveryStateSha256(value), StringComparison.OrdinalIgnoreCase))
                return false;

            if (IsPairedRecoveryState(value))
            {
                if (!TryValidatePairedRecoveryState(value, out state, out hash))
                    return false;
            }
            else
            {
                if (!TryDecodeRecoveryBytes(value.OldBytesBase64, MaxManagedStrmBytes, out var old)
                    || !string.Equals(Convert.ToHexString(SHA256.HashData(old)), value.OldBytesSha256, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(value.OldBytesSha256, value.DestinationOldSha256, StringComparison.OrdinalIgnoreCase))
                    return false;
                state = value; hash = Convert.ToHexString(SHA256.HashData(bytes));
            }

            // PreviousStateSha256 binds the exact durable slot bytes, rather
            // than merely the self-reported semantic StateSha256 field.
            hash = Convert.ToHexString(SHA256.HashData(bytes));
            return ValidateRecoveryStateSemantics(state);
        }
        catch { state = null!; hash = string.Empty; return false; }
    }

    private static bool ValidateRecoveryStateSemantics(RecoveryStateDocument state)
    {
        static bool IsSafeRelative(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains('\u0000'))
                return false;
            var normalized = path.Replace('\\', '/');
            return normalized.Split('/', StringSplitOptions.None).All(segment => segment.Length > 0 && segment is not ("." or ".."));
        }

        if (!IsSafeRelative(state.Destination)
            || !Guid.TryParseExact(state.OperationId, "N", out _))
            return false;

        if (!IsPairedRecoveryState(state))
        {
            // Single-member recovery is also used by the bounded fixed-slot
            // capability tests and by generic text writers. Its exact content
            // contract is the journal hash; marker/item semantics are checked
            // by the ownership proof before this writer is reached.
            if (!TryDecodeRecoveryBytes(state.OldBytesBase64, MaxManagedStrmBytes, out var oldBytes)
                || !TryDecodeRecoveryBytes(state.DestinationNewBytesBase64, MaxManagedStrmBytes, out var newBytes)
                || !string.Equals(Convert.ToHexString(SHA256.HashData(oldBytes)), state.DestinationOldSha256, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Convert.ToHexString(SHA256.HashData(newBytes)), state.DestinationNewSha256, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!state.Destination.EndsWith(ManagedStrmMarkerSuffix, StringComparison.OrdinalIgnoreCase))
                return true;
            return TryValidateRecoveryMarkerBytes(oldBytes, null, null, null, out _)
                && TryValidateRecoveryMarkerBytes(newBytes, null, null, null, out _);
        }

        if (!IsSafeRelative(state.StreamDestination!) || !IsSafeRelative(state.MarkerDestination!))
            return false;
        var streamDestination = state.StreamDestination!.Replace('\\', '/');
        var markerDestination = state.MarkerDestination!.Replace('\\', '/');
        if (!string.Equals(state.Destination.Replace('\\', '/'), streamDestination, StringComparison.OrdinalIgnoreCase)
            || state.DestinationDevice != state.StreamOldDevice || state.DestinationInode != state.StreamOldInode
            || state.StreamOldDevice != state.StreamNewDevice || state.StreamOldInode != state.StreamNewInode
            || state.MarkerOldDevice != state.MarkerNewDevice || state.MarkerOldInode != state.MarkerNewInode)
            return false;

        if (state.StreamOldPresent is false)
        {
            if (!streamDestination.EndsWith(".mediainfo.json", StringComparison.OrdinalIgnoreCase)
                || state.StreamOldBytesBase64 != Convert.ToBase64String([])
                || state.StreamNewBytesBase64 is not null)
                return false;
            if (!string.Equals(markerDestination,
                    streamDestination[..^".mediainfo.json".Length] + ".strm" + ManagedStrmMarkerSuffix,
                    StringComparison.OrdinalIgnoreCase))
                return false;
            if (!TryDecodeRecoveryBytes(state.MarkerOldBytesBase64, MaxManagedMarkerBytes, out var oldMarker)
                || !TryDecodeRecoveryBytes(state.MarkerNewBytesBase64, MaxManagedMarkerBytes, out var newMarker))
                return false;
            return TryValidateRecoveryMarkerBytes(oldMarker, null, null, null, out var probeItemId,
                       expectedProbeHash: "none")
                && TryValidateRecoveryMarkerBytes(newMarker, null, null, null, out var newProbeItemId,
                       expectedProbeHash: state.StreamNewSha256)
                && probeItemId == newProbeItemId;
        }

        if (!streamDestination.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(markerDestination, streamDestination + ManagedStrmMarkerSuffix, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!TryDecodeRecoveryBytes(state.StreamOldBytesBase64, MaxManagedStrmBytes, out var streamOld)
            || !TryDecodeRecoveryBytes(state.StreamNewBytesBase64, MaxManagedStrmBytes, out var streamNew)
            || !TryDecodeRecoveryBytes(state.MarkerOldBytesBase64, MaxManagedMarkerBytes, out var markerOld)
            || !TryDecodeRecoveryBytes(state.MarkerNewBytesBase64, MaxManagedMarkerBytes, out var markerNew))
            return false;
        return TryValidateRecoveryMarkerBytes(streamOld: markerOld, expectedStreamBytes: streamOld,
                   expectedStreamIdentity: new FileIdentity(state.StreamOldDevice, state.StreamOldInode),
                   expectedOperationId: null, out var pairItemId,
                   expectedProbeHash: null)
            && TryValidateRecoveryMarkerBytes(markerNew, streamNew,
                   new FileIdentity(state.StreamNewDevice, state.StreamNewInode), null, out var newPairItemId,
                   expectedProbeHash: null)
            && pairItemId == newPairItemId;
    }

    private static bool TryValidateRecoveryMarkerBytes(
        byte[] streamOld,
        byte[]? expectedStreamBytes,
        FileIdentity? expectedStreamIdentity,
        Guid? expectedOperationId,
        out Guid itemId,
        string? expectedProbeHash = null)
    {
        itemId = default;
        try
        {
            var text = new UTF8Encoding(false, true).GetString(streamOld);
            if (text.Contains('\r') || text.Contains('\n')) return false;
            var parts = text.Split('/', StringSplitOptions.None);
            if (parts.Length == 6 && parts[0] == ManagedMarkerHeader && parts[1] == "2"
                && Guid.TryParseExact(parts[2], "D", out itemId)
                && TryParseCanonicalUInt64(parts[3], out var legacyProbeDevice)
                && TryParseCanonicalUInt64(parts[4], out var legacyProbeInode)
                && (parts[5] == "none" || IsSha256(parts[5]))
                && (parts[5] == "none" ? legacyProbeDevice == 0 && legacyProbeInode == 0
                    : legacyProbeDevice != 0 && legacyProbeInode != 0))
                return true;
            if (parts.Length != 11 || parts[0] != ManagedMarkerHeader || parts[1] != "4"
                || !Guid.TryParseExact(parts[2], "N", out var operationId)
                || (expectedOperationId is not null && operationId != expectedOperationId.Value)
                || !Guid.TryParseExact(parts[3], "D", out itemId)
                || !TryParseCanonicalUInt64(parts[4], out var streamDevice)
                || !TryParseCanonicalUInt64(parts[5], out var streamInode)
                || !IsSha256(parts[6]) || !TryParseCanonicalUInt64(parts[7], out var probeDevice)
                || !TryParseCanonicalUInt64(parts[8], out var probeInode)
                || !(parts[9] == "none" || IsSha256(parts[9]))
                || !TryDecodeManagedUrl(parts[10], out var url)
                || !string.Equals(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))), parts[6], StringComparison.OrdinalIgnoreCase)
                || (expectedStreamIdentity is not null
                    && new FileIdentity(streamDevice, streamInode) != expectedStreamIdentity.Value)
                || !TryGetStreamId(url, out var urlId) || urlId != itemId
                || !TryValidateRecoveryStreamUrl(url)
                || !string.Equals(Convert.ToBase64String(Encoding.UTF8.GetBytes(url)).TrimEnd('=').Replace('+', '-').Replace('/', '_'), parts[10], StringComparison.Ordinal))
                return false;
            if (expectedStreamBytes is not null
                && !expectedStreamBytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(url)))
                return false;
            if (expectedProbeHash is not null && !string.Equals(parts[9], expectedProbeHash, StringComparison.OrdinalIgnoreCase))
                return false;
            if (parts[9] == "none" ? probeDevice != 0 || probeInode != 0 : probeDevice == 0 || probeInode == 0)
                return false;
            return true;
        }
        catch { itemId = default; return false; }
    }

    private static bool TryValidateRecoveryStreamUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)
            || !TryGetSingleQueryParameter(uri.Query, out var key, out var token))
            return false;
        return string.Equals(key, "apikey", StringComparison.OrdinalIgnoreCase)
            ? !string.IsNullOrWhiteSpace(token)
            : string.Equals(key, "token", StringComparison.OrdinalIgnoreCase)
                && TryParseStreamTokenParts(token, out _);
    }

    private static bool TryValidatePairedRecoveryState(
        RecoveryStateDocument value, out RecoveryStateDocument state, out string hash)
    {
        state = null!; hash = string.Empty;
        if (string.IsNullOrWhiteSpace(value.StreamDestination)
            || string.IsNullOrWhiteSpace(value.MarkerDestination)
            || Path.IsPathRooted(value.StreamDestination)
            || Path.IsPathRooted(value.MarkerDestination)
            || value.StreamDestination.Contains('\u0000')
            || value.MarkerDestination.Contains('\u0000')
            || value.StreamOldDevice == 0 || value.StreamOldInode == 0
            || value.StreamNewDevice == 0 || value.StreamNewInode == 0
            || value.MarkerOldDevice == 0 || value.MarkerOldInode == 0
            || value.MarkerNewDevice == 0 || value.MarkerNewInode == 0
            || !IsSha256(value.StreamOldSha256) || !IsSha256(value.StreamNewSha256)
            || !IsSha256(value.MarkerOldSha256) || !IsSha256(value.MarkerNewSha256)
            || value.StreamOldBytesBase64 is null
            || value.MarkerOldBytesBase64 is null || value.MarkerNewBytesBase64 is null)
            return false;

        var streamOldLimit = value.StreamOldPresent is not false ? MaxManagedStrmBytes : MaxManagedProbeBytes;
        if (!TryDecodeRecoveryBytes(value.StreamOldBytesBase64, streamOldLimit, out var streamOld)
            || !TryDecodeRecoveryBytes(value.MarkerOldBytesBase64, MaxManagedMarkerBytes, out var markerOld)
            || !TryDecodeRecoveryBytes(value.MarkerNewBytesBase64, MaxManagedMarkerBytes, out var markerNew)
            || !string.Equals(Convert.ToHexString(SHA256.HashData(streamOld)), value.StreamOldSha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Convert.ToHexString(SHA256.HashData(markerOld)), value.MarkerOldSha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Convert.ToHexString(SHA256.HashData(markerNew)), value.MarkerNewSha256, StringComparison.OrdinalIgnoreCase)
            || value.Destination != value.StreamDestination
            || value.DestinationDevice != value.StreamOldDevice
            || value.DestinationInode != value.StreamOldInode
            || !string.Equals(value.DestinationOldSha256, value.StreamOldSha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(value.DestinationNewSha256, value.StreamNewSha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(value.OldBytesSha256, value.StreamOldSha256, StringComparison.OrdinalIgnoreCase))
            return false;

        if (value.StreamOldPresent is not false)
        {
            if (!TryDecodeRecoveryBytes(value.StreamNewBytesBase64, MaxManagedStrmBytes, out var streamNew)
                || !string.Equals(Convert.ToHexString(SHA256.HashData(streamNew)), value.StreamNewSha256, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        else if (value.StreamNewBytesBase64 is not null)
        {
            // Addition transactions deliberately do not duplicate a potentially
            // multi-megabyte probe in the fixed-size journal. Its inode and hash
            // are enough to either finish or safely retire the publication.
            return false;
        }

        state = value; hash = value.StateSha256; return true;
    }

    private static bool TryDecodeRecoveryBytes(string? encoded, int maxBytes, out byte[] bytes)
    {
        bytes = [];
        if (encoded is null || encoded.Length > ((maxBytes + 2) / 3) * 4 + 4)
            return false;
        try
        {
            bytes = Convert.FromBase64String(encoded);
            return bytes.Length <= maxBytes;
        }
        catch (FormatException) { return false; }
    }
    private static bool TrySelectLatestRecoveryState(
        string canonicalRoot, string recoveryRoot, out RecoveryStateDocument state, out string hash,
        out int slot, out bool anySlot, out RecoverySlotExpectation inactiveTarget,
        out RecoverySlotExpectation predecessor)
    {
        state = null!; hash = string.Empty; slot = -1;
        var path0 = RecoverySlotPath(recoveryRoot, 0);
        var path1 = RecoverySlotPath(recoveryRoot, 1);
        var snapshot0 = ReadRecoverySlotExpectation(canonicalRoot, path0, 0);
        var snapshot1 = ReadRecoverySlotExpectation(canonicalRoot, path1, 1);
        anySlot = snapshot0.Exists || snapshot1.Exists;
        var valid0 = snapshot0.State is not null;
        var valid1 = snapshot1.State is not null;
        inactiveTarget = null!;
        predecessor = null!;
        if (!valid0 && !valid1)
            return false;

        // A missing/torn/foreign sibling is not a recoverable append point. In
        // particular, never treat a valid genesis plus a foreign sibling as a
        // license to overwrite that sibling.
        if ((!valid0 && snapshot0.Exists) || (!valid1 && snapshot1.Exists))
            return false;

        if (valid0 && valid1)
        {
            if (!IsAnchoredRecoveryState(snapshot0.State!) || !IsAnchoredRecoveryState(snapshot1.State!))
                return false;
            if (IsRecoveryGenerationNewer(snapshot0.State!.Generation, snapshot1.State!.Generation))
            {
                if (!IsRecoveryPredecessorValid(snapshot0.State, snapshot1.State, snapshot1.RawSha256)
                    || !IsAnchoredRecoveryState(snapshot1.State))
                    return false;
                state = snapshot0.State; hash = snapshot0.RawSha256; slot = 0;
                inactiveTarget = snapshot1;
                predecessor = snapshot0;
            }
            else if (IsRecoveryGenerationNewer(snapshot1.State!.Generation, snapshot0.State!.Generation))
            {
                if (!IsRecoveryPredecessorValid(snapshot1.State, snapshot0.State, snapshot0.RawSha256)
                    || !IsAnchoredRecoveryState(snapshot0.State))
                    return false;
                state = snapshot1.State; hash = snapshot1.RawSha256; slot = 1;
                inactiveTarget = snapshot0;
                predecessor = snapshot1;
            }
            else
                return false;
            return true;
        }

        // A sole slot is accepted only as the exact canonical genesis. Any
        // later generation needs its complete predecessor bytes in the other
        // slot; generation numbers alone are never an anchor.
        var sole = valid0 ? snapshot0 : snapshot1;
        if (!IsCanonicalRecoveryGenesis(sole.State!))
            return false;
        state = sole.State!;
        hash = sole.RawSha256;
        slot = sole.Slot;
        predecessor = sole;
        inactiveTarget = valid0 ? snapshot1 : snapshot0;
        return true;
    }

    private static RecoverySlotExpectation ReadRecoverySlotExpectation(string canonicalRoot, string path, int slot)
    {
        if (!TryReadBoundedFileForPath(canonicalRoot, path, MaxRecoveryJournalBytes, out var bytes, out var identity)
            || identity is null || bytes.Length == 0)
        {
            var exists = false;
            try
            {
                exists = TryPathExistsAnchored(canonicalRoot, path, out var isDirectory) && !isDirectory;
            }
            catch
            {
                // A rooted traversal failure is an occupied/unprovable slot,
                // never an invitation to create over it.
                exists = true;
            }
            return new(slot, exists, exists ? CaptureFileIdentity(canonicalRoot, path) : null,
                bytes, bytes.Length == 0 ? string.Empty : Convert.ToHexString(SHA256.HashData(bytes)),
                null, string.Empty);
        }

        var rawHash = Convert.ToHexString(SHA256.HashData(bytes));
        return TryReadRecoveryStateBytes(bytes, out var parsed, out _)
            ? new(slot, true, identity, bytes, rawHash, parsed, parsed.StateSha256)
            : new(slot, true, identity, bytes, rawHash, null, string.Empty);
    }

    private static bool IsCanonicalRecoveryGenesis(RecoveryStateDocument state)
        => state.Version == 1
            && state.Generation == 1
            && state.PreviousStateSha256 == "none"
            && state.Phase == "intent";

    private static bool IsAnchoredRecoveryState(RecoveryStateDocument state)
        => state.Generation == 1
            ? IsCanonicalRecoveryGenesis(state)
            : state.PreviousStateSha256 != "none";

    private static bool IsRecoveryPredecessorValid(
        RecoveryStateDocument next, RecoveryStateDocument predecessor, string predecessorHash)
        => next.Generation == NextRecoveryGeneration(predecessor.Generation)
            && string.Equals(next.PreviousStateSha256, predecessorHash, StringComparison.OrdinalIgnoreCase);

    private static RecoverySlotExpectation WriteRecoverySlotWithHook(
        string canonicalRoot, string recoveryRoot, int slot, byte[] bytes, Action<string>? mutationHook,
        RecoverySlotExpectation expectedTarget,
        RecoverySlotExpectation expectedPredecessor)
    {
        if (bytes.Length == 0 || bytes.Length > MaxRecoveryJournalBytes)
            throw new IOException("Recovery slot payload exceeds the fixed bound.");
        var path = RecoverySlotPath(recoveryRoot, slot);
        var name = slot == 0 ? RecoverySlot0FileName : RecoverySlot1FileName;
        if (!TryReadRecoveryStateBytes(bytes, out var next, out _))
            throw new IOException("Recovery slot grammar/content is invalid.");

        ValidateRecoverySlotAppend(next, expectedTarget, expectedPredecessor);
        // This is the deterministic pre-open watcher barrier. If the selected
        // pathname is replaced here, the no-follow open below sees the foreign
        // identity and fails before obtaining a writable capability.
        mutationHook?.Invoke(path);
        if (ContainsSymlinkInPath(path))
            throw new IOException("Recovery slot path contains a reparse point.");

        if (!OperatingSystem.IsLinux())
        {
            // Hold the selected predecessor before opening/creating the target.
            // The hold is the invariant; the pathname checks below are only the
            // second half of the proof and are never used as an ownership token.
            FileStream? heldPredecessor = null;
            FileStream? targetStream = null;
            var targetMutationStarted = false;
            var targetPayloadDurable = false;
            var targetIdentity = default(FileIdentity);
            try
            {
                var predecessorPath = RecoverySlotPath(recoveryRoot, expectedPredecessor.Slot);
                if (expectedPredecessor.Exists)
                {
                    if (ContainsSymlinkInPath(predecessorPath))
                        throw new IOException("Recovery predecessor path contains a reparse point.");
                    heldPredecessor = OpenWindowsRecoverySlot(predecessorPath, expectedExists: true);
                    VerifyHeldRecoveryPredecessor(heldPredecessor, expectedPredecessor);
                }
                else
                {
                    VerifyRecoverySlotExpectationPath(canonicalRoot, recoveryRoot, expectedPredecessor);
                }

                targetStream = OpenWindowsRecoverySlot(path, expectedTarget.Exists);
                targetIdentity = GetFileIdentityWindows(targetStream.SafeFileHandle);
                VerifyRecoverySlotTarget(canonicalRoot, targetStream, path, recoveryRoot, expectedTarget, targetIdentity);

                // Post-open barrier and final path check happen before SetLength
                // or Write. A replaced well-formed slot is still foreign and
                // remains byte-for-byte untouched.
                mutationHook?.Invoke(path);
                if (GetFileIdentityWindows(targetStream.SafeFileHandle) != targetIdentity
                    || CaptureFileIdentity(recoveryRoot, path) != targetIdentity)
                    throw new IOException("Recovery slot identity changed before publication.");
                VerifyRecoverySlotTarget(canonicalRoot, targetStream, path, recoveryRoot, expectedTarget, targetIdentity);
                VerifyHeldOrAbsentRecoveryPredecessor(canonicalRoot, recoveryRoot, expectedPredecessor, heldPredecessor);
                // From this point until the final predecessor proof, the held
                // target may contain an uncommitted next state. If that proof
                // fails, restore through this handle; never use the pathname
                // as a rollback capability.
                targetMutationStarted = true;
                targetStream.Position = 0;
                targetStream.SetLength(0);
                targetStream.Write(bytes);
                targetStream.Flush(true);
                targetPayloadDurable = true;
                if (GetFileIdentityWindows(targetStream.SafeFileHandle) != targetIdentity
                    || CaptureFileIdentity(recoveryRoot, path) != targetIdentity)
                    throw new IOException("Recovery slot identity changed during publication.");
                SyncDirectory(recoveryRoot);

                // This is deliberately after target publication and before the
                // final invariant check. Tests use it to replace the named
                // predecessor at the exact publication boundary.
                mutationHook?.Invoke(RecoverySlotPath(recoveryRoot, expectedPredecessor.Slot));
                VerifyHeldOrAbsentRecoveryPredecessor(canonicalRoot, recoveryRoot, expectedPredecessor, heldPredecessor);
                targetStream.Position = 0;
                var afterHeld = ReadBoundedBytes(targetStream, MaxRecoveryJournalBytes);
                if (afterHeld is null || !afterHeld.AsSpan().SequenceEqual(bytes)
                    || !TryReadBoundedFileForPath(canonicalRoot, path, MaxRecoveryJournalBytes, out var after, out var afterIdentity)
                    || afterIdentity != targetIdentity || !after.AsSpan().SequenceEqual(bytes))
                    throw new IOException("Recovery slot post-publication proof failed.");
                return new RecoverySlotExpectation(slot, true, targetIdentity, bytes,
                    Convert.ToHexString(SHA256.HashData(bytes)), next, next.StateSha256);
            }
            catch
            {
                if (targetMutationStarted)
                    TryRestoreRecoveryTargetWindows(canonicalRoot, targetStream, path, recoveryRoot, expectedTarget, targetIdentity,
                        targetPayloadDurable);
                throw;
            }
            finally
            {
                targetStream?.Dispose();
                heldPredecessor?.Dispose();
            }
        }

        var rootFd = OpenDirectoryForMutation(canonicalRoot);
        var dirFd = rootFd;
        var fd = -1;
        FileStream? targetStreamLinux = null;
        HeldLinuxFile? heldPredecessorLinux = null;
        var targetMutationStartedLinux = false;
        var targetPayloadDurableLinux = false;
        var targetIdentityLinux = default(FileIdentity);
        var rootDevice = default(ulong);
        var targetExistedLinux = false;
        try
        {
            rootDevice = ValidateOpenedDirectoryDescriptor(rootFd, canonicalRoot, expectedDevice: null);
            dirFd = OpenDirectoryChain(canonicalRoot, rootFd, rootDevice, RecoveryDirectoryName,
                createDirectories: false, invokeMutationHook: false);
            var predecessorPath = RecoverySlotPath(recoveryRoot, expectedPredecessor.Slot);
            if (expectedPredecessor.Exists)
            {
                heldPredecessorLinux = OpenHeldLinuxFile(recoveryRoot, predecessorPath, rootDevice);
                VerifyHeldRecoveryPredecessor(heldPredecessorLinux, expectedPredecessor);
            }
            else
            {
                VerifyRecoverySlotExpectationPath(canonicalRoot, recoveryRoot, expectedPredecessor);
            }

            fd = OpenReadWriteFileAt(dirFd, rootDevice, name, path);
            targetExistedLinux = fd >= 0;
            if (!targetExistedLinux)
            {
                var errno = Marshal.GetLastWin32Error();
                if (expectedTarget.Exists || errno != ErrnoNoEnt)
                    throw new IOException($"Unable to open recovery slot without replacement. errno={errno}");
                fd = OpenAnonymousWritableFileAt(dirFd, rootDevice, recoveryRoot, readWrite: true);
                if (fd < 0)
                    throw new PlatformNotSupportedException("Recovery requires fixed-slot filesystem support.");
            }
            else if (!expectedTarget.Exists)
            {
                _ = CloseDirectoryHandle(fd);
                fd = -1;
                throw new IOException("Recovery slot appeared before exclusive publication.");
            }

            targetStreamLinux = new FileStream(
                new global::Microsoft.Win32.SafeHandles.SafeFileHandle((IntPtr)fd, true),
                FileAccess.ReadWrite, MaxRecoveryJournalBytes, false);
            fd = -1;
            targetIdentityLinux = GetFileIdentity(checked((int)targetStreamLinux.SafeFileHandle.DangerousGetHandle()));
            VerifyRecoverySlotTarget(canonicalRoot, targetStreamLinux, path, recoveryRoot, expectedTarget, targetIdentityLinux,
                dirFd, rootDevice, name);

            mutationHook?.Invoke(path);
            if ((targetExistedLinux && !PathHasIdentity(dirFd, rootDevice, name, path, targetIdentityLinux))
                || (!targetExistedLinux && PathHasIdentity(dirFd, rootDevice, name, path, targetIdentityLinux)))
                throw new IOException("Recovery slot identity changed before publication.");
            VerifyRecoverySlotTarget(canonicalRoot, targetStreamLinux, path, recoveryRoot, expectedTarget, targetIdentityLinux,
                dirFd, rootDevice, name);
            VerifyHeldOrAbsentRecoveryPredecessor(canonicalRoot, recoveryRoot, expectedPredecessor, heldPredecessorLinux);

            targetMutationStartedLinux = true;
            targetStreamLinux.Position = 0;
            targetStreamLinux.SetLength(0);
            targetStreamLinux.Write(bytes);
            targetStreamLinux.Flush(true);
            targetPayloadDurableLinux = true;
            if (SyncFd(checked((int)targetStreamLinux.SafeFileHandle.DangerousGetHandle())) != 0)
                throw new IOException("Failed to fsync recovery slot.");
            if (!targetExistedLinux && LinkFileAtNoReplace(
                    checked((int)targetStreamLinux.SafeFileHandle.DangerousGetHandle()), dirFd, name) != 0)
                throw new IOException("Failed to publish recovery slot without replacement.");
            if (!PathHasIdentity(dirFd, rootDevice, name, path, targetIdentityLinux)
                || SyncDirectoryFd(dirFd) != 0)
                throw new IOException("Recovery slot publication identity or durability proof failed.");

            // See the Windows branch: the hook is after target publication, so
            // a watcher replacement is checked against both the held inode and
            // the current named predecessor before this append can succeed.
            mutationHook?.Invoke(predecessorPath);
            VerifyHeldOrAbsentRecoveryPredecessor(canonicalRoot, recoveryRoot, expectedPredecessor, heldPredecessorLinux);
            targetStreamLinux.Position = 0;
            var afterHeld = ReadBoundedBytes(targetStreamLinux, MaxRecoveryJournalBytes);
            if (afterHeld is null || !afterHeld.AsSpan().SequenceEqual(bytes)
                || !TryReadBoundedFileForPath(canonicalRoot, path, MaxRecoveryJournalBytes, out var after, out var afterIdentity)
                || afterIdentity != targetIdentityLinux || !after.AsSpan().SequenceEqual(bytes))
                throw new IOException("Recovery slot post-publication proof failed.");
            return new RecoverySlotExpectation(slot, true, targetIdentityLinux, bytes,
                Convert.ToHexString(SHA256.HashData(bytes)), next, next.StateSha256);
        }
        catch
        {
            if (targetMutationStartedLinux)
                TryRestoreRecoveryTargetLinux(targetStreamLinux, path, expectedTarget, targetIdentityLinux,
                    dirFd, rootDevice, name, targetExistedLinux, targetPayloadDurableLinux);
            throw;
        }
        finally
        {
            targetStreamLinux?.Dispose();
            heldPredecessorLinux?.Dispose();
            if (fd >= 0) _ = CloseDirectoryHandle(fd);
            if (dirFd != rootFd) _ = CloseDirectoryHandle(dirFd);
            _ = CloseDirectoryHandle(rootFd);
        }
    }

    // Rollback is capability-based. The pathname is consulted only to decide
    // whether the held inode is still named; a replacement at that pathname is
    // never opened, truncated, or deleted. A newly-created target whose full
    // intent was already fsynced is retained as the canonical fail-closed
    // uncommitted state; an interrupted/torn write is reduced to an empty slot.
    private static void TryRestoreRecoveryTargetWindows(
        string canonicalRoot, FileStream? held, string path, string recoveryRoot,
        RecoverySlotExpectation expected, FileIdentity identity,
        bool payloadDurable)
    {
        if (held is null)
            return;

        try
        {
            if (GetFileIdentityWindows(held.SafeFileHandle) != identity)
                return;
            held.Position = 0;
            if (expected.Exists)
            {
                held.SetLength(0);
                held.Write(expected.Bytes);
                held.Flush(true);
            }
            else if (!payloadDurable)
            {
                held.SetLength(0);
                held.Flush(true);
            }

            // File flush makes the held bytes durable even if a watcher won
            // the name. Sync the directory only when that name still resolves
            // to our held identity.
            if (CaptureFileIdentity(canonicalRoot, path) == identity)
                SyncDirectory(recoveryRoot);
        }
        catch
        {
            // The original publication exception remains authoritative. A
            // foreign replacement must not turn rollback into pathname damage.
        }
    }

    private static void TryRestoreRecoveryTargetLinux(
        FileStream? held, string path, RecoverySlotExpectation expected,
        FileIdentity identity, int directoryFd, ulong rootDevice, string name,
        bool wasNamed, bool payloadDurable)
    {
        if (held is null)
            return;

        try
        {
            var fd = checked((int)held.SafeFileHandle.DangerousGetHandle());
            if (GetFileIdentity(fd) != identity)
                return;
            held.Position = 0;
            if (expected.Exists)
            {
                held.SetLength(0);
                held.Write(expected.Bytes);
                held.Flush(true);
            }
            else if (!payloadDurable)
            {
                held.SetLength(0);
                held.Flush(true);
            }
            if (SyncFd(fd) != 0)
                return;

            // An O_TMPFILE inode has no pathname until LinkFileAtNoReplace.
            // If publication linked it, sync the directory only if the name
            // still resolves to our held inode; a foreign replacement is left
            // entirely alone.
            if (wasNamed && PathHasIdentity(directoryFd, rootDevice, name, path, identity))
                _ = SyncDirectoryFd(directoryFd);
        }
        catch
        {
            // See the Windows helper: preserve the original failure and never
            // use a path operation as rollback.
        }
    }

    private static void ValidateRecoverySlotAppend(
        RecoveryStateDocument next, RecoverySlotExpectation target,
        RecoverySlotExpectation predecessor)
    {
        if (next.Generation == 1)
        {
            if (!IsCanonicalRecoveryGenesis(next) || predecessor.Exists)
                throw new IOException("Recovery slot initial predecessor is invalid.");
            return;
        }

        if (!predecessor.Exists || predecessor.State is null
            || !IsRecoveryPredecessorValid(next, predecessor.State, predecessor.RawSha256))
            throw new IOException("Recovery slot predecessor is invalid.");

        if (target.Exists && (target.Identity is null || target.State is null
            || !IsSha256(target.RawSha256) || target.Bytes.Length == 0
            || (predecessor.Exists && predecessor.State is not null
                && !IsRecoveryPredecessorValid(predecessor.State, target.State, target.RawSha256))))
            throw new IOException("Recovery slot target expectation is invalid.");
    }

    private static void VerifyRecoverySlotExpectationPath(
        string canonicalRoot, string recoveryRoot, RecoverySlotExpectation expected)
    {
        var path = RecoverySlotPath(recoveryRoot, expected.Slot);
        if (!expected.Exists)
        {
            if (TryPathExistsAnchored(canonicalRoot, path, out _))
                throw new IOException("Recovery predecessor appeared after selection.");
            return;
        }

        var readable = TryReadBoundedFileForPath(canonicalRoot, path, MaxRecoveryJournalBytes, out var bytes, out var identity);
        var sameIdentity = expected.Identity is not null && identity == expected.Identity.Value;
        var sameBytes = readable && bytes.AsSpan().SequenceEqual(expected.Bytes);
        var sameHash = readable && string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), expected.RawSha256,
            StringComparison.OrdinalIgnoreCase);
        var validState = readable && TryReadRecoveryStateBytes(bytes, out var parsed, out _)
            && expected.State is not null && parsed.StateSha256 == expected.State.StateSha256;
        if (!sameIdentity || !sameBytes || !sameHash || !validState)
            throw new IOException("Recovery predecessor changed after selection.");
    }

    private static void VerifyHeldRecoveryPredecessor(
        FileStream held, RecoverySlotExpectation expected)
    {
        if (!expected.Exists || expected.Identity is null || GetFileIdentityWindows(held.SafeFileHandle) != expected.Identity.Value)
            throw new IOException("Held recovery predecessor identity differs from selection.");

        held.Position = 0;
        VerifyRecoverySlotExpectationBytes(expected, ReadBoundedBytes(held, MaxRecoveryJournalBytes));
    }

    private static void VerifyHeldRecoveryPredecessor(
        HeldLinuxFile held, RecoverySlotExpectation expected)
    {
        if (!expected.Exists || expected.Identity is null || held.Identity != expected.Identity.Value)
            throw new IOException("Held recovery predecessor identity differs from selection.");

        using var view = new FileStream(
            new global::Microsoft.Win32.SafeHandles.SafeFileHandle((IntPtr)held.FileFd, ownsHandle: false),
            FileAccess.Read, MaxRecoveryJournalBytes, isAsync: false);
        VerifyRecoverySlotExpectationBytes(expected, ReadBoundedBytes(view, MaxRecoveryJournalBytes));
    }

    private static void VerifyHeldOrAbsentRecoveryPredecessor(
        string canonicalRoot, string recoveryRoot, RecoverySlotExpectation expected, FileStream? held)
    {
        if (expected.Exists)
        {
            if (held is null)
                throw new IOException("Recovery predecessor was not held before publication.");
            VerifyHeldRecoveryPredecessor(held, expected);
        }
        else
        {
            VerifyRecoverySlotExpectationPath(canonicalRoot, recoveryRoot, expected);
        }

        // Holding the inode proves the bytes of the selected object; this
        // second proof binds the same bytes and identity to the live slot name.
        VerifyRecoverySlotExpectationPath(canonicalRoot, recoveryRoot, expected);
    }

    private static void VerifyHeldOrAbsentRecoveryPredecessor(
        string canonicalRoot, string recoveryRoot, RecoverySlotExpectation expected, HeldLinuxFile? held)
    {
        if (expected.Exists)
        {
            if (held is null)
                throw new IOException("Recovery predecessor was not held before publication.");
            VerifyHeldRecoveryPredecessor(held, expected);
        }
        else
        {
            VerifyRecoverySlotExpectationPath(canonicalRoot, recoveryRoot, expected);
        }

        VerifyRecoverySlotExpectationPath(canonicalRoot, recoveryRoot, expected);
    }

    private static void VerifyRecoverySlotExpectationBytes(
        RecoverySlotExpectation expected, byte[]? actual)
    {
        if (!expected.Exists || actual is null || !actual.AsSpan().SequenceEqual(expected.Bytes)
            || !string.Equals(Convert.ToHexString(SHA256.HashData(actual)), expected.RawSha256,
                StringComparison.OrdinalIgnoreCase)
            || expected.State is null
            || !TryReadRecoveryStateBytes(actual, out var parsed, out _)
            || parsed.Generation != expected.State.Generation
            || parsed.StateSha256 != expected.State.StateSha256)
            throw new IOException("Recovery predecessor bytes differ from selected state.");
    }

    private static void VerifyRecoverySlotTarget(
        string canonicalRoot, FileStream stream, string path, string recoveryRoot,
        RecoverySlotExpectation expected, FileIdentity identity,
        int directoryFd = -1, ulong rootDevice = 0, string? name = null)
    {
        if (expected.Exists)
        {
            if (expected.Identity is null || identity != expected.Identity.Value)
                throw new IOException("Recovery slot identity differs from selected target.");
            stream.Position = 0;
            var actual = ReadBoundedBytes(stream, MaxRecoveryJournalBytes);
            if (actual is null || !actual.AsSpan().SequenceEqual(expected.Bytes)
                || !string.Equals(Convert.ToHexString(SHA256.HashData(actual)), expected.RawSha256,
                    StringComparison.OrdinalIgnoreCase)
                || !TryReadRecoveryStateBytes(actual, out var parsed, out _)
                || expected.State is null
                || parsed.StateSha256 != expected.State.StateSha256)
                throw new IOException("Recovery slot bytes differ from selected target.");
        }
        else if (expected.Identity is not null)
        {
            throw new IOException("Missing recovery slot target identity proof.");
        }

        if (directoryFd >= 0 && name is not null &&
            ((expected.Exists && !PathHasIdentity(directoryFd, rootDevice, name, path, identity))
             || (!expected.Exists && PathHasIdentity(directoryFd, rootDevice, name, path, identity))))
            throw new IOException("Recovery slot pathname no longer resolves to the selected target.");
    }

    private static RecoveryArtifacts PersistInPlaceRecoveryJournal(
        string canonicalRoot, string canonicalDestination, FileIdentity identity,
        byte[] oldContent, byte[] newContent)
    {
        if (oldContent.Length > MaxManagedStrmBytes || newContent.Length > MaxManagedStrmBytes)
            throw new IOException("In-place recovery payload exceeds the fixed bound.");
        EnsureRecoveryRoot(canonicalRoot);
        var recoveryRoot = Path.Combine(canonicalRoot, RecoveryDirectoryName);
        var hasLatest = TrySelectLatestRecoveryState(canonicalRoot, recoveryRoot, out var latest, out var latestHash,
            out var latestSlot, out var anySlot, out var inactiveTarget, out var predecessorTarget);
        if (anySlot && !hasLatest) throw new IOException("Both fixed recovery slots are invalid.");
        if (hasLatest && latest.Phase is not "complete")
            throw new IOException("A pending recovery operation must be resolved before another write.");
        var operationId = Guid.NewGuid().ToString("N");
        var intentSlot = hasLatest ? 1 - latestSlot : 0;
        var intent = CreateRecoveryState(
            hasLatest ? NextRecoveryGeneration(latest.Generation) : 1UL, operationId, "intent",
            hasLatest ? latestHash : "none",
            NormalizeRelativePathForComparison(Path.GetRelativePath(canonicalRoot, canonicalDestination)),
            identity, oldContent, newContent);
        var intentBytes = JsonSerializer.SerializeToUtf8Bytes(intent, RecoveryJsonOptions);
        var intentTarget = hasLatest
            ? inactiveTarget
            : ReadRecoverySlotExpectation(canonicalRoot, RecoverySlotPath(recoveryRoot, intentSlot), intentSlot);
        var initialPredecessor = hasLatest
            ? predecessorTarget
            : ReadRecoverySlotExpectation(canonicalRoot, RecoverySlotPath(recoveryRoot, 1 - intentSlot), 1 - intentSlot);
        var intentPredecessor = WriteRecoverySlotWithHook(
            canonicalRoot, recoveryRoot, intentSlot, intentBytes, null, intentTarget, initialPredecessor);
        var ready = CreateRecoveryState(NextRecoveryGeneration(intent.Generation), operationId, "ready",
            Convert.ToHexString(SHA256.HashData(intentBytes)), intent.Destination, identity, oldContent, newContent);
        var readyBytes = JsonSerializer.SerializeToUtf8Bytes(ready, RecoveryJsonOptions);
        var readyTarget = initialPredecessor;
        WriteRecoverySlotWithHook(canonicalRoot, recoveryRoot, 1 - intentSlot, readyBytes, null, readyTarget, intentPredecessor);
        return new RecoveryArtifacts(RecoverySlotPath(recoveryRoot, 1 - intentSlot), string.Empty,
            identity, ready.OldBytesSha256, operationId);
    }

    private static RecoveryStateDocument PersistPairedRecoveryJournal(
        string canonicalRoot,
        string streamPath,
        FileIdentity streamIdentity,
        byte[] streamOldBytes,
        byte[] streamNewBytes,
        string markerPath,
        FileIdentity markerIdentity,
        byte[] markerOldBytes,
        byte[] markerNewBytes,
        bool? streamOldPresent = null,
        bool includeStreamNewBytes = true,
        string? streamNewSha256Override = null,
        Action<string>? mutationHook = null)
    {
        var streamLimit = streamOldPresent is not false ? MaxManagedStrmBytes : MaxManagedProbeBytes;
        if (streamOldBytes.Length > streamLimit
            || (includeStreamNewBytes && streamNewBytes.Length > streamLimit)
            || markerOldBytes.Length > MaxManagedMarkerBytes || markerNewBytes.Length > MaxManagedMarkerBytes)
            throw new IOException("Paired recovery payload exceeds its fixed bound.");

        EnsureRecoveryRoot(canonicalRoot);
        var recoveryRoot = Path.Combine(canonicalRoot, RecoveryDirectoryName);
        var hasLatest = TrySelectLatestRecoveryState(canonicalRoot, recoveryRoot, out var latest, out var latestHash,
            out var latestSlot, out var anySlot, out var inactiveTarget, out var predecessorTarget);
        if (anySlot && !hasLatest)
            throw new IOException("Both fixed recovery slots are invalid.");
        if (hasLatest && latest.Phase is not "complete")
            throw new IOException("A pending recovery operation must be resolved before another write.");

        var operationId = Guid.NewGuid().ToString("N");
        var streamDestination = NormalizeRelativePathForComparison(Path.GetRelativePath(canonicalRoot, streamPath));
        var markerDestination = NormalizeRelativePathForComparison(Path.GetRelativePath(canonicalRoot, markerPath));
        var intentSlot = hasLatest ? 1 - latestSlot : 0;
        var intent = CreatePairedRecoveryState(
            hasLatest ? NextRecoveryGeneration(latest.Generation) : 1UL, operationId, "intent",
            hasLatest ? latestHash : "none", streamDestination, streamIdentity, streamIdentity,
            streamOldBytes, streamNewBytes, markerDestination, markerIdentity, markerIdentity,
            markerOldBytes, markerNewBytes, streamOldPresent, includeStreamNewBytes, streamNewSha256Override);
        var intentBytes = JsonSerializer.SerializeToUtf8Bytes(intent, RecoveryJsonOptions);
        var intentTarget = hasLatest
            ? inactiveTarget
            : ReadRecoverySlotExpectation(canonicalRoot, RecoverySlotPath(recoveryRoot, intentSlot), intentSlot);
        var initialPredecessor = hasLatest
            ? predecessorTarget
            : ReadRecoverySlotExpectation(canonicalRoot, RecoverySlotPath(recoveryRoot, 1 - intentSlot), 1 - intentSlot);
        var intentPredecessor = WriteRecoverySlotWithHook(canonicalRoot, recoveryRoot, intentSlot, intentBytes, mutationHook,
            intentTarget, initialPredecessor);

        var ready = CreatePairedRecoveryState(
            NextRecoveryGeneration(intent.Generation), operationId, "ready",
            Convert.ToHexString(SHA256.HashData(intentBytes)),
            streamDestination, streamIdentity, streamIdentity, streamOldBytes, streamNewBytes,
            markerDestination, markerIdentity, markerIdentity, markerOldBytes, markerNewBytes,
            streamOldPresent, includeStreamNewBytes, streamNewSha256Override);
        var readyBytes = JsonSerializer.SerializeToUtf8Bytes(ready, RecoveryJsonOptions);
        var readyTarget = initialPredecessor;
        WriteRecoverySlotWithHook(canonicalRoot, recoveryRoot, 1 - intentSlot, readyBytes, mutationHook,
            readyTarget, intentPredecessor);
        return ready;
    }

    private static bool WriteRecoveryDestination(string canonicalRoot, RecoveryStateDocument state, byte[] newBytes)
    {
        var destination = Path.GetFullPath(Path.Combine(canonicalRoot, state.Destination));
        var expected = new FileIdentity(state.DestinationDevice, state.DestinationInode);
        if (!IsPathWithinRoot(canonicalRoot, destination) || ContainsSymlinkInPath(canonicalRoot, destination)) return false;
        try
        {
            var marker = string.Equals(state.Destination, state.MarkerDestination, StringComparison.Ordinal);
            var oldEncoded = marker ? state.MarkerOldBytesBase64 : state.StreamOldBytesBase64;
            var newEncoded = marker ? state.MarkerNewBytesBase64 : state.StreamNewBytesBase64;
            var limit = marker ? MaxManagedMarkerBytes : MaxManagedStrmBytes;
            if (oldEncoded is null || newEncoded is null
                || !TryDecodeRecoveryBytes(oldEncoded, limit, out var oldJournalBytes)
                || !TryDecodeRecoveryBytes(newEncoded, limit, out var newJournalBytes)
                || !TryReadBoundedFileForPath(canonicalRoot, destination, limit, out var actualBytes, out var actualIdentity)
                || actualIdentity is null || actualIdentity.Value != expected
                || (!actualBytes.AsSpan().SequenceEqual(oldJournalBytes)
                    && !actualBytes.AsSpan().SequenceEqual(newJournalBytes)))
                return false;

            // WriteOwnedBytes reopens from the rooted descriptor namespace and
            // performs this exact-byte CAS again immediately before mutation.
            return WriteOwnedBytes(canonicalRoot, destination, expected, actualBytes, newBytes);
        }
        catch { return false; }
    }

    private static bool PublishRecoverySuppressed(
        string canonicalRoot, RecoveryStateDocument pending, string hash, int slot)
    {
        var suppressed = pending with
        {
            Generation = NextRecoveryGeneration(pending.Generation),
            Phase = "suppressed",
            PreviousStateSha256 = hash,
            StateSha256 = string.Empty
        };
        suppressed = suppressed with { StateSha256 = ComputeRecoveryStateSha256(suppressed) };
        return PublishRecoveryPhase(canonicalRoot, pending, suppressed, slot);
    }

    private static bool PublishRecoveryComplete(string canonicalRoot, RecoveryStateDocument pending, string hash, int slot)
    {
        var complete = pending with
        {
            Generation = NextRecoveryGeneration(pending.Generation),
            Phase = "complete",
            PreviousStateSha256 = hash,
            StateSha256 = string.Empty
        };
        complete = complete with { StateSha256 = ComputeRecoveryStateSha256(complete) };
        return PublishRecoveryPhase(canonicalRoot, pending, complete, slot);
    }

    private static bool PublishRecoveryPhase(
        string canonicalRoot, RecoveryStateDocument pending,
        RecoveryStateDocument next, int slot)
    {
        var recoveryRoot = Path.Combine(canonicalRoot, RecoveryDirectoryName);
        if (!TrySelectLatestRecoveryState(canonicalRoot, recoveryRoot, out var selected, out _, out var selectedSlot,
                out _, out var target, out var predecessor)
            || selectedSlot != slot
            || selected.StateSha256 != pending.StateSha256)
            return false;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(next, RecoveryJsonOptions);
        WriteRecoverySlotWithHook(canonicalRoot, recoveryRoot, 1 - slot, bytes, null, target, predecessor);
        return true;
    }

    private static bool TryWriteRecoveryPairMember(
        string canonicalRoot, RecoveryStateDocument state, bool marker, bool writeNew)
    {
        var destination = marker ? state.MarkerDestination! : state.StreamDestination!;
        var device = marker
            ? (writeNew ? state.MarkerNewDevice : state.MarkerOldDevice)
            : (writeNew ? state.StreamNewDevice : state.StreamOldDevice);
        var inode = marker
            ? (writeNew ? state.MarkerNewInode : state.MarkerOldInode)
            : (writeNew ? state.StreamNewInode : state.StreamOldInode);
        var bytes = marker
            ? (writeNew ? state.MarkerNewBytesBase64 : state.MarkerOldBytesBase64)
            : (writeNew ? state.StreamNewBytesBase64 : state.StreamOldBytesBase64);
        var hash = marker
            ? (writeNew ? state.MarkerNewSha256 : state.MarkerOldSha256)
            : (writeNew ? state.StreamNewSha256 : state.StreamOldSha256);
        if (bytes is null || hash is null || !TryDecodeRecoveryBytes(bytes,
                marker ? MaxManagedMarkerBytes : MaxManagedStrmBytes, out var decoded))
            return false;

        var member = state with
        {
            Destination = destination,
            DestinationDevice = device,
            DestinationInode = inode,
            DestinationOldSha256 = hash,
            DestinationNewSha256 = hash,
            OldBytesBase64 = bytes,
            OldBytesSha256 = hash
        };
        return WriteRecoveryDestination(canonicalRoot, member, decoded);
    }

    private static bool RecoverPendingProbeAddition(
        string canonicalRoot, RecoveryStateDocument state, string hash, int slot,
        string probePath, string markerPath, Action<string>? mutationHook = null)
    {
        var probeRead = TryReadBoundedFileForPath(canonicalRoot, probePath, MaxManagedProbeBytes, out var liveProbe, out var probeId);
        var markerRead = TryReadBoundedFileForPath(canonicalRoot, markerPath, MaxManagedMarkerBytes, out var liveMarker, out var markerId);
        if (!markerRead || markerId is null)
            return false;

        var markerOldIdentity = new FileIdentity(state.MarkerOldDevice, state.MarkerOldInode);
        var markerNewIdentity = new FileIdentity(state.MarkerNewDevice, state.MarkerNewInode);
        var markerOldMatch = markerId.Value == markerOldIdentity
            && liveMarker.AsSpan().SequenceEqual(Convert.FromBase64String(state.MarkerOldBytesBase64!));
        var markerNewMatch = markerId.Value == markerNewIdentity
            && liveMarker.AsSpan().SequenceEqual(Convert.FromBase64String(state.MarkerNewBytesBase64!));
        if (!markerOldMatch && !markerNewMatch)
        {
            // Same-inode foreign/torn edits are not recoverable. Do not restore
            // an inode merely because its identity is still journaled.
            return false;
        }

        var probeNewIdentity = new FileIdentity(state.StreamNewDevice, state.StreamNewInode);
        var probeNewMatch = probeRead && probeId == probeNewIdentity
            && string.Equals(Convert.ToHexString(SHA256.HashData(liveProbe)), state.StreamNewSha256,
                StringComparison.OrdinalIgnoreCase);

        // The probe was checked while bound to its expected identity/hash. The
        // seam deliberately runs after that check, modelling a pathname swap
        // immediately before the old implementation's unlinkat. Re-read after
        // it; a foreign replacement is suppressed in the fixed journal and is
        // never deleted or overwritten.
        if (probeNewMatch)
        {
            try { mutationHook?.Invoke(probePath); }
            catch { /* a crash seam must not prevent safe recovery on retry */ }
            probeNewMatch = TryReadBoundedFileForPath(
                canonicalRoot, probePath, MaxManagedProbeBytes, out liveProbe, out probeId)
                && probeId == probeNewIdentity
                && string.Equals(Convert.ToHexString(SHA256.HashData(liveProbe)), state.StreamNewSha256,
                    StringComparison.OrdinalIgnoreCase);
        }

        if (!probeRead && TryPathExistsAnchored(canonicalRoot, probePath, out _))
            return false;

        // The absence of the first member is the durable old state. A new
        // marker may never be published without its probe. If a replacement
        // won the pathname race, retain the old stream+marker pair and record
        // logical suppression rather than attempting path cleanup.
        if (!probeNewMatch)
        {
            if (!probeRead)
                return markerOldMatch && (state.Phase is "intent" or "ready")
                    ? PublishRecoveryComplete(canonicalRoot, state, hash, slot)
                    : false;

            if (!markerOldMatch && markerNewMatch
                && state.Phase is ("intent" or "ready" or "publication")
                && TryWriteRecoveryPairMember(canonicalRoot, state, marker: true, writeNew: false))
                markerOldMatch = true;

            return markerOldMatch && state.Phase is ("intent" or "ready" or "publication")
                ? PublishRecoverySuppressed(canonicalRoot, state, hash, slot)
                : false;
        }

        if (markerNewMatch)
            return PublishRecoveryComplete(canonicalRoot, state, hash, slot);

        // A probe orphan is still our exact prepared inode. Promote the marker
        // while its identity remains guarded, producing a valid new pair. This
        // is the Linux-safe convergence path that replaces unlink rollback.
        if (state.Phase is "intent" or "ready" or "publication")
        {
            if (!TryWriteRecoveryPairMember(canonicalRoot, state, marker: true, writeNew: true))
                return false;
            return PublishRecoveryComplete(canonicalRoot, state, hash, slot);
        }

        return false;
    }

    private static bool RecoverPendingPairedUpdate(
        string canonicalRoot, RecoveryStateDocument state, string hash, int slot,
        Action<string>? mutationHook = null)
    {
        var streamPath = Path.GetFullPath(Path.Combine(canonicalRoot, state.StreamDestination!));
        var markerPath = Path.GetFullPath(Path.Combine(canonicalRoot, state.MarkerDestination!));
        if (!IsPathWithinRoot(canonicalRoot, streamPath)
            || !IsPathWithinRoot(canonicalRoot, markerPath)
            || !string.Equals(
                NormalizeRelativePathForComparison(Path.GetRelativePath(canonicalRoot, streamPath)),
                state.StreamDestination, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                NormalizeRelativePathForComparison(Path.GetRelativePath(canonicalRoot, markerPath)),
                state.MarkerDestination, StringComparison.OrdinalIgnoreCase)
            || IsUnderRecovery(state.StreamDestination!)
            || IsUnderRecovery(state.MarkerDestination!)
            || ContainsSymlinkInPath(canonicalRoot, streamPath)
            || ContainsSymlinkInPath(canonicalRoot, markerPath))
            return false;

        if (state.StreamOldPresent is false)
            return RecoverPendingProbeAddition(canonicalRoot, state, hash, slot, streamPath, markerPath, mutationHook);

        var streamOld = Convert.FromBase64String(state.StreamOldBytesBase64!);
        var streamNew = Convert.FromBase64String(state.StreamNewBytesBase64!);
        var markerOld = Convert.FromBase64String(state.MarkerOldBytesBase64!);
        var markerNew = Convert.FromBase64String(state.MarkerNewBytesBase64!);
        var streamRead = TryReadBoundedFileForPath(canonicalRoot, streamPath, MaxManagedStrmBytes, out var liveStream, out var streamId);
        var markerRead = TryReadBoundedFileForPath(canonicalRoot, markerPath, MaxManagedMarkerBytes, out var liveMarker, out var markerId);
        if (!streamRead || !markerRead || streamId is null || markerId is null)
            return false;

        var streamOldIdentity = new FileIdentity(state.StreamOldDevice, state.StreamOldInode);
        var streamNewIdentity = new FileIdentity(state.StreamNewDevice, state.StreamNewInode);
        var markerOldIdentity = new FileIdentity(state.MarkerOldDevice, state.MarkerOldInode);
        var markerNewIdentity = new FileIdentity(state.MarkerNewDevice, state.MarkerNewInode);
        var streamIdentityOwned = streamId.Value == streamOldIdentity || streamId.Value == streamNewIdentity;
        var markerIdentityOwned = markerId.Value == markerOldIdentity || markerId.Value == markerNewIdentity;
        if (!streamIdentityOwned || !markerIdentityOwned)
            return false;

        var streamOldMatch = streamId.Value == streamOldIdentity
            && liveStream.AsSpan().SequenceEqual(streamOld);
        var streamNewMatch = streamId.Value == new FileIdentity(state.StreamNewDevice, state.StreamNewInode)
            && liveStream.AsSpan().SequenceEqual(streamNew);
        var markerOldMatch = markerId.Value == new FileIdentity(state.MarkerOldDevice, state.MarkerOldInode)
            && liveMarker.AsSpan().SequenceEqual(markerOld);
        var markerNewMatch = markerId.Value == new FileIdentity(state.MarkerNewDevice, state.MarkerNewInode)
            && liveMarker.AsSpan().SequenceEqual(markerNew);

        // Same-inode edits are not a recoverable partial write. Only the exact
        // journaled old or new bytes may be acted on; preserve every other
        // value and leave the operation suppressed for an operator/retry.
        if ((!streamOldMatch && !streamNewMatch) || (!markerOldMatch && !markerNewMatch))
            return false;

        if ((streamOldMatch && markerOldMatch) || (streamNewMatch && markerNewMatch))
            return PublishRecoveryComplete(canonicalRoot, state, hash, slot);

        // Before the publication record, a mixed pair is rolled back. After it,
        // the operation is allowed to finish the new pair, but only when the
        // held bytes are already exactly one of the two journaled values.
        if (state.Phase == "publication"
            && ((streamNewMatch && markerOldMatch) || (streamOldMatch && markerNewMatch)))
        {
            if (streamNewMatch && markerOldMatch)
            {
                if (!TryWriteRecoveryPairMember(canonicalRoot, state, marker: true, writeNew: true)) return false;
            }
            else if (!TryWriteRecoveryPairMember(canonicalRoot, state, marker: false, writeNew: true))
                return false;
            return PublishRecoveryComplete(canonicalRoot, state, hash, slot);
        }

        // A partial write can leave the expected inode with neither hash. Never
        // publish a torn value; restore both members only while each pathname is
        // still bound to the identities journaled before publication.
        var streamIdentityExpected = streamId.Value == new FileIdentity(state.StreamOldDevice, state.StreamOldInode);
        var markerIdentityExpected = markerId.Value == new FileIdentity(state.MarkerOldDevice, state.MarkerOldInode);
        if (!streamIdentityExpected || !markerIdentityExpected
            || !TryWriteRecoveryPairMember(canonicalRoot, state, marker: false, writeNew: false)
            || !TryWriteRecoveryPairMember(canonicalRoot, state, marker: true, writeNew: false))
            return false;
        return PublishRecoveryComplete(canonicalRoot, state, hash, slot);
    }

    private static bool RecoverPendingInPlaceUpdates(string libraryRoot, CancellationToken ct)
        => RecoverPendingInPlaceUpdatesWithHook(libraryRoot, ct, null);

    private static bool RecoverPendingInPlaceUpdatesWithHook(
        string libraryRoot, CancellationToken ct, Action<string>? mutationHook)
    {
        var canonicalRoot = GetCanonicalLibraryRoot(libraryRoot);
        var recoveryRoot = Path.Combine(canonicalRoot, RecoveryDirectoryName);
        if (!TryPathExistsAnchored(canonicalRoot, recoveryRoot, out var recoveryIsDirectory)
            || !recoveryIsDirectory)
            return true;
        if (ContainsSymlinkInPath(canonicalRoot, recoveryRoot)) return false;
        ct.ThrowIfCancellationRequested();
        if (!TrySelectLatestRecoveryState(canonicalRoot, recoveryRoot, out var state, out var hash, out var slot, out var anySlot,
                out _, out _))
            return !anySlot;
        if (IsPairedRecoveryState(state))
            return state.Phase is "complete" or "suppressed"
                || RecoverPendingPairedUpdate(canonicalRoot, state, hash, slot, mutationHook);
        if (state.Phase is "complete" or "suppressed") return true;
        var destination = Path.GetFullPath(Path.Combine(canonicalRoot, state.Destination));
        if (!IsPathWithinRoot(canonicalRoot, destination)
            || IsUnderRecovery(state.Destination)
            || !string.Equals(
                NormalizeRelativePathForComparison(Path.GetRelativePath(canonicalRoot, destination)),
                NormalizeRelativePathForComparison(state.Destination), StringComparison.OrdinalIgnoreCase)
            || ContainsSymlinkInPath(canonicalRoot, destination)) return false;
        if (!TryReadBoundedFileForPath(canonicalRoot, destination, MaxManagedStrmBytes, out var live, out var identity)
            || identity is null || identity.Value != new FileIdentity(state.DestinationDevice, state.DestinationInode)) return false;
        var liveHash = Convert.ToHexString(SHA256.HashData(live));
        var oldHash = state.DestinationOldSha256;
        if (!string.Equals(liveHash, oldHash, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(liveHash, state.DestinationNewSha256, StringComparison.OrdinalIgnoreCase))
        {
            // The held inode has been edited to a value outside the journal's
            // exact old/new set. Never restore it: the bytes are foreign or
            // torn, even when its device/inode still matches the journal.
            return false;
        }
        return PublishRecoveryComplete(canonicalRoot, state, hash, slot);
    }

    private static bool TryDeleteRecoveryArtifacts(
        string recoveryRoot, string journalPath, string oldPath, FileIdentity? expectedOldIdentity = null,
        string? expectedOldSha256 = null, Action<string>? mutationHook = null)
    {
        // Compatibility name retained for the writer. Recovery artifacts are
        // never deleted; the pending slot is advanced to complete.
        _ = journalPath; _ = oldPath; _ = expectedOldIdentity; _ = expectedOldSha256; _ = mutationHook;
        var root = Path.GetDirectoryName(recoveryRoot);
        return root is not null && RecoverPendingInPlaceUpdates(root, CancellationToken.None);
    }

    private static bool IsSha256(string? value)
        => value is not null && value.Length == 64 && value.All(Uri.IsHexDigit);

    private static bool TryParseCanonicalUInt64(string? value, out ulong result)
    {
        result = 0;
        if (string.IsNullOrEmpty(value) || value.Length > 20
            || (value.Length > 1 && value[0] == '0'))
            return false;
        foreach (var character in value)
            if (character is < '0' or > '9')
                return false;
        return ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result);
    }

    private static bool IsStrictManagedRecord(BoundedFirstLine marker)
    {
        if (marker.Bytes.Any(static value => value is (byte)'\r' or (byte)'\n'))
            return false;
        return Encoding.UTF8.GetByteCount(marker.Content) == marker.Bytes.Length;
    }

    private readonly record struct PairedRecoveryJournal(
        RecoveryStateDocument Ready, int Slot,
        RecoverySlotExpectation InactiveTarget, RecoverySlotExpectation LatestTarget);

    private static PairedRecoveryJournal PersistPairedRecoveryJournalWithSlot(
        string canonicalRoot,
        string streamPath,
        FileIdentity streamIdentity,
        byte[] streamOldBytes,
        byte[] streamNewBytes,
        string markerPath,
        FileIdentity markerIdentity,
        byte[] markerOldBytes,
        byte[] markerNewBytes,
        bool? streamOldPresent = null,
        bool includeStreamNewBytes = true,
        string? streamNewSha256Override = null,
        Action<string>? mutationHook = null)
    {
        var ready = PersistPairedRecoveryJournal(canonicalRoot, streamPath, streamIdentity,
            streamOldBytes, streamNewBytes, markerPath, markerIdentity, markerOldBytes, markerNewBytes,
            streamOldPresent, includeStreamNewBytes, streamNewSha256Override, mutationHook);
        var recoveryRoot = Path.Combine(canonicalRoot, RecoveryDirectoryName);
        if (!TrySelectLatestRecoveryState(canonicalRoot, recoveryRoot, out var selected, out _, out var slot, out _,
                out var inactiveTarget, out var latestTarget)
            || selected.OperationId != ready.OperationId)
            throw new IOException("Paired recovery ready state was not durable.");
        return new PairedRecoveryJournal(ready, slot, inactiveTarget, latestTarget);
    }

    private static RecoveryStateDocument CreateNextPairedPhase(
        RecoveryStateDocument state, string phase, string previousRawSha256)
    {
        var next = state with
        {
            Generation = NextRecoveryGeneration(state.Generation),
            Phase = phase,
            PreviousStateSha256 = previousRawSha256,
            StateSha256 = string.Empty
        };
        return next with { StateSha256 = ComputeRecoveryStateSha256(next) };
    }

    // The descriptor is the publication capability. Compare its complete,
    // bounded old contents immediately before truncation: device/inode alone
    // does not distinguish an operator edit of the same inode.
    private static bool WriteOwnedBytes(
        string canonicalRoot, string destinationPath, FileIdentity expectedIdentity,
        byte[] expectedBytes, byte[] bytes)
    {
        if (expectedBytes.Length > MaxManagedProbeBytes || bytes.Length > MaxManagedProbeBytes)
            return false;
        try
        {
            if (!OperatingSystem.IsLinux())
            {
                using var stream = new FileStream(destinationPath, FileMode.Open, FileAccess.ReadWrite,
                    FileShare.ReadWrite | FileShare.Delete, 16 * 1024, FileOptions.WriteThrough);
                if (GetFileIdentityWindows(stream.SafeFileHandle) != expectedIdentity
                    || ReadBoundedBytes(stream, MaxManagedProbeBytes) is not { } actual
                    || !actual.AsSpan().SequenceEqual(expectedBytes))
                    return false;
                stream.Position = 0;
                stream.Write(bytes);
                stream.SetLength(bytes.Length);
                stream.Flush(true);
                return GetFileIdentityWindows(stream.SafeFileHandle) == expectedIdentity
                    && CaptureFileIdentity(Path.GetDirectoryName(destinationPath) ?? string.Empty, destinationPath) == expectedIdentity;
            }

            var relative = Path.GetRelativePath(canonicalRoot, destinationPath);
            var directory = Path.GetDirectoryName(relative);
            var name = Path.GetFileName(destinationPath);
            var rootFd = OpenDirectoryForMutation(canonicalRoot);
            var directoryFd = rootFd;
            var fileFd = -1;
            try
            {
                var device = ValidateOpenedDirectoryDescriptor(rootFd, canonicalRoot, expectedDevice: null);
                directoryFd = string.IsNullOrWhiteSpace(directory)
                    ? rootFd
                    : OpenDirectoryChain(canonicalRoot, rootFd, device, directory, false, false);
                fileFd = OpenReadWriteFileAt(directoryFd, device, name, destinationPath);
                if (fileFd < 0 || GetFileIdentity(fileFd) != expectedIdentity)
                    return false;
                using var stream = new FileStream(
                    new global::Microsoft.Win32.SafeHandles.SafeFileHandle((IntPtr)fileFd, true),
                    FileAccess.ReadWrite, 16 * 1024, false);
                fileFd = -1;
                if (ReadBoundedBytes(stream, MaxManagedProbeBytes) is not { } actual
                    || !actual.AsSpan().SequenceEqual(expectedBytes))
                    return false;
                stream.Position = 0;
                stream.Write(bytes);
                stream.SetLength(bytes.Length);
                stream.Flush(true);
                if (SyncFd(checked((int)stream.SafeFileHandle.DangerousGetHandle())) != 0
                    || GetFileIdentity(checked((int)stream.SafeFileHandle.DangerousGetHandle())) != expectedIdentity
                    || !PathHasIdentity(directoryFd, device, name, destinationPath, expectedIdentity))
                    return false;
                return SyncDirectoryFd(directoryFd) == 0;
            }
            finally
            {
                if (fileFd >= 0) _ = CloseDirectoryHandle(fileFd);
                if (directoryFd != rootFd) _ = CloseDirectoryHandle(directoryFd);
                _ = CloseDirectoryHandle(rootFd);
            }
        }
        catch { return false; }
    }

    private bool CommitProbeAddition(
        Configuration.PluginConfiguration config,
        PreparedOutput preparedProbe,
        string probePath,
        string markerPath,
        FileIdentity markerIdentity,
        byte[] oldMarkerBytes,
        byte[] newMarkerBytes,
        CancellationToken ct,
        out ProbeOwnership committedProbe)
    {
        committedProbe = default;
        var canonicalRoot = GetCanonicalLibraryRoot(config.LibraryPath);
        PairedRecoveryJournal journal;
        try
        {
            // The first paired member is a prepared, not-yet-published probe.
            // Its old pathname is explicitly absent, so the fixed journal need
            // only carry its identity/hash and never duplicate probe bytes.
            journal = PersistPairedRecoveryJournalWithSlot(
                canonicalRoot, probePath, preparedProbe.Identity,
                [], [], markerPath, markerIdentity, oldMarkerBytes, newMarkerBytes,
                streamOldPresent: false, includeStreamNewBytes: false,
                streamNewSha256Override: preparedProbe.Sha256,
                mutationHook: _pathMutationHook);
            ct.ThrowIfCancellationRequested();

            preparedProbe.Publish(canonicalRoot, false, _pathMutationHook);
            if (!PathHasIdentityForPath(canonicalRoot, probePath, preparedProbe.Identity))
                throw new IOException("Probe identity changed after publication.");
            ct.ThrowIfCancellationRequested();

            var publication = CreateNextPairedPhase(journal.Ready, "publication", journal.LatestTarget.RawSha256);
            var publicationBytes = JsonSerializer.SerializeToUtf8Bytes(publication, RecoveryJsonOptions);
            var recoveryRoot = Path.Combine(canonicalRoot, RecoveryDirectoryName);
            WriteRecoverySlotWithHook(canonicalRoot, recoveryRoot, 1 - journal.Slot, publicationBytes, _pathMutationHook,
                journal.InactiveTarget, journal.LatestTarget);
            ct.ThrowIfCancellationRequested();

            if (!WriteOwnedBytes(canonicalRoot, markerPath, markerIdentity, oldMarkerBytes, newMarkerBytes))
                throw new IOException("Failed to publish probe completion marker.");
            _pathMutationHook?.Invoke(markerPath);
            if (!PathHasIdentityForPath(canonicalRoot, markerPath, markerIdentity)
                || !PathHasIdentityForPath(canonicalRoot, probePath, preparedProbe.Identity))
                throw new IOException("Probe pair identity changed after publication.");
            ct.ThrowIfCancellationRequested();

            var complete = CreateNextPairedPhase(publication, "complete",
                Convert.ToHexString(SHA256.HashData(publicationBytes)));
            var completeBytes = JsonSerializer.SerializeToUtf8Bytes(complete, RecoveryJsonOptions);
            var publicationExpectation = new RecoverySlotExpectation(1 - journal.Slot, true,
                journal.InactiveTarget.Identity, publicationBytes,
                Convert.ToHexString(SHA256.HashData(publicationBytes)), publication, publication.StateSha256);
            WriteRecoverySlotWithHook(canonicalRoot, recoveryRoot, journal.Slot, completeBytes,
                _pathMutationHook, journal.LatestTarget, publicationExpectation);
            committedProbe = new ProbeOwnership(preparedProbe.Identity, preparedProbe.Sha256);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            try { RecoverPendingInPlaceUpdatesWithHook(canonicalRoot, CancellationToken.None, _pathMutationHook); } catch { }
            throw;
        }
        catch
        {
            try { RecoverPendingInPlaceUpdatesWithHook(canonicalRoot, CancellationToken.None, _pathMutationHook); } catch { }
            return false;
        }
    }

    private bool CommitTokenRotation(
        Configuration.PluginConfiguration config,
        string streamPath,
        string markerPath,
        FileIdentity streamIdentity,
        byte[] oldStreamBytes,
        byte[] newStreamBytes,
        FileIdentity markerIdentity,
        byte[] oldMarkerBytes,
        byte[] newMarkerBytes,
        CancellationToken ct)
    {
        var canonicalRoot = GetCanonicalLibraryRoot(config.LibraryPath);
        PairedRecoveryJournal journal;
        try
        {
            journal = PersistPairedRecoveryJournalWithSlot(canonicalRoot, streamPath, streamIdentity,
                oldStreamBytes, newStreamBytes, markerPath, markerIdentity, oldMarkerBytes, newMarkerBytes,
                mutationHook: _pathMutationHook);
            ct.ThrowIfCancellationRequested();

            // The stream is the first publication. The ready slot still proves
            // the old pair until the publication slot is durable.
            if (!WriteOwnedBytes(canonicalRoot, streamPath, streamIdentity, oldStreamBytes, newStreamBytes))
                throw new IOException("Failed to publish rotated stream.");
            _pathMutationHook?.Invoke(streamPath);
            if (!PathHasIdentityForPath(canonicalRoot, streamPath, streamIdentity))
                throw new IOException("Rotated stream identity changed after publication.");
            ct.ThrowIfCancellationRequested();

            var publication = CreateNextPairedPhase(journal.Ready, "publication", journal.LatestTarget.RawSha256);
            var publicationBytes = JsonSerializer.SerializeToUtf8Bytes(publication, RecoveryJsonOptions);
            var recoveryRoot = Path.Combine(canonicalRoot, RecoveryDirectoryName);
            WriteRecoverySlotWithHook(canonicalRoot, recoveryRoot, 1 - journal.Slot, publicationBytes, _pathMutationHook,
                journal.InactiveTarget, journal.LatestTarget);
            ct.ThrowIfCancellationRequested();

            if (!WriteOwnedBytes(canonicalRoot, markerPath, markerIdentity, oldMarkerBytes, newMarkerBytes))
                throw new IOException("Failed to publish rotated completion marker.");
            _pathMutationHook?.Invoke(markerPath);
            if (!PathHasIdentityForPath(canonicalRoot, markerPath, markerIdentity)
                || !PathHasIdentityForPath(canonicalRoot, streamPath, streamIdentity))
                throw new IOException("Rotated pair identity changed after publication.");
            ct.ThrowIfCancellationRequested();

            var complete = CreateNextPairedPhase(publication, "complete",
                Convert.ToHexString(SHA256.HashData(publicationBytes)));
            var completeBytes = JsonSerializer.SerializeToUtf8Bytes(complete, RecoveryJsonOptions);
            var publicationExpectation = new RecoverySlotExpectation(1 - journal.Slot, true,
                journal.InactiveTarget.Identity, publicationBytes,
                Convert.ToHexString(SHA256.HashData(publicationBytes)), publication, publication.StateSha256);
            WriteRecoverySlotWithHook(canonicalRoot, recoveryRoot, journal.Slot, completeBytes,
                _pathMutationHook, journal.LatestTarget, publicationExpectation);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            try { RecoverPendingInPlaceUpdatesWithHook(canonicalRoot, CancellationToken.None, _pathMutationHook); } catch { }
            throw;
        }
        catch
        {
            try { RecoverPendingInPlaceUpdatesWithHook(canonicalRoot, CancellationToken.None, _pathMutationHook); } catch { }
            return false;
        }
    }

    private static byte[]? ReadBoundedBytes(FileStream stream, int maxBytes)
    {
        if (!stream.CanSeek) return null;
        var size = OperatingSystem.IsLinux()
            ? GetFileSize(checked((int)stream.SafeFileHandle.DangerousGetHandle()))
            : stream.Length;
        if (size < 0 || size > maxBytes) return null;
        var bytes = new byte[checked((int)size)];
        stream.Position = 0;
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static long GetFileSize(int fd)
    {
        var buffer = Marshal.AllocHGlobal(FstatBufferSize);
        try
        {
            if (FStat(fd, buffer) != 0) return -1;
            return Marshal.ReadInt64(buffer, 48);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static void EnsureRecoveryRoot(string canonicalRoot)
    {
        var recoveryRoot = Path.Combine(canonicalRoot, RecoveryDirectoryName);
        if (!IsPathWithinRoot(canonicalRoot, recoveryRoot))
            throw new InvalidOperationException("Recovery path escapes the library root.");
        if (OperatingSystem.IsLinux())
        {
            // Recovery is part of the library namespace. Never make its
            // immediate parent the descriptor root: traverse the fixed name
            // from the one opened library root, rejecting symlinks, mounts and
            // foreign nested bind mounts in the kernel.
            var rootFd = OpenDirectoryForMutation(canonicalRoot);
            var recoveryFd = rootFd;
            try
            {
                var device = ValidateOpenedDirectoryDescriptor(rootFd, canonicalRoot, expectedDevice: null);
                recoveryFd = OpenDirectoryChain(canonicalRoot, rootFd, device, RecoveryDirectoryName,
                    createDirectories: true, invokeMutationHook: false);
                if (SyncDirectoryFd(recoveryFd) != 0)
                    throw new IOException("Failed to fsync recovery directory.");
                if (SyncDirectoryFd(rootFd) != 0)
                    throw new IOException("Failed to fsync library root after recovery directory creation.");
            }
            finally
            {
                if (recoveryFd != rootFd)
                    _ = CloseDirectoryHandle(recoveryFd);
                _ = CloseDirectoryHandle(rootFd);
            }
            return;
        }

        if (ContainsSymlinkInPath(canonicalRoot, recoveryRoot))
            throw new InvalidOperationException("Recovery path contains reparse point.");
        var recoveryExisted = Directory.Exists(recoveryRoot);
        Directory.CreateDirectory(recoveryRoot);
        // A newly-created namespace is not durable until its parent is synced;
        // do this before creating journals or mutating the destination.
        if (!recoveryExisted)
            SyncDirectory(canonicalRoot);
        SyncDirectory(recoveryRoot);
    }

    private static Task<WriteCommitResult> WriteTextAtomicallyAsync(
        string libraryRoot,
        string destinationPath,
        string content,
        CancellationToken ct,
        string? expectedGuardPath = null,
        FileIdentity? expectedGuardIdentity = null,
        Action<string>? mutationHook = null)
        => WriteTextAtomicallyAsyncCore(libraryRoot, destinationPath, content, ct, noReplace: false,
            expectedIdentity: null, expectedGuardPath, expectedGuardIdentity, mutationHook,
            invokeDestinationMutationHook: true);

    private static Task<WriteCommitResult> WriteTextAtomicallyWithExpectedIdentityAsync(
        string libraryRoot,
        string destinationPath,
        string content,
        CancellationToken ct,
        FileIdentity expectedIdentity,
        string? expectedGuardPath = null,
        FileIdentity? expectedGuardIdentity = null,
        Action<string>? mutationHook = null)
        => WriteTextAtomicallyAsyncCore(
            libraryRoot, destinationPath, content, ct, noReplace: false, expectedIdentity,
            expectedGuardPath, expectedGuardIdentity, mutationHook, true);

    private static Task<WriteCommitResult> WriteTextAtomicallyAsyncNoReplace(
        string libraryRoot,
        string destinationPath,
        string content,
        CancellationToken ct,
        string? expectedGuardPath = null,
        FileIdentity? expectedGuardIdentity = null,
        Action<string>? mutationHook = null)
        => WriteTextAtomicallyAsyncCore(
            libraryRoot, destinationPath, content, ct, noReplace: true,
            expectedIdentity: null, expectedGuardPath, expectedGuardIdentity, mutationHook,
            invokeDestinationMutationHook: true);

    private static async Task<WriteCommitResult> WriteTextAtomicallyAsyncCore(
        string libraryRoot,
        string destinationPath,
        string content,
        CancellationToken ct,
        bool noReplace,
        FileIdentity? expectedIdentity = null,
        string? expectedGuardPath = null,
        FileIdentity? expectedGuardIdentity = null,
        Action<string>? mutationHook = null,
        bool invokeDestinationMutationHook = true)
    {
        var canonicalRoot = GetCanonicalLibraryRoot(libraryRoot);
        var canonicalDestination = Path.GetFullPath(destinationPath);

        if (!IsPathWithinRoot(canonicalRoot, canonicalDestination))
            throw new InvalidOperationException("Destination path resolves outside library root.");

        EnsureCaseInsensitivePathComponents(canonicalRoot, canonicalDestination, allowExistingFinal: true);

        if (OperatingSystem.IsLinux())
        {
            // An expected inode is already owned content. Linux has no
            // unlink-by-fd or inode-CAS rename primitive, so never replace its
            // pathname. Hold that inode open and update it in place; a pathname
            // swap then leaves the foreign replacement untouched. The final
            // identity check is deliberately fail-closed.
            if (expectedIdentity is not null)
            {
                return await WriteTextInPlaceLinuxAsync(
                    canonicalRoot,
                    canonicalDestination,
                    content,
                    ct,
                    expectedIdentity.Value,
                    expectedGuardPath,
                    expectedGuardIdentity,
                    mutationHook).ConfigureAwait(false);
            }

            return await WriteTextAtomicallyAsyncLinux(canonicalRoot, canonicalDestination, content, ct, noReplace, expectedGuardPath, expectedGuardIdentity, mutationHook).ConfigureAwait(false);
        }

        if (expectedIdentity is not null)
        {
            return await WriteTextInPlaceWindowsAsync(
                canonicalRoot,
                canonicalDestination,
                content,
                ct,
                expectedIdentity.Value,
                expectedGuardPath,
                expectedGuardIdentity,
                mutationHook).ConfigureAwait(false);
        }

        // An existing destination is a CAS target, not a replace target. Open
        // the exact destination handle as the first operation. If it is absent,
        // the no-replace publication below wins or fails; a destination that
        // appears in that gap is never opened and never overwritten. This also
        // closes the check-then-open race where a foreign file could otherwise
        // be mistaken for the previously observed destination.
        if (!noReplace)
        {
            using var heldDestination = TryOpenWindowsHeldFile(canonicalDestination, requireDeleteAccess: false);
            if (heldDestination is not null)
            {
                var heldIdentity = GetFileIdentityWindows(heldDestination.SafeFileHandle);
                if (CaptureFileIdentity(Path.GetDirectoryName(canonicalDestination) ?? string.Empty, canonicalDestination)
                    != heldIdentity)
                    throw new IOException($"Destination identity changed before in-place update of '{canonicalDestination}'.");

                return await WriteTextInPlaceWindowsAsync(
                    canonicalRoot,
                    canonicalDestination,
                    content,
                    ct,
                    heldIdentity,
                    expectedGuardPath,
                    expectedGuardIdentity,
                    mutationHook,
                    heldDestination).ConfigureAwait(false);
            }
        }

        return await WriteTextAtomicallyAsyncWindows(
            canonicalDestination,
            content,
            ct,
            noReplace,
            expectedGuardPath,
            expectedGuardIdentity,
            invokeDestinationMutationHook ? mutationHook : null).ConfigureAwait(false);
    }

    private static async Task<WriteCommitResult> WriteTextInPlaceWindowsAsync(
        string canonicalRoot,
        string destinationPath,
        string content,
        CancellationToken ct,
        FileIdentity expectedIdentity,
        string? expectedGuardPath,
        FileIdentity? expectedGuardIdentity,
        Action<string>? mutationHook,
        FileStream? heldDestination = null)
    {
        if (ContainsSymlinkInPath(Path.GetFullPath(destinationPath)))
            throw new InvalidOperationException("Destination path contains reparse point.");
        var destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("Destination directory is invalid.");

        var fullGuardPath = expectedGuardPath is null ? null : Path.GetFullPath(expectedGuardPath);
        FileIdentity? fullGuardIdentity = null;
        FileStream? guardHandle = null;

        if (fullGuardPath is not null)
        {
            if (expectedGuardIdentity is null)
                throw new IOException($"Cannot bind ownership guard for '{expectedGuardPath}'.");

            guardHandle = OpenWindowsHeldFile(fullGuardPath);
            fullGuardIdentity = GetFileIdentityWindows(guardHandle.SafeFileHandle);
            if (fullGuardIdentity != expectedGuardIdentity.Value)
                throw new IOException($"Ownership identity changed before writing '{expectedGuardPath}'.");
            if (CaptureFileIdentity(Path.GetDirectoryName(fullGuardPath) ?? string.Empty, fullGuardPath) != fullGuardIdentity)
                throw new IOException($"Ownership identity changed before writing '{expectedGuardPath}'.");
        }

        var stream = heldDestination ?? OpenWindowsHeldFile(destinationPath, requireDeleteAccess: false);
        try
        {
            if (GetFileIdentityWindows(stream.SafeFileHandle) != expectedIdentity)
                throw new IOException($"Destination identity changed before in-place update of '{destinationPath}'.");
        if (guardHandle is not null && fullGuardPath is not null && fullGuardIdentity is not null)
        {
            if (GetFileIdentityWindows(guardHandle.SafeFileHandle) != fullGuardIdentity.Value
                || CaptureFileIdentity(Path.GetDirectoryName(fullGuardPath) ?? string.Empty, fullGuardPath) != fullGuardIdentity.Value)
                throw new IOException($"Ownership identity changed before writing '{expectedGuardPath}'.");
        }

        if (stream.Length > MaxManagedStrmBytes)
            throw new IOException($"Owned text file is unexpectedly large: '{destinationPath}'.");
        var original = new byte[checked((int)stream.Length)];
        stream.Position = 0;
        stream.ReadExactly(original);
        RecoveryArtifacts? recovery = null;
        try
        {
            var replacement = new UTF8Encoding(false).GetBytes(content);
            recovery = PersistInPlaceRecoveryJournal(
                canonicalRoot,
                destinationPath,
                expectedIdentity,
                original,
                replacement);
            mutationHook?.Invoke(destinationPath);
            ct.ThrowIfCancellationRequested();
            stream.Position = 0;
            await stream.WriteAsync(replacement, ct).ConfigureAwait(false);
            stream.SetLength(replacement.Length);
            await stream.FlushAsync(ct).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);

            if (GetFileIdentityWindows(stream.SafeFileHandle) != expectedIdentity
                || CaptureFileIdentity(destinationDirectory, destinationPath) != expectedIdentity)
                throw new IOException($"Destination identity changed during in-place update of '{destinationPath}'.");
            if (guardHandle is not null && fullGuardPath is not null && fullGuardIdentity is not null)
            {
                if (GetFileIdentityWindows(guardHandle.SafeFileHandle) != fullGuardIdentity.Value
                    || CaptureFileIdentity(Path.GetDirectoryName(fullGuardPath) ?? string.Empty, fullGuardPath) != fullGuardIdentity.Value)
                    throw new IOException($"Ownership identity changed during in-place update of '{destinationPath}'.");
            }

            SyncDirectory(destinationDirectory);
            ct.ThrowIfCancellationRequested();
            if (recovery is not null
                && !TryDeleteRecoveryArtifacts(
                    Path.GetDirectoryName(recovery.Value.JournalPath)!,
                    recovery.Value.JournalPath,
                    recovery.Value.OldContentPath,
                    recovery.Value.OldIdentity,
                    recovery.Value.OldSha256,
                    mutationHook))
                throw new IOException($"Could not durably retire recovery artifacts for '{destinationPath}'.");
            return new WriteCommitResult(true, expectedIdentity);
        }
        catch (Exception failure)
        {
            try
            {
                var observed = ReadBoundedBytes(stream, MaxManagedStrmBytes);
                var oldHash = Convert.ToHexString(SHA256.HashData(original));
                var replacementHash = Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false).GetBytes(content)));
                if (observed is null
                    || (!string.Equals(Convert.ToHexString(SHA256.HashData(observed)), oldHash, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(Convert.ToHexString(SHA256.HashData(observed)), replacementHash, StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("Refusing in-place recovery after an unjournaled same-inode edit.");
                stream.Position = 0;
                await stream.WriteAsync(original, CancellationToken.None).ConfigureAwait(false);
                stream.SetLength(original.Length);
                await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
                if (recovery is not null
                    && !TryDeleteRecoveryArtifacts(
                        Path.GetDirectoryName(recovery.Value.JournalPath)!,
                        recovery.Value.JournalPath,
                        recovery.Value.OldContentPath,
                        recovery.Value.OldIdentity,
                        recovery.Value.OldSha256,
                        mutationHook))
                    throw new IOException($"Could not durably retire recovery artifacts for '{destinationPath}'.");
            }
            catch (Exception recoveryFailure)
            {
                throw new IOException(
                    $"In-place update and recovery failed for '{destinationPath}'.",
                    new AggregateException(failure, recoveryFailure));
            }

            throw;
        }
        finally
        {
        }
        }
        finally
        {
            guardHandle?.Dispose();
            if (heldDestination is null)
                stream.Dispose();
        }
    }

    private static async Task<WriteCommitResult> WriteTextInPlaceLinuxAsync(
        string canonicalRoot,
        string canonicalDestination,
        string content,
        CancellationToken ct,
        FileIdentity expectedIdentity,
        string? expectedGuardPath,
        FileIdentity? expectedGuardIdentity,
        Action<string>? mutationHook)
    {
        var destinationRelative = Path.GetRelativePath(canonicalRoot, canonicalDestination);
        var destinationDirectory = Path.GetDirectoryName(destinationRelative);
        var destinationFileName = Path.GetFileName(canonicalDestination);
        if (string.IsNullOrWhiteSpace(destinationFileName))
            throw new InvalidOperationException("Destination filename is invalid.");

        var rootFd = OpenDirectoryForMutation(canonicalRoot);
        var destinationDirFd = rootFd;
        var destinationFd = -1;
        HeldLinuxFile? guard = null;
        try
        {
            var rootDevice = ValidateOpenedDirectoryDescriptor(rootFd, canonicalRoot, expectedDevice: null);
            destinationDirFd = string.IsNullOrWhiteSpace(destinationDirectory)
                ? rootFd
                : OpenDirectoryChain(canonicalRoot, rootFd, rootDevice, destinationDirectory, createDirectories: false);

            destinationFd = OpenReadWriteFileAt(
                destinationDirFd,
                rootDevice,
                destinationFileName,
                canonicalDestination);
            if (destinationFd < 0 || GetFileIdentity(destinationFd) != expectedIdentity)
                throw new IOException($"Destination identity changed before in-place update of '{canonicalDestination}'.");

            if (expectedGuardPath is not null)
            {
                if (expectedGuardIdentity is null)
                    throw new IOException($"Cannot bind ownership guard for '{expectedGuardPath}'.");
                guard = OpenHeldLinuxFile(canonicalRoot, Path.GetFullPath(expectedGuardPath), rootDevice);
                if (guard.Identity != expectedGuardIdentity.Value)
                    throw new IOException($"Ownership identity changed before writing '{expectedGuardPath}'.");
            }

            await using var stream = new FileStream(
                new global::Microsoft.Win32.SafeHandles.SafeFileHandle((IntPtr)destinationFd, ownsHandle: true),
                FileAccess.ReadWrite,
                bufferSize: 16 * 1024,
                isAsync: false);
            destinationFd = -1;

            if (stream.Length > MaxManagedStrmBytes)
                throw new IOException($"Owned text file is unexpectedly large: '{canonicalDestination}'.");
            var original = new byte[checked((int)stream.Length)];
            stream.Position = 0;
            stream.ReadExactly(original);
            RecoveryArtifacts? recovery = null;

            try
            {
                var replacement = new UTF8Encoding(false).GetBytes(content);
                recovery = PersistInPlaceRecoveryJournal(
                    canonicalRoot,
                    canonicalDestination,
                    expectedIdentity,
                    original,
                    replacement);
                // The test hook models a concurrent pathname replacement. It runs
                // after the expected fd and the durable recovery record are held
                // and before any bytes are changed.
                mutationHook?.Invoke(canonicalDestination);
                ct.ThrowIfCancellationRequested();
                stream.Position = 0;
                await stream.WriteAsync(replacement, ct).ConfigureAwait(false);
                stream.SetLength(replacement.Length);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
                if (SyncFd(checked((int)stream.SafeFileHandle.DangerousGetHandle())) != 0)
                    throw new IOException($"Failed to fsync in-place update of '{canonicalDestination}'.");

                if (GetFileIdentity(checked((int)stream.SafeFileHandle.DangerousGetHandle())) != expectedIdentity
                    || !PathHasIdentity(destinationDirFd, rootDevice, destinationFileName, canonicalDestination, expectedIdentity))
                {
                    throw new IOException($"Destination identity changed during in-place update of '{canonicalDestination}'.");
                }

                if (guard is not null
                    && !PathHasIdentity(
                        guard.DirectoryFd,
                        rootDevice,
                        Path.GetFileName(Path.GetFullPath(expectedGuardPath!)),
                        Path.GetFullPath(expectedGuardPath!),
                        guard.Identity))
                {
                    throw new IOException($"Ownership identity changed during in-place update of '{canonicalDestination}'.");
                }

                if (SyncDirectoryFd(destinationDirFd) != 0)
                    throw new IOException($"Failed to fsync directory containing '{canonicalDestination}'.");
                ct.ThrowIfCancellationRequested();
                if (recovery is not null
                    && !TryDeleteRecoveryArtifacts(
                        Path.GetDirectoryName(recovery.Value.JournalPath)!,
                        recovery.Value.JournalPath,
                        recovery.Value.OldContentPath,
                        recovery.Value.OldIdentity,
                        recovery.Value.OldSha256,
                        mutationHook))
                    throw new IOException($"Could not durably retire recovery artifacts for '{canonicalDestination}'.");
                return new WriteCommitResult(true, expectedIdentity);
            }
            catch (Exception failure)
            {
                // Recover the held inode in place. This never follows the
                // destination pathname, so a concurrent pathname swap cannot
                // overwrite or delete a foreign file. If recovery cannot be
                // made durable, report that fact instead of claiming success.
                try
                {
                    var observed = ReadBoundedBytes(stream, MaxManagedStrmBytes);
                    var oldHash = Convert.ToHexString(SHA256.HashData(original));
                    var replacementHash = Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false).GetBytes(content)));
                    if (observed is null
                        || (!string.Equals(Convert.ToHexString(SHA256.HashData(observed)), oldHash, StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(Convert.ToHexString(SHA256.HashData(observed)), replacementHash, StringComparison.OrdinalIgnoreCase)))
                        throw new IOException("Refusing in-place recovery after an unjournaled same-inode edit.");
                    stream.Position = 0;
                    await stream.WriteAsync(original, CancellationToken.None).ConfigureAwait(false);
                    stream.SetLength(original.Length);
                    await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                    if (SyncFd(checked((int)stream.SafeFileHandle.DangerousGetHandle())) != 0)
                        throw new IOException($"Failed to fsync recovery of '{canonicalDestination}'.");
                    if (recovery is not null
                        && !TryDeleteRecoveryArtifacts(
                            Path.GetDirectoryName(recovery.Value.JournalPath)!,
                            recovery.Value.JournalPath,
                            recovery.Value.OldContentPath,
                            recovery.Value.OldIdentity,
                            recovery.Value.OldSha256,
                            mutationHook))
                        throw new IOException($"Could not durably retire recovery artifacts for '{canonicalDestination}'.");
                }
                catch (Exception recoveryFailure)
                {
                    throw new IOException(
                        $"In-place update and recovery failed for '{canonicalDestination}'.",
                        new AggregateException(failure, recoveryFailure));
                }

                throw;
            }
        }
        finally
        {
            guard?.Dispose();
            if (destinationFd >= 0)
                _ = CloseDirectoryHandle(destinationFd);
            if (destinationDirFd != rootFd)
                _ = CloseDirectoryHandle(destinationDirFd);
            _ = CloseDirectoryHandle(rootFd);
        }
    }

    private static Task<WriteCommitResult> WriteTextAtomicallyAsyncWindows(
        string destinationPath,
        string content,
        CancellationToken ct,
        bool noReplace,
        string? expectedGuardPath,
        FileIdentity? expectedGuardIdentity,
        Action<string>? mutationHook)
    {
        var destinationDir = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrWhiteSpace(destinationDir))
            throw new InvalidOperationException("Destination path is invalid.");

        if (ContainsSymlinkInPath(Path.GetFullPath(destinationPath)))
            throw new InvalidOperationException("Destination path contains reparse point.");

        Directory.CreateDirectory(destinationDir);
        SyncDirectory(destinationDir);

        var destinationRoot = Path.GetPathRoot(destinationPath) ?? string.Empty;
        var fullDestination = Path.GetFullPath(destinationPath);

        string? fullGuard = expectedGuardPath is null ? null : Path.GetFullPath(expectedGuardPath);
        FileIdentity? fullGuardIdentity = null;
        FileStream? guardHandle = null;
        if (fullGuard is not null)
        {
            guardHandle = OpenWindowsHeldFile(fullGuard);
            fullGuardIdentity = GetFileIdentityWindows(guardHandle.SafeFileHandle);
            if (expectedGuardIdentity is null || fullGuardIdentity != expectedGuardIdentity.Value)
                throw new IOException($"Ownership identity changed before replacing '{expectedGuardPath}'.");
            if (CaptureFileIdentity(Path.GetDirectoryName(fullGuard) ?? string.Empty, fullGuard) != fullGuardIdentity)
                throw new IOException($"Ownership identity changed before replacing '{expectedGuardPath}'.");
        }

        // Existing destinations are handled by WriteTextInPlaceWindowsAsync.
        // This path is therefore strictly a fresh no-replace publication. Do
        // not add a replace-style fallback here: a foreign destination that
        // appears after the initial open must remain foreign and make the
        // publication fail closed.
        ct.ThrowIfCancellationRequested();
        // The hook is a publication seam, not preparation ownership proof.
        // Keep metadata staging invisible so a hook cannot cause an owned
        // preparation pathname to be mistaken for the final output.
        using var prepared = PreparedOutput.Create(destinationRoot, destinationPath, content, ct, mutationHook: null);
        var destinationIdentity = prepared.Identity;
        var published = false;

        try
        {
            // This helper is fresh-publication-only. Both an explicit no-replace
            // write and a destination that appears after the initial CAS open
            // use the same no-replace rename, so a foreign destination cannot
            // be replaced.
            _ = noReplace;
            // Preserve the deterministic insertion seam for the legacy
            // replace-style caller. Publication remains no-replace, so a
            // foreign destination inserted here is retained and causes the
            // held preparation rename to fail.
            if (!noReplace)
                mutationHook?.Invoke(destinationPath);
            prepared.Publish(destinationRoot, replace: false, mutationHook);
            published = true;

            if (CaptureFileIdentity(destinationDir, destinationPath) != destinationIdentity)
                throw new IOException($"Destination identity changed during commit of '{destinationPath}'.");

            if (guardHandle is not null && fullGuard is not null && fullGuardIdentity is not null)
            {
                if (GetFileIdentityWindows(guardHandle.SafeFileHandle) != fullGuardIdentity.Value
                    || CaptureFileIdentity(Path.GetDirectoryName(fullGuard) ?? string.Empty, fullGuard) != fullGuardIdentity.Value)
                    throw new IOException($"Ownership identity changed during commit of '{expectedGuardPath}'.");
            }

            SyncFile(destinationPath);
            SyncDirectory(destinationDir);
            ct.ThrowIfCancellationRequested();

            return Task.FromResult(new WriteCommitResult(true, destinationIdentity));
        }
        catch (Exception ex) when (published)
        {
            throw new WriteCommitException($"Write durability failed for '{destinationPath}'.", ex, new WriteCommitResult(true, destinationIdentity));
        }
        finally
        {
            guardHandle?.Dispose();
        }
    }

    private static async Task<WriteCommitResult> WriteTextAtomicallyAsyncLinux(
        string canonicalRoot,
        string canonicalDestination,
        string content,
        CancellationToken ct,
        bool noReplace,
        string? expectedGuardPath,
        FileIdentity? expectedGuardIdentity,
        Action<string>? mutationHook)
    {
        // Fresh Linux never falls back to a named temporary. A named fallback
        // creates an unlink race during cancellation/rollback and is not a safe
        // substitute for O_TMPFILE. Existing owned files use the descriptor-
        // bound in-place path above, so this is the only creation path.
        _ = noReplace;
        var anonymousResult = await TryWriteTextAtomicallyLinuxAnonymousAsync(
            canonicalRoot,
            canonicalDestination,
            content,
            ct,
            expectedGuardPath,
            expectedGuardIdentity,
            mutationHook).ConfigureAwait(false);
        if (anonymousResult.HasValue)
            return anonymousResult.Value;

        throw new PlatformNotSupportedException(
            "Fresh Linux NZBDAV output requires an O_TMPFILE-capable filesystem; named temporary fallback is disabled.");
    }
    // Compatibility wrapper used by the capability gate. New reconciliation
    // callers must supply a content proof as well as identity.
    private static void CopyFileToQuarantine(
        string libraryRoot,
        string sourcePath,
        string destinationPath,
        CancellationToken ct,
        FileIdentity? expectedIdentity,
        Action<string>? mutationHook)
        => CopyFileToQuarantineWithProof(libraryRoot, sourcePath, destinationPath, ct, expectedIdentity, null, mutationHook, false);

    private static void CopyFileToQuarantineWithProof(
        string libraryRoot,
        string sourcePath,
        string destinationPath,
        CancellationToken ct,
        FileIdentity? expectedIdentity,
        string? expectedSha256,
        Action<string>? mutationHook,
        bool matchingDestinationProven)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(destinationPath))
            throw new InvalidOperationException("Source and destination paths are required.");

        var canonicalRoot = GetCanonicalLibraryRoot(libraryRoot);
        var canonicalSource = Path.GetFullPath(sourcePath);
        var canonicalDestination = Path.GetFullPath(destinationPath);
        if (!IsPathWithinRoot(canonicalRoot, canonicalSource)
            || !IsPathWithinRoot(canonicalRoot, canonicalDestination))
            throw new InvalidOperationException("Quarantine path resolves outside library root.");

        EnsureCaseInsensitivePathComponents(canonicalRoot, canonicalDestination, allowExistingFinal: true);
        ct.ThrowIfCancellationRequested();
        expectedIdentity ??= CaptureFileIdentity(canonicalRoot, canonicalSource)
            ?? throw new IOException($"Cannot bind source identity for '{canonicalSource}'.");

        var destinationExists = TryPathExistsAnchored(canonicalRoot, canonicalDestination, out var destinationIsDirectory);
        if (destinationExists)
        {
            // A restart after the copy has committed but before its tombstone
            // was durable must converge.  Exact bounded content is sufficient
            // to recognize the already-published copy, but it is deliberately
            // not an ownership proof: a matching foreign occupant is retained
            // and is never deleted or adopted by the plugin.
            if (destinationIsDirectory)
                throw new IOException($"Quarantine destination is a foreign directory: '{canonicalDestination}'.");

            var destinationMaxBytes = Path.GetExtension(canonicalDestination).Equals(".json", StringComparison.OrdinalIgnoreCase)
                ? MaxManagedProbeBytes : MaxManagedStrmBytes;
            if (expectedSha256 is null)
            {
                if (!TryReadBoundedFileForPath(canonicalRoot, canonicalSource, destinationMaxBytes,
                        out var sourceBytes, out _))
                    throw new IOException($"Cannot prove source content for '{canonicalSource}'.");
                expectedSha256 = Convert.ToHexString(SHA256.HashData(sourceBytes));
            }

            if (TryReadBoundedFileForPath(canonicalRoot, canonicalDestination, destinationMaxBytes,
                    out var existingBytes, out _)
                && string.Equals(Convert.ToHexString(SHA256.HashData(existingBytes)), expectedSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                if (!matchingDestinationProven)
                    throw new IOException($"Matching quarantine destination has no managed marker proof: '{canonicalDestination}'.");
                return;
            }

            throw new IOException($"Quarantine destination is a foreign or mismatched occupant: '{canonicalDestination}'.");
        }

        if (OperatingSystem.IsLinux())
        {
            CopyFileToQuarantineLinux(canonicalRoot, canonicalSource, canonicalDestination, ct, expectedIdentity.Value, expectedSha256, mutationHook)
                .GetAwaiter()
                .GetResult();
            return;
        }

        CopyFileToQuarantineWindows(canonicalRoot, canonicalSource, canonicalDestination, ct, expectedIdentity.Value, expectedSha256, mutationHook);
    }

    private static void CopyFileToQuarantineWindows(
        string canonicalRoot,
        string sourcePath,
        string destinationPath,
        CancellationToken ct,
        FileIdentity expectedIdentity,
        string? expectedSha256,
        Action<string>? mutationHook)
    {
        if (ContainsSymlinkInPath(Path.GetFullPath(sourcePath)) || ContainsSymlinkInPath(Path.GetFullPath(destinationPath)))
            throw new InvalidOperationException("Quarantine path contains reparse point.");

        var sourceDirectory = Path.GetDirectoryName(sourcePath)
            ?? throw new InvalidOperationException("Source directory is invalid.");
        var destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("Destination directory is invalid.");
        Directory.CreateDirectory(destinationDirectory);
        // A deterministic staging pathname is a collision boundary, not an
        // ownership record. A pre-existing .tmp is foreign even when its bytes
        // happen to match the source; only this operation's held handle may be
        // moved to quarantine.
        var tempPath = destinationPath + ".tmp";
        FileIdentity? tempIdentity = null;
        FileStream? tempFile = null;
        var committed = false;
        try
        {
            using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 16 * 1024,
                options: FileOptions.SequentialScan);
            if (GetFileIdentityWindows(source.SafeFileHandle) != expectedIdentity)
                throw new IOException($"Source identity changed before quarantine: '{sourcePath}'.");
            var sourceProofBytes = ReadBoundedBytes(source, Path.GetExtension(sourcePath).Equals(".json", StringComparison.OrdinalIgnoreCase)
                ? MaxManagedProbeBytes
                : MaxManagedStrmBytes);
            if (sourceProofBytes is null
                || (expectedSha256 is not null && !string.Equals(Convert.ToHexString(SHA256.HashData(sourceProofBytes)), expectedSha256, StringComparison.OrdinalIgnoreCase)))
                throw new IOException($"Source content proof changed before quarantine: '{sourcePath}'.");

            mutationHook?.Invoke(sourcePath);
            if (CaptureFileIdentity(sourceDirectory, sourcePath) != expectedIdentity)
                throw new IOException($"Source identity changed before quarantine copy: '{sourcePath}'.");

            // CREATE_NEW is the only proof that this operation created the
            // staging inode. Do not open/adopt an existing pathname.
            tempFile = OpenWindowsOwnedFile(tempPath, createNew: true);
            tempIdentity = GetFileIdentityWindows(tempFile.SafeFileHandle);
            source.Position = 0;
            source.CopyTo(tempFile, 16 * 1024);
            tempFile.Flush(flushToDisk: true);
            var stagedBytes = ReadBoundedBytes(tempFile, MaxManagedProbeBytes);
            var requiredHash = expectedSha256
                ?? Convert.ToHexString(SHA256.HashData(sourceProofBytes));
            if (stagedBytes is null
                || !string.Equals(Convert.ToHexString(SHA256.HashData(stagedBytes)), requiredHash, StringComparison.OrdinalIgnoreCase)
                || GetFileIdentityWindows(tempFile.SafeFileHandle) != tempIdentity.Value
                || CaptureFileIdentity(destinationDirectory, tempPath) != tempIdentity.Value)
                throw new IOException($"Quarantine staging identity or content proof changed: '{tempPath}'.");

            ct.ThrowIfCancellationRequested();
            // Recheck the creation handle immediately before this NOREPLACE
            // move. A destination occupant is never overwritten, and any
            // source-path identity change fails closed.
            MoveWindowsOwnedFileNoReplace(tempFile, tempPath, destinationPath, tempIdentity.Value, mutationHook);
            committed = true;
            mutationHook?.Invoke(destinationPath);

            if (CaptureFileIdentity(destinationDirectory, destinationPath) != tempIdentity.Value)
                throw new IOException($"Quarantine destination identity changed: '{destinationPath}'.");
            // sourcePath may now be a watcher-owned replacement. The rename
            // already published this held inode; never require or mutate the
            // old pathname after the handle-based operation.

            SyncFile(destinationPath);
            SyncDirectory(destinationDirectory);
            ct.ThrowIfCancellationRequested();
        }
        catch (Exception ex) when (committed)
        {
            throw new QuarantineCopyDurabilityException(
                $"Quarantine copy durability failed for '{destinationPath}'.",
                ex,
                commitOccurred: true);
        }
        finally
        {
            // Proof failure at the old staging pathname must not leak the
            // operation handle while the foreign replacement is retained.
            try
            {
                if (File.Exists(tempPath))
                {
                    if (tempIdentity is null)
                        throw new IOException($"Cannot prove temporary quarantine cleanup identity for '{tempPath}'.");
                    if (!TryReadBoundedFileForPath(canonicalRoot, tempPath, MaxManagedProbeBytes, out var leftoverBytes, out var leftoverIdentity)
                        || leftoverIdentity is null
                        || leftoverIdentity.Value != tempIdentity.Value)
                        throw new IOException($"Refusing to delete replaced temporary quarantine path '{tempPath}'.");
                    DeleteOpenedWindowsFile(
                        tempPath,
                        MaxManagedProbeBytes,
                        leftoverIdentity.Value,
                        Convert.ToHexString(SHA256.HashData(leftoverBytes)),
                        mutationHook);
                    SyncDirectory(destinationDirectory);
                }
            }
            finally
            {
                tempFile?.Dispose();
            }
        }
    }

    private static async Task<WriteCommitResult?> TryWriteTextAtomicallyLinuxAnonymousAsync(
        string canonicalRoot,
        string canonicalDestination,
        string content,
        CancellationToken ct,
        string? expectedGuardPath,
        FileIdentity? expectedGuardIdentity,
        Action<string>? mutationHook)
    {
        var destinationRelative = Path.GetRelativePath(canonicalRoot, canonicalDestination);
        var destinationDirectory = Path.GetDirectoryName(destinationRelative);
        var destinationFileName = Path.GetFileName(canonicalDestination);
        if (string.IsNullOrWhiteSpace(destinationFileName))
            throw new InvalidOperationException("Destination filename is invalid.");

        var rootFd = OpenDirectoryForMutation(canonicalRoot);
        var destinationDirFd = rootFd;
        var tempFd = -1;
        var committed = false;
        try
        {
            var rootDevice = ValidateOpenedDirectoryDescriptor(rootFd, canonicalRoot, expectedDevice: null);
            destinationDirFd = string.IsNullOrWhiteSpace(destinationDirectory)
                ? rootFd
                : OpenDirectoryChain(canonicalRoot, rootFd, rootDevice, destinationDirectory, createDirectories: true);
            var destinationDirectoryPath = string.IsNullOrWhiteSpace(destinationDirectory)
                ? canonicalRoot
                : Path.Combine(canonicalRoot, destinationDirectory);

            ct.ThrowIfCancellationRequested();
            tempFd = OpenAnonymousWritableFileAt(destinationDirFd, rootDevice, destinationDirectoryPath);
            if (tempFd < 0)
            {
                var errno = Marshal.GetLastWin32Error();
                if (IsAnonymousTemporaryUnsupported(errno))
                    return null;
                throw new IOException($"Failed to create anonymous temporary inode in '{destinationDirectoryPath}'. errno={errno}");
            }

            var tempIdentity = GetFileIdentity(tempFd);
            await using var tempStream = new FileStream(
                new global::Microsoft.Win32.SafeHandles.SafeFileHandle((IntPtr)tempFd, ownsHandle: true),
                FileAccess.Write,
                bufferSize: 16 * 1024,
                isAsync: false);
            tempFd = -1;
            await using (var writer = new StreamWriter(tempStream, new UTF8Encoding(false), 1024, leaveOpen: true))
            {
                await writer.WriteAsync(content.AsMemory(), ct).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
            }
            await tempStream.FlushAsync(ct).ConfigureAwait(false);
            tempStream.Flush(true);
            if (SyncFd(checked((int)tempStream.SafeFileHandle.DangerousGetHandle())) != 0)
                throw new IOException($"Failed to fsync anonymous temporary inode for '{canonicalDestination}'.");

            HeldLinuxFile? guard = null;
            try
            {
                if (expectedGuardPath is not null)
                {
                    if (expectedGuardIdentity is null)
                        throw new IOException($"Cannot bind ownership guard for '{expectedGuardPath}'.");
                    guard = OpenHeldLinuxFile(canonicalRoot, Path.GetFullPath(expectedGuardPath), rootDevice);
                    if (guard.Identity != expectedGuardIdentity.Value)
                        throw new IOException($"Ownership identity changed before writing '{expectedGuardPath}'.");
                }

                if (LinkFileAtNoReplace(
                        checked((int)tempStream.SafeFileHandle.DangerousGetHandle()),
                        destinationDirFd,
                        destinationFileName) != 0)
                    throw new IOException($"Failed to create '{canonicalDestination}' without replacement. errno={Marshal.GetLastWin32Error()}");
                committed = true;
                mutationHook?.Invoke(canonicalDestination);
                if (!PathHasIdentity(destinationDirFd, rootDevice, destinationFileName, canonicalDestination, tempIdentity))
                    throw new IOException($"Committed destination identity mismatch: '{canonicalDestination}'.");
                if (guard is not null
                    && !PathHasIdentity(guard.DirectoryFd, rootDevice,
                        Path.GetFileName(Path.GetFullPath(expectedGuardPath!)),
                        Path.GetFullPath(expectedGuardPath!), guard.Identity))
                    throw new IOException($"Ownership identity changed during commit of '{canonicalDestination}'.");
                if (SyncDirectoryFd(destinationDirFd) != 0)
                    throw new IOException($"Failed to fsync directory containing '{canonicalDestination}'.");
                ct.ThrowIfCancellationRequested();
                return new WriteCommitResult(true, tempIdentity);
            }
            catch (Exception ex) when (committed)
            {
                // If a race removed our link, closing the descriptor drops the
                // anonymous inode. Never perform pathname rollback.
                throw new WriteCommitException(
                    $"Write durability failed for '{canonicalDestination}'.",
                    ex,
                    new WriteCommitResult(true, tempIdentity));
            }
            finally
            {
                guard?.Dispose();
            }
        }
        finally
        {
            if (tempFd >= 0)
                _ = CloseDirectoryHandle(tempFd);
            if (destinationDirFd != rootFd)
                _ = CloseDirectoryHandle(destinationDirFd);
            _ = CloseDirectoryHandle(rootFd);
        }
    }

    private static async Task CopyFileToQuarantineLinux(
        string canonicalRoot,
        string canonicalSource,
        string canonicalDestination,
        CancellationToken ct,
        FileIdentity expectedIdentity,
        string? expectedSha256,
        Action<string>? mutationHook)
    {
        var sourceRelative = Path.GetRelativePath(canonicalRoot, canonicalSource);
        var sourceDirectory = Path.GetDirectoryName(sourceRelative);
        var sourceFileName = Path.GetFileName(canonicalSource);
        var destinationRelative = Path.GetRelativePath(canonicalRoot, canonicalDestination);
        var destinationDirectory = Path.GetDirectoryName(destinationRelative);
        var destinationFileName = Path.GetFileName(canonicalDestination);
        if (string.IsNullOrWhiteSpace(sourceFileName) || string.IsNullOrWhiteSpace(destinationFileName))
            throw new InvalidOperationException("Quarantine filename is invalid.");

        var rootFd = OpenDirectoryForMutation(canonicalRoot);
        var sourceDirFd = rootFd;
        var destinationDirFd = rootFd;
        var sourceFd = -1;
        var tempFd = -1;
        var committed = false;
        try
        {
            var rootDevice = ValidateOpenedDirectoryDescriptor(rootFd, canonicalRoot, expectedDevice: null);
            sourceDirFd = string.IsNullOrWhiteSpace(sourceDirectory)
                ? rootFd
                : OpenDirectoryChain(canonicalRoot, rootFd, rootDevice, sourceDirectory, createDirectories: false);
            destinationDirFd = string.IsNullOrWhiteSpace(destinationDirectory)
                ? rootFd
                : OpenDirectoryChain(canonicalRoot, rootFd, rootDevice, destinationDirectory, createDirectories: true);

            sourceFd = OpenReadableFileAt(sourceDirFd, rootDevice, sourceFileName, canonicalSource);
            if (sourceFd < 0 || GetFileIdentity(sourceFd) != expectedIdentity)
                throw new IOException($"Source identity changed before quarantine: {canonicalSource}.");
            using var source = new FileStream(
                new global::Microsoft.Win32.SafeHandles.SafeFileHandle((IntPtr)sourceFd, ownsHandle: true),
                FileAccess.Read,
                bufferSize: 16 * 1024,
                isAsync: false);
            sourceFd = -1;
            var sourceProofBytes = ReadBoundedBytes(source, Path.GetExtension(canonicalSource).Equals(".json", StringComparison.OrdinalIgnoreCase)
                ? MaxManagedProbeBytes
                : MaxManagedStrmBytes);
            if (sourceProofBytes is null
                || (expectedSha256 is not null && !string.Equals(Convert.ToHexString(SHA256.HashData(sourceProofBytes)), expectedSha256, StringComparison.OrdinalIgnoreCase)))
                throw new IOException($"Source content proof changed before quarantine: '{canonicalSource}'.");

            mutationHook?.Invoke(canonicalSource);
            if (!PathHasIdentity(sourceDirFd, rootDevice, sourceFileName, canonicalSource, expectedIdentity))
                throw new IOException($"Source identity changed before quarantine copy: {canonicalSource}.");

            // O_TMPFILE is mandatory on fresh Linux. A named fallback would
            // reintroduce a pathname cleanup race and is therefore forbidden.
            tempFd = OpenAnonymousWritableFileAt(
                destinationDirFd,
                rootDevice,
                string.IsNullOrWhiteSpace(destinationDirectory)
                    ? canonicalRoot
                    : Path.Combine(canonicalRoot, destinationDirectory),
                readWrite: true);
            if (tempFd < 0)
            {
                var errno = Marshal.GetLastWin32Error();
                throw new PlatformNotSupportedException(
                    $"Fresh Linux quarantine requires an O_TMPFILE-capable filesystem; errno={errno}.");
            }

            var anonymousIdentity = GetFileIdentity(tempFd);
            await using var anonymous = new FileStream(
                new global::Microsoft.Win32.SafeHandles.SafeFileHandle((IntPtr)tempFd, ownsHandle: true),
                FileAccess.ReadWrite,
                bufferSize: 16 * 1024,
                isAsync: false);
            tempFd = -1;
            source.Position = 0;
            await source.CopyToAsync(anonymous, 16 * 1024, ct).ConfigureAwait(false);
            await anonymous.FlushAsync(ct).ConfigureAwait(false);
            anonymous.Flush(flushToDisk: true);
            if (SyncFd(checked((int)anonymous.SafeFileHandle.DangerousGetHandle())) != 0)
                throw new IOException($"Failed to fsync anonymous quarantine copy {canonicalDestination}.");

            // The source pathname can be rewritten in place after its initial
            // proof while retaining the same inode. Validate the held,
            // anonymous destination before it has any name: an unexpected
            // copy is discarded on descriptor disposal rather than published
            // at the deterministic quarantine path.
            var expectedCopySha256 = expectedSha256 ?? Convert.ToHexString(SHA256.HashData(sourceProofBytes));
            var copiedBytes = ReadBoundedBytes(anonymous, Path.GetExtension(canonicalSource).Equals(".json", StringComparison.OrdinalIgnoreCase)
                ? MaxManagedProbeBytes
                : MaxManagedStrmBytes);
            if (copiedBytes is null
                || !string.Equals(Convert.ToHexString(SHA256.HashData(copiedBytes)), expectedCopySha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Anonymous quarantine copy content proof changed: {canonicalDestination}.");

            ct.ThrowIfCancellationRequested();
            if (LinkFileAtNoReplace(
                    checked((int)anonymous.SafeFileHandle.DangerousGetHandle()),
                    destinationDirFd,
                    destinationFileName) != 0)
                throw new IOException($"Failed to create quarantine copy {canonicalDestination}. errno={Marshal.GetLastWin32Error()}");
            committed = true;
            mutationHook?.Invoke(canonicalDestination);

            // Neither source nor destination is overwritten. A post-check
            // failure retains the exact pathnames rather than attempting a
            // racy rollback unlink.
            if (!PathHasIdentity(destinationDirFd, rootDevice, destinationFileName, canonicalDestination, anonymousIdentity))
                throw new IOException($"Quarantine destination identity changed: {canonicalDestination}.");
            if (!PathHasIdentity(sourceDirFd, rootDevice, sourceFileName, canonicalSource, expectedIdentity))
                throw new IOException($"Source identity changed during quarantine: {canonicalSource}.");
            if (SyncDirectoryFd(destinationDirFd) != 0)
                throw new IOException($"Failed to fsync quarantine directory {canonicalDestination}.");
            ct.ThrowIfCancellationRequested();
        }
        catch (Exception ex) when (committed)
        {
            throw new QuarantineCopyDurabilityException(
                $"Quarantine copy durability failed for {canonicalDestination}.",
                ex,
                commitOccurred: true);
        }
        finally
        {
            if (tempFd >= 0)
                _ = CloseDirectoryHandle(tempFd);
            if (sourceFd >= 0)
                _ = CloseDirectoryHandle(sourceFd);
            if (destinationDirFd != rootFd)
                _ = CloseDirectoryHandle(destinationDirFd);
            if (sourceDirFd != rootFd && sourceDirFd != destinationDirFd)
                _ = CloseDirectoryHandle(sourceDirFd);
            _ = CloseDirectoryHandle(rootFd);
        }
    }
    private static string BuildLogicalTombstonePath(string strmPath)
        => strmPath + LogicalTombstoneSuffix;

    private static bool TryGetLogicalTombstone(
        string libraryRoot,
        string path,
        out FileIdentity identity,
        out string sourceSha256)
    {
        identity = default;
        sourceSha256 = string.Empty;
        if (!TryReadBoundedFirstLineForPath(libraryRoot, path, MaxManagedMarkerBytes, out var line))
            return false;

        if (line.Bytes.Any(static value => value is (byte)'\r' or (byte)'\n'))
            return false;
        if (!string.Equals(line.Content, line.Content.Trim(), StringComparison.Ordinal))
            return false;
        var parts = line.Content.Split('/', StringSplitOptions.None);
        // v1 bound only device/inode and is not accepted. Inode reuse must
        // never make a foreign replacement invisible to any caller. v2 is an
        // exact six-field record; accepting a suffix would permit an attacker
        // to smuggle a different run-id grammar past migration code.
        if (parts.Length != 6
            || !string.Equals(parts[0], "nzbdav.tombstone", StringComparison.Ordinal)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            || version != 2
            || !ulong.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var device)
            || !ulong.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var inode)
            || device == 0
            || inode == 0
            || parts[4].Length != 64
            || !parts[4].All(Uri.IsHexDigit)
            || !IsValidRunId(parts[5]))
            return false;

        sourceSha256 = parts[4];
        identity = new FileIdentity(device, inode);
        return true;
    }

    /// <summary>
    /// Returns true only when the tombstone records the identity and content of
    /// the current source inode. A tombstone is not a pathname deny-list: a
    /// foreign file replacing the old inode at the same pathname remains visible.
    /// </summary>
    internal static bool IsLogicallyTombstoned(string libraryRoot, string strmPath)
    {
        try
        {
            var canonicalRoot = GetCanonicalLibraryRoot(libraryRoot);
            var tombstonePath = BuildLogicalTombstonePath(strmPath);
            if (!IsPathWithinRoot(canonicalRoot, Path.GetFullPath(tombstonePath))
                || !TryGetLogicalTombstone(canonicalRoot, tombstonePath, out var tombstoneIdentity, out var tombstoneHash)
                || !TryReadBoundedFileForPath(canonicalRoot, strmPath, MaxManagedStrmBytes, out var sourceBytes, out var sourceIdentity)
                || sourceIdentity is null
                || sourceIdentity.Value != tombstoneIdentity)
                return false;

            // Identity and content are read from the same held descriptor. A
            // hash/read failure is foreign-visible (fail open), never a reason
            // to suppress media; v2 always carries a content hash.
            var sourceHash = Convert.ToHexString(SHA256.HashData(sourceBytes));
            return string.Equals(sourceHash, tombstoneHash, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // Malformed or inaccessible tombstones fail open for foreign
            // visibility. They never become an ownership or deletion proof.
            return false;
        }
    }

    /// <summary>
    /// Reads and proves all provider inputs while the caller owns the media
    /// gate. Legacy markers may prove a .strm, but never prove a probe sidecar;
    /// that is an intentional fail-open migration rule.
    /// </summary>
    internal static bool TryReadManagedMediaForProvider(
        string libraryRoot,
        string strmPath,
        string? baseUrl,
        Guid expectedItemId,
        out string streamUrl,
        out byte[] probeBytes)
    {
        streamUrl = string.Empty;
        probeBytes = [];
        _ = expectedItemId; // Jellyfin's BaseItem.Id is not a backend manifest id.
        if (!TryParseBackendBaseUri(baseUrl, out var baseUri))
            return false;

        var markerPath = BuildManagedMarkerPath(strmPath);
        // Provider ownership is a completed proof only. In particular, do not
        // fall back to a probe hash or discard the marker's manifest item id.
        if (!TryGetManagedOwnership(libraryRoot, markerPath, strmPath, baseUri, expectedItemId: null,
                out var ownership, completedOnly: true)
            || ownership.Probe is null)
            return false;

        var probePath = BuildSafeProbePath(strmPath);
        if (!TryReadBoundedFileForPath(libraryRoot, probePath, MaxManagedProbeBytes, out probeBytes, out var probeIdentity)
            || probeIdentity is null || probeIdentity.Value != ownership.Probe.Value.Identity
            || !string.Equals(Convert.ToHexString(SHA256.HashData(probeBytes)), ownership.Probe.Value.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            probeBytes = [];
            return false;
        }
        streamUrl = ownership.Strm.Content.Trim();
        return true;
    }

    private static void WriteLogicalTombstone(
        string canonicalRoot,
        string sourcePath,
        string tombstonePath,
        FileIdentity sourceIdentity,
        string sourceHash,
        string runId,
        CancellationToken ct,
        Action<string>? mutationHook)
    {
        if (!PathHasIdentityForPath(canonicalRoot, sourcePath, sourceIdentity))
            throw new IOException($"Source identity changed before logical tombstone: '{sourcePath}'.");

        if (TryPathExistsAnchored(canonicalRoot, tombstonePath, out _))
        {
            if (TryGetLogicalTombstone(canonicalRoot, tombstonePath, out var existingIdentity, out var existingHash)
                && existingIdentity == sourceIdentity
                && TryGetFileSha256(canonicalRoot, sourcePath, MaxManagedStrmBytes, out var existingSourceHash)
                && string.Equals(existingHash, existingSourceHash, StringComparison.OrdinalIgnoreCase))
                return;
            throw new IOException($"Logical tombstone collision: '{tombstonePath}'.");
        }

        if (!TryReadBoundedFileForPath(canonicalRoot, sourcePath, MaxManagedStrmBytes, out var sourceBytes, out var currentIdentity)
            || currentIdentity is null
            || currentIdentity.Value != sourceIdentity
            || !string.Equals(Convert.ToHexString(SHA256.HashData(sourceBytes)), sourceHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"Source content proof changed before logical tombstone: '{sourcePath}'.");
        // Reflection-based callers may provide an old arbitrary run label. Do
        // not emit a record that the strict v2 reader would later reject.
        if (!IsValidRunId(runId))
            runId = CreateRunId();
        var content = $"nzbdav.tombstone/2/{sourceIdentity.Device}/{sourceIdentity.Inode}/{sourceHash}/{runId}";
        WriteTextAtomicallyAsyncNoReplace(
            canonicalRoot,
            tombstonePath,
            content,
            ct,
            expectedGuardPath: sourcePath,
            expectedGuardIdentity: sourceIdentity,
            mutationHook: mutationHook).GetAwaiter().GetResult();
    }

    private static bool TryGetFileSha256(string libraryRoot, string path, int maxBytes, out string hash)
    {
        hash = string.Empty;
        try
        {
            if (!TryReadBoundedFileForPath(libraryRoot, path, maxBytes, out var bytes, out _))
                return false;
            hash = Convert.ToHexString(SHA256.HashData(bytes));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool LibraryRootExists(string libraryRoot)
    {
        try
        {
            var canonicalRoot = GetCanonicalLibraryRoot(libraryRoot);
            if (OperatingSystem.IsLinux())
            {
                var rootFd = OpenDirectoryForMutation(canonicalRoot);
                _ = CloseDirectoryHandle(rootFd);
                return true;
            }

            return Directory.Exists(canonicalRoot) && !ContainsSymlinkInPath(canonicalRoot, canonicalRoot);
        }
        catch
        {
            return false;
        }
    }

    // Linux enumeration is rooted at a held directory descriptor. The proc fd
    // path is used only to enumerate names; every returned path is subsequently
    // opened/read through TryReadBoundedFileForPath, whose openat2 resolution is
    // RESOLVE_BENEATH|RESOLVE_NO_SYMLINKS|RESOLVE_NO_XDEV. This avoids recursive
    // Directory.EnumerateFiles traversal through a moved library root or mount.
    private static IEnumerable<string> EnumerateAnchoredStrmFiles(string canonicalRoot)
    {
        if (!OperatingSystem.IsLinux())
        {
            foreach (var path in Directory.EnumerateFiles(canonicalRoot, "*.strm", SearchOption.AllDirectories))
                yield return Path.GetFullPath(path);
            yield break;
        }

        var rootFd = OpenDirectoryForMutation(canonicalRoot);
        try
        {
            var descriptorRoot = $"/proc/self/fd/{rootFd}";
            foreach (var path in Directory.EnumerateFiles(descriptorRoot, "*.strm", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(descriptorRoot, path);
                if (relative == "." || Path.IsPathRooted(relative)
                    || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    continue;

                var candidate = Path.GetFullPath(Path.Combine(canonicalRoot, relative));
                if (IsPathWithinRoot(canonicalRoot, candidate))
                    yield return candidate;
            }
        }
        finally
        {
            _ = CloseDirectoryHandle(rootFd);
        }
    }

    private static string[] EnumerateAnchoredDirectoryEntries(string canonicalRoot, string directory)
    {
        if (!OperatingSystem.IsLinux())
            return Directory.EnumerateFileSystemEntries(directory).ToArray();

        var rootFd = OpenDirectoryForMutation(canonicalRoot);
        var directoryFd = rootFd;
        try
        {
            var rootDevice = ValidateOpenedDirectoryDescriptor(rootFd, canonicalRoot, expectedDevice: null);
            var relative = Path.GetRelativePath(canonicalRoot, directory);
            if (relative != ".")
                directoryFd = OpenDirectoryChain(canonicalRoot, rootFd, rootDevice, relative,
                    createDirectories: false, invokeMutationHook: false);

            return Directory.EnumerateFileSystemEntries($"/proc/self/fd/{directoryFd}")
                .Select(entry => Path.Combine(directory, Path.GetFileName(entry)))
                .ToArray();
        }
        finally
        {
            if (directoryFd != rootFd)
                _ = CloseDirectoryHandle(directoryFd);
            _ = CloseDirectoryHandle(rootFd);
        }
    }

    private static bool TryPathExistsAnchored(string libraryRoot, string path, out bool isDirectory)
    {
        isDirectory = false;
        try
        {
            var canonicalRoot = GetCanonicalLibraryRoot(libraryRoot);
            var canonicalPath = Path.GetFullPath(path);
            if (!IsPathWithinRoot(canonicalRoot, canonicalPath))
                return false;

            if (!OperatingSystem.IsLinux())
            {
                if (ContainsSymlinkInPath(canonicalRoot, canonicalPath))
                    return false;
                isDirectory = Directory.Exists(canonicalPath);
                return isDirectory || File.Exists(canonicalPath);
            }

            var relative = Path.GetRelativePath(canonicalRoot, canonicalPath);
            var directory = Path.GetDirectoryName(relative);
            var name = Path.GetFileName(relative);
            if (string.IsNullOrWhiteSpace(name))
                return false;

            var rootFd = OpenDirectoryForMutation(canonicalRoot);
            var directoryFd = rootFd;
            try
            {
                var rootDevice = ValidateOpenedDirectoryDescriptor(rootFd, canonicalRoot, expectedDevice: null);
                directoryFd = string.IsNullOrWhiteSpace(directory)
                    ? rootFd
                    : OpenDirectoryChain(canonicalRoot, rootFd, rootDevice, directory,
                        createDirectories: false, invokeMutationHook: false);
                var fd = OpenAt2(directoryFd, name,
                    LinuxOpenPath | LinuxOpen.NoFollow | LinuxOpen.CloseOnExec,
                    0, ResolveBeneath | ResolveNoSymlinks | ResolveNoXdev);
                if (fd < 0)
                {
                    var errno = Marshal.GetLastWin32Error();
                    if (errno == ErrnoNoEnt)
                        return false;
                    throw new IOException($"Unable to inspect rooted path '{canonicalPath}'. errno={errno}");
                }

                try
                {
                    var mode = GetFileMode(fd, out var device);
                    if (device != rootDevice)
                        throw new IOException($"Path '{canonicalPath}' is on unexpected filesystem.");
                    isDirectory = (mode & LinuxModeMask) == LinuxModeDirectory;
                    return isDirectory || (mode & LinuxModeMask) == LinuxModeRegular;
                }
                finally
                {
                    _ = CloseDirectoryHandle(fd);
                }
            }
            finally
            {
                if (directoryFd != rootFd)
                    _ = CloseDirectoryHandle(directoryFd);
                _ = CloseDirectoryHandle(rootFd);
            }
        }
        catch (IOException ex) when (ex.Message.Contains("errno=2", StringComparison.Ordinal))
        {
            // A missing parent is an ordinary absent path. Other rooted
            // traversal failures, including a nested bind mount, remain
            // fail-closed exceptions rather than being mistaken for absence.
            isDirectory = false;
            return false;
        }
        catch
        {
            isDirectory = false;
            throw;
        }
    }

    private static bool PathHasIdentityForPath(string canonicalRoot, string path, FileIdentity expected)
    {
        if (!OperatingSystem.IsLinux())
            return CaptureFileIdentity(canonicalRoot, path) == expected;

        var relative = Path.GetRelativePath(canonicalRoot, path);
        var directory = Path.GetDirectoryName(relative);
        var name = Path.GetFileName(relative);
        if (string.IsNullOrWhiteSpace(name))
            return false;
        var rootFd = OpenDirectoryForMutation(canonicalRoot);
        try
        {
            var rootDevice = ValidateOpenedDirectoryDescriptor(rootFd, canonicalRoot, expectedDevice: null);
            var directoryFd = string.IsNullOrWhiteSpace(directory)
                ? rootFd
                : OpenDirectoryChain(canonicalRoot, rootFd, rootDevice, directory, createDirectories: false, invokeMutationHook: false);
            try
            {
                return PathHasIdentity(directoryFd, rootDevice, name, path, expected);
            }
            finally
            {
                if (directoryFd != rootFd)
                    _ = CloseDirectoryHandle(directoryFd);
            }
        }
        finally
        {
            _ = CloseDirectoryHandle(rootFd);
        }
    }

    private static void EnsureQuarantineRoot(string libraryRoot)
    {
        var canonicalRoot = GetCanonicalLibraryRoot(libraryRoot);
        var quarantineRoot = Path.Combine(canonicalRoot, ".quarantine");
        EnsureCaseInsensitivePathComponents(canonicalRoot, quarantineRoot, allowExistingFinal: true);

        if (ContainsSymlinkInPath(quarantineRoot))
            throw new InvalidOperationException("Quarantine path contains reparse point.");

        if (OperatingSystem.IsLinux())
        {
            var rootFd = OpenDirectoryForMutation(canonicalRoot);
            var quarantineFd = rootFd;
            try
            {
                var device = ValidateOpenedDirectoryDescriptor(rootFd, canonicalRoot, expectedDevice: null);
                quarantineFd = OpenDirectoryChain(canonicalRoot, rootFd, device, ".quarantine",
                    createDirectories: true, invokeMutationHook: false);
                if (SyncDirectoryFd(quarantineFd) != 0 || SyncDirectoryFd(rootFd) != 0)
                    throw new IOException("Failed to fsync quarantine namespace.");
            }
            finally
            {
                if (quarantineFd != rootFd)
                    _ = CloseDirectoryHandle(quarantineFd);
                _ = CloseDirectoryHandle(rootFd);
            }
        }
        else
        {
            Directory.CreateDirectory(quarantineRoot);
            SyncDirectory(quarantineRoot);
        }

        var noMediaPath = Path.Combine(quarantineRoot, ".nomedia");
        if (!TryReadBoundedFileForPath(canonicalRoot, noMediaPath, MaxManagedMarkerBytes, out _, out _))
        {
            WriteTextAtomicallyAsyncNoReplace(canonicalRoot, noMediaPath,
                "NZBDAV quarantine - do not scan", CancellationToken.None).GetAwaiter().GetResult();
        }

        // Keep the final durability descriptor rooted at the canonical library
        // handle too. The path-based Unix fallback is intentionally not used
        // for managed quarantine state (it cannot reject a nested bind mount).
        if (OperatingSystem.IsLinux())
            SyncAnchoredDirectory(canonicalRoot, ".quarantine");
        else
            SyncDirectory(quarantineRoot);
    }

    private static string GetCanonicalLibraryRoot(string libraryRoot)
    {
        var fullRoot = Path.GetFullPath(libraryRoot);
        var pathRoot = Path.GetPathRoot(fullRoot);
        if (string.IsNullOrWhiteSpace(pathRoot))
            throw new InvalidOperationException($"Cannot determine path root for '{libraryRoot}'.");

        var trimmed = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var trimmedRoot = pathRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(trimmed, trimmedRoot, StringComparison.Ordinal))
            return pathRoot;

        if (string.IsNullOrEmpty(trimmed))
            return pathRoot;

        return trimmed;
    }

    private static bool IsPathWithinRoot(string root, string candidate)
    {
        // Path containment follows the host filesystem. Do not fold case on a
        // case-sensitive Unix filesystem: /tmp/Media and /tmp/media are
        // different roots, not alternate spellings of one root.
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(candidate, root, comparison))
            return true;

        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrEmpty(normalizedRoot))
            normalizedRoot = root;

        var rootWithSeparator = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            || normalizedRoot.EndsWith(Path.AltDirectorySeparatorChar)
                ? normalizedRoot
                : normalizedRoot + Path.DirectorySeparatorChar;

        return candidate.StartsWith(rootWithSeparator, comparison);
    }

    private static bool ValidateSyncOutputPathsBeforeNetwork(string canonicalRoot, params string[] paths)
    {
        foreach (var path in paths)
        {
            var candidate = Path.GetFullPath(path);
            if (!IsPathWithinRoot(canonicalRoot, candidate)
                || !IsSafeSyncOutputPathBeforeNetwork(canonicalRoot, candidate))
                return false;
        }

        return true;
    }

    private static bool IsSafeSyncOutputPathBeforeNetwork(string canonicalRoot, string candidate)
    {
        if (!OperatingSystem.IsLinux())
            return !ContainsSymlinkInPath(canonicalRoot, candidate);

        // Do not use a pathname attribute walk on Linux. This openat2 probe is
        // rooted at the configured library and rejects a symlink or nested bind
        // in any existing component. ENOENT is safe for this preflight because
        // no existing component can then redirect that missing suffix. Actual
        // writers repeat the descriptor-rooted resolution when they mutate.
        var relative = Path.GetRelativePath(canonicalRoot, candidate);
        if (string.IsNullOrWhiteSpace(relative) || relative == ".")
            return false;

        var rootFd = -1;
        var pathFd = -1;
        try
        {
            rootFd = OpenDirectoryForMutation(canonicalRoot);
            var rootDevice = ValidateOpenedDirectoryDescriptor(rootFd, canonicalRoot, expectedDevice: null);
            pathFd = OpenAt2(
                rootFd,
                relative,
                LinuxOpenPath | LinuxOpen.NoFollow | LinuxOpen.CloseOnExec,
                0,
                ResolveBeneath | ResolveNoSymlinks | ResolveNoXdev);
            if (pathFd < 0)
                return Marshal.GetLastWin32Error() == ErrnoNoEnt;

            var mode = GetFileMode(pathFd, out var device);
            return (mode & LinuxModeMask) == LinuxModeRegular && device == rootDevice;
        }
        catch
        {
            // Unsupported openat2 and every unexpected traversal failure are
            // fail-closed before issuing a network request.
            return false;
        }
        finally
        {
            if (pathFd >= 0)
                _ = CloseDirectoryHandle(pathFd);
            if (rootFd >= 0)
                _ = CloseDirectoryHandle(rootFd);
        }
    }

    private static bool ContainsSymlinkInPath(string root, string candidate)
    {
        if (OperatingSystem.IsLinux())
        {
            // Linux callers never use this pathname probe as a safety proof.
            // All managed reads/mutations below are descriptor-rooted openat2
            // operations with RESOLVE_BENEATH|RESOLVE_NO_SYMLINKS|RESOLVE_NO_XDEV;
            // returning false here prevents a legacy File.Exists/GetAttributes
            // walk from reintroducing a check-then-use race.
            return false;
        }

        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrEmpty(normalizedRoot))
            normalizedRoot = root;
        var directory = Path.GetDirectoryName(candidate);
        if (directory is null)
            return false;

        while (!string.IsNullOrEmpty(directory) && directory.Length >= normalizedRoot.Length)
        {
            if (Directory.Exists(directory) || File.Exists(directory))
            {
                if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
                    return true;
            }

            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (string.Equals(directory, normalizedRoot, comparison))
                break;

            directory = Path.GetDirectoryName(directory);
            if (directory is null)
                break;
        }

        return File.Exists(candidate)
               && File.GetAttributes(candidate).HasFlag(FileAttributes.ReparsePoint);
    }

    private static bool ContainsSymlinkInPath(string candidate)
    {
        var fullPath = Path.GetFullPath(candidate);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
            return true;

        return ContainsSymlinkInPath(root, fullPath);
    }

    private static void EnsureCaseInsensitivePathComponents(string root, string candidate, bool allowExistingFinal)
    {
        var relative = Path.GetRelativePath(root, candidate);
        if (relative == ".")
            return;

        var current = root;
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Where(static part => !string.IsNullOrEmpty(part));
        foreach (var part in parts)
        {
            var currentIsRoot = string.Equals(
                GetCanonicalLibraryRoot(root), GetCanonicalLibraryRoot(current),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            var currentExists = currentIsRoot
                ? LibraryRootExists(root)
                : TryPathExistsAnchored(root, current, out var currentIsDirectory) && currentIsDirectory;
            if (!currentExists)
                break;

            var entries = EnumerateAnchoredDirectoryEntries(GetCanonicalLibraryRoot(root), current);
            var matching = entries.FirstOrDefault(entry =>
                string.Equals(Path.GetFileName(entry), part, StringComparison.OrdinalIgnoreCase));
            var exact = Path.Combine(current, part);
            if (!OperatingSystem.IsWindows()
                && matching is not null
                && !string.Equals(matching, exact, StringComparison.Ordinal))
                throw new InvalidOperationException($"Case-insensitive path collision at '{candidate}'.");

            if (matching is not null)
            {
                current = matching;
                continue;
            }

            current = exact;
        }

        if (!allowExistingFinal && TryPathExistsAnchored(root, candidate, out _))
            throw new IOException($"Destination already exists: '{candidate}'.");
    }

    private const int WindowsFileFlagOpenReparsePoint = 0x00200000;
    private const int WindowsFileFlagWriteThrough = unchecked((int)0x80000000);
    private const int WindowsFileDelete = 0x00010000;
    private const int WindowsFileOpenDirectory = 0x02000000;

    private static FileStream OpenWindowsHeldFile(string path, bool requireDeleteAccess = true)
    {
        var stream = TryOpenWindowsHeldFile(path, requireDeleteAccess);
        return stream ?? throw new IOException($"Unable to open owned file '{path}'. errno={Marshal.GetLastWin32Error()}");
    }

    private static FileStream? TryOpenWindowsHeldFile(string path, bool requireDeleteAccess = true)
    {
        // Existing destinations are CAS objects. Do not share DELETE while the
        // exact destination handle is held: a rename-away followed by a
        // foreign install must fail rather than replace an unproved inode.
        var desiredAccess = unchecked((int)0xC0000000) // GENERIC_READ | GENERIC_WRITE
            | (requireDeleteAccess ? WindowsFileDelete : 0);
        var shareMode = requireDeleteAccess
            ? (int)(FileShare.ReadWrite | FileShare.Delete)
            : (int)FileShare.Read;
        var handle = OpenDirectoryHandleWindows(
            path,
            desiredAccess,
            shareMode,
            IntPtr.Zero,
            3, // OPEN_EXISTING
            WindowsFileAttributeNormal,
            IntPtr.Zero);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            var error = Marshal.GetLastWin32Error();
            // Only a proven absence permits falling through to no-replace
            // creation. Access, sharing, reparse, and other errors fail closed.
            if (error is 2 or 3)
                return null;
            throw new IOException($"Unable to open owned file '{path}'. errno={error}");
        }

        return new FileStream(
            new global::Microsoft.Win32.SafeHandles.SafeFileHandle(handle, ownsHandle: true),
            FileAccess.ReadWrite,
            16 * 1024, isAsync: false);
    }

    private static FileStream OpenWindowsOwnedFile(string path, bool createNew)
    {
        // SetFileInformationByHandle(FileRenameInfo) requires DELETE access on
        // the source handle.
        var handle = OpenDirectoryHandleWindows(
            path,
            unchecked((int)0xC0000000) | WindowsFileDelete, // GENERIC_READ | GENERIC_WRITE | DELETE
            (int)(FileShare.ReadWrite | FileShare.Delete),
            IntPtr.Zero,
            createNew ? 1 : 3, // CREATE_NEW / OPEN_EXISTING
            WindowsFileAttributeNormal,
            IntPtr.Zero);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            throw new IOException($"Unable to open owned staging file '{path}'. errno={Marshal.GetLastWin32Error()}");

        return new FileStream(
            new global::Microsoft.Win32.SafeHandles.SafeFileHandle(handle, ownsHandle: true),
            FileAccess.ReadWrite, 16 * 1024, isAsync: false);
    }

    private static void MoveWindowsOwnedFileNoReplace(
        FileStream file, string sourcePath, string destinationPath, FileIdentity expectedIdentity,
        Action<string>? mutationHook = null)
        => MoveWindowsOwnedFile(file, sourcePath, destinationPath, expectedIdentity, null, null, false, mutationHook);

    private static void MoveWindowsOwnedFile(
        FileStream file, string sourcePath, string destinationPath, FileIdentity expectedIdentity,
        Action<string>? mutationHook)
        => MoveWindowsOwnedFile(file, sourcePath, destinationPath, expectedIdentity, null, null, false, mutationHook);

    private static void MoveWindowsOwnedFile(
        FileStream file, string sourcePath, string destinationPath, FileIdentity expectedIdentity,
        FileStream? expectedDestination, FileIdentity? expectedDestinationIdentity, bool replaceIfExists,
        Action<string>? mutationHook)
    {
        var fullSource = Path.GetFullPath(sourcePath);
        var sourceDirectory = Path.GetDirectoryName(fullSource) ?? string.Empty;
        var fullDestination = Path.GetFullPath(destinationPath);
        var destinationDirectory = Path.GetDirectoryName(fullDestination)
            ?? throw new IOException($"Destination directory is invalid: '{destinationPath}'.");

        if (GetFileIdentityWindows(file.SafeFileHandle) != expectedIdentity
            || CaptureFileIdentity(sourceDirectory, fullSource) != expectedIdentity)
            throw new IOException($"Owned staging identity changed before moving '{sourcePath}'.");

        if (replaceIfExists)
        {
            if (expectedDestination is null || expectedDestinationIdentity is null)
                throw new IOException($"Replacement publication requires expected destination state for '{destinationPath}'.");

            var expectedDestinationIdentityValue = expectedDestinationIdentity.Value;
            var preMoveHandleIdentity = GetFileIdentityWindows(expectedDestination.SafeFileHandle);
            var preMoveCapturedIdentity = CaptureFileIdentity(destinationDirectory, fullDestination);
            if (preMoveHandleIdentity != expectedDestinationIdentityValue
                || preMoveCapturedIdentity != expectedDestinationIdentityValue)
                throw new IOException($"Ownership identity changed before moving '{destinationPath}'.");
        }
        // FILE_RENAME_INFO renames the inode represented by the held handle,

        // This is the final deterministic watcher seam. The source pathname
        // may be replaced after the proof; the held handle below must still be
        // the only object that gets published.
        mutationHook?.Invoke(sourcePath);

        if (GetFileIdentityWindows(file.SafeFileHandle) != expectedIdentity)
            throw new IOException($"Owned staging handle changed before moving '{sourcePath}'.");

        if (replaceIfExists)
        {
            var expectedDestinationIdentityValue = expectedDestinationIdentity.GetValueOrDefault();
            var preRenameHandleIdentity = GetFileIdentityWindows(expectedDestination!.SafeFileHandle);
            var preRenameCapturedIdentity = CaptureFileIdentity(destinationDirectory, fullDestination);
            if (preRenameHandleIdentity != expectedDestinationIdentityValue
                || preRenameCapturedIdentity != expectedDestinationIdentityValue)
                throw new IOException($"Ownership identity changed before moving '{destinationPath}'.");
        }

        // FILE_RENAME_INFO renames the inode represented by the held handle,
        // not whatever currently occupies sourcePath. ReplaceIfExists is
        // selected to enforce the atomic publication policy for this operation.
        if (ContainsSymlinkInPath(destinationDirectory) || ContainsSymlinkInPath(fullDestination))
            throw new IOException($"Destination path contains a reparse point: '{destinationPath}'.");

        var nativeDestination = fullDestination;
        var fileNameBytes = Encoding.Unicode.GetBytes(nativeDestination);
        var fileNameOffset = IntPtr.Size == 8 ? 20 : 12;
        // Windows consumes the documented byte length, but some filesystem
        // rename providers still inspect the trailing WCHAR. Keep that
        // terminator inside the bounded native buffer without counting it in
        // FileNameLength.
        var bufferSize = checked(fileNameOffset + fileNameBytes.Length + sizeof(char));
        var renameInfo = Marshal.AllocHGlobal(bufferSize);
        try
        {
            // FILE_RENAME_INFO layout: BOOL ReplaceIfExists, HANDLE
            // RootDirectory, DWORD FileNameLength, WCHAR FileName[]. A null
            // root binds the fully-qualified native destination path.
            Marshal.WriteInt32(renameInfo, 0, replaceIfExists ? 1 : 0);
            Marshal.WriteIntPtr(renameInfo, IntPtr.Size == 8 ? 8 : 4, IntPtr.Zero);
            Marshal.WriteInt32(renameInfo, IntPtr.Size == 8 ? 16 : 8, fileNameBytes.Length);
            Marshal.Copy(fileNameBytes, 0, IntPtr.Add(renameInfo, fileNameOffset), fileNameBytes.Length);
            Marshal.WriteInt16(renameInfo, fileNameOffset + fileNameBytes.Length, 0);

            if (!SetFileInformationByHandle(
                    file.SafeFileHandle.DangerousGetHandle(), WindowsFileRenameInfo,
                    renameInfo, checked((uint)bufferSize)))
                throw new IOException($"Failed to publish owned staging inode '{sourcePath}'. errno={Marshal.GetLastWin32Error()}");

        }
        finally
        {
            Marshal.FreeHGlobal(renameInfo);
        }

        // The destination must now resolve to the exact held inode. Do not
        // require sourcePath to remain absent: a watcher-owned replacement at
        // that old pathname is intentionally left untouched.
        if (ContainsSymlinkInPath(destinationDirectory) || ContainsSymlinkInPath(fullDestination)
            || GetFileIdentityWindows(file.SafeFileHandle) != expectedIdentity
            || CaptureFileIdentity(destinationDirectory, fullDestination) != expectedIdentity)
        {
            throw new IOException($"Owned staging destination identity changed: '{destinationPath}'.");
        }
    }
    private static FileStream OpenWindowsRecoverySlot(string path, bool expectedExists)
    {
        if (!expectedExists)
            return new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete, MaxRecoveryJournalBytes,
                FileOptions.WriteThrough);

        var handle = OpenDirectoryHandleWindows(
            path,
            unchecked((int)0xC0000000), // GENERIC_READ | GENERIC_WRITE
            (int)(FileShare.ReadWrite | FileShare.Delete),
            IntPtr.Zero,
            3, // OPEN_EXISTING
            WindowsFileAttributeNormal | WindowsFileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            throw new IOException($"Unable to open selected recovery slot. errno={Marshal.GetLastWin32Error()}");

        return new FileStream(
            new global::Microsoft.Win32.SafeHandles.SafeFileHandle(handle, ownsHandle: true),
            FileAccess.ReadWrite, MaxRecoveryJournalBytes, isAsync: false);
    }

    private static void SyncAnchoredDirectory(string canonicalRoot, string relativePath)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Anchored directory sync is Linux-only.");

        var rootFd = OpenDirectoryForMutation(canonicalRoot);
        var directoryFd = rootFd;
        try
        {
            var device = ValidateOpenedDirectoryDescriptor(rootFd, canonicalRoot, expectedDevice: null);
            directoryFd = string.IsNullOrWhiteSpace(relativePath)
                ? rootFd
                : OpenDirectoryChain(canonicalRoot, rootFd, device, relativePath,
                    createDirectories: false, invokeMutationHook: false);
            if (SyncDirectoryFd(directoryFd) != 0)
                throw new IOException($"Failed to fsync anchored directory '{relativePath}'.");
        }
        finally
        {
            if (directoryFd != rootFd)
                _ = CloseDirectoryHandle(directoryFd);
            _ = CloseDirectoryHandle(rootFd);
        }
    }

    private static void SyncDirectory(string path)
    {
        if (string.IsNullOrEmpty(path))
            return;

        if (!Directory.Exists(path))
            return;

        if (OperatingSystem.IsWindows())
        {
            if (!SyncDirectoryWindows(path))
                throw new IOException($"Directory fsync failed for '{path}'.");
            return;
        }

        if (!SyncDirectoryUnix(path))
        {
            if (OperatingSystem.IsLinux())
                throw new InvalidOperationException($"Directory fsync failed for '{path}'.");

            // Fall back for non-Linux/Unix-like platforms where direct directory
            // handles are not available.
            using var directory = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.None);
            directory.Flush(true);
        }
    }

    private static bool SyncDirectoryWindows(string path)
    {
        const int GenericWrite = 0x40000000;
        const int ShareRead = 1;
        const int ShareWrite = 2;
        const int ShareDelete = 4;
        const int OpenExisting = 3;
        const int FileFlagBackupSemantics = 0x02000000;
        const int FileAttributeNormal = 0x00000080;

        var handle = OpenDirectoryHandleWindows(
            path,
            GenericWrite,
            ShareRead | ShareWrite | ShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileAttributeNormal | FileFlagBackupSemantics,
            IntPtr.Zero);

        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            return false;

        try
        {
            return FlushFileBuffers(handle);
        }
        finally
        {
            _ = CloseDirectoryHandleWindows(handle);
        }
    }

    private static void SyncFile(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        file.Flush(true);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        IntPtr fileHandle,
        out WindowsFileInformation information);

    private const int WindowsFileRenameInfo = 3;
    private const int WindowsFileDispositionInfo = 4;
    private const int WindowsFileAttributeNormal = 0x00000080;

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsFileDispositionInformation
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool DeleteFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        IntPtr fileHandle,
        int fileInformationClass,
        ref WindowsFileDispositionInformation fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "SetFileInformationByHandle")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        IntPtr fileHandle,
        int fileInformationClass,
        IntPtr fileInformation,
        uint bufferSize);


    private static FileIdentity GetFileIdentityWindows(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle.DangerousGetHandle(), out var information))
            throw new IOException($"Failed to identify file handle. errno={Marshal.GetLastWin32Error()}");

        return new FileIdentity(
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);
    }

    private static void DeleteWindowsFileHandle(
        FileStream file,
        string path,
        int maxBytes,
        FileIdentity? identity,
        string expectedSha256,
        Action<string>? mutationHook)
    {
        var actualIdentity = GetFileIdentityWindows(file.SafeFileHandle);
        if (identity is not null && actualIdentity != identity.Value)
            throw new IOException($"Refusing to delete replaced path '{path}'.");
        var bytes = ReadBoundedBytes(file, maxBytes);
        if (bytes is null
            || !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"Content proof changed before deleting '{path}'.");
        mutationHook?.Invoke(path);
        var disposition = new WindowsFileDispositionInformation { DeleteFile = true };
        if (!SetFileInformationByHandle(
                file.SafeFileHandle.DangerousGetHandle(),
                WindowsFileDispositionInfo,
                ref disposition,
                (uint)Marshal.SizeOf<WindowsFileDispositionInformation>()))
            throw new IOException($"Failed to retire owned path '{path}'. errno={Marshal.GetLastWin32Error()}");
    }

    private static void DeleteOpenedWindowsFile(
        string path,
        int maxBytes,
        FileIdentity expectedIdentity,
        string expectedSha256,
        Action<string>? mutationHook)
    {
        if (!File.Exists(path))
            return;
        var rawHandle = OpenDirectoryHandleWindows(
            path,
            unchecked((int)0xC0000000) | 0x00010000,
            (int)(FileShare.ReadWrite | FileShare.Delete),
            IntPtr.Zero,
            3,
            WindowsFileAttributeNormal,
            IntPtr.Zero);
        if (rawHandle == IntPtr.Zero || rawHandle == new IntPtr(-1))
            throw new IOException($"Cannot open owned path for retirement '{path}'. errno={Marshal.GetLastWin32Error()}");
        using var safeHandle = new Microsoft.Win32.SafeHandles.SafeFileHandle(rawHandle, ownsHandle: true);
        using var file = new FileStream(safeHandle, FileAccess.ReadWrite, 16 * 1024, isAsync: false);
        if (GetFileIdentityWindows(file.SafeFileHandle) != expectedIdentity)
            throw new IOException($"Refusing to delete replaced path '{path}'.");
        var bytes = ReadBoundedBytes(file, maxBytes);
        if (bytes is null
            || !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"Content proof changed before deleting '{path}'.");
        mutationHook?.Invoke(path);
        var disposition = new WindowsFileDispositionInformation { DeleteFile = true };
        if (!SetFileInformationByHandle(
                file.SafeFileHandle.DangerousGetHandle(),
                WindowsFileDispositionInfo,
                ref disposition,
                (uint)Marshal.SizeOf<WindowsFileDispositionInformation>()))
            throw new IOException($"Failed to retire owned path '{path}'. errno={Marshal.GetLastWin32Error()}");
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    private static extern IntPtr OpenDirectoryHandleWindows(
        string path,
        int desiredAccess,
        int shareMode,
        IntPtr securityAttributes,
        int creationDisposition,
        int flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(IntPtr hFile);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "CloseHandle")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDirectoryHandleWindows(IntPtr handle);

    [DllImport("libc", SetLastError = true, EntryPoint = "open")]
    private static extern int OpenPath(string pathname, int flags, int mode);

    [DllImport("libc", SetLastError = true, EntryPoint = "openat")]
    private static extern int OpenAt(int dirFd, string pathname, int flags, int mode);

    [DllImport("libc", SetLastError = true, EntryPoint = "syscall")]
    private static extern long LinuxSyscall(long number, int dirFd, string pathname, ref LinuxOpenHow how, ulong size);

    [DllImport("libc", SetLastError = true, EntryPoint = "syscall")]
    private static extern long LinuxRenameAt2(
        long number, int oldDirFd, string oldPath, int newDirFd, string newPath, uint flags);

    [DllImport("libc", SetLastError = true, EntryPoint = "fstat")]
    private static extern int FStat(int fd, IntPtr stat);

    [DllImport("libc", SetLastError = true, EntryPoint = "mkdirat")]
    private static extern int MakeDirectoryAt(int dirFd, string pathname, int mode);

    [DllImport("libc", SetLastError = true, EntryPoint = "linkat")]
    private static extern int LinkAt(int oldDirFd, string oldPath, int newDirFd, string newPath, int flags);

    [DllImport("libc", SetLastError = true, EntryPoint = "fsync")]
    private static extern int SyncDirectoryHandle(int fd);

    [DllImport("libc", SetLastError = true, EntryPoint = "close")]
    private static extern int CloseDirectoryHandle(int fd);

    private const int OpenPathModeDirectoryCreate = 493;
    private const int OpenPathModeFileCreate = 384;

    // These are Linux ABI values, not portable .NET constants. In particular,
    // musl's arm64 values differ from x64 for O_NOFOLLOW and O_DIRECTORY.
    private static readonly LinuxOpenFlags LinuxOpenFlagsLayout = ResolveLinuxOpenFlags();

    private static LinuxOpenFlags ResolveLinuxOpenFlags()
    {
        if (!OperatingSystem.IsLinux())
            return new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, "n/a");

        return LinuxOpenFlagsFor(RuntimeInformation.OSArchitecture);
    }

    internal static LinuxOpenFlags LinuxOpen => LinuxOpenFlagsLayout;

    internal static LinuxOpenFlags LinuxOpenFlagsFor(Architecture architecture) => architecture switch
    {
        // O_TMPFILE is O_TMPFILE_BASE | O_DIRECTORY. O_DIRECTORY differs
        // between the two supported Linux ABIs, so do not reuse the x64 value
        // on arm64.
        Architecture.X64 => new(0, 1, 2, 0x20000, 0x10000, 0x40, 0x80, 0x200, 0x80000, 0x410000, "x64"),
        Architecture.Arm64 => new(0, 1, 2, 0x8000, 0x4000, 0x40, 0x80, 0x200, 0x80000, 0x404000, "arm64"),
        _ => throw new NotSupportedException($"Unsupported Linux architecture '{architecture}'.")
    };

    internal readonly record struct LinuxOpenFlags(
        int ReadOnly, int WriteOnly, int ReadWrite, int NoFollow, int Directory, int Create,
        int Exclusive, int Truncate, int CloseOnExec, int TmpFile, string Architecture);

    private const int LinuxFStatDeviceOffset = 0;
    private const uint LinuxModeMask = 0xF000;
    private const uint LinuxModeDirectory = 0x4000;
    private const uint LinuxModeRegular = 0x8000;

    private const int ErrnoNoEnt = 2;
    private const int ErrnoExist = 17;
    private const int ErrnoInvalid = 22;
    private const int ErrnoNoSys = 38;
    private const int ErrnoOpNotSupp = 95;
    private const int LinuxAtFdcwd = -100;
    // O_PATH is octal 010000000 (0x200000) on the supported Linux ABIs.
    private const int LinuxOpenPath = 0x200000;
    private const long LinuxSyscallOpenAt2 = 437;
    private const long LinuxSyscallRenameAt2X64 = 316;
    private const long LinuxSyscallRenameAt2Arm64 = 276;
    private const uint RenameNoReplace = 1;
    private const uint RenameExchange = 2;
    // AT_SYMLINK_FOLLOW is the capability-free linkat mode used with
    // /proc/self/fd/<fd>; this is 0x400 on both x64 and arm64.
    private const int LinkAtSymlinkFollow = 0x400;
    // O_TMPFILE is part of LinuxOpenFlags because its value includes the ABI's
    // O_DIRECTORY bit. It creates an inode with no pathname; closing the last
    // descriptor discards it.
    private const ulong ResolveNoXdev = 0x01;
    private const ulong ResolveNoSymlinks = 0x04;
    private const ulong ResolveBeneath = 0x08;

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxOpenHow
    {
        public ulong Flags;
        public ulong Mode;
        public ulong Resolve;
    }
    private const int LinuxStatSizeX64 = 144;
    private const int LinuxStatSizeArm64 = 128;

    private static readonly LinuxStatLayout LinuxAbiLayout = ResolveLinuxStatLayout();
    private static LinuxStatLayout ResolveLinuxStatLayout()
    {
        if (!OperatingSystem.IsLinux())
            return new(0, 0, 0, 0, "n/a");

        if (RuntimeInformation.OSArchitecture == Architecture.X64)
            return new(LinuxStatSizeX64, 24, 0, 8, "x64");

        if (RuntimeInformation.OSArchitecture == Architecture.Arm64)
            return new(LinuxStatSizeArm64, 16, 0, 8, "arm64");

        throw new NotSupportedException($"Unsupported Linux architecture '{RuntimeInformation.OSArchitecture}'.");
    }

    internal static LinuxStatLayout LinuxAbi => LinuxAbiLayout;
    internal static int FstatBufferSize => LinuxAbiLayout.FstatBufferSize;
    internal static int FstatModeOffset => LinuxAbiLayout.FstatModeOffset;
    internal static int FstatDeviceOffset => LinuxAbiLayout.FstatDeviceOffset;

    internal readonly record struct LinuxStatLayout(
        int FstatBufferSize,
        int FstatModeOffset,
        int FstatDeviceOffset,
        int FstatInodeOffset,
        string Architecture);

    internal static void ValidateOpenAtPathComponent(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Path component cannot be null or whitespace.");

        if (path == "." || path == "..")
            throw new InvalidOperationException("Path component cannot be '.' or '..'.");

        if (path.IndexOfAny(['/', '\\']) >= 0)
        {
            throw new InvalidOperationException("Path component cannot include separators.");
        }

        if (path.Contains('\u0000'))
            throw new InvalidOperationException("Path component cannot contain null bytes.");
    }

    private static HeldLinuxFile OpenHeldLinuxFile(string canonicalRoot, string fullPath, ulong expectedDevice)
    {
        var relative = Path.GetRelativePath(canonicalRoot, fullPath);
        var directory = Path.GetDirectoryName(relative);
        var name = Path.GetFileName(relative);
        if (string.IsNullOrWhiteSpace(name))
            throw new IOException($"Cannot hold identity for '{fullPath}'.");

        var rootFd = OpenDirectoryForMutation(canonicalRoot);
        var directoryFd = rootFd;
        var fileFd = -1;
        try
        {
            directoryFd = string.IsNullOrWhiteSpace(directory)
                ? rootFd
                : OpenDirectoryChain(canonicalRoot, rootFd, expectedDevice, directory, createDirectories: false, invokeMutationHook: false);
            fileFd = OpenReadableFileAt(directoryFd, expectedDevice, name, fullPath, invokeMutationHook: false);
            if (fileFd < 0)
                throw new IOException($"Cannot hold identity for '{fullPath}'. errno={Marshal.GetLastWin32Error()}");

            var identity = GetFileIdentity(fileFd);
            var held = new HeldLinuxFile(rootFd, directoryFd, fileFd, identity);
            // Ownership of all three descriptors has transferred to HeldLinuxFile.
            rootFd = -1;
            directoryFd = -1;
            fileFd = -1;
            return held;
        }
        catch
        {
            if (fileFd >= 0)
                _ = CloseDirectoryHandle(fileFd);
            if (directoryFd >= 0 && directoryFd != rootFd)
                _ = CloseDirectoryHandle(directoryFd);
            if (rootFd >= 0)
                _ = CloseDirectoryHandle(rootFd);
            throw;
        }
    }

    private static bool PathHasIdentity(int directoryFd, ulong rootDevice, string name, string fullPath, FileIdentity expected)
    {
        var fd = OpenReadableFileAt(directoryFd, rootDevice, name, fullPath, invokeMutationHook: false);
        if (fd < 0)
            return false;
        try { return GetFileIdentity(fd) == expected; }
        finally { _ = CloseDirectoryHandle(fd); }
    }

    private static int OpenDirectoryForMutation(string root)
    {
        // openat(2)+st_dev cannot detect a bind mount on the same device. Linux
        // targets therefore require openat2(2); silently falling back would turn
        // a containment failure into an unsafe write.
        var fd = OpenAt2(
            LinuxAtFdcwd,
            root,
            LinuxOpen.ReadOnly | LinuxOpen.Directory | LinuxOpen.NoFollow | LinuxOpen.CloseOnExec,
            0,
            ResolveNoSymlinks);
        if (fd < 0)
            throw new IOException($"Failed to open library root '{root}'. errno={Marshal.GetLastWin32Error()}");

        try
        {
            _ = ValidateOpenedDirectoryDescriptor(fd, root, expectedDevice: null);
            return fd;
        }
        catch
        {
            _ = CloseDirectoryHandle(fd);
            throw;
        }
    }

    private static int OpenDirectoryChain(
        string root,
        int rootFd,
        ulong rootDevice,
        string relativePath,
        bool createDirectories,
        bool invokeMutationHook = true)
    {
        var currentFd = rootFd;
        var currentPath = root;
        try
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                return currentFd;

            foreach (var segment in relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Where(s => !string.IsNullOrWhiteSpace(s)))
            {
                ValidateOpenAtPathComponent(segment);
                var nextPath = Path.Combine(currentPath, segment);
                var nextFd = OpenDirectoryAt(currentFd, rootDevice, segment, nextPath, LinuxOpen.ReadOnly | LinuxOpen.Directory | LinuxOpen.NoFollow | LinuxOpen.CloseOnExec);
                if (nextFd < 0)
                {
                    var errno = Marshal.GetLastWin32Error();
                    if (!createDirectories || errno != ErrnoNoEnt)
                        throw new IOException($"Failed to open '{Path.Combine(currentPath, segment)}' while traversing library. errno={errno}");

                    if (MakeDirectoryAt(currentFd, segment, OpenPathModeDirectoryCreate) != 0)
                        throw new IOException($"Failed to create '{Path.Combine(currentPath, segment)}' while traversing library. errno={Marshal.GetLastWin32Error()}");

                    // mkdir is not durable until its parent is committed.
                    if (SyncDirectoryFd(currentFd) != 0)
                        throw new IOException($"Failed to fsync parent directory '{currentPath}'.");

                    nextFd = OpenDirectoryAt(currentFd, rootDevice, segment, nextPath, LinuxOpen.ReadOnly | LinuxOpen.Directory | LinuxOpen.NoFollow | LinuxOpen.CloseOnExec);
                    if (nextFd < 0)
                        throw new IOException($"Failed to reopen '{Path.Combine(currentPath, segment)}' after creating it. errno={Marshal.GetLastWin32Error()}");
                }

                if (currentFd != rootFd)
                    _ = CloseDirectoryHandle(currentFd);

                currentPath = nextPath;
                currentFd = nextFd;
            }

            return currentFd;
        }
        catch
        {
            // The caller owns rootFd, but this method owns every intermediate
            // descriptor it has acquired, including on a failed next component.
            if (currentFd != rootFd)
                _ = CloseDirectoryHandle(currentFd);
            throw;
        }
    }

    private static int OpenDirectoryAt(int dirFd, ulong rootDevice, string name, string fullPath, int flags)
    {
        return OpenAtNoFollow(dirFd, name, rootDevice, fullPath, flags, 0, LinuxModeDirectory);
    }

    private static int OpenWritableFileAt(int dirFd, ulong rootDevice, string name, string fullPath, bool isNew)
    {
        var flags = LinuxOpen.WriteOnly | LinuxOpen.NoFollow | LinuxOpen.CloseOnExec;
        if (isNew)
            flags |= LinuxOpen.Create | LinuxOpen.Exclusive | LinuxOpen.Truncate;

        return OpenAtNoFollow(dirFd, name, rootDevice, fullPath, flags, OpenPathModeFileCreate, LinuxModeRegular);
    }

    private static int OpenAnonymousWritableFileAt(
        int dirFd,
        ulong rootDevice,
        string directoryPath,
        bool readWrite = false)
    {
        var accessMode = readWrite ? LinuxOpen.ReadWrite : LinuxOpen.WriteOnly;
        var flags = accessMode | LinuxOpen.CloseOnExec | LinuxOpen.TmpFile;
        var fd = OpenAt2(dirFd, ".", flags, (ulong)OpenPathModeFileCreate, ResolveBeneath | ResolveNoSymlinks | ResolveNoXdev);
        if (fd < 0)
            return fd;

        try
        {
            _ = ValidateOpenedDescriptor(fd, directoryPath, LinuxModeRegular, rootDevice);
            return fd;
        }
        catch
        {
            _ = CloseDirectoryHandle(fd);
            throw;
        }
    }

    private static bool IsAnonymousTemporaryUnsupported(int errno)
        => errno is ErrnoInvalid or ErrnoOpNotSupp or 95 /* ENOTSUP on some libc headers */;

    private static int OpenReadWriteFileAt(int dirFd, ulong rootDevice, string name, string fullPath)
        => OpenAtNoFollow(
            dirFd,
            name,
            rootDevice,
            fullPath,
            LinuxOpen.ReadWrite | LinuxOpen.NoFollow | LinuxOpen.CloseOnExec,
            0,
            LinuxModeRegular);

    private static int OpenReadableFileAt(
        int dirFd,
        ulong rootDevice,
        string name,
        string fullPath,
        bool invokeMutationHook = true)
    {
        return OpenAtNoFollow(
            dirFd,
            name,
            rootDevice,
            fullPath,
            LinuxOpen.ReadOnly | LinuxOpen.NoFollow | LinuxOpen.CloseOnExec,
            0,
            LinuxModeRegular,
            invokeMutationHook);
    }

    private static int OpenPathFileAt(
        int dirFd,
        ulong rootDevice,
        string name,
        string fullPath,
        bool invokeMutationHook = true)
    {
        return OpenAtNoFollow(
            dirFd,
            name,
            rootDevice,
            fullPath,
            LinuxOpenPath | LinuxOpen.NoFollow | LinuxOpen.CloseOnExec,
            0,
            LinuxModeRegular,
            invokeMutationHook);
    }

    private static int OpenAtNoFollow(
        int dirFd,
        string path,
        ulong rootDevice,
        string fullPath,
        int flags,
        int mode,
        uint expectedMode,
        bool invokeMutationHook = true)
    {
        ValidateOpenAtPathComponent(path);

        // Every component and file operation is beneath the already-open root,
        // with symlinks and mount crossings forbidden by the kernel.
        var fd = OpenAt2(dirFd, path, flags, (ulong)mode, ResolveBeneath | ResolveNoSymlinks | ResolveNoXdev);
        if (fd < 0)
            return fd;

        try
        {
            _ = ValidateOpenedDescriptor(fd, fullPath, expectedMode, rootDevice);
            return fd;
        }
        catch
        {
            _ = CloseDirectoryHandle(fd);
            throw;
        }
    }

    private static ulong ValidateOpenedDescriptor(int fd, string fullPath, uint expectedMode, ulong rootDevice)
    {
        var mode = GetFileMode(fd, out var device);
        if ((mode & LinuxModeMask) != expectedMode)
            throw new IOException($"Path '{fullPath}' has unexpected file type.");

        if (device != rootDevice)
            throw new IOException($"Path '{fullPath}' is on unexpected filesystem.");

        return device;
    }

    private static ulong ValidateOpenedDirectoryDescriptor(int fd, string fullPath, ulong? expectedDevice)
    {
        return ValidateOpenedDescriptor(fd, fullPath, LinuxModeDirectory, expectedDevice ?? GetFileDevice(fd));
    }

    private static uint GetFileMode(int fd, out ulong device)
    {
        var buffer = Marshal.AllocHGlobal(FstatBufferSize);
        try
        {
            if (FStat(fd, buffer) != 0)
                throw new IOException($"Failed to fstat path '{fd}'. errno={Marshal.GetLastWin32Error()}");

            device = GetFileDeviceFromBuffer(buffer);
            return (uint)Marshal.ReadInt32(buffer, FstatModeOffset);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static ulong GetFileDeviceFromBuffer(IntPtr buffer)
    {
        if (IntPtr.Size == 8)
            return (ulong)Marshal.ReadInt64(buffer, FstatDeviceOffset);

        return (uint)Marshal.ReadInt32(buffer, FstatDeviceOffset);
    }

    private static ulong GetFileDevice(int fd)
    {
        _ = GetFileMode(fd, out var device);
        return device;
    }

    private static FileIdentity GetFileIdentity(int fd)
    {
        _ = GetFileMode(fd, out var device);
        var buffer = Marshal.AllocHGlobal(FstatBufferSize);
        try
        {
            if (FStat(fd, buffer) != 0)
                throw new IOException($"Failed to fstat file descriptor '{fd}'. errno={Marshal.GetLastWin32Error()}");
            return new FileIdentity(device, GetFileInodeFromBuffer(buffer));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static ulong GetFileInodeFromBuffer(IntPtr buffer)
    {
        if (IntPtr.Size == 8)
            return (ulong)Marshal.ReadInt64(buffer, LinuxAbiLayout.FstatInodeOffset);

        return (uint)Marshal.ReadInt32(buffer, LinuxAbiLayout.FstatInodeOffset);
    }

    private static int OpenAt2(int dirFd, string path, int flags, ulong mode, ulong resolve)
    {
        var how = new LinuxOpenHow
        {
            Flags = unchecked((ulong)(uint)flags),
            Mode = mode,
            Resolve = resolve
        };
        var result = LinuxSyscall(LinuxSyscallOpenAt2, dirFd, path, ref how, (ulong)Marshal.SizeOf<LinuxOpenHow>());
        if (result < 0)
        {
            var errno = Marshal.GetLastWin32Error();
            if (errno == ErrnoNoSys)
                throw new PlatformNotSupportedException("Linux openat2 is required for safe NZBDAV library mutation.");
            return -1;
        }

        return checked((int)result);
    }

    private static int SyncFd(int fd) => SyncDirectoryHandle(fd);

    private static int SyncDirectoryFd(int fd) => SyncDirectoryHandle(fd);

    private static int LinkFileAtNoReplace(int sourceFd, int destinationDirFd, string destinationName)
    {
        // AT_EMPTY_PATH requires CAP_DAC_READ_SEARCH for an unlinked inode.
        // The capability-free, verified path is the proc descriptor followed by
        // AT_SYMLINK_FOLLOW (0x400) on both supported ABIs.
        var procFdPath = $"/proc/self/fd/{sourceFd}";
        return LinkAt(LinuxAtFdcwd, procFdPath, destinationDirFd, destinationName, LinkAtSymlinkFollow);
    }

    private static long LinuxRenameAt2Number
        => RuntimeInformation.OSArchitecture == Architecture.Arm64
            ? LinuxSyscallRenameAt2Arm64
            : LinuxSyscallRenameAt2X64;

    private static int RenameNoReplaceAt(int oldDirFd, string oldName, int newDirFd, string newName)
        => checked((int)LinuxRenameAt2(LinuxRenameAt2Number, oldDirFd, oldName, newDirFd, newName, RenameNoReplace));

    private static int RenameExchangeAt(int oldDirFd, string oldName, int newDirFd, string newName)
        => checked((int)LinuxRenameAt2(LinuxRenameAt2Number, oldDirFd, oldName, newDirFd, newName, RenameExchange));

    private static bool SyncDirectoryUnix(string path)
    {
        var fd = OpenPath(path, LinuxOpen.ReadOnly | LinuxOpen.Directory | LinuxOpen.NoFollow | LinuxOpen.CloseOnExec, 0);
        if (fd < 0)
            return false;

        try
        {
            return SyncDirectoryHandle(fd) == 0;
        }
        finally
        {
            _ = CloseDirectoryHandle(fd);
        }
    }

    private static string ResolveContentPathWithinLibrary(string libraryRoot, string relativePath)
    {
        var root = GetCanonicalLibraryRoot(libraryRoot);
        var destination = Path.GetFullPath(Path.Combine(root, relativePath));

        if (!IsPathWithinRoot(root, destination))
            throw new InvalidOperationException("Stream path resolves outside library root.");

        if (ContainsSymlinkInPath(root, destination))
            throw new InvalidOperationException("Stream path traverses filesystem reparse point.");

        return destination;
    }

    private static string BuildSafeProbePath(string strmPath)
    {
        return Path.ChangeExtension(strmPath, ".mediainfo.json");
    }

    private static string CreateRunId()
    {
        return DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff'Z'", CultureInfo.InvariantCulture)
               + "-"
               + Guid.NewGuid().ToString("N")[..8];
    }

    private static bool IsValidRunId(string? runId)
    {
        if (string.IsNullOrEmpty(runId) || runId.Length != 28
            || runId[8] != '-' || runId[18] != 'Z' || runId[19] != '-')
            return false;

        for (var i = 0; i < runId.Length; i++)
        {
            if (i is 8 or 18 or 19)
                continue;
            var c = runId[i];
            var decimalPart = i < 18;
            if (decimalPart
                ? c is < '0' or > '9'
                : !Uri.IsHexDigit(c))
                return false;
        }

        return true;
    }

    private static bool IsVideoFile(string filename)
    {
        var ext = Path.GetExtension(filename)?.ToLowerInvariant();
        return ext is ".mkv" or ".mp4" or ".avi" or ".mov" or ".wmv" or ".flv"
            or ".m4v" or ".ts" or ".m2ts" or ".webm" or ".mpg" or ".mpeg";
    }

    private static string BuildSafeStrmPath(string libraryPath, ManifestItem videoFile, IReadOnlyDictionary<Guid, ManifestItem> allItems)
    {
        var strmRelativePath = Path.ChangeExtension(BuildStrmRelativePath(videoFile, allItems), ".strm");
        return ResolveContentPathWithinLibrary(libraryPath, strmRelativePath);
    }

    private static string BuildStrmRelativePath(ManifestItem videoFile, IReadOnlyDictionary<Guid, ManifestItem> allItems)
    {
        var relativePath = ValidateSafeRelativePath(videoFile.Path, isManifestPath: true);

        var fileName = Path.GetFileNameWithoutExtension(relativePath);
        var ext = Path.GetExtension(relativePath);
        if (string.IsNullOrEmpty(ext))
            ext = Path.GetExtension(videoFile.Name);

        if (LooksObfuscated(fileName)
            && videoFile.ParentId.HasValue
            && allItems.TryGetValue(videoFile.ParentId.Value, out var parent)
            && parent.Type == "directory")
        {
            var parentDirectory = Path.GetDirectoryName(relativePath) ?? string.Empty;
            var normalizedParentName = ValidateManifestSegment(parent.Name, MaxManifestParentNameLength);
            relativePath = string.IsNullOrEmpty(parentDirectory)
                ? normalizedParentName + ext
                : Path.Combine(parentDirectory, normalizedParentName + ext);
        }

        return relativePath;
    }

    private static string ValidateSafeRelativePath(string manifestPath, bool isManifestPath)
    {
        return ValidateManifestPathInternal(manifestPath, isManifestPath);
    }

    private static string ValidateManifestPathInternal(string? manifestPath, bool isManifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
            throw new ArgumentException("Manifest path is empty.");

        // Preserve the backend spelling. Validation must reject aliases rather
        // than trimming/normalizing them into a different accepted name.
        var withoutPrefix = manifestPath;
        if (withoutPrefix.StartsWith('/') && isManifestPath)
        {
            if (withoutPrefix.Equals("/content", StringComparison.OrdinalIgnoreCase))
                withoutPrefix = string.Empty;
            else if (withoutPrefix.StartsWith("/content/", StringComparison.OrdinalIgnoreCase))
                withoutPrefix = withoutPrefix["/content/".Length..];
            else
                throw new ArgumentException("Manifest rooted path must begin with /content.");
        }

        if (string.IsNullOrWhiteSpace(withoutPrefix))
            throw new ArgumentException("Manifest path resolved to empty path.");

        withoutPrefix = withoutPrefix.TrimStart('/');

        var segments = withoutPrefix.Split('/', StringSplitOptions.None);
        if (segments.Length == 0 || segments.Any(static segment => segment.Length == 0))
            throw new ArgumentException("Manifest path has no valid segments.");

        if (segments.Length > MaxManifestPathSegments)
            throw new ArgumentException($"Manifest path has too many segments ({segments.Length}).");

        var normalizedSegments = new string[segments.Length];
        for (var i = 0; i < segments.Length; i++)
        {
            normalizedSegments[i] = ValidateManifestSegment(segments[i]);
        }

        var normalized = string.Join('/', normalizedSegments);
        if (normalized.Length > MaxManifestPathLength)
            throw new ArgumentException($"Manifest path exceeds maximum length ({MaxManifestPathLength}).");

        return normalized;
    }

    private static string ValidateManifestSegment(string segment)
    {
        return ValidateManifestSegment(segment, MaxManifestNameLength);
    }

    private static string ValidateManifestSegment(string segment, int maxLength)
    {
        // These are rejected on every platform because the manifest namespace
        // must be portable to Windows. Do not use Path.GetInvalidFileNameChars
        // alone: on Linux it deliberately accepts most of this set.
        if (string.IsNullOrWhiteSpace(segment))
            throw new ArgumentException("Manifest path segment is empty.");

        if (segment.Length > maxLength)
            throw new ArgumentException($"Manifest path segment exceeds max length ({maxLength}).");

        if (segment == "." || segment == "..")
            throw new ArgumentException("Manifest path contains traversal segment.");

        if (segment.IndexOfAny(['<', '>', ':', '"', '/', '\\', '|', '?', '*']) >= 0
            || segment.Any(static c => c <= '\u001f' || c == '\u007f'))
            throw new ArgumentException("Manifest path segment contains invalid filename characters.");

        if (segment[^1] is '.' or ' ')
            throw new ArgumentException("Manifest path segment cannot end with a period or space.");

        if (IsReservedRelativePathSegment(segment) || IsWindowsDeviceName(segment))
            throw new ArgumentException($"Manifest path segment '{segment}' is reserved.");

        // Return the exact accepted spelling. In particular, never trim a
        // trailing alias or normalize it into a collision with a sibling.
        return segment;
    }

    private static string BuildManagedMarkerPath(string strmPath) => strmPath + ManagedStrmMarkerSuffix;
    private static string BuildManagedIntentMarkerPath(string strmPath) => strmPath + ManagedIntentMarkerSuffix;

    private static string BuildManagedIntentContent(Guid itemId, string streamUrl, string? probeData)
    {
        // Kept for migration callers. New writers use the identity-bearing
        // overload below; a zero identity can never become completed ownership.
        var probeHash = probeData is null ? "none" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(probeData)));
        return BuildManagedIntentContent(itemId, Guid.NewGuid().ToString("N"), streamUrl,
            new FileIdentity(1, 1), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(streamUrl))),
            probeData is null ? null : new ProbeOwnership(new FileIdentity(1, 1), probeHash));
    }

    private static string BuildManagedIntentContent(
        Guid itemId, string operationId, string streamUrl, FileIdentity streamIdentity,
        string streamHash, ProbeOwnership? probe)
        => BuildManagedStateContent("intent", operationId, itemId, streamUrl, streamIdentity, streamHash, probe);

    private static string BuildManagedStateContent(
        string phase, string operationId, Guid itemId, string streamUrl, FileIdentity streamIdentity,
        string streamHash, ProbeOwnership? probe)
    {
        var encodedUrl = Convert.ToBase64String(Encoding.UTF8.GetBytes(streamUrl)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var probeDevice = probe?.Identity.Device ?? 0;
        var probeInode = probe?.Identity.Inode ?? 0;
        var probeHash = probe?.Sha256 ?? "none";
        var content = $"{ManagedMarkerHeader}/{(phase == "intent" ? "4i" : "4")}/{operationId}/{itemId:D}/{streamIdentity.Device}/{streamIdentity.Inode}/{streamHash}/{probeDevice}/{probeInode}/{probeHash}/{encodedUrl}";
        if (Encoding.UTF8.GetByteCount(content) > MaxManagedMarkerBytes)
            throw new IOException("Managed ownership record exceeds its bounded marker size.");
        return content;
    }

    private static bool TryDecodeManagedUrl(string encoded, out string url)
    {
        url = string.Empty;
        try
        {
            if (string.IsNullOrEmpty(encoded) || encoded.Length > MaxManagedStrmBytes)
                return false;
            var padded = encoded.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
            url = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(padded));
            return !string.IsNullOrEmpty(url);
        }
        catch { return false; }
    }

    private static bool TryReadManagedIntent(string libraryRoot, string markerPath, Guid? expectedItemId, out ManagedIntent intent)
    {
        intent = default;
        if (!TryReadBoundedFirstLineForPath(libraryRoot, markerPath, MaxManagedMarkerBytes, out var marker)
            || !IsStrictManagedRecord(marker)) return false;
        try
        {
            var parts = marker.Content.Split('/', StringSplitOptions.None);
            if (parts.Length != 11 || parts[0] != ManagedMarkerHeader || parts[1] != "4i"
                || !Guid.TryParseExact(parts[2], "N", out var operationId)
                || !Guid.TryParseExact(parts[3], "D", out var itemId)
                || (expectedItemId.HasValue && expectedItemId.Value != itemId)
                || !TryParseCanonicalUInt64(parts[4], out var streamDevice)
                || !TryParseCanonicalUInt64(parts[5], out var streamInode)
                || streamDevice == 0 || streamInode == 0 || !IsSha256(parts[6])
                || !TryParseCanonicalUInt64(parts[7], out var probeDevice)
                || !TryParseCanonicalUInt64(parts[8], out var probeInode)
                || !(parts[9] == "none" || IsSha256(parts[9]))
                || !TryDecodeManagedUrl(parts[10], out var streamUrl)) return false;
            var streamIdentity = new FileIdentity(streamDevice, streamInode);
            ProbeOwnership? probe = parts[9] == "none" ? null : new ProbeOwnership(new FileIdentity(probeDevice, probeInode), parts[9]);
            if (probe is null ? probeDevice != 0 || probeInode != 0 : probeDevice == 0 || probeInode == 0
                || !string.Equals(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(streamUrl))), parts[6], StringComparison.OrdinalIgnoreCase)) return false;
            intent = new ManagedIntent(marker, markerPath, operationId, itemId, streamUrl, parts[6], streamIdentity, probe);
            return true;
        }
        catch { intent = default; return false; }
    }

    private static string BuildManagedMarkerContent(string libraryRoot, Guid itemId, string strmPath, string probePath)
    {
        if (!TryReadBoundedFirstLineForPath(libraryRoot, strmPath, MaxManagedStrmBytes, out var strm)
            || !Uri.TryCreate(strm.Content, UriKind.Absolute, out var uri)
            || !TryGetStreamId(uri, out var urlId) || urlId != itemId)
            throw new IOException($"Cannot prove stream ownership for '{strmPath}'.");
        ProbeOwnership? probe = null;
        if (TryPathExistsAnchored(libraryRoot, probePath, out _))
        {
            if (!TryReadBoundedFileForPath(libraryRoot, probePath, MaxManagedProbeBytes, out var bytes, out var identity) || identity is null)
                throw new IOException($"Cannot prove probe ownership for '{strmPath}'.");
            probe = new ProbeOwnership(identity.Value, Convert.ToHexString(SHA256.HashData(bytes)));
        }
        return BuildManagedStateContent("complete", Guid.NewGuid().ToString("N"), itemId,
            strm.Content, strm.Identity ?? throw new IOException("Cannot prove stream identity."), strm.Sha256, probe);
    }

    private readonly record struct BoundedFirstLine(string Content, FileIdentity? Identity, byte[] Bytes)
    {
        public string Sha256 => Convert.ToHexString(SHA256.HashData(Bytes));
    }

    private readonly record struct ManagedIntent(
        BoundedFirstLine Marker, string MarkerPath, Guid OperationId, Guid ItemId,
        string StreamUrl, string StreamSha256, FileIdentity StreamIdentity, ProbeOwnership? Probe);

    private readonly record struct ProbeOwnership(FileIdentity Identity, string Sha256);
    private readonly record struct ManagedOwnership(
        BoundedFirstLine Marker, BoundedFirstLine Strm, Guid ItemId, ProbeOwnership? Probe,
        FileIdentity StreamIdentity, string StreamSha256, Guid OperationId);

    private static bool IsSuppressedProbeReplacement(string libraryRoot, string probePath)
    {
        try
        {
            var canonicalRoot = GetCanonicalLibraryRoot(libraryRoot);
            var recoveryRoot = Path.Combine(canonicalRoot, RecoveryDirectoryName);
            if (!TrySelectLatestRecoveryState(canonicalRoot, recoveryRoot, out var state, out _, out _, out _, out _, out _)
                || state.Phase != "suppressed"
                || state.StreamOldPresent is not false
                || string.IsNullOrWhiteSpace(state.StreamDestination))
                return false;

            var expectedPath = Path.GetFullPath(Path.Combine(canonicalRoot, state.StreamDestination));
            var actualPath = Path.GetFullPath(probePath);
            if (!IsPathWithinRoot(canonicalRoot, actualPath)
                || !string.Equals(
                    NormalizeRelativePathForComparison(Path.GetRelativePath(canonicalRoot, expectedPath)),
                    NormalizeRelativePathForComparison(Path.GetRelativePath(canonicalRoot, actualPath)),
                    StringComparison.OrdinalIgnoreCase))
                return false;

            return TryReadBoundedFileForPath(canonicalRoot, actualPath, MaxManagedProbeBytes, out var bytes, out var identity)
                && identity is not null
                && (identity.Value != new FileIdentity(state.StreamNewDevice, state.StreamNewInode)
                    || !string.Equals(
                        Convert.ToHexString(SHA256.HashData(bytes)), state.StreamNewSha256,
                        StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetManagedOwnership(
        string libraryRoot,
        string markerPath, string strmPath, Uri backendBase, Guid? expectedItemId,
        out ManagedOwnership ownership, bool completedOnly = false,
        bool allowSuppressedProbe = false, string? suppressedProbeRoot = null)
    {
        ownership = default;
        if (!TryReadBoundedFirstLineForPath(libraryRoot, markerPath, MaxManagedMarkerBytes, out var marker)
            || !IsStrictManagedRecord(marker)
            || !TryReadBoundedFirstLineForPath(libraryRoot, strmPath, MaxManagedStrmBytes, out var strm)) return false;
        try
        {
            var parts = marker.Content.Split('/', StringSplitOptions.None);
            if (parts.Length < 3 || parts[0] != ManagedMarkerHeader) return false;
            ProbeOwnership? probe = null;
            FileIdentity streamIdentity;
            string streamHash;
            Guid markerItemId;
            Guid operationId;
            if (parts.Length == 11 && parts[1] == "4")
            {
                if (!Guid.TryParseExact(parts[2], "N", out operationId))
                    return false;
                // v4 has operation id before manifest id.
                if (!Guid.TryParseExact(parts[3], "D", out markerItemId)
                    || (expectedItemId.HasValue && expectedItemId.Value != markerItemId)
                    || !TryParseCanonicalUInt64(parts[4], out var sd)
                    || !TryParseCanonicalUInt64(parts[5], out var si)
                    || !IsSha256(parts[6]) || !TryParseCanonicalUInt64(parts[7], out var pd)
                    || !TryParseCanonicalUInt64(parts[8], out var pi)
                    || !(parts[9] == "none" || IsSha256(parts[9])) || !TryDecodeManagedUrl(parts[10], out var url)
                    || !string.Equals(strm.Content, url, StringComparison.Ordinal)
                    || !string.Equals(strm.Sha256, parts[6], StringComparison.OrdinalIgnoreCase)
                    || !Uri.TryCreate(url, UriKind.Absolute, out var streamUri)
                    || !TryGetStreamId(streamUri, out var urlId) || urlId != markerItemId
                    || ClassifyManagedStreamUrl(url, backendBase, markerItemId, out _) is not (ManagedStreamUrlType.CanonicalToken or ManagedStreamUrlType.LegacyApiKey)) return false;
                streamIdentity = new FileIdentity(sd, si); streamHash = parts[6];
                if (strm.Identity != streamIdentity) return false;
                if (parts[9] == "none")
                {
                    if (pd != 0 || pi != 0)
                        return false;
                    var probePath = BuildSafeProbePath(strmPath);
                    if (TryPathExistsAnchored(libraryRoot, probePath, out _)
                        && (!allowSuppressedProbe
                            || suppressedProbeRoot is null
                            || !IsSuppressedProbeReplacement(suppressedProbeRoot, probePath)))
                        return false;
                }
                else
                {
                    if (pd == 0 || pi == 0) return false;
                    var probePath = BuildSafeProbePath(strmPath);
                    if (!TryReadBoundedFileForPath(libraryRoot, probePath, MaxManagedProbeBytes, out var bytes, out var probeIdentity)
                        || probeIdentity is null || probeIdentity.Value != new FileIdentity(pd, pi)
                        || !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), parts[9], StringComparison.OrdinalIgnoreCase)) return false;
                    probe = new ProbeOwnership(probeIdentity.Value, parts[9]);
                }
                ownership = new ManagedOwnership(marker, strm, markerItemId, probe, streamIdentity, streamHash, operationId);
                return true;
            }
            // v2 is a migration input only. It must bind its manifest id to the
            // URL, but is never accepted by provider/reconciliation callers.
            if (completedOnly || parts.Length != 6 || parts[1] != "2" || !Guid.TryParseExact(parts[2], "D", out markerItemId)
                || (expectedItemId.HasValue && expectedItemId.Value != markerItemId)) return false;
            if (!Uri.TryCreate(strm.Content, UriKind.Absolute, out var legacyUri)
                || !TryGetStreamId(legacyUri, out var legacyId) || legacyId != markerItemId
                || ClassifyManagedStreamUrl(strm.Content, backendBase, markerItemId, out _) is not (ManagedStreamUrlType.CanonicalToken or ManagedStreamUrlType.LegacyApiKey)) return false;
            streamIdentity = strm.Identity ?? throw new IOException("Missing stream identity.");
            streamHash = strm.Sha256; operationId = Guid.NewGuid();
            if (!TryParseCanonicalUInt64(parts[3], out var legacyProbeDevice)
                || !TryParseCanonicalUInt64(parts[4], out var legacyProbeInode)) return false;
            if (parts[5] == "none")
            {
                // `none` is a complete no-probe claim, not a bypass for the
                // identity fields. Canonical zeroes are mandatory.
                if (legacyProbeDevice != 0 || legacyProbeInode != 0
                    || TryPathExistsAnchored(libraryRoot, BuildSafeProbePath(strmPath), out _)) return false;
            }
            else
            {
                if (legacyProbeDevice == 0 || legacyProbeInode == 0 || !IsSha256(parts[5])) return false;
                var probePath = BuildSafeProbePath(strmPath);
                if (!TryReadBoundedFileForPath(libraryRoot, probePath, MaxManagedProbeBytes, out var bytes, out var probeIdentity)
                    || probeIdentity is null || probeIdentity.Value != new FileIdentity(legacyProbeDevice, legacyProbeInode)
                    || !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), parts[5], StringComparison.OrdinalIgnoreCase)) return false;
                probe = new ProbeOwnership(probeIdentity.Value, parts[5]);
            }
            ownership = new ManagedOwnership(marker, strm, markerItemId, probe, streamIdentity, streamHash, operationId);
            return true;
        }
        catch { ownership = default; return false; }
    }

    private static BoundedFirstLine? TryReadBoundedFirstLine(
        string libraryRoot,
        string path,
        int maxBytes)
        => TryReadBoundedFirstLineForPath(libraryRoot, path, maxBytes, out var result) ? result : null;

    private static bool TryReadBoundedFirstLineForPath(
        string libraryRoot, string path, int maxBytes, out BoundedFirstLine result)
    {
        result = default;
        try
        {
            if (!TryReadBoundedFileForPath(libraryRoot, path, maxBytes, out var fileBytes, out var identity))
                return false;

            var lineLength = Array.IndexOf(fileBytes, (byte)'\n');
            if (lineLength < 0)
                lineLength = fileBytes.Length;
            if (lineLength > maxBytes)
                return false;
            if (lineLength > 0 && fileBytes[lineLength - 1] == '\r')
                lineLength--;

            var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(fileBytes, 0, lineLength);
            result = new BoundedFirstLine(text, identity, fileBytes);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadBoundedFileForPath(
        string libraryRoot,
        string path,
        int maxBytes,
        out byte[] bytes,
        out FileIdentity? identity)
    {
        bytes = [];
        identity = null;
        if (maxBytes < 0)
            return false;

        try
        {
            var canonicalRoot = GetCanonicalLibraryRoot(libraryRoot);
            var canonicalPath = Path.GetFullPath(path);
            if (!IsPathWithinRoot(canonicalRoot, canonicalPath)
                || ContainsSymlinkInPath(canonicalRoot, canonicalPath))
                return false;

            if (!OperatingSystem.IsLinux())
            {
                using var stream = new FileStream(canonicalPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 16 * 1024, FileOptions.SequentialScan);
                bytes = ReadBoundedBytes(stream, maxBytes) ?? [];
                if (bytes.Length == 0 && stream.Length != 0)
                    return false;
                identity = GetFileIdentityWindows(stream.SafeFileHandle);
                return true;
            }

            // Never rebase at the immediate parent. Open the configured root
            // once, then resolve every directory component and the final file
            // relative to that descriptor with openat2 beneath/no-symlink/no-xdev.
            var relative = Path.GetRelativePath(canonicalRoot, canonicalPath);
            var directory = Path.GetDirectoryName(relative);
            var name = Path.GetFileName(relative);
            if (string.IsNullOrWhiteSpace(name))
                return false;

            var rootFd = OpenDirectoryForMutation(canonicalRoot);
            var directoryFd = rootFd;
            try
            {
                var rootDevice = ValidateOpenedDirectoryDescriptor(rootFd, canonicalRoot, expectedDevice: null);
                directoryFd = string.IsNullOrWhiteSpace(directory)
                    ? rootFd
                    : OpenDirectoryChain(canonicalRoot, rootFd, rootDevice, directory,
                        createDirectories: false, invokeMutationHook: false);
                var fileFd = OpenReadableFileAt(directoryFd, rootDevice, name, canonicalPath,
                    invokeMutationHook: false);
                if (fileFd < 0)
                    return false;
                try
                {
                    using var stream = new FileStream(
                        new global::Microsoft.Win32.SafeHandles.SafeFileHandle((IntPtr)fileFd, ownsHandle: false),
                        FileAccess.Read, 16 * 1024, isAsync: false);
                    bytes = ReadBoundedBytes(stream, maxBytes) ?? [];
                    if (bytes.Length == 0 && GetFileSize(fileFd) != 0)
                        return false;
                    identity = GetFileIdentity(fileFd);
                    return true;
                }
                finally
                {
                    _ = CloseDirectoryHandle(fileFd);
                }
            }
            finally
            {
                if (directoryFd != rootFd)
                    _ = CloseDirectoryHandle(directoryFd);
                _ = CloseDirectoryHandle(rootFd);
            }
        }
        catch
        {
            bytes = [];
            identity = null;
            return false;
        }
    }

    private static bool IsReservedRelativePathSegment(string segment)
    {
        const StringComparison comparison = StringComparison.OrdinalIgnoreCase;

        foreach (var reserved in ReservedRelativePathSegments)
        {
            if (string.Equals(segment, reserved, comparison))
                return true;
        }

        return false;
    }

    private static bool IsWindowsDeviceName(string segment)
    {
        var dotIndex = segment.IndexOf('.');
        var baseName = dotIndex >= 0 ? segment[..dotIndex] : segment;
        baseName = baseName.TrimEnd('.', ' ');
        return baseName.Length != 0
            && WindowsDeviceNames.Contains(baseName, StringComparer.OrdinalIgnoreCase);
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
