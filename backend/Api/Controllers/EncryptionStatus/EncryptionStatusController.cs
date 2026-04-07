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
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var configItems = await dbClient.Ctx.ConfigItems.ToListAsync().ConfigureAwait(false);
        var plaintextSecretsCount = configItems.Count(item =>
            SensitiveConfigKeys.IsSensitive(item.ConfigName) && !item.IsEncrypted);

        var migrationCompletedAt = configItems
            .FirstOrDefault(item => item.ConfigName == "encryption.migration-completed-at")
            ?.ConfigValue;
        var postMigrationAcknowledged = configItems
            .FirstOrDefault(item => item.ConfigName == "encryption.post-migration-acknowledged")
            ?.ConfigValue;

        return Ok(new EncryptionStatusResponse(
            encryptionService.IsKeyConfigured,
            plaintextSecretsCount,
            encryptionService.IsKeyConfigured ? "none" : plaintextSecretsCount > 0 ? "warning" : "info",
            migrationCompletedAt,
            postMigrationAcknowledged));
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
    string? PostMigrationAcknowledged);
