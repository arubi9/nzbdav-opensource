using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Setup.Core;
using Serilog;

namespace NzbWebDAV.Services;

public static class StartupEncryptionCheck
{
    private static readonly HashSet<string> BootstrapConfigKeys = new(StringComparer.Ordinal)
    {
        "api.key",
        "api.strm-key",
    };

    public static async Task RunAsync(
        DavDatabaseContext db,
        ConfigEncryptionService encryption,
        CancellationToken cancellationToken = default)
    {
        var allConfig = await db.ConfigItems
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var hasEncryptedCompletionRows = await db.SetupCompletionOperations
            .AnyAsync(operation => operation.ActiveSessionCiphertext != null
                                || operation.RevocationSessionCiphertext != null
                                || operation.CandidateSessionCiphertext != null
                                || operation.CandidateOperationCiphertext != null
                                || operation.EmergencySessionCiphertext != null
                                || operation.EmergencyOperationCiphertext != null,
                cancellationToken)
            .ConfigureAwait(false);
        var hasEncryptedRepairRows = await db.SetupGrants
            .AnyAsync(grant => grant.RepairSessionCiphertext != null, cancellationToken)
            .ConfigureAwait(false);
        var hasEncryptedRows = allConfig.Any(c => c.IsEncrypted)
                               || hasEncryptedCompletionRows
                               || hasEncryptedRepairRows;
        var hasAdminAccount = await db.Accounts
            .AnyAsync(a => a.Type == Account.AccountType.Admin, cancellationToken)
            .ConfigureAwait(false);
        var isFreshInstall = allConfig.Count == 0 || !hasAdminAccount;

        if (!encryption.IsKeyConfigured)
        {
            if (await SetupEmergencyCandidateJournal.ExistsAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    "Found an encrypted setup emergency journal but NZBDAV_MASTER_KEY is not set. " +
                    "Keep the primary key configured until emergency recovery has completed.");
            }

            if (hasEncryptedRows)
            {
                throw new InvalidOperationException(
                    "Found encrypted config but NZBDAV_MASTER_KEY is not set. " +
                    "The master key is required to decrypt existing config. " +
                    "If you lost the key, delete the config database and reconfigure.");
            }

            if (isFreshInstall)
            {
                throw new InvalidOperationException(
                    "NZBDAV_MASTER_KEY is required for new installations. " +
                    "Generate one with: openssl rand -base64 32 " +
                    "See docs/setup-guide.md for details.");
            }

            Log.Warning("====================================================================");
            Log.Warning("  Config secrets are stored in plaintext.");
            Log.Warning("  Set NZBDAV_MASTER_KEY to enable encryption at rest.");
            Log.Warning("  See docs/setup-guide.md#encryption for details.");
            Log.Warning("====================================================================");
            return;
        }

        ValidateManagedCaseCanonicalization(allConfig);
        try
        {
            await MigrateAndRotateAsync(db, encryption, cancellationToken).ConfigureAwait(false);

            // The emergency journal is outside the database transaction, so it has
            // its own locked, atomic old-key -> primary-key migration. Do this on
            // every startup and in --encryption-maintenance before the old key can
            // be removed from the environment.
            var emergencyConfigManager = new ConfigManager(encryption);
            await SetupEmergencyCandidateJournal.MigrateAsync(
                    emergencyConfigManager,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CryptographicException exception)
        {
            // Startup is a public lifecycle boundary. Never let the encryption
            // provider expose its diagnostic (or any value derived from the
            // ciphertext) through that contract. MigrateAndRotateAsync owns the
            // database transaction, so this conversion still leaves a failed
            // validation with no committed writes.
            throw new InvalidOperationException(
                "Startup encryption validation failed.",
                exception);
        }
    }

    private static void ValidateManagedCaseCanonicalization(IReadOnlyList<ConfigItem> allConfig)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in allConfig)
        {
            if (SensitiveConfigKeys.TryGetCanonicalKey(item.ConfigName, out var canonicalName)
                && (SensitiveConfigKeys.Keys.Contains(canonicalName)
                    || SensitiveConfigKeys.TryGetCanonicalSetupKey(item.ConfigName, out _))
                && !seen.Add(canonicalName))
            {
                throw new InvalidOperationException(
                    $"Duplicate managed config key '{canonicalName}' exists with multiple casings.");
            }
        }
    }

    private static async Task MigrateAndRotateAsync(
        DavDatabaseContext db,
        ConfigEncryptionService encryption,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var migrated = 0;
            var rotated = 0;
            var rotatedCompletionOperations = 0;
            var rotatedRepairSessions = 0;
            var migratedKeys = new List<string>();

            foreach (var row in await db.ConfigItems
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                // Older v1/v2 startup passes accidentally classified setup
                // markers as secrets. Restore them to their canonical
                // plaintext representation in this same transaction before
                // any consumer can read the cache. Completion is strict: its
                // value is an authenticated boolean, never a decrypted
                // arbitrary string.
                if (row.IsEncrypted && IsPlaintextSetupMarker(row.ConfigName))
                {
                    var canonicalName = SensitiveConfigKeys.TryGetCanonicalKey(row.ConfigName, out var markerName)
                        ? markerName
                        : row.ConfigName;
                    var (markerValue, _, _) = encryption.Decrypt(canonicalName, row.ConfigValue);
                    if (string.Equals(canonicalName, SetupConfigKeys.Completed, StringComparison.Ordinal)
                        && !bool.TryParse(markerValue, out _))
                        throw new InvalidOperationException("The setup completion marker must contain an authenticated boolean.");

                    row.ConfigName = canonicalName;
                    row.ConfigValue = string.Equals(canonicalName, SetupConfigKeys.Completed, StringComparison.Ordinal)
                        ? (bool.Parse(markerValue) ? "true" : "false")
                        : markerValue;
                    row.IsEncrypted = false;
                    migrated++;
                    continue;
                }

                if (!row.IsEncrypted && SensitiveConfigKeys.IsSensitive(row.ConfigName))
                {
                    row.ConfigValue = encryption.Encrypt(row.ConfigName, row.ConfigValue);
                    row.IsEncrypted = true;
                    migrated++;
                    migratedKeys.Add(
                        SensitiveConfigKeys.TryGetCanonicalKey(row.ConfigName, out var canonicalName)
                            ? canonicalName
                            : row.ConfigName);
                    continue;
                }

                if (row.IsEncrypted)
                {
                    var (plaintext, usedOldKey, requiresFormatUpgrade) =
                        encryption.Decrypt(row.ConfigName, row.ConfigValue);
                    if (usedOldKey || requiresFormatUpgrade)
                    {
                        row.ConfigValue = encryption.Encrypt(row.ConfigName, plaintext);
                        rotated++;
                    }
                }
            }

            // Completion rows are outside ConfigItems but use the same
            // authenticated setup snapshot boundary. Rotate every ciphertext
            // in the same transaction as config rows; otherwise removing the
            // old key after startup would strand an in-flight completion.
            foreach (var operation in await db.SetupCompletionOperations
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                rotatedCompletionOperations += RotateCompletionValue(operation, "active", operation.ActiveSessionCiphertext);
                rotatedCompletionOperations += RotateCompletionValue(operation, "revocation", operation.RevocationSessionCiphertext);
                rotatedCompletionOperations += RotateCompletionValue(operation, "candidate", operation.CandidateSessionCiphertext);
                rotatedCompletionOperations += RotateCompletionValue(operation, "candidate-operation", operation.CandidateOperationCiphertext);
                rotatedCompletionOperations += RotateCompletionValue(operation, "emergency", operation.EmergencySessionCiphertext);
                rotatedCompletionOperations += RotateCompletionValue(operation, "emergency-operation", operation.EmergencyOperationCiphertext);
            }

            foreach (var grant in await db.SetupGrants
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                if (string.IsNullOrWhiteSpace(grant.RepairSessionCiphertext))
                    continue;

                var (plaintext, usedOldKey, requiresFormatUpgrade) = encryption.Decrypt(
                    "setup.grant.repair-session",
                    grant.RepairSessionCiphertext);
                if (usedOldKey || requiresFormatUpgrade)
                {
                    grant.RepairSessionCiphertext = encryption.Encrypt(
                        "setup.grant.repair-session",
                        plaintext);
                    rotatedRepairSessions++;
                }
            }

            var migratedNonBootstrapKeys = migratedKeys
                .Where(key => !BootstrapConfigKeys.Contains(key))
                .ToList();

            if (migratedNonBootstrapKeys.Count > 0)
            {
                var alreadyHasMarker = await db.ConfigItems
                    .AnyAsync(
                        c => c.ConfigName == "encryption.migration-completed-at",
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!alreadyHasMarker)
                {
                    db.ConfigItems.Add(new ConfigItem
                    {
                        ConfigName = "encryption.migration-completed-at",
                        ConfigValue = DateTime.UtcNow.ToString("O"),
                        IsEncrypted = false,
                    });
                }
            }

            if (migrated > 0 || rotated > 0 || rotatedCompletionOperations > 0 || rotatedRepairSessions > 0)
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            if (migrated > 0)
            {
                Log.Information("Encrypted {Count} existing config secrets on startup", migrated);
                if (migratedNonBootstrapKeys.Count > 0)
                {
                    Log.Warning("===========================================================");
                    Log.Warning("  Historical backups of your config database (if any) are");
                    Log.Warning("  still PLAINTEXT. Rotate usenet provider passwords,");
                    Log.Warning("  Radarr/Sonarr API keys, and the NZBDAV API key NOW if");
                    Log.Warning("  there is any chance a pre-migration backup is exposed.");
                    Log.Warning("===========================================================");
                }
            }

            if (rotated > 0 || rotatedCompletionOperations > 0 || rotatedRepairSessions > 0)
            {
                Log.Information(
                    "Key rotation complete: {Count} config rows, {CompletionCount} completion snapshots, and {RepairCount} repair sessions re-encrypted with primary key. " +
                    "NZBDAV_MASTER_KEY_OLD can now be unset.",
                    rotated, rotatedCompletionOperations, rotatedRepairSessions);
            }
        }
        catch
        {
            // Never let a cancelled token prevent rollback.  Promotion of the
            // durable key stage happens outside this method, only after this
            // commit has completed successfully.
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        static bool IsPlaintextSetupMarker(string configName)
        {
            if (!SensitiveConfigKeys.TryGetCanonicalSetupKey(configName, out var canonical))
                return false;

            return canonical is SetupConfigKeys.Completed
                or SetupConfigKeys.StreamTokenLegacyMigrationStart
                or SetupConfigKeys.RunProgress
                or SetupConfigKeys.RunProgressReasons
                or SetupConfigKeys.RepairRequired
                or SetupConfigKeys.RepairCheckedAtUtc
                or SetupConfigKeys.SonarrRepairReason
                or SetupConfigKeys.RadarrRepairReason
                or SetupConfigKeys.RepairReasons
                or SetupConfigKeys.RevocationPending;
        }

        int RotateCompletionValue(SetupCompletionOperation operation, string key, string? ciphertext)
        {
            if (string.IsNullOrWhiteSpace(ciphertext))
                return 0;

            var (plaintext, usedOldKey, requiresFormatUpgrade) =
                encryption.Decrypt($"setup.completion.{key}", ciphertext);
            if (!usedOldKey && !requiresFormatUpgrade)
                return 0;

            var rotatedValue = encryption.Encrypt($"setup.completion.{key}", plaintext);
            switch (key)
            {
                case "active": operation.ActiveSessionCiphertext = rotatedValue; break;
                case "revocation": operation.RevocationSessionCiphertext = rotatedValue; break;
                case "candidate": operation.CandidateSessionCiphertext = rotatedValue; break;
                case "candidate-operation": operation.CandidateOperationCiphertext = rotatedValue; break;
                case "emergency": operation.EmergencySessionCiphertext = rotatedValue; break;
                case "emergency-operation": operation.EmergencyOperationCiphertext = rotatedValue; break;
            }

            return 1;
        }
    }
}
