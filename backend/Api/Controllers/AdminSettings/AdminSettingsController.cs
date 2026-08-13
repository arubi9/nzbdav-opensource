using System.Collections.Frozen;
using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using Microsoft.EntityFrameworkCore.Storage;
using NzbWebDAV.Api.Controllers;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Setup.Core;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.Controllers.AdminSettings;

/// <summary>Authenticated, browser-safe settings boundary.</summary>
[ApiController]
[Route("api/admin-settings")]
public sealed class AdminSettingsController(
    DavDatabaseClient dbClient,
    ConfigManager configManager,
    ConfigEncryptionService encryption,
    Func<IsolationLevel, CancellationToken, Task<IDbContextTransaction>>? beginTransaction = null,
    Func<DavDatabaseContext>? freshContextFactory = null)
    : BaseApiController
{
    private readonly Func<IsolationLevel, CancellationToken, Task<IDbContextTransaction>> _beginTransaction =
        beginTransaction ?? ((isolation, cancellationToken) =>
            dbClient.Ctx.Database.BeginTransactionAsync(isolation, cancellationToken));
    private readonly Func<DavDatabaseContext> _freshContextFactory =
        freshContextFactory ?? (() => new DavDatabaseContext());
    private const int MaxItems = 128;
    private const int MaxValueLength = 64 * 1024;
    private const int MaxRequestBodyLength = 8 * 1024 * 1024;
    private const int MaxJsonDepth = 16;
    private const int MaxHostLength = 2048;
    private const int MaxApiKeyLength = 4096;
    private const int MaxQueueMessageLength = 4096;
    // This is intentionally an allowlist rather than a denylist. ConfigItems
    // also contains setup state, provider records, and extension data which
    // must never cross this browser boundary.
    private static readonly FrozenSet<string> PublicKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "general.base-url", "api.categories", "api.manual-category", "api.ensure-importable-video",
        "api.ensure-article-existence-categories", "api.ignore-history-limit", "api.download-file-blocklist",
        "api.duplicate-nzb-behavior", "api.import-strategy", "api.completed-downloads-dir", "api.user-agent",
        "usenet.max-download-connections", "usenet.streaming-priority", "usenet.article-buffer-size",
        "webdav.user", "webdav.show-hidden-files", "webdav.enforce-readonly", "webdav.preview-par2-files",
        "rclone.mount-dir", "media.library-dir", "repair.enable", "cache.max-size-gb", "cache.max-age-hours",
        "cache.directory", "cache.precache-enable", "cache.precache-max-file-size-mb",
        "cache.read-ahead-enable", "cache.read-ahead-segments", "cache.l2.enabled", "cache.l2.endpoint",
        "cache.l2.bucket-name", "cache.l2.ssl", "cache.metadata-shared-enabled", "cache.metadata-retention-days",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> WriteOnlyKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "api.key", "cache.l2.access-key", "cache.l2.secret-key", "webdav.pass", "arr.instances"
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly string[] ReadableNames = PublicKeys
        .Concat(WriteOnlyKeys)
        .ToArray();
    private static readonly JsonSerializerOptions ResponseJsonOptions = new(JsonSerializerDefaults.Web);

    protected override async Task<IActionResult> HandleRequest()
    {
        if (HttpMethods.IsGet(HttpContext.Request.Method))
            return Ok(await ReadAsync(HttpContext.RequestAborted).ConfigureAwait(false));
        if (!HttpMethods.IsPost(HttpContext.Request.Method))
            throw new BadHttpRequestException("Invalid request payload.");
        if (string.IsNullOrWhiteSpace(HttpContext.Request.ContentType)
            || !MediaTypeHeaderValue.TryParse(HttpContext.Request.ContentType, out var mediaType)
            || !string.Equals(mediaType.MediaType.Value, "application/json", StringComparison.OrdinalIgnoreCase))
            throw new BadHttpRequestException("Invalid request content type.");

        AdminSettingsRequest? request;
        try
        {
            var body = await ReadRequestBodyAsync(HttpContext.RequestAborted).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = MaxJsonDepth });
            RejectDuplicateProperties(document.RootElement);
            request = JsonSerializer.Deserialize<AdminSettingsRequest>(body, RequestJsonOptions);
        }
        catch (JsonException)
        {
            throw new BadHttpRequestException("Invalid request payload.");
        }
        if (request is null || request.Config is null || request.ClearSecrets is null
            || request.Config.Count > MaxItems || request.ClearSecrets.Count > WriteOnlyKeys.Count)
            throw new BadHttpRequestException("Invalid request payload.");
        foreach (var pair in request.Config)
            if (pair.Key.Length > 256 || pair.Value is { Length: > MaxValueLength })
                throw new BadHttpRequestException("Invalid request payload.");

        return Ok(await UpdateAsync(request, HttpContext.RequestAborted).ConfigureAwait(false));
    }

    private static readonly JsonSerializerOptions RequestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = MaxJsonDepth,
    };

    private static readonly JsonSerializerOptions ArrJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = MaxJsonDepth,
    };

    private async Task<AdminSettingsResponse> ReadAsync(CancellationToken cancellationToken)
    {
        var rows = await ReadBoundaryRowsAsync(cancellationToken).ConfigureAwait(false);
        return BuildResponse(rows);
    }

    private async Task<List<ConfigItem>> ReadBoundaryRowsAsync(CancellationToken cancellationToken)
    {
        var names = ReadableNames.Select(name => name.ToLowerInvariant()).ToArray();
        var rows = await dbClient.Ctx.ConfigItems
            .AsNoTracking()
            .Where(row => names.Contains(row.ConfigName.ToLower()))
            .Take(MaxItems + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (rows.Count > MaxItems)
            throw new InvalidOperationException("Settings response exceeds the allowed item count.");
        return rows;
    }

    private async Task<List<ConfigItem>> ReadBoundaryRowsForUpdateAsync(CancellationToken cancellationToken)
    {
        var names = ReadableNames.Select(name => name.ToLowerInvariant()).ToArray();
        var rows = await dbClient.Ctx.ConfigItems
            .Where(row => names.Contains(row.ConfigName.ToLower()))
            .Take(MaxItems + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (rows.Count > MaxItems)
            throw new InvalidOperationException("Settings response exceeds the allowed item count.");
        return rows;
    }

    private static readonly ConfigValueSetter[] PublicConfigSetters =
    [
        new("general.base-url", (config, value) => config.GeneralBaseUrl = value),
        new("api.categories", (config, value) => config.ApiCategories = value),
        new("api.manual-category", (config, value) => config.ApiManualCategory = value),
        new("api.ensure-importable-video", (config, value) => config.ApiEnsureImportableVideo = value),
        new("api.ensure-article-existence-categories", (config, value) => config.ApiEnsureArticleExistenceCategories = value),
        new("api.ignore-history-limit", (config, value) => config.ApiIgnoreHistoryLimit = value),
        new("api.download-file-blocklist", (config, value) => config.ApiDownloadFileBlocklist = value),
        new("api.duplicate-nzb-behavior", (config, value) => config.ApiDuplicateNzbBehavior = value),
        new("api.import-strategy", (config, value) => config.ApiImportStrategy = value),
        new("api.completed-downloads-dir", (config, value) => config.ApiCompletedDownloadsDir = value),
        new("api.user-agent", (config, value) => config.ApiUserAgent = value),
        new("usenet.max-download-connections", (config, value) => config.UsenetMaxDownloadConnections = value),
        new("usenet.streaming-priority", (config, value) => config.UsenetStreamingPriority = value),
        new("usenet.article-buffer-size", (config, value) => config.UsenetArticleBufferSize = value),
        new("webdav.user", (config, value) => config.WebdavUser = value),
        new("webdav.show-hidden-files", (config, value) => config.WebdavShowHiddenFiles = value),
        new("webdav.enforce-readonly", (config, value) => config.WebdavEnforceReadonly = value),
        new("webdav.preview-par2-files", (config, value) => config.WebdavPreviewPar2Files = value),
        new("rclone.mount-dir", (config, value) => config.RcloneMountDir = value),
        new("media.library-dir", (config, value) => config.MediaLibraryDir = value),
        new("repair.enable", (config, value) => config.RepairEnable = value),
        new("cache.max-size-gb", (config, value) => config.CacheMaxSizeGb = value),
        new("cache.max-age-hours", (config, value) => config.CacheMaxAgeHours = value),
        new("cache.directory", (config, value) => config.CacheDirectory = value),
        new("cache.precache-enable", (config, value) => config.CachePrecacheEnable = value),
        new("cache.precache-max-file-size-mb", (config, value) => config.CachePrecacheMaxFileSizeMb = value),
        new("cache.read-ahead-enable", (config, value) => config.CacheReadAheadEnable = value),
        new("cache.read-ahead-segments", (config, value) => config.CacheReadAheadSegments = value),
        new("cache.l2.enabled", (config, value) => config.CacheL2Enabled = value),
        new("cache.l2.endpoint", (config, value) => config.CacheL2Endpoint = value),
        new("cache.l2.bucket-name", (config, value) => config.CacheL2BucketName = value),
        new("cache.l2.ssl", (config, value) => config.CacheL2Ssl = value),
        new("cache.metadata-shared-enabled", (config, value) => config.CacheMetadataSharedEnabled = value),
        new("cache.metadata-retention-days", (config, value) => config.CacheMetadataRetentionDays = value),
        new("api.key", (config, value) => config.ApiKey = value),

        new("cache.l2.access-key", (config, value) => config.CacheL2AccessKey = value),
        new("cache.l2.secret-key", (config, value) => config.CacheL2SecretKey = value),
        new("webdav.pass", (config, value) => config.WebdavPass = value),
        new("arr.instances", (config, value) => config.ArrInstances = value),
    ];

    private static readonly SecretStateSetter[] SecretStateSetterDefinitions =
    [
        new("api.key", (state, value) => state.ApiKey = value),

        new("cache.l2.access-key", (state, value) => state.CacheL2AccessKey = value),
        new("cache.l2.secret-key", (state, value) => state.CacheL2SecretKey = value),
        new("webdav.pass", (state, value) => state.WebdavPass = value),
        new("arr.instances", (state, value) => state.ArrInstances = value),
    ];

    private static readonly FrozenDictionary<string, Action<AdminSettingsConfig, string>> ConfigSetters = PublicConfigSetters
        .ToDictionary(static setter => setter.Name, static setter => setter.Assign)
        .ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, Action<AdminSettingsHasSecrets, bool>> SecretStateSetters = SecretStateSetterDefinitions
        .ToDictionary(static setter => setter.Name, static setter => setter.Assign)
        .ToFrozenDictionary(StringComparer.Ordinal);

    private AdminSettingsResponse BuildResponse(IEnumerable<ConfigItem> rows)
    {
        var materializedRows = rows.ToList();
        if (materializedRows.Count > MaxItems)
            throw new InvalidOperationException("Settings response exceeds the allowed item count.");

        var config = new AdminSettingsConfig();
        var hasSecrets = new AdminSettingsHasSecrets();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in materializedRows)
        {
            var name = SensitiveConfigKeys.TryGetCanonicalKey(row.ConfigName, out var canonical)
                ? canonical : row.ConfigName;
            // The query is an optimization, not the security boundary. Keep
            // this check so a future query change cannot widen the response.
            if (!PublicKeys.Contains(name) && !WriteOnlyKeys.Contains(name)) continue;
            if (!seenNames.Add(name)) throw new InvalidOperationException("Settings contain duplicate keys.");
            if (row.ConfigValue.Length > MaxValueLength)
                throw new InvalidOperationException("Settings response contains an oversized value.");

            if (SensitiveConfigKeys.IsSensitive(name))
            {
                var plaintext = DecryptSafely(name, row);
                if (name == "arr.instances")
                {
                    var safe = ParseLegacyArr(plaintext);
                    if (!ConfigSetters.TryGetValue(name, out var setConfig))
                        throw new InvalidOperationException("Settings response contained an unknown sensitive key.");

                    setConfig(config, SerializePublicArr(safe, out var hasApiKey));
                    if (!SecretStateSetters.TryGetValue(name, out var setState))
                        throw new InvalidOperationException("Settings response contained an unknown secret key.");

                    setState(hasSecrets, hasApiKey);
                }
                else
                {
                    if (!ConfigSetters.TryGetValue(name, out var setConfig)
                        || !SecretStateSetters.TryGetValue(name, out var setSecret))
                        throw new InvalidOperationException("Settings response contained an unknown sensitive key.");

                    setConfig(config, string.Empty);
                    setSecret(hasSecrets, !string.IsNullOrEmpty(plaintext));
                }
            }
            else
            {
                if (row.IsEncrypted) throw new InvalidOperationException("Settings contain an invalid encrypted value.");
                if (!ConfigSetters.TryGetValue(name, out var setConfig))
                    throw new InvalidOperationException("Settings response contained an unknown key.");
                setConfig(config, row.ConfigValue);
            }
        }

        var response = new AdminSettingsResponse { Config = config, HasSecrets = hasSecrets };
        if (JsonSerializer.SerializeToUtf8Bytes(response, ResponseJsonOptions).Length > MaxRequestBodyLength)
            throw new InvalidOperationException("Settings response exceeds the allowed size.");
        return response;
    }

    private sealed record ConfigValueSetter(
        string Name,
        Action<AdminSettingsConfig, string> Assign);

    private sealed record SecretStateSetter(string Name, Action<AdminSettingsHasSecrets, bool> Assign);

    private async Task<AdminSettingsResponse> UpdateAsync(AdminSettingsRequest request, CancellationToken cancellationToken)
    {
        var clear = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in request.ClearSecrets)
        {
            if (!SensitiveConfigKeys.TryGetCanonicalKey(key, out var canonical)
                || !WriteOnlyKeys.Contains(canonical)
                || !clear.Add(canonical))
                throw new BadHttpRequestException("Invalid request payload.");
        }

        var requested = new List<(string Name, string Value)>();
        var suppliedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in request.Config)
        {
            var name = SensitiveConfigKeys.TryGetCanonicalKey(pair.Key, out var canonical)
                ? canonical : pair.Key;
            if (!suppliedNames.Add(name)) throw new BadHttpRequestException("Invalid request payload.");
            if (!PublicKeys.Contains(name) && !WriteOnlyKeys.Contains(name))
                throw new BadHttpRequestException("Invalid request payload.");
            if (SensitiveConfigKeys.IsSensitive(name) && !WriteOnlyKeys.Contains(name))
                throw new InvalidOperationException("This secret is maintained by a dedicated flow.");
            if (pair.Value is null || pair.Value.Length > MaxValueLength)
                throw new BadHttpRequestException("Invalid request payload.");
            if (SensitiveConfigKeys.IsSensitive(name) && name != "arr.instances" && string.IsNullOrEmpty(pair.Value))
            {
                if (clear.Contains(name)) requested.Add((name, string.Empty));
                else continue; // blank write-only values preserve the current secret
            }
            else
            {
                var value = name == "webdav.pass" && pair.Value.Length > 0
                    ? PasswordUtil.Hash(pair.Value)
                    : pair.Value;
                // Arr merging is deliberately deferred until the fresh rows
                // have been loaded while holding both mutation fences.
                requested.Add((name, value));
            }
        }
        foreach (var key in clear.Where(key => !requested.Any(item => item.Name == key)))
            requested.Add((key, string.Empty));
        if (clear.Contains("arr.instances") && !requested.Any(item => item.Name == "arr.instances"))
            requested.Add(("arr.instances", JsonSerializer.Serialize(new ArrSettingsWriteDto(), ArrJsonOptions)));

        if (requested.Count == 0) return await ReadAsync(cancellationToken).ConfigureAwait(false);
        // Admin POST is a durable configuration mutation, including a
        // currently-public field. Refuse the whole operation before opening a
        // transaction when encryption is unavailable; otherwise a public
        // value could be persisted/cache-published while a later secret in
        // the same request fails.
        if (!encryption.IsKeyConfigured)
            throw new InvalidOperationException("A configured NZBDAV_MASTER_KEY is required for settings updates.");

        AdminSettingsResponse? response = null;
        await configManager.WithMutationGateAsync(async () =>
        {
            var transaction = await _beginTransaction(IsolationLevel.Serializable, cancellationToken)
                .ConfigureAwait(false);
            var lifetime = new SetupPersistenceTransaction(transaction);
            List<ConfigItem>? plainItems = null;
            Dictionary<string, ConfigItem>? expectedRows = null;
            var mutatedConfigNames = new HashSet<string>(StringComparer.Ordinal);
            Exception? disposeFailure = null;
            try
            {
                // This is the same cross-process fence used by setup writers;
                // all writers acquire gate -> transaction -> fence in order.
                await SetupMutationFenceLock.AcquireAsync(dbClient.Ctx, transaction, cancellationToken)
                    .ConfigureAwait(false);
                var rows = await ReadBoundaryRowsForUpdateAsync(cancellationToken).ConfigureAwait(false);
                var byName = new Dictionary<string, ConfigItem>(StringComparer.Ordinal);
                foreach (var row in rows)
                {
                    var logical = SensitiveConfigKeys.TryGetCanonicalKey(row.ConfigName, out var canonical)
                        ? canonical : row.ConfigName;
                    if (!byName.TryAdd(logical, row))
                        throw new InvalidOperationException("Settings contain duplicate keys.");
                }

                plainItems = [];
                foreach (var item in requested)
                {
                    var value = item.Name == "arr.instances"
                        ? MergeArrSecrets(GetStoredArr(byName), item.Value, clear.Contains(item.Name))
                        : item.Value;
                    plainItems.Add(new ConfigItem { ConfigName = item.Name, ConfigValue = value });
                }
                var storedItems = configManager.PrepareForStorage(plainItems);
                ValidateStoredItems(storedItems);

                // Keep the complete intended boundary, not only changed rows.
                // This is also the exact snapshot used to settle an uncertain
                // commit from a fresh context.
                expectedRows = byName.ToDictionary(pair => pair.Key, pair => Clone(pair.Value), StringComparer.Ordinal);
                foreach (var item in storedItems) expectedRows[item.ConfigName] = Clone(item);
                response = BuildResponse(expectedRows.Values);

                foreach (var item in storedItems)
                {
                    if (!byName.TryGetValue(item.ConfigName, out var existing))
                    {
                        mutatedConfigNames.Add(item.ConfigName);
                        dbClient.Ctx.ConfigItems.Add(Clone(item));
                    }
                    else if (existing.ConfigName != item.ConfigName)
                    {
                        mutatedConfigNames.Add(existing.ConfigName);
                        mutatedConfigNames.Add(item.ConfigName);
                        dbClient.Ctx.ConfigItems.Remove(existing);
                        dbClient.Ctx.ConfigItems.Add(Clone(item));
                    }
                    else
                    {
                        mutatedConfigNames.Add(existing.ConfigName);
                        existing.ConfigValue = item.ConfigValue;
                        existing.IsEncrypted = item.IsEncrypted;
                    }
                }
                // This is the last request cancellation point. Commit is an
                // irreversible operation and must never use RequestAborted.
                cancellationToken.ThrowIfCancellationRequested();
                await dbClient.Ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                await lifetime.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                disposeFailure = await lifetime.TryDisposeAsync().ConfigureAwait(false);
                if (disposeFailure is not null)
                {
                    try
                    {
                        response = await ResolveCommitAttemptAsync(
                            lifetime, disposeFailure, expectedRows, plainItems, disposeFailure)
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        DetachTrackedConfigItems(dbClient.Ctx, mutatedConfigNames);
                    }
                    return;
                }
            }
            catch (Exception exception)
            {
                if (lifetime.CommitAttempted)
                {
                    // Commit exceptions do not prove rollback. Dispose first;
                    // reconciliation must never run against a live transaction.
                    try
                    {
                        response = await ResolveCommitAttemptAsync(
                            lifetime, exception, expectedRows, plainItems, disposeFailure)
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        DetachTrackedConfigItems(dbClient.Ctx, mutatedConfigNames);
                    }
                    return;
                }

                // Before commit, rollback is safe. It is deliberately not
                // attempted after a commit attempt, including cancellation.
                var rollbackFailure = await lifetime.TryRollbackAsync().ConfigureAwait(false);
                var closeFailure = await lifetime.TryDisposeAsync().ConfigureAwait(false);
                DetachTrackedConfigItems(dbClient.Ctx, mutatedConfigNames);
                if (rollbackFailure is not null || closeFailure is not null)
                    throw new SetupTransactionRecoveryRequiredException(exception, rollbackFailure, closeFailure);
                if (SetupTransactionOutcome.IsRetryableWriteAbort(exception))
                    throw new ConcurrencyConflictException();
                throw;
            }

            // Publication is after durable commit and transaction disposal,
            // while the mutation gate is still held. It occurs exactly once.
            configManager.UpdateValuesNoLock(plainItems!);
        }, cancellationToken).ConfigureAwait(false);
        return response ?? throw new InvalidOperationException("Settings response was not produced.");
    }

    private async Task<AdminSettingsResponse> ResolveCommitAttemptAsync(
        SetupPersistenceTransaction lifetime,
        Exception operationFailure,
        IReadOnlyDictionary<string, ConfigItem>? expectedRows,
        IReadOnlyList<ConfigItem>? plainItems,
        Exception? priorDisposeFailure)
    {
        var disposeFailure = priorDisposeFailure ?? await lifetime.TryDisposeAsync().ConfigureAwait(false);
        if (disposeFailure is not null)
        {
            // Reconciliation requires a fresh context and a closed old
            // transaction. If closing failed, stop with typed recovery rather
            // than reading or publishing against an ambiguous connection.
            throw new SetupTransactionRecoveryRequiredException(
                operationFailure,
                rollbackFailure: null,
                disposeFailure: disposeFailure);
        }
        var outcome = SetupTransactionOutcome.ClassifyCommitFailure(operationFailure, lifetime.State);
        List<ConfigItem>? actualRows = null;
        Exception? readFailure = null;
        try
        {
            actualRows = await ReadFreshBoundaryRowsAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            readFailure = exception;
        }

        var durableIntended = actualRows is not null
            && expectedRows is not null
            && RowsMatchExactly(expectedRows, actualRows);
        if (durableIntended
            && disposeFailure is null
            && (lifetime.State == SetupTransactionState.Committed
                || outcome == SetupCommitOutcome.Uncertain))
        {
            // A provider may commit and then throw (or report cancellation).
            // The fresh durable snapshot is authoritative; do not return the
            // request cancellation or publish a second cache event here.
            configManager.UpdateValuesNoLock(plainItems?.Select(Clone).ToList()
                ?? throw new InvalidOperationException("Settings update was incomplete."));
            return BuildResponse(actualRows!);
        }

        // No cache or event is published for a rollback or an unresolved
        // outcome. In particular, never blindly roll back after CommitAsync.
        if (outcome == SetupCommitOutcome.DefinitivelyAborted && readFailure is null)
            throw new ConcurrencyConflictException();
        throw new SetupTransactionRecoveryRequiredException(
            operationFailure,
            readFailure,
            disposeFailure);
    }

    private async Task<List<ConfigItem>> ReadFreshBoundaryRowsAsync(CancellationToken cancellationToken)
    {
        await using var context = _freshContextFactory();
        var names = ReadableNames.Select(name => name.ToLowerInvariant()).ToArray();
        var rows = await context.ConfigItems
            .AsNoTracking()
            .Where(row => names.Contains(row.ConfigName.ToLower()))
            .Take(MaxItems + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (rows.Count > MaxItems)
            throw new InvalidOperationException("Settings response exceeds the allowed item count.");
        return rows;
    }

    private static bool RowsMatchExactly(
        IReadOnlyDictionary<string, ConfigItem> expected,
        IEnumerable<ConfigItem> actualRows)
    {
        var actual = new Dictionary<string, ConfigItem>(StringComparer.Ordinal);
        foreach (var row in actualRows)
        {
            var logical = SensitiveConfigKeys.TryGetCanonicalKey(row.ConfigName, out var canonical)
                ? canonical : row.ConfigName;
            if (!actual.TryAdd(logical, row))
                return false;
        }
        if (actual.Count != expected.Count)
            return false;
        foreach (var pair in expected)
        {
            if (!actual.TryGetValue(pair.Key, out var row)
                || row.IsEncrypted != pair.Value.IsEncrypted
                || !string.Equals(row.ConfigValue, pair.Value.ConfigValue, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private sealed class ArrSettingsWriteDto
    {
        public List<ArrInstanceWriteDto> RadarrInstances { get; set; } = [];
        public List<ArrInstanceWriteDto> SonarrInstances { get; set; } = [];
        public List<ArrQueueRuleDto> QueueRules { get; set; } = [];
    }

    private sealed class ArrInstanceWriteDto
    {
        public string? Host { get; set; }
        public string? ApiKey { get; set; }
    }

    private string? GetStoredArr(IReadOnlyDictionary<string, ConfigItem> rows)
    {
        if (!rows.TryGetValue("arr.instances", out var row)) return null;
        return DecryptSafely("arr.instances", row);
    }

    private static void ValidateStoredItems(IEnumerable<ConfigItem> items)
    {
        foreach (var item in items)
        {
            if (item.ConfigValue.Length > MaxValueLength)
                throw new BadHttpRequestException("Settings value exceeds the allowed stored size.");
            if (SensitiveConfigKeys.IsSensitive(item.ConfigName)
                && !ConfigEncryptionService.IsEncryptedFormat(item.ConfigValue))
                throw new InvalidOperationException("Secret settings must be stored as authenticated ciphertext.");
        }
    }

    private string DecryptSafely(string name, ConfigItem row)
    {
        try { return row.IsEncrypted ? encryption.Decrypt(name, row.ConfigValue).plaintext : row.ConfigValue; }
        catch (Exception exception) when (exception is CryptographicException or InvalidOperationException or FormatException)
        { throw new InvalidOperationException("Settings could not be read safely."); }
    }

    private static string MergeArrSecrets(string? currentText, string requestedText, bool clearAll)
    {
        var requested = ParseStrictArr(requestedText);
        var current = string.IsNullOrWhiteSpace(currentText) ? new ArrSettingsWriteDto() : ParseLegacyArr(currentText);
        var result = new ArrSettingsWriteDto
        {
            RadarrInstances = MergeInstances(requested.RadarrInstances, current.RadarrInstances, clearAll),
            SonarrInstances = MergeInstances(requested.SonarrInstances, current.SonarrInstances, clearAll),
            QueueRules = requested.QueueRules.Select(CanonicalQueueRule).ToList(),
        };
        return JsonSerializer.Serialize(result, ArrJsonOptions);
    }

    private static List<ArrInstanceWriteDto> MergeInstances(
        List<ArrInstanceWriteDto> requested, List<ArrInstanceWriteDto> current, bool clearAll)
    {
        if (requested.Count > MaxItems || current.Count > MaxItems)
            throw new BadHttpRequestException("Invalid Arr settings.");
        var oldByIdentity = BuildIdentityMap(current);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ArrInstanceWriteDto>(requested.Count);
        foreach (var instance in requested)
        {
            var identity = NormalizeHostIdentity(instance.Host);
            if (identity is null || !seen.Add(identity))
                throw new BadHttpRequestException("Arr instance identities are ambiguous.");
            var requestedKey = instance.ApiKey ?? string.Empty;
            var key = requestedKey;
            if (!clearAll && string.IsNullOrEmpty(key))
            {
                if (!oldByIdentity.TryGetValue(identity, out var prior) || string.IsNullOrEmpty(prior.ApiKey))
                    throw new BadHttpRequestException("Arr instances with no API key require a replacement.");
                key = prior.ApiKey;
            }
            result.Add(new ArrInstanceWriteDto { Host = identity, ApiKey = key });
        }
        return result;
    }

    private static Dictionary<string, ArrInstanceWriteDto> BuildIdentityMap(IEnumerable<ArrInstanceWriteDto> instances)
    {
        var result = new Dictionary<string, ArrInstanceWriteDto>(StringComparer.Ordinal);
        foreach (var instance in instances)
        {
            var identity = NormalizeHostIdentity(instance.Host);
            if (identity is null || !result.TryAdd(identity, instance))
                throw new BadHttpRequestException("Arr instance identities are ambiguous.");
            ValidateInstance(instance);
        }
        return result;
    }

    private static void RejectCrossCollectionCollisions(ArrSettingsWriteDto settings)
    {
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var instance in settings.RadarrInstances.Concat(settings.SonarrInstances))
        {
            var identity = NormalizeHostIdentity(instance.Host);
            if (identity is null || !identities.Add(identity))
                throw new BadHttpRequestException("Arr instance identities are ambiguous.");
        }
    }

    private static ArrSettingsWriteDto ParseStrictArr(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = MaxJsonDepth });
            RejectDuplicateProperties(document.RootElement);
            RejectResponseOnlyArrProperties(document.RootElement);
            var value = JsonSerializer.Deserialize<ArrSettingsWriteDto>(document.RootElement.GetRawText(), ArrJsonOptions)
                        ?? throw new JsonException();
            if (value.RadarrInstances is null || value.SonarrInstances is null || value.QueueRules is null)
                throw new JsonException();
            if (value.RadarrInstances.Count + value.SonarrInstances.Count > MaxItems
                || value.QueueRules.Count > MaxItems)
                throw new JsonException();
            foreach (var instance in value.RadarrInstances.Concat(value.SonarrInstances)) ValidateInstance(instance);
            foreach (var rule in value.QueueRules) CanonicalQueueRule(rule);
            RejectCrossCollectionCollisions(value);
            return value;
        }
        catch (JsonException)
        {
            throw new BadHttpRequestException("Invalid Arr settings.");
        }
    }

    // Legacy values are read permissively so old extension data can be
    // discarded, but known values still have strict types and bounds.
    private static ArrSettingsWriteDto ParseLegacyArr(string text)
    {
        if (text.Length > MaxValueLength)
            throw new InvalidOperationException("Settings could not be read safely.");
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = MaxJsonDepth });
            RejectDuplicateProperties(document.RootElement);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
            var root = document.RootElement;
            var result = new ArrSettingsWriteDto
            {
                RadarrInstances = ParseLegacyInstances(root, "RadarrInstances"),
                SonarrInstances = ParseLegacyInstances(root, "SonarrInstances"),
                QueueRules = ParseLegacyRules(root),
            };
            BuildIdentityMap(result.RadarrInstances);
            BuildIdentityMap(result.SonarrInstances);
            RejectCrossCollectionCollisions(result);
            return result;
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Settings could not be read safely.");
        }
    }

    private static List<ArrInstanceWriteDto> ParseLegacyInstances(JsonElement root, string name)
    {
        if (!TryGetProperty(root, name, out var value)) return [];
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > MaxItems) throw new JsonException();
        var result = new List<ArrInstanceWriteDto>();
        foreach (var entry in value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) throw new JsonException();
            var host = NormalizeHostIdentity(GetOptionalString(entry, "Host", MaxHostLength));
            var key = GetOptionalString(entry, "ApiKey", MaxApiKeyLength) ?? string.Empty;
            var instance = new ArrInstanceWriteDto { Host = host, ApiKey = key };
            ValidateInstance(instance);
            result.Add(instance);
        }
        return result;
    }

    private static string? NormalizeHostIdentity(string? host)
    {
        var canonical = NormalizeIdentity(host);
        if (!string.IsNullOrEmpty(canonical)) return canonical;

        if (string.IsNullOrWhiteSpace(host)
            || host.AsSpan().IndexOf("://", StringComparison.Ordinal) >= 0)
            return null;

        return NormalizeIdentity($"http://{host.Trim()}");
    }

    private static List<ArrQueueRuleDto> ParseLegacyRules(JsonElement root)
    {
        if (!TryGetProperty(root, "QueueRules", out var value)) return [];
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > MaxItems) throw new JsonException();
        var result = new List<ArrQueueRuleDto>();
        foreach (var entry in value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) throw new JsonException();
            var message = GetOptionalString(entry, "Message", MaxQueueMessageLength);
            if (message is null || !TryGetProperty(entry, "Action", out var action)
                || action.ValueKind != JsonValueKind.Number || !action.TryGetInt32(out var actionValue))
                throw new JsonException();
            result.Add(CanonicalQueueRule(new ArrQueueRuleDto { Message = message, Action = actionValue }));
        }
        return result;
    }

    private static ArrQueueRuleDto CanonicalQueueRule(ArrQueueRuleDto rule)
    {
        if (rule.Message is null || rule.Message.Length > MaxQueueMessageLength || rule.Action is < 0 or > 3)
            throw new BadHttpRequestException("Invalid Arr settings.");
        return new ArrQueueRuleDto { Message = rule.Message, Action = rule.Action };
    }

    private static void ValidateInstance(ArrInstanceWriteDto instance)
    {
        if (NormalizeHostIdentity(instance.Host) is null || instance.ApiKey is null || instance.ApiKey.Length > MaxApiKeyLength)
            throw new BadHttpRequestException("Invalid Arr settings.");
    }

    private static string SerializePublicArr(ArrSettingsWriteDto source, out bool hasApiKey)
    {
        var projected = new ArrSettingsDto
        {
            RadarrInstances = source.RadarrInstances.Select(x => new ArrInstanceDto { Host = NormalizeHostIdentity(x.Host) ?? string.Empty, HasApiKey = !string.IsNullOrEmpty(x.ApiKey) }).ToList(),
            SonarrInstances = source.SonarrInstances.Select(x => new ArrInstanceDto { Host = NormalizeHostIdentity(x.Host) ?? string.Empty, HasApiKey = !string.IsNullOrEmpty(x.ApiKey) }).ToList(),
            QueueRules = source.QueueRules.Select(x => new ArrQueueRuleDto { Message = x.Message, Action = x.Action }).ToList(),
        };

        hasApiKey = projected.RadarrInstances.Concat(projected.SonarrInstances).Any(x => x.HasApiKey);
        return JsonSerializer.Serialize(projected, ArrJsonOptions);
    }

    private static string? NormalizeIdentity(string? host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > MaxHostLength)
            return null;

        var trimmed = host.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            return null;

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return null;

        if (string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
            return null;

        var rawAfterScheme = trimmed[(trimmed.IndexOf("://", StringComparison.Ordinal) + 3)..];
        var authorityEnd = rawAfterScheme.IndexOf('/');
        var authority = authorityEnd >= 0 ? rawAfterScheme[..authorityEnd] : rawAfterScheme;
        if (string.IsNullOrWhiteSpace(authority) || authority.Contains('@'))
            return null;

        var hostPart = uri.IdnHost?.Trim();
        if (string.IsNullOrWhiteSpace(hostPart))
            return null;

        hostPart = hostPart.TrimEnd('.');
        if (string.IsNullOrWhiteSpace(hostPart))
            return null;

        var scheme = uri.Scheme.ToLowerInvariant();
        var defaultPort = scheme == Uri.UriSchemeHttp ? 80 : 443;
        var normalizedPort = uri.IsDefaultPort || uri.Port == defaultPort ? -1 : uri.Port;

        var authorityUri = new UriBuilder
        {
            Scheme = scheme,
            Host = hostPart,
            Port = normalizedPort,
        }.Uri.Authority;

        var pathWithSuffix = authorityEnd >= 0 ? rawAfterScheme[authorityEnd..] : string.Empty;
        var canonicalPath = NormalizePathAndPercentEncoding(pathWithSuffix);
        if (canonicalPath is null)
            return null;

        canonicalPath = canonicalPath.TrimEnd('/');
        if (canonicalPath == "/")
            canonicalPath = string.Empty;

        var result = $"{scheme}://{authorityUri}{canonicalPath}";
        return result.Length <= MaxHostLength ? result : null;
    }

    private static string? NormalizePathAndPercentEncoding(string path)
    {
        var normalizedPath = NormalizePercentEncoding(path);
        if (normalizedPath is null)
            return null;

        var segments = new List<string>(4);
        var segmentStart = 0;

        for (var index = 0; index <= normalizedPath.Length; index++)
        {
            if (index < normalizedPath.Length && normalizedPath[index] != '/')
                continue;

            var rawSegment = normalizedPath.Substring(segmentStart, index - segmentStart);
            segmentStart = index + 1;
            if (rawSegment.Length == 0)
                continue;

            if (rawSegment == ".")
                continue;

            if (rawSegment == "..")
            {
                if (segments.Count > 0)
                    segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(rawSegment);
        }

        if (segments.Count == 0)
            return string.Empty;

        var normalized = new System.Text.StringBuilder(normalizedPath.Length);
        for (var index = 0; index < segments.Count; index++)
            normalized.Append('/').Append(segments[index]);

        return normalized.ToString();
    }

    private static string? NormalizePercentEncoding(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        var normalized = new System.Text.StringBuilder(path.Length);
        for (var index = 0; index < path.Length; index++)
        {
            var current = path[index];
            if (current != '%')
            {
                normalized.Append(current);
                continue;
            }

            if (index + 2 >= path.Length || !Uri.IsHexEncoding(path, index))
                return null;

            normalized.Append('%');
            normalized.Append(char.ToUpperInvariant(path[index + 1]));
            normalized.Append(char.ToUpperInvariant(path[index + 2]));
            index += 2;
        }

        return normalized.ToString();
    }

    private static void DetachTrackedConfigItems(DavDatabaseContext context, IReadOnlyCollection<string> configNames)
    {
        if (configNames.Count == 0)
            return;

        var targets = new HashSet<string>(configNames, StringComparer.Ordinal);
        foreach (var entry in context.ChangeTracker.Entries<ConfigItem>().ToList())
            if (targets.Contains(entry.Entity.ConfigName))
                entry.State = EntityState.Detached;
    }

    private static string? GetOptionalString(JsonElement objectValue, string name, int maxLength)
    {
        if (!TryGetProperty(objectValue, name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new JsonException();
        var result = value.GetString();
        if (result is null || result.Length > maxLength) throw new JsonException();
        return result;
    }

    private static bool TryGetProperty(JsonElement objectValue, string name, out JsonElement value)
    {
        foreach (var property in objectValue.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        value = default;
        return false;
    }

    private static void RejectDuplicateProperties(JsonElement element, int depth = 0)
    {
        if (depth > MaxJsonDepth) throw new JsonException();
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException();
                RejectDuplicateProperties(property.Value, depth + 1);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) RejectDuplicateProperties(child, depth + 1);
    }

    private static void RejectResponseOnlyArrProperties(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
        foreach (var collectionName in new[] { "RadarrInstances", "SonarrInstances" })
        {
            if (!TryGetProperty(root, collectionName, out var collection)) continue;
            if (collection.ValueKind != JsonValueKind.Array) throw new JsonException();
            foreach (var instance in collection.EnumerateArray())
            {
                if (instance.ValueKind != JsonValueKind.Object) throw new JsonException();
                foreach (var property in instance.EnumerateObject())
                    if (property.Name.Equals("HasApiKey", StringComparison.OrdinalIgnoreCase))
                        throw new JsonException();
            }
        }
    }

    private async Task<byte[]> ReadRequestBodyAsync(CancellationToken cancellationToken)
    {
        if (HttpContext.Request.ContentLength > MaxRequestBodyLength)
            throw new BadHttpRequestException("Invalid request payload.");
        await using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await HttpContext.Request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > MaxRequestBodyLength)
                throw new BadHttpRequestException("Invalid request payload.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return buffer.ToArray();
    }

    private static ConfigItem Clone(ConfigItem item) => new() { ConfigName = item.ConfigName, ConfigValue = item.ConfigValue, IsEncrypted = item.IsEncrypted };
}
