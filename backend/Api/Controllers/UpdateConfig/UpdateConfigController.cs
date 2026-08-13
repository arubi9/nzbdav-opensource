using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Setup.Core;

namespace NzbWebDAV.Api.Controllers.UpdateConfig;

[ApiController]
[Route("api/update-config")]
public class UpdateConfigController(DavDatabaseClient dbClient, ConfigManager configManager) : BaseApiController
{
    private static readonly TimeSpan StreamSigningKeyPreviousOverlap = TimeSpan.FromDays(14);

    private async Task<UpdateConfigResponse> UpdateConfig(UpdateConfigRequest request)
    {
        var cancellationToken = HttpContext.RequestAborted;

        // Reject setup-owned and internally-maintained keys before any
        // canonicalization, encryption, database work, or event publication.
        ValidateSetupManagedKeys(request.ConfigItems);
        ValidateSetupManagedKeys(request.ClearSecretKeys.Select(key => new ConfigItem { ConfigName = key }));
        var canonicalItems = CanonicalizeRequest(request.ConfigItems);
        ValidateStreamSigningKey(canonicalItems);

        // The request may arrive before startup migration has rewritten a
        // legacy-cased sensitive row. Read without tracking and match managed
        // names through the canonical registry, so this path cannot create a
        // second row beside an alias.
        foreach (var entry in dbClient.Ctx.ChangeTracker.Entries<ConfigItem>().ToList())
            entry.State = EntityState.Detached;

        await configManager.WithMutationGateAsync(async () =>
        {
            // Re-check the current value while holding the mutation gate so a
            // concurrent rotation cannot turn this validated no-op into a
            // lost update. This remains before encryption and all DB work.
            var unchangedStreamKey = canonicalItems.FirstOrDefault(item =>
                item.ConfigName == "api.strm-key"
                && string.Equals(item.ConfigValue, configManager.GetStrmKey(), StringComparison.Ordinal));
            if (unchangedStreamKey is not null)
                canonicalItems.Remove(unchangedStreamKey);

            if (canonicalItems.Count == 0)
                return;

            MergeWriteOnlySecrets(canonicalItems, request.ClearSecretKeys);
            if (canonicalItems.Count == 0)
                return;

            var streamTokenState = configManager.GetStreamTokenState();
            ApplyStreamSigningKeyRotation(canonicalItems, streamTokenState);
            var itemsForStorage = configManager.PrepareForStorage(canonicalItems);

            await using var transaction = await dbClient.Ctx.Database
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            await SetupMutationFenceLock.AcquireAsync(dbClient.Ctx, transaction, cancellationToken)
                .ConfigureAwait(false);
            try
            {
            var allRows = await dbClient.Ctx.ConfigItems
                .AsNoTracking()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var requestedNames = itemsForStorage
                .Select(item => item.ConfigName)
                .ToHashSet(StringComparer.Ordinal);
            var existingByName = new Dictionary<string, ConfigItem>(StringComparer.Ordinal);
            var managedRows = new Dictionary<string, ConfigItem>(StringComparer.Ordinal);
            foreach (var row in allRows)
            {
                var logicalName = GetLogicalName(row.ConfigName);
                if (SensitiveConfigKeys.TryGetCanonicalKey(row.ConfigName, out _)
                    && !managedRows.TryAdd(logicalName!, row))
                {
                    throw new InvalidOperationException(
                        $"Duplicate managed config key '{logicalName}' exists with multiple casings.");
                }

                if (requestedNames.Contains(logicalName!))
                    existingByName[logicalName!] = row;
            }

            foreach (var item in itemsForStorage)
            {
                existingByName.TryGetValue(item.ConfigName, out var existing);
                if (existing is null)
                {
                    dbClient.Ctx.ConfigItems.Add(Clone(item));
                    continue;
                }

                if (!string.Equals(existing.ConfigName, item.ConfigName, StringComparison.Ordinal))
                {
                    // ConfigName is the primary key; remove+insert is the
                    // portable way to canonicalize a legacy alias atomically.
                    dbClient.Ctx.ConfigItems.Remove(existing);
                    dbClient.Ctx.ConfigItems.Add(Clone(item));
                    continue;
                }

                existing.ConfigValue = item.ConfigValue;
                existing.IsEncrypted = item.IsEncrypted;
                dbClient.Ctx.ConfigItems.Update(existing);
            }

            await dbClient.Ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                foreach (var entry in dbClient.Ctx.ChangeTracker.Entries<ConfigItem>().ToList())
                    entry.State = EntityState.Detached;
                throw;
            }

            // Update the in-memory cache only after the database write succeeds,
            // while the common gate is still held.
            configManager.UpdateValuesNoLock(canonicalItems);
        }, cancellationToken).ConfigureAwait(false);

        return new UpdateConfigResponse { Status = true };
    }

    private void MergeWriteOnlySecrets(List<ConfigItem> items, IReadOnlySet<string> clearKeys)
    {
        foreach (var item in items.Where(item => SensitiveConfigKeys.IsSensitive(item.ConfigName)))
        {
            if (clearKeys.Contains(item.ConfigName))
                continue;
            var current = configManager.GetConfigValueForWrite(item.ConfigName);
            if (item.ConfigName == "arr.instances")
            {
                item.ConfigValue = MergeArrSecrets(current, item.ConfigValue);
                continue;
            }
            if (string.IsNullOrEmpty(item.ConfigValue) && current is not null)
                item.ConfigValue = current;
        }
    }

    private static string MergeArrSecrets(string? currentText, string requestedText)
    {
        if (string.IsNullOrWhiteSpace(currentText)) return requestedText;
        try
        {
            var current = JsonNode.Parse(currentText) as JsonObject;
            var requested = JsonNode.Parse(requestedText) as JsonObject;
            if (current is null || requested is null) return requestedText;
            foreach (var collection in new[] { "RadarrInstances", "SonarrInstances" })
            {
                if (requested[collection] is not JsonArray next || current[collection] is not JsonArray old) continue;
                foreach (var entry in next.OfType<JsonObject>())
                {
                    var host = entry["Host"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(host)) continue;
                    var prior = old.OfType<JsonObject>().FirstOrDefault(candidate =>
                        string.Equals(candidate["Host"]?.GetValue<string>(), host, StringComparison.OrdinalIgnoreCase));
                    if (prior is null) continue;
                    foreach (var key in prior.Select(pair => pair.Key).Where(key =>
                                 key.Contains("key", StringComparison.OrdinalIgnoreCase)
                                 || key.Contains("password", StringComparison.OrdinalIgnoreCase)
                                 || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
                                 || key.Contains("token", StringComparison.OrdinalIgnoreCase)))
                    {
                        if (entry[key] is null || string.IsNullOrEmpty(entry[key]?.GetValue<string>()))
                            entry[key] = prior[key]?.DeepClone();
                    }
                }
            }
            return requested.ToJsonString();
        }
        catch (JsonException) { return requestedText; }
        catch (InvalidOperationException) { return requestedText; }
    }

    private void ApplyStreamSigningKeyRotation(List<ConfigItem> itemsForStorage, ConfigManager.StreamTokenState streamTokenState)
    {
        var requestedCurrentKey = itemsForStorage
            .FirstOrDefault(item => item.ConfigName == "api.strm-key")?.ConfigValue;
        if (string.IsNullOrWhiteSpace(requestedCurrentKey))
            return;

        if (string.Equals(requestedCurrentKey, streamTokenState.Current, StringComparison.Ordinal))
            return;

        if (!streamTokenState.PreviousRingValid)
            throw new InvalidOperationException("The persisted stream signing-key history is malformed; rotation is refused.");

        var now = TimeProvider.System.GetUtcNow();
        var normalized = new List<ConfigManager.StreamKeyHistoryEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in streamTokenState.PreviousKeys)
        {
            if (entry.ExpiresAt <= now || !seen.Add(entry.Key))
                continue;
            normalized.Add(entry);
        }

        // Rollback reuse removes the requested key before adding the old current.
        normalized.RemoveAll(entry => string.Equals(entry.Key, requestedCurrentKey, StringComparison.Ordinal));
        if (!normalized.Any(entry => string.Equals(entry.Key, streamTokenState.Current, StringComparison.Ordinal)))
        {
            if (normalized.Count >= ConfigManager.MaxStreamSigningKeyHistory)
                throw new InvalidOperationException(
                    $"The stream signing-key history is full ({ConfigManager.MaxStreamSigningKeyHistory} unexpired keys); refusing to evict a valid key.");

            normalized.Add(new ConfigManager.StreamKeyHistoryEntry(
                streamTokenState.Current,
                now.Add(StreamSigningKeyPreviousOverlap)));
        }

        Upsert(itemsForStorage, "api.strm-key-previous-ring", ConfigManager.SerializeStreamKeyRing(normalized));
    }

    private static List<ConfigItem> CanonicalizeRequest(IEnumerable<ConfigItem> items)
    {
        var result = new List<ConfigItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var logicalName = GetLogicalName(item.ConfigName);
            if (!seen.Add(logicalName!))
                throw new InvalidOperationException($"Duplicate config key '{logicalName}' was supplied with multiple aliases.");

            result.Add(Clone(item));
            result[^1].ConfigName = logicalName!;
        }
        return result;
    }

    private static void Upsert(List<ConfigItem> itemsForStorage, string name, string value)
    {
        var existing = itemsForStorage.FirstOrDefault(item => item.ConfigName == name);
        if (existing is null)
        {
            itemsForStorage.Add(new ConfigItem
            {
                ConfigName = name,
                ConfigValue = value,
                IsEncrypted = false,
            });
            return;
        }

        existing.ConfigValue = value;
    }

    private static void ValidateSetupManagedKeys(IEnumerable<ConfigItem> items)
    {
        foreach (var item in items)
        {
            // This endpoint is the legacy generic settings writer. Every
            // registry-managed key has a dedicated boundary now; accepting a
            // secret here would allow callers to bypass its validation,
            // redaction, and merge semantics (notably arr.instances).
            if (SensitiveConfigKeys.IsSensitive(item.ConfigName))
            {
                if (string.Equals(item.ConfigName, "api.strm-key", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (SensitiveConfigKeys.TryGetCanonicalSetupKey(item.ConfigName, out _)
                    || item.ConfigName.StartsWith("setup.", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.ConfigName, "api.strm-key-previous-ring", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Setup config key '{item.ConfigName}' can only be changed through the dedicated setup persistence path.");
                }

                throw new InvalidOperationException(
                    $"Sensitive config key '{item.ConfigName}' can only be changed through its dedicated settings API.");
            }

            if (item.ConfigName.StartsWith("setup.", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Setup config key '{item.ConfigName}' can only be changed through the dedicated setup persistence path.");
        }
    }

    private static void ValidateStreamSigningKey(IEnumerable<ConfigItem> items)
    {
        var item = items.FirstOrDefault(config =>
            string.Equals(config.ConfigName, "api.strm-key", StringComparison.Ordinal));
        if (item is null)
            return;

        if (string.IsNullOrWhiteSpace(item.ConfigValue)
            || item.ConfigValue.Length > ConfigManager.MaxStreamSigningKeyLength)
        {
            throw new InvalidOperationException(
                $"api.strm-key must be non-empty and at most {ConfigManager.MaxStreamSigningKeyLength} characters.");
        }
    }

    private static string? GetLogicalName(string configName)
    {
        if (SensitiveConfigKeys.TryGetCanonicalKey(configName, out var canonicalName))
            return canonicalName;

        return configName.StartsWith("setup.", StringComparison.OrdinalIgnoreCase)
            ? throw new InvalidOperationException($"Unknown reserved setup config key '{configName}'.")
            : configName;
    }

    private static ConfigItem Clone(ConfigItem item)
        => new()
        {
            ConfigName = item.ConfigName,
            ConfigValue = item.ConfigValue,
            IsEncrypted = item.IsEncrypted,
        };

    protected override bool AllowGet => false;

    protected override async Task<IActionResult> HandleRequest()
    {
        if (!HttpContext.Request.HasFormContentType)
            throw new BadHttpRequestException("Config updates require form content.");
        var request = new UpdateConfigRequest(HttpContext);
        var response = await UpdateConfig(request).ConfigureAwait(false);
        return Ok(response);
    }
}
