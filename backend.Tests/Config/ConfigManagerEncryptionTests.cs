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
        Assert.StartsWith("v2:", configItems[0].ConfigValue);
        Assert.False(configItems[1].IsEncrypted);
        Assert.Equal("http://example.test", configItems[1].ConfigValue);
    }

    [Fact]
    public void GetUsenetProviderConfig_AcceptsTheStringEnumPersistenceContract()
    {
        using var encryption = new ConfigEncryptionService();
        var configManager = new ConfigManager(encryption);
        configManager.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = "usenet.providers",
                ConfigValue = "{\"Providers\":[{\"Type\":\"Pooled\",\"Host\":\"news.example\",\"Port\":563,\"UseSsl\":true,\"User\":\"user\",\"Pass\":\"password\",\"MaxConnections\":1}]}"
            }
        ]);

        var provider = Assert.Single(configManager.GetUsenetProviderConfig().Providers);
        Assert.Equal(NzbWebDAV.Models.ProviderType.Pooled, provider.Type);
        Assert.Equal("news.example", provider.Host);
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
            apiKeyRow.ConfigValue = encryption.Encrypt("api.key", "restored-api-key");
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
                    ConfigValue = encryption.Encrypt("api.key", "already-encrypted"),
                }
            ]));

        Assert.Contains("Double-encryption detected", ex.Message);
    }

    [Fact]
    public void PrepareForStorage_EncryptsSensitiveCopies_WithoutMutatingOriginals()
    {
        _fixture.SetKeys(masterKey: _fixture.CreateKey(), oldKey: null);
        using var encryption = new ConfigEncryptionService();
        var configManager = new ConfigManager(encryption);
        var original = new List<ConfigItem>
        {
            new()
            {
                ConfigName = "cache.l2.secret-key",
                ConfigValue = "plain-secret"
            }
        };

        var prepared = configManager.PrepareForStorage(original);

        Assert.Equal("plain-secret", original[0].ConfigValue);
        Assert.False(original[0].IsEncrypted);
        Assert.StartsWith("v2:", prepared[0].ConfigValue);
        Assert.True(prepared[0].IsEncrypted);
    }
}
