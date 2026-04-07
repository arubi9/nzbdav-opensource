using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
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
        ConfigEncryptionService encryption)
    {
        var allConfig = await db.ConfigItems.ToListAsync().ConfigureAwait(false);
        var hasEncryptedRows = allConfig.Any(c => c.IsEncrypted);
        var hasAdminAccount = await db.Accounts
            .AnyAsync(a => a.Type == Account.AccountType.Admin)
            .ConfigureAwait(false);
        var isFreshInstall = allConfig.Count == 0 || !hasAdminAccount;

        if (!encryption.IsKeyConfigured)
        {
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

        await MigrateAndRotateAsync(db, encryption).ConfigureAwait(false);
    }

    private static async Task MigrateAndRotateAsync(
        DavDatabaseContext db,
        ConfigEncryptionService encryption)
    {
        await using var transaction = await db.Database.BeginTransactionAsync().ConfigureAwait(false);

        try
        {
            var migrated = 0;
            var rotated = 0;
            var migratedKeys = new List<string>();

            foreach (var row in await db.ConfigItems.ToListAsync().ConfigureAwait(false))
            {
                if (!row.IsEncrypted && SensitiveConfigKeys.IsSensitive(row.ConfigName))
                {
                    row.ConfigValue = encryption.Encrypt(row.ConfigValue);
                    row.IsEncrypted = true;
                    migrated++;
                    migratedKeys.Add(row.ConfigName);
                    continue;
                }

                if (row.IsEncrypted)
                {
                    var (plaintext, usedOldKey) = encryption.Decrypt(row.ConfigValue);
                    if (usedOldKey)
                    {
                        row.ConfigValue = encryption.Encrypt(plaintext);
                        rotated++;
                    }
                }
            }

            var migratedNonBootstrapKeys = migratedKeys
                .Where(key => !BootstrapConfigKeys.Contains(key))
                .ToList();

            if (migratedNonBootstrapKeys.Count > 0)
            {
                var alreadyHasMarker = await db.ConfigItems
                    .AnyAsync(c => c.ConfigName == "encryption.migration-completed-at")
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

            if (migrated > 0 || rotated > 0)
                await db.SaveChangesAsync().ConfigureAwait(false);

            await transaction.CommitAsync().ConfigureAwait(false);

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

            if (rotated > 0)
            {
                Log.Information(
                    "Key rotation complete: {Count} rows re-encrypted with primary key. " +
                    "NZBDAV_MASTER_KEY_OLD can now be unset.",
                    rotated);
            }
        }
        catch
        {
            await transaction.RollbackAsync().ConfigureAwait(false);
            throw;
        }
    }
}
