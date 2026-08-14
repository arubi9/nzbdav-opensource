using System.Collections.ObjectModel;
using System.Globalization;
using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Setup.Core;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Config;

public class ConfigManager
{
    public static readonly string AppVersion = EnvironmentUtil.GetEnvironmentVariable("NZBDAV_VERSION") ?? "unknown";

    // This gate is deliberately static: ConfigManager instances are used by
    // startup/tests as well as the request pipeline, but all of them share one
    // process-wide ordering between durable writes and published snapshots.
    private static readonly SemaphoreSlim ConfigMutationGate = new(1, 1);
    internal const int MaxStreamSigningKeyHistory = 32;
    internal const int MaxStreamSigningKeyLength = 4096;
    private static readonly JsonSerializerOptions StreamKeyRingJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly ConfigEncryptionService _encryption;
    private readonly Dictionary<string, string> _config = new();
    public event EventHandler<ConfigEventArgs>? OnConfigChanged;

    public ConfigManager() : this(new ConfigEncryptionService())
    {
    }

    public ConfigManager(ConfigEncryptionService encryption)
    {
        _encryption = encryption;
    }

    public Task LoadConfig()
        => WithMutationGateAsync(LoadConfigNoLock);

    private async Task LoadConfigNoLock()
    {
        await using var dbContext = new DavDatabaseContext();
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(IsolationLevel.Serializable)
            .ConfigureAwait(false);

        Dictionary<string, string> newValues;
        try
        {
            var configItems = await dbContext.ConfigItems
                .AsNoTracking()
                .ToListAsync()
                .ConfigureAwait(false);

            // Validate every managed name before mutating anything. A canonical
            // row plus an alias (or multiple aliases) is ambiguous and must
            // fail closed rather than choosing one value.
            foreach (var configItem in configItems)
                _ = CanonicalizeLoadedName(configItem.ConfigName);

            var hasLegacyMigrationStartRow = configItems.Any(item =>
                string.Equals(
                    CanonicalizeLoadedName(item.ConfigName),
                    SetupConfigKeys.StreamTokenLegacyMigrationStart,
                    StringComparison.Ordinal));
            if (!hasLegacyMigrationStartRow)
            {
                var markerRow = new ConfigItem
                {
                    ConfigName = SetupConfigKeys.StreamTokenLegacyMigrationStart,
                    ConfigValue = DateTime.UtcNow.ToString("O"),
                    IsEncrypted = false,
                };
                dbContext.ConfigItems.Add(markerRow);
                configItems.Add(markerRow);
            }

            var managedRows = configItems
                .Where(item => SensitiveConfigKeys.TryGetCanonicalKey(item.ConfigName, out _))
                .GroupBy(item => SensitiveConfigKeys.CanonicalNames[item.ConfigName], StringComparer.Ordinal)
                .ToList();
            foreach (var group in managedRows)
            {
                var rows = group.ToList();
                if (rows.Count > 1)
                    throw new InvalidOperationException(
                        $"Duplicate managed config key '{group.Key}' exists with multiple casings.");

                var row = rows[0];
                if (string.Equals(row.ConfigName, group.Key, StringComparison.Ordinal))
                    continue;

                // EF cannot update a primary-key value in place portably. Remove
                // and insert the canonical row in this same startup transaction,
                // preserving the value and encryption flag byte-for-byte.
                dbContext.ConfigItems.Remove(row);
                dbContext.ConfigItems.Add(new ConfigItem
                {
                    ConfigName = group.Key,
                    ConfigValue = row.ConfigValue,
                    IsEncrypted = row.IsEncrypted,
                });
            }

            if (dbContext.ChangeTracker.HasChanges())
                await dbContext.SaveChangesAsync().ConfigureAwait(false);

            newValues = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var configItem in configItems)
            {
                var configName = CanonicalizeLoadedName(configItem.ConfigName);
                if (newValues.ContainsKey(configName))
                    throw new InvalidOperationException(
                        $"Duplicate managed config key '{configName}' exists with multiple casings.");

                var value = configItem.ConfigValue;
                if (configItem.IsEncrypted)
                {
                    var (plaintext, usedOldKey, _) =
                        _encryption.Decrypt(configName, configItem.ConfigValue);
                    value = plaintext;

                    if (usedOldKey)
                    {
                        Log.Warning(
                            "Config key '{Name}' was decrypted with NZBDAV_MASTER_KEY_OLD outside the startup rotation pass. " +
                            "A full rotation requires a restart; keep NZBDAV_MASTER_KEY_OLD set until then.",
                            configName);
                    }
                }

                newValues[configName] = value;
            }

            // Persist the bounded ring when loading an installation that still
            // has the singular previous-key pair. This makes the migration
            // durable and means the next rotation cannot discard K0.
            if (!newValues.ContainsKey("api.strm-key-previous-ring")
                && newValues.TryGetValue("api.strm-key-previous", out var migratedKey)
                && newValues.TryGetValue("api.strm-key-previous-expires-at", out var migratedExpiry)
                && !string.IsNullOrWhiteSpace(migratedKey)
                && ParseDateTimeOffset(migratedExpiry) is { } parsedMigratedExpiry)
            {
                var ringValue = SerializeStreamKeyRing([new StreamKeyHistoryEntry(migratedKey, parsedMigratedExpiry)]);
                var ringRow = new ConfigItem
                {
                    ConfigName = "api.strm-key-previous-ring",
                    ConfigValue = _encryption.IsKeyConfigured
                        ? _encryption.Encrypt("api.strm-key-previous-ring", ringValue)
                        : ringValue,
                    IsEncrypted = _encryption.IsKeyConfigured,
                };
                dbContext.ConfigItems.Add(ringRow);
                newValues[ringRow.ConfigName] = ringValue;
                await dbContext.SaveChangesAsync().ConfigureAwait(false);
            }

            await transaction.CommitAsync().ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        Dictionary<string, string>? changedConfig = null;
        lock (_config)
        {
            foreach (var (key, value) in newValues)
            {
                if (_config.TryGetValue(key, out var existing) && existing == value)
                    continue;
                changedConfig ??= new Dictionary<string, string>(StringComparer.Ordinal);
                changedConfig[key] = value;
            }

            _config.Clear();
            foreach (var (key, value) in newValues)
                _config[key] = value;
        }

        if (changedConfig is { Count: > 0 })
        {
            Log.Information("Config hot-reloaded {Count} changed key(s): {Keys}", changedConfig.Count, string.Join(", ", changedConfig.Keys));
            PublishConfigChanged(new ConfigEventArgs
            {
                ChangedConfig = RedactChangedConfig(changedConfig),
            });
        }
    }

    private string? GetConfigValue(string configName)
    {
        if (SensitiveConfigKeys.TryGetCanonicalKey(configName, out var canonicalName))
            configName = canonicalName;

        lock (_config)
        {
            return _config.TryGetValue(configName, out string? value) ? value : null;
        }
    }

    // Settings write paths may preserve an omitted write-only secret, but no
    // controller may serialize this value into a response.
    internal string? GetConfigValueForWrite(string configName) => GetConfigValue(configName);

    private string? GetConfigValueNoLock(string configName)
    {
        if (SensitiveConfigKeys.TryGetCanonicalKey(configName, out var canonicalName))
            configName = canonicalName;

        return _config.TryGetValue(configName, out string? value) ? value : null;
    }

    private T? GetConfigValue<T>(string configName)
    {
        var rawValue = StringUtil.EmptyToNull(GetConfigValue(configName));
        return rawValue == null ? default : JsonSerializer.Deserialize<T>(rawValue, ConfigValueJsonOptions);
    }

    // Configuration rows are produced by both legacy numeric-enum writers and
    // setup's explicit string-enum contract. Accept both representations when
    // hot-reloading usenet.providers so a valid setup save cannot leave the
    // live NNTP pipeline on stale settings.
    private static readonly JsonSerializerOptions ConfigValueJsonOptions = new()
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public void UpdateValues(List<ConfigItem> configItems)
    {
        ConfigMutationGate.Wait();
        try
        {
            UpdateValuesNoLock(configItems);
        }
        finally
        {
            ConfigMutationGate.Release();
        }
    }

    // The persistence controllers call this while already holding the gate.
    // Keeping the core operation separate avoids a semaphore self-deadlock.
    internal void UpdateValuesNoLock(List<ConfigItem> configItems)
    {
        CanonicalizeManagedNames(configItems);
        EnsureGenericSetupCacheWriteAllowed(configItems);

        var changedConfig = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var item in configItems)
        {
            changedConfig[item.ConfigName] = item.ConfigValue;

            if (!SensitiveConfigKeys.IsSensitive(item.ConfigName))
            {
                item.IsEncrypted = false;
                continue;
            }

            if (!_encryption.IsKeyConfigured)
            {
                item.IsEncrypted = false;
                continue;
            }

            EncryptPlaintextItem(item);
        }

        lock (_config)
        {
            foreach (var configItem in configItems)
            {
                _config[configItem.ConfigName] = changedConfig[configItem.ConfigName];
            }
        }

        PublishConfigChanged(new ConfigEventArgs
        {
            ChangedConfig = RedactChangedConfig(changedConfig),
        });
    }

    public List<ConfigItem> PrepareForStorage(List<ConfigItem> configItems)
    {
        CanonicalizeManagedNames(configItems);
        EnsureGenericSetupWriteAllowed(configItems);

        return configItems.Select(item =>
        {
            var copy = new ConfigItem
            {
                ConfigName = item.ConfigName,
                ConfigValue = item.ConfigValue,
                IsEncrypted = item.IsEncrypted
            };

            if (!SensitiveConfigKeys.IsSensitive(copy.ConfigName))
            {
                copy.IsEncrypted = false;
                return copy;
            }

            if (!_encryption.IsKeyConfigured)
            {
                if (IsSetupPersistenceSecret(copy.ConfigName))
                    throw MasterKeyRequiredForSetup();

                copy.IsEncrypted = false;
                return copy;
            }

            EncryptPlaintextItem(copy);
            return copy;
        }).ToList();
    }

    public string GetRcloneMountDir()
    {
        var mountDir = StringUtil.EmptyToNull(GetConfigValue("rclone.mount-dir"))
                       ?? EnvironmentUtil.GetEnvironmentVariable("MOUNT_DIR")
                       ?? "/mnt/nzbdav";
        if (mountDir.EndsWith('/')) mountDir = mountDir.TrimEnd('/');
        return mountDir;
    }

    public string GetApiKey()
    {
        // This env fallback is load-bearing during onboarding/bootstrap before
        // the persisted ConfigItems row exists.
        return StringUtil.EmptyToNull(GetConfigValue("api.key"))
               ?? EnvironmentUtil.GetRequiredVariable("FRONTEND_BACKEND_API_KEY");
    }

    public string GetStrmKey()
    {
        return GetConfigValue("api.strm-key")
               ?? throw new InvalidOperationException("The `api.strm-key` config does not exist.");
    }

    internal sealed record StreamKeyHistoryEntry(string Key, DateTimeOffset ExpiresAt);
    internal sealed record StreamTokenState(
        string Current,
        IReadOnlyList<StreamKeyHistoryEntry> PreviousKeys,
        DateTimeOffset? LegacyMigrationStart,
        bool PreviousRingValid = true)
    {
        // Compatibility accessors for callers that only understand the old
        // singular previous-key fields.
        public string? Previous => PreviousKeys.FirstOrDefault()?.Key;
        public DateTimeOffset? PreviousExpiresAt => PreviousKeys.FirstOrDefault()?.ExpiresAt;
    }

    internal StreamTokenState GetStreamTokenState()
        => GetStreamTokenStateNoLock();

    internal StreamTokenState GetStreamTokenStateSnapshot()
    {
        ConfigMutationGate.Wait();
        try
        {
            return GetStreamTokenState();
        }
        finally
        {
            ConfigMutationGate.Release();
        }
    }

    private StreamTokenState GetStreamTokenStateNoLock()
    {
        lock (_config)
        {
            var ringText = StringUtil.EmptyToNull(GetConfigValueNoLock("api.strm-key-previous-ring"));
            var ring = ParseStreamKeyRing(ringText, out var ringValid);
            if (ringText is null)
            {
                var oldKey = StringUtil.EmptyToNull(GetConfigValueNoLock("api.strm-key-previous"));
                var oldExpiry = ParseDateTimeOffset(GetConfigValueNoLock("api.strm-key-previous-expires-at"));
                if (oldKey is not null && oldExpiry is not null)
                    ring = [new StreamKeyHistoryEntry(oldKey, oldExpiry.Value)];
            }

            return new StreamTokenState(
                GetConfigValueNoLock("api.strm-key")
                ?? throw new InvalidOperationException("The `api.strm-key` config does not exist."),
                ring,
                ParseDateTimeOffset(GetConfigValueNoLock(SetupConfigKeys.StreamTokenLegacyMigrationStart)),
                ringValid);
        }
    }

    public string? GetStrmKeyPrevious()
    {
        lock (_config)
        {
            var historyKey = GetPreviousHistoryNoLock().FirstOrDefault()?.Key;
            return historyKey ?? StringUtil.EmptyToNull(GetConfigValueNoLock("api.strm-key-previous"));
        }
    }

    public DateTimeOffset? GetStrmKeyPreviousExpiresAtUtc()
    {
        lock (_config)
            return GetPreviousHistoryNoLock().FirstOrDefault()?.ExpiresAt;
    }

    private IReadOnlyList<StreamKeyHistoryEntry> GetPreviousHistoryNoLock()
    {
        var ringText = StringUtil.EmptyToNull(GetConfigValueNoLock("api.strm-key-previous-ring"));
        var ring = ParseStreamKeyRing(ringText, out _);
        if (ringText is not null)
            return ring;

        var oldKey = StringUtil.EmptyToNull(GetConfigValueNoLock("api.strm-key-previous"));
        var oldExpiry = ParseDateTimeOffset(GetConfigValueNoLock("api.strm-key-previous-expires-at"));
        return oldKey is not null && oldExpiry is not null
            ? [new StreamKeyHistoryEntry(oldKey, oldExpiry.Value)]
            : [];
    }

    internal static string SerializeStreamKeyRing(IEnumerable<StreamKeyHistoryEntry> entries)
        => JsonSerializer.Serialize(entries.Select(entry => new
        {
            key = entry.Key,
            expiresAt = entry.ExpiresAt.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture)
        }), StreamKeyRingJsonOptions);

    private static IReadOnlyList<StreamKeyHistoryEntry> ParseStreamKeyRing(string? raw, out bool valid)
    {
        valid = true;
        if (raw is null)
            return [];

        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind != JsonValueKind.Array
                || document.RootElement.GetArrayLength() > MaxStreamSigningKeyHistory)
            {
                valid = false;
                return [];
            }

            var result = new List<StreamKeyHistoryEntry>();
            var seenKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object
                    || !element.TryGetProperty("key", out var keyElement)
                    || !element.TryGetProperty("expiresAt", out var expiryElement)
                    || keyElement.ValueKind != JsonValueKind.String
                    || expiryElement.ValueKind != JsonValueKind.String)
                {
                    valid = false;
                    return [];
                }

                var key = keyElement.GetString();
                var expiryText = expiryElement.GetString();
                if (string.IsNullOrWhiteSpace(key) || key.Length > MaxStreamSigningKeyLength
                    || !seenKeys.Add(key)
                    || expiryText is null || !DateTimeOffset.TryParse(
                        expiryText, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out var expiry))
                {
                    valid = false;
                    return [];
                }

                result.Add(new StreamKeyHistoryEntry(key, expiry));
            }

            return result;
        }
        catch (JsonException)
        {
            valid = false;
            return [];
        }
    }

    private static DateTimeOffset? ParseDateTimeOffset(string? raw)
        => raw is null
            ? null
            : DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed)
                ? parsed
                : null;


    public DateTimeOffset? GetStreamTokenLegacyMigrationStartUtc()
    {
        var raw = StringUtil.EmptyToNull(GetConfigValue(SetupConfigKeys.StreamTokenLegacyMigrationStart));
        return ParseDateTimeOffset(raw);
    }

    public string? GetPluginApiKey()
        => StringUtil.EmptyToNull(GetConfigValue(SetupConfigKeys.PluginApiKey));

    public string? GetSetupUsenetProviders()
        => StringUtil.EmptyToNull(GetConfigValue(SetupConfigKeys.UsenetProviders));

    public string? GetSetupIndexers()
        => StringUtil.EmptyToNull(GetConfigValue(SetupConfigKeys.Indexers));

    public string? GetSetupJellyfinApiKey()
        => StringUtil.EmptyToNull(GetConfigValue(SetupConfigKeys.JellyfinApiKey));

    public string? GetSetupPluginApiKey()
        => GetPluginApiKey();

    public string? GetSetupPluginApiKeyPrevious()
        => StringUtil.EmptyToNull(GetConfigValue(SetupConfigKeys.PluginApiKeyPrevious));

    public DateTime? GetSetupPluginApiKeyPreviousExpiresAtUtc()
    {
        var raw = StringUtil.EmptyToNull(GetConfigValue(SetupConfigKeys.PluginApiKeyPreviousExpiresAt));
        return raw is null
            ? null
            : DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed)
                ? parsed
                : null;
    }

    public string? GetSetupRunProgress()
        => StringUtil.EmptyToNull(GetConfigValue(SetupConfigKeys.RunProgress));

    public string? GetSetupRunProgressReasons()
        => StringUtil.EmptyToNull(GetConfigValue(SetupConfigKeys.RunProgressReasons));

    public bool IsSetupRepairRequired()
        => string.Equals(GetConfigValue(SetupConfigKeys.RepairRequired), "true", StringComparison.OrdinalIgnoreCase);

    public string? GetSetupSonarrRepairReason()
        => StringUtil.EmptyToNull(GetConfigValue(SetupConfigKeys.SonarrRepairReason));

    public string? GetSetupRadarrRepairReason()
        => StringUtil.EmptyToNull(GetConfigValue(SetupConfigKeys.RadarrRepairReason));

    public DateTime? GetSetupRepairCheckedAtUtc()
    {
        var raw = StringUtil.EmptyToNull(GetConfigValue(SetupConfigKeys.RepairCheckedAtUtc));
        return raw is not null && DateTime.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed.ToUniversalTime()
            : null;
    }

    public bool IsSetupCompleted()
        => bool.TryParse(GetConfigValue(SetupConfigKeys.Completed), out var completed) && completed;

    /// <summary>
    /// Prepares setup rows for persistence while requiring encryption for every
    /// setup secret. The general configuration path retains its legacy
    /// plaintext-compatible behavior for existing installations.
    /// </summary>
    internal List<ConfigItem> PrepareSetupForStorage(List<ConfigItem> configItems)
    {
        CanonicalizeManagedNames(configItems);
        if (configItems.Any(item => !IsSetupPersistenceKey(item.ConfigName)))
            throw new InvalidOperationException("Setup persistence accepts only setup config keys.");

        return configItems.Select(item =>
        {
            var copy = new ConfigItem
            {
                ConfigName = item.ConfigName,
                ConfigValue = item.ConfigValue,
                IsEncrypted = item.IsEncrypted,
            };

            if (IsSetupPersistenceMarker(copy.ConfigName))
            {
                if (IsCompletionMarker(copy.ConfigName)
                    && (copy.IsEncrypted || !bool.TryParse(copy.ConfigValue, out _)))
                    throw new InvalidOperationException("The setup completion marker must be an unencrypted boolean.");

                // Markers are deliberately canonical plaintext. This branch
                // is the setup-only equivalent of the generic non-sensitive
                // path and prevents a future registry change from turning a
                // health/status value into ciphertext.
                copy.IsEncrypted = false;
                return copy;
            }

            if (!IsSetupPersistenceSecret(copy.ConfigName))
                throw new InvalidOperationException($"Unsupported setup config key '{copy.ConfigName}'.");

            // A retry may provide the already-persisted setup row. Authenticate
            // it before preserving it; a valid old-key value is rotated to the
            // current primary key rather than copied back unchanged.
            if (copy.IsEncrypted)
            {
                if (!ConfigEncryptionService.IsEncryptedFormat(copy.ConfigValue))
                    throw new InvalidOperationException(
                        $"Encrypted setup config key '{copy.ConfigName}' does not contain valid ciphertext.");

                var (plaintext, usedOldKey, requiresFormatUpgrade) =
                    _encryption.Decrypt(copy.ConfigName, copy.ConfigValue);
                if (usedOldKey || requiresFormatUpgrade)
                    copy.ConfigValue = _encryption.Encrypt(copy.ConfigName, plaintext);

                copy.IsEncrypted = true;
                return copy;
            }

            if (!_encryption.IsKeyConfigured)
                throw MasterKeyRequiredForSetup();

            EncryptPlaintextItem(copy);
            return copy;
        }).ToList();
    }

    /// <summary>
    /// Applies the canonical spelling to every managed key and rejects
    /// case-only duplicates before a value can enter the cache or database.
    /// </summary>
    private static void CanonicalizeManagedNames(IList<ConfigItem> configItems)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in configItems)
        {
            var canonicalName = CanonicalizeManagedName(item.ConfigName);
            if (canonicalName is null)
                continue;

            if (!seen.Add(canonicalName))
                throw new InvalidOperationException(
                    $"Duplicate managed config key '{canonicalName}' was supplied with multiple casings.");

            item.ConfigName = canonicalName;
        }
    }

    private static string? CanonicalizeManagedName(string configName)
    {
        if (SensitiveConfigKeys.TryGetCanonicalKey(configName, out var canonicalName))
            return canonicalName;

        if (IsSetupKey(configName))
            throw new InvalidOperationException(
                $"Unknown reserved setup config key '{configName}'.");

        return null;
    }

    private static string CanonicalizeLoadedName(string configName)
        => CanonicalizeManagedName(configName) ?? configName;

    private static bool IsSetupPersistenceKey(string configName)
        => IsSetupKey(configName)
           || string.Equals(configName, "usenet.providers", StringComparison.OrdinalIgnoreCase);

    private static bool IsSetupKey(string configName)
        => configName.StartsWith("setup.", StringComparison.OrdinalIgnoreCase);

    private static bool IsCompletionMarker(string configName)
        => string.Equals(configName, SetupConfigKeys.Completed, StringComparison.OrdinalIgnoreCase);

    private static bool IsSetupPersistenceMarker(string configName)
        => IsSetupKey(configName)
           && !IsSetupPersistenceSecret(configName);

    private static bool IsSetupPersistenceSecret(string configName)
        => string.Equals(configName, "usenet.providers", StringComparison.OrdinalIgnoreCase)
           || (IsSetupKey(configName)
               && !IsCompletionMarker(configName)
               && SensitiveConfigKeys.IsSensitive(configName));

    private static InvalidOperationException MasterKeyRequiredForSetup()
        => new("NZBDAV_MASTER_KEY is required before setup secrets can be persisted.");

    private static void EnsureGenericSetupWriteAllowed(IEnumerable<ConfigItem> configItems)
    {
        foreach (var item in configItems)
        {
            if (!IsSetupPersistenceKey(item.ConfigName))
                continue;

            throw new InvalidOperationException(
                $"Setup config key '{item.ConfigName}' can only be changed through the dedicated setup persistence path.");
        }
    }

    // UpdateValues also serves the in-memory construction path used by queue
    // workers. Provider settings may be loaded into that cache here, but every
    // setup-owned marker/secret remains restricted to SetupConfigPersistence;
    // the database-facing PrepareForStorage path rejects providers as well.
    private static void EnsureGenericSetupCacheWriteAllowed(IEnumerable<ConfigItem> configItems)
    {
        foreach (var item in configItems)
        {
            if (IsSetupKey(item.ConfigName))
                throw new InvalidOperationException(
                    $"Setup config key '{item.ConfigName}' can only be changed through the dedicated setup persistence path.");
        }
    }

    /// <summary>
    /// Authenticates an existing setup ciphertext for the persistence upsert.
    /// Plaintext stays within that boundary and is never logged or exposed by
    /// the setup API.
    /// </summary>
    internal (string Plaintext, bool UsedOldKey, bool RequiresFormatUpgrade) DecryptSetupValue(
        string configName,
        string ciphertext)
        => _encryption.Decrypt(configName, ciphertext);

    // Completion snapshots live in their own table and must not enter the
    // normal configuration cache. They still use the same authenticated
    // installation encryption boundary and key-rotation semantics.
    internal string EncryptSetupSnapshot(string configName, string plaintext)
        => _encryption.Encrypt(configName, plaintext);

    internal (string Plaintext, bool UsedOldKey, bool RequiresFormatUpgrade) DecryptSetupSnapshot(
        string configName,
        string ciphertext)
        => _encryption.Decrypt(configName, ciphertext);

    /// <summary>
    /// Updates the in-memory setup snapshot after its transaction has committed.
    /// This intentionally does not expose or re-encrypt values.
    /// </summary>
    internal void ApplySetupValues(IEnumerable<ConfigItem> configItems)
    {
        ConfigMutationGate.Wait();
        try
        {
            ApplySetupValuesNoLock(configItems);
        }
        finally
        {
            ConfigMutationGate.Release();
        }
    }

    // Called by SetupConfigPersistence while the common gate is held.
    internal void ApplySetupValuesNoLock(IEnumerable<ConfigItem> configItems)
    {
        var items = configItems.Select(item => new ConfigItem
        {
            ConfigName = item.ConfigName,
            ConfigValue = item.ConfigValue,
            IsEncrypted = false,
        }).ToList();
        CanonicalizeManagedNames(items);
        if (items.Any(item => !IsSetupPersistenceKey(item.ConfigName)))
            throw new InvalidOperationException("Setup persistence accepts only setup config keys.");

        var changedConfig = new Dictionary<string, string>(StringComparer.Ordinal);
        lock (_config)
        {
            foreach (var item in items)
            {
                if (_config.TryGetValue(item.ConfigName, out var existing)
                    && string.Equals(existing, item.ConfigValue, StringComparison.Ordinal))
                    continue;

                changedConfig[item.ConfigName] = item.ConfigValue;
                _config[item.ConfigName] = item.ConfigValue;
            }
        }

        if (changedConfig.Count > 0)
        {
            PublishConfigChanged(new ConfigEventArgs
            {
                ChangedConfig = RedactChangedConfig(changedConfig),
            });
        }
    }

    /// <summary>
    /// Runs a complete durable config operation and its publication under the
    /// process-wide ordering gate. Callers must use the no-lock core methods
    /// when this callback performs a cache publication.
    /// </summary>
    internal async Task WithMutationGateAsync(
        Func<Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await ConfigMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await operation().ConfigureAwait(false);
        }
        finally
        {
            ConfigMutationGate.Release();
        }
    }

    internal async Task<T> WithMutationGateAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await ConfigMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            ConfigMutationGate.Release();
        }
    }

    private void PublishConfigChanged(ConfigEventArgs eventArgs)
    {
        // Subscribers are independent cache/service consumers. One broken
        // subscriber must not prevent later subscribers from observing a
        // committed change or turn a successful save into a failure.
        foreach (var subscriber in OnConfigChanged?.GetInvocationList()
                     ?? Array.Empty<Delegate>())
        {
            try
            {
                // Give each subscriber its own immutable wrapper. In addition
                // to preventing mutation through a cast to IDictionary, this
                // prevents one subscriber from affecting later subscribers.
                var subscriberArgs = new ConfigEventArgs
                {
                    ChangedConfig = ReadOnlyChangedConfig(eventArgs.ChangedConfig),
                };
                ((EventHandler<ConfigEventArgs>)subscriber)(this, subscriberArgs);
            }
            catch (Exception exception)
            {
                Log.Warning(exception, "A configuration-change subscriber failed; continuing notification.");
            }
        }
    }

    private static IReadOnlyDictionary<string, string> RedactChangedConfig(
        IReadOnlyDictionary<string, string> changedConfig)
        => ReadOnlyChangedConfig(changedConfig.ToDictionary(
            pair => pair.Key,
            pair => SensitiveConfigKeys.IsSensitive(pair.Key) ? "[redacted]" : pair.Value,
            StringComparer.Ordinal));

    private static IReadOnlyDictionary<string, string> ReadOnlyChangedConfig(
        IReadOnlyDictionary<string, string> changedConfig)
        => new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(changedConfig, StringComparer.Ordinal));

    private void EncryptPlaintextItem(ConfigItem item)
    {
        if (ConfigEncryptionService.IsEncryptedFormat(item.ConfigValue))
        {
            throw new InvalidOperationException(
                $"Double-encryption detected for config key '{item.ConfigName}'. " +
                "Value already has an encrypted format prefix before the encryption pass. " +
                "Reject the write.");
        }

        item.ConfigValue = _encryption.Encrypt(item.ConfigName, item.ConfigValue);
        item.IsEncrypted = true;
    }

    public List<string> GetApiCategories()
    {
        var value = StringUtil.EmptyToNull(GetConfigValue("api.categories"))
                    ?? EnvironmentUtil.GetEnvironmentVariable("CATEGORIES")
                    ?? "audio,software,tv,movies";

        return value.Split(',')
            .Prepend(GetManualUploadCategory())
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
    }

    public string GetManualUploadCategory()
    {
        return StringUtil.EmptyToNull(GetConfigValue("api.manual-category"))
               ?? "uncategorized";
    }

    public string? GetWebdavUser()
    {
        return StringUtil.EmptyToNull(GetConfigValue("webdav.user"))
               ?? EnvironmentUtil.GetEnvironmentVariable("WEBDAV_USER")
               ?? "admin";
    }

    public string? GetWebdavPasswordHash()
    {
        var hashedPass = StringUtil.EmptyToNull(GetConfigValue("webdav.pass"));
        if (hashedPass != null) return hashedPass;
        var pass = EnvironmentUtil.GetEnvironmentVariable("WEBDAV_PASSWORD");
        if (pass != null) return PasswordUtil.Hash(pass);
        return null;
    }

    public bool IsEnsureImportableVideoEnabled()
    {
        var defaultValue = true;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("api.ensure-importable-video"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public bool ShowHiddenWebdavFiles()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("webdav.show-hidden-files"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public string? GetLibraryDir()
    {
        return StringUtil.EmptyToNull(GetConfigValue("media.library-dir"));
    }

    public int GetMaxDownloadConnections()
    {
        // Default to the connection budget the operator actually declared for
        // their providers. This used to be clamped to 15, which silently threw
        // away most of a large provider allowance and left the pool idle on
        // fast uplinks. The pool is already sized to TotalPooledConnections, so
        // this only stops the downloader from under-using it.
        return int.Parse(
            StringUtil.EmptyToNull(GetConfigValue("usenet.max-download-connections"))
            ?? GetUsenetProviderConfig().TotalPooledConnections.ToString()
        );
    }

    /// <summary>
    /// How many read-ahead segments to warm concurrently. Warming used to be
    /// strictly sequential, which pinned the prefetcher to a single NNTP
    /// connection and capped streaming throughput far below the available
    /// uplink. Defaults to most of the download budget while leaving headroom
    /// so live reads are never starved by prefetch.
    /// </summary>
    public int GetReadAheadConcurrency()
    {
        var configured = StringUtil.EmptyToNull(GetConfigValue("cache.read-ahead-concurrency"));
        if (configured != null)
            return Math.Max(1, int.Parse(configured));

        return Math.Max(4, GetMaxDownloadConnections() * 3 / 4);
    }

    /// <summary>
    /// How many download requests may queue behind the connection budget before
    /// the downloader sheds load. This has to scale with concurrent viewers,
    /// not just with the connection count: every playback session keeps up to
    /// `usenet.article-buffer-size` segment fetches in flight, so a small
    /// multiple of the connection budget starts throwing mid-playback at only a
    /// handful of simultaneous streams. Queued waiters are cheap (one
    /// TaskCompletionSource each), so this can be generous.
    /// </summary>
    public int GetMaxPendingDownloads()
    {
        var configured = StringUtil.EmptyToNull(GetConfigValue("usenet.max-pending-downloads"));
        if (configured != null)
            return Math.Max(1, int.Parse(configured));

        return Math.Max(256, GetMaxDownloadConnections() * 16);
    }

    public int GetArticleBufferSize()
    {
        return int.Parse(
            StringUtil.EmptyToNull(GetConfigValue("usenet.article-buffer-size"))
            ?? "40"
        );
    }

    public SemaphorePriorityOdds GetStreamingPriority()
    {
        var stringValue = StringUtil.EmptyToNull(GetConfigValue("usenet.streaming-priority"));
        var numericalValue = int.Parse(stringValue ?? "80");
        return new SemaphorePriorityOdds() { HighPriorityOdds = numericalValue };
    }

    public bool IsEnforceReadonlyWebdavEnabled()
    {
        var defaultValue = true;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("webdav.enforce-readonly"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public HashSet<string> GetEnsureArticleExistenceCategories()
    {
        var configValue = GetConfigValue("api.ensure-article-existence-categories");
        return (configValue ?? "").Split(',')
            .Select(x => x.Trim())
            .Select(x => x.ToLower())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet();
    }

    public bool IsPreviewPar2FilesEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("webdav.preview-par2-files"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public bool IsIgnoreSabHistoryLimitEnabled()
    {
        var defaultValue = true;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("api.ignore-history-limit"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public bool IsRepairJobEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("repair.enable"));
        var isRepairJobEnabled = (configValue != null ? bool.Parse(configValue) : defaultValue);
        return isRepairJobEnabled
               && GetLibraryDir() != null
               && GetArrConfig().GetInstanceCount() > 0;
    }

    public ArrConfig GetArrConfig()
    {
        var defaultValue = new ArrConfig();
        return GetConfigValue<ArrConfig>("arr.instances") ?? defaultValue;
    }

    public UsenetProviderConfig GetUsenetProviderConfig()
    {
        var defaultValue = new UsenetProviderConfig();
        return GetConfigValue<UsenetProviderConfig>("usenet.providers") ?? defaultValue;
    }

    public string GetDuplicateNzbBehavior()
    {
        var defaultValue = "increment";
        return GetConfigValue("api.duplicate-nzb-behavior") ?? defaultValue;
    }

    public HashSet<string> GetBlocklistedFiles()
    {
        var defaultValue = "*.nfo, *.par2, *.sfv, *sample.mkv";
        return (GetConfigValue("api.download-file-blocklist") ?? defaultValue)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.ToLower())
            .ToHashSet();
    }

    public string GetImportStrategy()
    {
        // The full-stack image has no rclone mount: its compose wires the Arr
        // root folders and the Jellyfin library to the strm output directory.
        // Defaulting to symlinks there made every completed job report a
        // /mnt/nzbdav/completed-symlinks path that exists in no container, so
        // the Arrs failed the import with "No files found are eligible for
        // import". An explicit operator value still wins.
        return GetConfigValue("api.import-strategy")
            ?? (SetupEnvironmentOptions.FromEnvironment().FullStackEnabled ? "strm" : "symlinks");
    }

    public string GetStrmCompletedDownloadDir()
    {
        return GetConfigValue("api.completed-downloads-dir") ?? "/data/completed-downloads";
    }

    public string GetBaseUrl()
    {
        return GetConfigValue("general.base-url") ?? "http://localhost:3000";
    }

    public string GetUserAgent()
    {
        var defaultValue = $"nzbdav/{AppVersion}";
        return StringUtil.EmptyToNull(GetConfigValue("api.user-agent"))
               ?? EnvironmentUtil.GetEnvironmentVariable("NZB_GRAB_USER_AGENT")
               ?? defaultValue;
    }

    public int GetCacheMaxSizeGb()
    {
        // NZBDAV_CACHE_MAX_SIZE_GB overrides the shared DB config so each node
        // can cap its local cache independently (e.g. ingest nodes with smaller disks).
        var envOverride = EnvironmentUtil.GetEnvironmentVariable("NZBDAV_CACHE_MAX_SIZE_GB");
        if (!string.IsNullOrEmpty(envOverride) && int.TryParse(envOverride, out var envGb))
            return envGb;
        return int.Parse(StringUtil.EmptyToNull(GetConfigValue("cache.max-size-gb")) ?? "10");
    }

    public int GetCacheMaxAgeHours()
        => int.Parse(StringUtil.EmptyToNull(GetConfigValue("cache.max-age-hours")) ?? "6");

    public int GetQueueParallelism()
    {
        var envOverride = EnvironmentUtil.GetEnvironmentVariable("NZBDAV_QUEUE_PARALLELISM");
        if (!string.IsNullOrEmpty(envOverride) && int.TryParse(envOverride, out var envVal))
            return Math.Max(1, envVal);
        return int.Parse(StringUtil.EmptyToNull(GetConfigValue("queue.parallelism")) ?? "1");
    }

    public string? GetCacheDirectory()
        => StringUtil.EmptyToNull(GetConfigValue("cache.directory"));

    public bool IsSharedHeaderCacheEnabled()
    {
        if (string.IsNullOrEmpty(EnvironmentUtil.GetEnvironmentVariable("DATABASE_URL")))
            return false;

        var value = StringUtil.EmptyToNull(GetConfigValue("cache.metadata-shared-enabled"));
        return value == null || bool.Parse(value);
    }

    public int GetMetadataRetentionDays()
        => int.Parse(StringUtil.EmptyToNull(GetConfigValue("cache.metadata-retention-days")) ?? "90");

    public bool IsPrecacheEnabled()
    {
        var val = StringUtil.EmptyToNull(GetConfigValue("cache.precache-enable"));
        return val != null ? bool.Parse(val) : true;
    }

    public long GetPrecacheMaxFileSize()
        => long.Parse(StringUtil.EmptyToNull(GetConfigValue("cache.precache-max-file-size-mb")) ?? "5") * 1024 * 1024;

    public int GetReadAheadSegments()
        => int.Parse(StringUtil.EmptyToNull(GetConfigValue("cache.read-ahead-segments")) ?? "200");

    public bool IsReadAheadEnabled()
    {
        var val = StringUtil.EmptyToNull(GetConfigValue("cache.read-ahead-enable"));
        return val != null ? bool.Parse(val) : true;
    }

    public int GetSnapshotDebounceSeconds()
        => int.Parse(StringUtil.EmptyToNull(GetConfigValue("cache.snapshot-debounce-seconds")) ?? "5");

    public bool IsL2Enabled()
    {
        var val = StringUtil.EmptyToNull(GetConfigValue("cache.l2.enabled"));
        return val != null ? bool.Parse(val) : false;
    }

    public string? GetL2Endpoint()
        => StringUtil.EmptyToNull(GetConfigValue("cache.l2.endpoint"));

    /// <summary>
    /// Root directory for the filesystem-backed L2 tier (typically a mounted NAS).
    /// When set, it takes precedence over the S3 endpoint: the object-storage
    /// round trip is pure overhead once the bytes are reachable through the
    /// filesystem.
    /// </summary>
    public string? GetL2Path()
    {
        // NZBDAV_L2_PATH overrides the shared DB config for the same reason as
        // NZBDAV_CACHE_MAX_SIZE_GB: a mount point is a property of the node, not
        // of the cluster. Nodes without the NAS mounted must not inherit it.
        var envOverride = EnvironmentUtil.GetEnvironmentVariable("NZBDAV_L2_PATH");
        if (!string.IsNullOrWhiteSpace(envOverride))
            return envOverride;
        return StringUtil.EmptyToNull(GetConfigValue("cache.l2.path"));
    }

    public string GetL2BucketName()
        => StringUtil.EmptyToNull(GetConfigValue("cache.l2.bucket-name")) ?? "nzbdav-segments";

    public string? GetL2AccessKey()
        => StringUtil.EmptyToNull(GetConfigValue("cache.l2.access-key"))
           ?? EnvironmentUtil.GetEnvironmentVariable("NZBDAV_L2_ACCESS_KEY");

    public string? GetL2SecretKey()
        => StringUtil.EmptyToNull(GetConfigValue("cache.l2.secret-key"))
           ?? EnvironmentUtil.GetEnvironmentVariable("NZBDAV_L2_SECRET_KEY");

    public bool IsL2SslEnabled()
    {
        var val = StringUtil.EmptyToNull(GetConfigValue("cache.l2.ssl"));
        return val != null ? bool.Parse(val) : false;
    }

    public int GetL2WriteQueueCapacity()
        => int.Parse(StringUtil.EmptyToNull(GetConfigValue("cache.l2.write-queue-capacity")) ?? "16384");

    public int GetL2WriterParallelism()
        => int.Parse(StringUtil.EmptyToNull(GetConfigValue("cache.l2.writer-parallelism")) ?? "4");

    public string GetL2PrewarmPolicy()
        => StringUtil.EmptyToNull(GetConfigValue("cache.l2.prewarm-policy"))?.ToLowerInvariant() ?? "first-middle-last";

    public TimeSpan GetL2ReadTimeout()
        => TimeSpan.FromSeconds(
            int.Parse(StringUtil.EmptyToNull(GetConfigValue("cache.l2.read-timeout-seconds")) ?? "30"));

    public TimeSpan GetL2WriteTimeout()
        => TimeSpan.FromSeconds(
            int.Parse(StringUtil.EmptyToNull(GetConfigValue("cache.l2.write-timeout-seconds")) ?? "60"));

    public string GetL2StorageClass()
        => StringUtil.EmptyToNull(GetConfigValue("cache.l2.storage-class"))?.ToUpperInvariant()
           ?? "STANDARD";

    public sealed class ConfigEventArgs : EventArgs
    {
        public required IReadOnlyDictionary<string, string> ChangedConfig { get; init; }
    }
}
