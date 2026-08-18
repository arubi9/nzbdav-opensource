using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Api.Controllers.EncryptionStatus;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Api.Controllers.EncryptionStatus;

[Collection(nameof(backend.Tests.Services.ConfigEncryptionDatabaseCollection))]
public sealed class EncryptionStatusControllerTests
{
    private readonly backend.Tests.Services.ConfigEncryptionDatabaseFixture _fixture;

    public EncryptionStatusControllerTests(backend.Tests.Services.ConfigEncryptionDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Get_ReturnsWarningState_WhenPlaintextSecretsExistWithoutKey()
    {
        await _fixture.ResetAsync();
        _fixture.SetKeys(masterKey: null, oldKey: null);

        await using (var setupContext = await _fixture.CreateMigratedContextAsync())
        {
            var apiKeyRow = await setupContext.ConfigItems.SingleAsync(x => x.ConfigName == "api.key");
            apiKeyRow.ConfigValue = "plaintext-api-key";
            apiKeyRow.IsEncrypted = false;
            await setupContext.SaveChangesAsync();
        }

        await using var dbContext = await _fixture.CreateMigratedContextAsync();
        using var encryption = new ConfigEncryptionService();
        var controller = new EncryptionStatusController(new DavDatabaseClient(dbContext), encryption);

        var result = await controller.Get();
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<EncryptionStatusResponse>(ok.Value);

        Assert.False(response.KeySet);
        Assert.Equal(2, response.PlaintextSecretsCount);
        Assert.Equal("warning", response.BannerSeverity);
    }

    [Fact]
    public async Task Get_ReturnsWarningState_WhenPlaintextSecretsExistWithKey()
    {
        await _fixture.ResetAsync();
        _fixture.SetKeys(masterKey: _fixture.CreateKey(), oldKey: null);

        await using (var setupContext = await _fixture.CreateMigratedContextAsync())
        {
            var apiKeyRow = await setupContext.ConfigItems.SingleAsync(x => x.ConfigName == "api.key");
            apiKeyRow.ConfigValue = "still-plaintext";
            apiKeyRow.IsEncrypted = false;
            await setupContext.SaveChangesAsync();
        }

        await using var dbContext = await _fixture.CreateMigratedContextAsync();
        using var encryption = new ConfigEncryptionService();
        var response = Assert.IsType<EncryptionStatusResponse>(
            Assert.IsType<OkObjectResult>(await new EncryptionStatusController(
                new DavDatabaseClient(dbContext), encryption).Get()).Value);

        Assert.True(response.KeySet);
        Assert.True(response.PlaintextSecretsCount > 0);
        Assert.Equal("warning", response.BannerSeverity);
    }

    [Fact]
    public async Task Get_ReturnsMigrationMetadata_WhenMarkerRowsExist()
    {
        await _fixture.ResetAsync();
        _fixture.SetKeys(masterKey: _fixture.CreateKey(), oldKey: null);

        await using (var setupContext = await _fixture.CreateMigratedContextAsync())
        {
            setupContext.ConfigItems.AddRange(
                new ConfigItem
                {
                    ConfigName = "encryption.migration-completed-at",
                    ConfigValue = "2026-04-07T12:00:00.0000000Z",
                    IsEncrypted = false,
                },
                new ConfigItem
                {
                    ConfigName = "encryption.post-migration-acknowledged",
                    ConfigValue = "2026-04-07T12:30:00.0000000Z",
                    IsEncrypted = false,
                });
            await setupContext.SaveChangesAsync();
        }

        await using var dbContext = await _fixture.CreateMigratedContextAsync();
        using var encryption = new ConfigEncryptionService();
        var controller = new EncryptionStatusController(new DavDatabaseClient(dbContext), encryption);

        var result = await controller.Get();
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<EncryptionStatusResponse>(ok.Value);

        Assert.True(response.KeySet);
        Assert.Equal("warning", response.BannerSeverity);
        Assert.Equal("2026-04-07T12:00:00.0000000Z", response.MigrationCompletedAt);
        Assert.True(response.PostMigrationAcknowledged);
        Assert.Equal("2026-04-07T12:30:00.0000000Z", response.PostMigrationAcknowledgedAt);
    }

    [Fact]
    public async Task AcknowledgePostMigration_IsIdempotentAcrossConcurrentContexts()
    {
        await _fixture.ResetAsync();
        _fixture.SetKeys(masterKey: _fixture.CreateKey(), oldKey: null);

        await using var context1 = await _fixture.CreateMigratedContextAsync();
        await using var context2 = await _fixture.CreateMigratedContextAsync();
        using var encryption1 = new ConfigEncryptionService();
        using var encryption2 = new ConfigEncryptionService();
        var first = new EncryptionStatusController(new DavDatabaseClient(context1), encryption1);
        var second = new EncryptionStatusController(new DavDatabaseClient(context2), encryption2);

        await Task.WhenAll(first.AcknowledgePostMigration(), second.AcknowledgePostMigration());

        await using var verify = await _fixture.CreateMigratedContextAsync();
        Assert.Equal(1, await verify.ConfigItems.CountAsync(
            item => item.ConfigName == "encryption.post-migration-acknowledged"));
    }

    [Fact]
    public async Task AcknowledgePostMigration_WritesMarker_WhenMissing()
    {
        await _fixture.ResetAsync();
        _fixture.SetKeys(masterKey: _fixture.CreateKey(), oldKey: null);

        await using var dbContext = await _fixture.CreateMigratedContextAsync();
        using var encryption = new ConfigEncryptionService();
        var controller = new EncryptionStatusController(new DavDatabaseClient(dbContext), encryption);

        var result = await controller.AcknowledgePostMigration();

        Assert.IsType<NoContentResult>(result);

        var marker = await dbContext.ConfigItems.SingleAsync(x => x.ConfigName == "encryption.post-migration-acknowledged");
        Assert.False(marker.IsEncrypted);
        Assert.False(string.IsNullOrWhiteSpace(marker.ConfigValue));
    }
}
