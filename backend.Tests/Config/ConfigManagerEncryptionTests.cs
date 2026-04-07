using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace backend.Tests.Config;

[Collection(nameof(backend.Tests.Services.ConfigEncryptionDatabaseCollection))]
public sealed class ConfigManagerEncryptionTests
{
    private readonly backend.Tests.Services.ConfigEncryptionDatabaseFixture _fixture;

    public ConfigManagerEncryptionTests(backend.Tests.Services.ConfigEncryptionDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void UpdateValues_EncryptsSensitiveItems_ButLeavesNonSensitiveItemsPlaintext()
    {
        _fixture.SetKeys(masterKey: _fixture.CreateKey(), oldKey: null);
        using var encryption = new ConfigEncryptionService();
        var configManager = new ConfigManager(encryption);
        var configItems = new List<ConfigItem>
        {
            new()
            {
                ConfigName = "api.key",
                ConfigValue = "secret-value",
            },
            new()
            {
                ConfigName = "general.base-url",
                ConfigValue = "http://example.test",
            }
        };

        configManager.UpdateValues(configItems);

        Assert.True(configItems[0].IsEncrypted);
        Assert.StartsWith("v1:", configItems[0].ConfigValue);
        Assert.False(configItems[1].IsEncrypted);
        Assert.Equal("http://example.test", configItems[1].ConfigValue);
    }

    [Fact]
    public async Task LoadConfig_DecryptsEncryptedRows_ForConsumers()
    {
        await _fixture.ResetAsync();
        _fixture.SetKeys(masterKey: _fixture.CreateKey(), oldKey: null);
        using var encryption = new ConfigEncryptionService();

        await using (var setupContext = await _fixture.CreateMigratedContextAsync())
        {
            var apiKeyRow = await setupContext.ConfigItems.SingleAsync(x => x.ConfigName == "api.key");
            apiKeyRow.ConfigValue = encryption.Encrypt("restored-api-key");
            apiKeyRow.IsEncrypted = true;
            await setupContext.SaveChangesAsync();
        }

        var configManager = new ConfigManager(encryption);
        await configManager.LoadConfig();

        Assert.Equal("restored-api-key", configManager.GetApiKey());
    }

    [Fact]
    public void UpdateValues_RejectsSensitiveValuesThatAlreadyUseTheEncryptedPrefix()
    {
        _fixture.SetKeys(masterKey: _fixture.CreateKey(), oldKey: null);
        using var encryption = new ConfigEncryptionService();
        var configManager = new ConfigManager(encryption);

        var ex = Assert.Throws<InvalidOperationException>(() => configManager.UpdateValues(
            [
                new ConfigItem
                {
                    ConfigName = "api.key",
                    ConfigValue = "v1:already-encrypted",
                }
            ]));

        Assert.Contains("Double-encryption detected", ex.Message);
    }
}
