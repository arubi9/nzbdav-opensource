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
        var sensitiveKeys = SensitiveConfigKeys.Keys.ToList();
        var plaintextSecretsCount = await dbClient.Ctx.ConfigItems
            .Where(item => sensitiveKeys.Contains(item.ConfigName) && !item.IsEncrypted)
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
            encryptionService.IsKeyConfigured ? "none" : plaintextSecretsCount > 0 ? "warning" : "info",
            migrationCompletedAt,
            postMigrationAcknowledgedAt != null,
            postMigrationAcknowledgedAt));
    }

    [HttpPost("acknowledge-post-migration")]
    public async Task<IActionResult> AcknowledgePostMigration()
    {
        var existing = await dbClient.Ctx.ConfigItems
            .FirstOrDefaultAsync(c => c.ConfigName == "encryption.post-migration-acknowledged")
            .ConfigureAwait(false);

        if (existing is null)
        {
            dbClient.Ctx.ConfigItems.Add(new ConfigItem
            {
                ConfigName = "encryption.post-migration-acknowledged",
                ConfigValue = DateTime.UtcNow.ToString("O"),
                IsEncrypted = false,
            });
            await dbClient.Ctx.SaveChangesAsync().ConfigureAwait(false);
        }

        return NoContent();
    }
}

public sealed record EncryptionStatusResponse(
    bool KeySet,
    int PlaintextSecretsCount,
    string BannerSeverity,
    string? MigrationCompletedAt,
    bool PostMigrationAcknowledged,
    string? PostMigrationAcknowledgedAt);
