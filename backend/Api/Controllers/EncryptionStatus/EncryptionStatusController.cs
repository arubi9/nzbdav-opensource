using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Api.Filters;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.EncryptionStatus;

[ApiController]
[Route("api/encryption-status")]
[ServiceFilter(typeof(ApiKeyAuthFilter))]
public sealed class EncryptionStatusController(
    DavDatabaseClient dbClient,
    ConfigEncryptionService encryptionService) : ControllerBase
{
    private static readonly string[] MarkerConfigKeys =
    [
        "encryption.migration-completed-at",
        "encryption.post-migration-acknowledged"
    ];

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        // Keep the database predicate case-insensitive to match
        // SensitiveConfigKeys.IsSensitive. Config names from older databases
        // may not use the registry's canonical casing.
        var sensitiveKeys = SensitiveConfigKeys.Keys
            .Select(key => key.ToUpperInvariant())
            .ToArray();
        var plaintextSecretsCount = await dbClient.Ctx.ConfigItems
            .Where(item => sensitiveKeys.Contains(item.ConfigName.ToUpper()) && !item.IsEncrypted)
            .CountAsync()
            .ConfigureAwait(false);

        var markers = await dbClient.Ctx.ConfigItems
            .Where(item => MarkerConfigKeys.Contains(item.ConfigName))
            .ToListAsync()
            .ConfigureAwait(false);

        var migrationCompletedAt = markers
            .FirstOrDefault(item => item.ConfigName == "encryption.migration-completed-at")
            ?.ConfigValue;
        var postMigrationAcknowledgedAt = markers
            .FirstOrDefault(item => item.ConfigName == "encryption.post-migration-acknowledged")
            ?.ConfigValue;

        return Ok(new EncryptionStatusResponse(
            encryptionService.IsKeyConfigured,
            plaintextSecretsCount,
            plaintextSecretsCount > 0 ? "warning" : encryptionService.IsKeyConfigured ? "none" : "info",
            migrationCompletedAt,
            postMigrationAcknowledgedAt != null,
            postMigrationAcknowledgedAt));
    }

    [HttpPost("acknowledge-post-migration")]
    public async Task<IActionResult> AcknowledgePostMigration()
    {
        var marker = DateTime.UtcNow.ToString("O");
        var provider = dbClient.Ctx.Database.ProviderName ?? string.Empty;
        if (provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            await dbClient.Ctx.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT OR IGNORE INTO "ConfigItems" ("ConfigName", "ConfigValue", "IsEncrypted")
                VALUES ({"encryption.post-migration-acknowledged"}, {marker}, {false})
                """).ConfigureAwait(false);
        }
        else if (provider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
        {
            await dbClient.Ctx.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "ConfigItems" ("ConfigName", "ConfigValue", "IsEncrypted")
                VALUES ({"encryption.post-migration-acknowledged"}, {marker}, {false})
                ON CONFLICT ("ConfigName") DO NOTHING
                """).ConfigureAwait(false);
        }
        else
        {
            // Providers without native insert-on-conflict support retain the
            // same idempotent behavior by narrowly ignoring only a duplicate
            // primary-key race.
            try
            {
                dbClient.Ctx.ConfigItems.Add(new ConfigItem
                {
                    ConfigName = "encryption.post-migration-acknowledged",
                    ConfigValue = marker,
                    IsEncrypted = false,
                });
                await dbClient.Ctx.SaveChangesAsync().ConfigureAwait(false);
            }
            catch (DbUpdateException ex) when (IsDuplicateKey(ex))
            {
                dbClient.Ctx.ChangeTracker.Clear();
            }
        }

        // Raw SQL bypasses EF's identity map. Clear it so a subsequent status
        // read cannot observe a stale missing marker (or a failed insert).
        dbClient.Ctx.ChangeTracker.Clear();
        return NoContent();
    }

    private static bool IsDuplicateKey(DbUpdateException exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is Microsoft.Data.Sqlite.SqliteException sqlite && sqlite.SqliteErrorCode == 19)
                return true;
            if (current is Npgsql.PostgresException postgres && postgres.SqlState == "23505")
                return true;
        }

        return false;
    }
}

public sealed record EncryptionStatusResponse(
    bool KeySet,
    int PlaintextSecretsCount,
    string BannerSeverity,
    string? MigrationCompletedAt,
    bool PostMigrationAcknowledged,
    string? PostMigrationAcknowledgedAt);
