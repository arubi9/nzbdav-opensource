using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace backend.Tests.Services;

[Collection(nameof(ConfigEncryptionDatabaseCollection))]
public sealed class StartupEncryptionCheckTests
{
    private readonly ConfigEncryptionDatabaseFixture _fixture;

    public StartupEncryptionCheckTests(ConfigEncryptionDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task RunAsync_WithoutKey_OnFreshInstall_Throws()
    {
        await _fixture.ResetAsync();
        _fixture.SetKeys(masterKey: null, oldKey: null);

        await using var dbContext = await _fixture.CreateMigratedContextAsync();
        using var encryption = new ConfigEncryptionService();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => StartupEncryptionCheck.RunAsync(dbContext, encryption));

        Assert.Contains("NZBDAV_MASTER_KEY is required for new installations", ex.Message);
    }

    [Fact]
    public async Task RunAsync_WithKey_EncryptsSensitivePlaintextRows_AndWritesMarker()
    {
        await _fixture.ResetAsync();
        _fixture.SetKeys(masterKey: _fixture.CreateKey(), oldKey: null);

        await using (var setupContext = await _fixture.CreateMigratedContextAsync())
        {
            var apiKeyRow = await setupContext.ConfigItems.SingleAsync(x => x.ConfigName == "api.key");
            apiKeyRow.ConfigValue = "plaintext-api-key";
            apiKeyRow.IsEncrypted = false;
            setupContext.ConfigItems.Add(new ConfigItem
            {
                ConfigName = "arr.instances",
                ConfigValue = "{}",
                IsEncrypted = false
            });
            await setupContext.SaveChangesAsync();
        }

        await using (var dbContext = await _fixture.CreateMigratedContextAsync())
        using (var encryption = new ConfigEncryptionService())
        {
            await StartupEncryptionCheck.RunAsync(dbContext, encryption);
        }

        await using var verifyContext = await _fixture.CreateMigratedContextAsync();
        var secretRow = await verifyContext.ConfigItems.SingleAsync(x => x.ConfigName == "api.key");
        var markerRow = await verifyContext.ConfigItems.SingleAsync(x => x.ConfigName == "encryption.migration-completed-at");

        Assert.True(secretRow.IsEncrypted);
        Assert.StartsWith("v1:", secretRow.ConfigValue);
        Assert.False(markerRow.IsEncrypted);
    }

    [Fact]
    public async Task RunAsync_WithOnlyBootstrapKeys_DoesNotWriteMigrationMarker()
    {
        await _fixture.ResetAsync();
        _fixture.SetKeys(masterKey: _fixture.CreateKey(), oldKey: null);

        await using (var dbContext = await _fixture.CreateMigratedContextAsync())
        using (var encryption = new ConfigEncryptionService())
        {
            await StartupEncryptionCheck.RunAsync(dbContext, encryption);
        }

        await using var verifyContext = await _fixture.CreateMigratedContextAsync();
        Assert.False(await verifyContext.ConfigItems.AnyAsync(x => x.ConfigName == "encryption.migration-completed-at"));
    }

    [Fact]
    public async Task RunAsync_WithoutKey_WithBootstrapRowsAndExistingAdmin_WarnsInsteadOfThrowing()
    {
        await _fixture.ResetAsync();
        _fixture.SetKeys(masterKey: null, oldKey: null);

        await using (var setupContext = await _fixture.CreateMigratedContextAsync())
        {
            setupContext.Accounts.Add(new Account
            {
                Type = Account.AccountType.Admin,
                Username = "admin",
                PasswordHash = "hash",
                RandomSalt = "salt"
            });
            await setupContext.SaveChangesAsync();
        }

        await using var dbContext = await _fixture.CreateMigratedContextAsync();
        using var encryption = new ConfigEncryptionService();

        await StartupEncryptionCheck.RunAsync(dbContext, encryption);
    }

    [Fact]
    public async Task RunAsync_WithoutKey_WhenEncryptedRowsExist_ThrowsLostKeyError()
    {
        await _fixture.ResetAsync();
        var masterKey = _fixture.CreateKey();
        _fixture.SetKeys(masterKey: masterKey, oldKey: null);

        string ciphertext;
        using (var encryption = new ConfigEncryptionService())
            ciphertext = encryption.Encrypt("secret-value");

        await using (var setupContext = await _fixture.CreateMigratedContextAsync())
        {
            var apiKeyRow = await setupContext.ConfigItems.SingleAsync(x => x.ConfigName == "api.key");
            apiKeyRow.ConfigValue = ciphertext;
            apiKeyRow.IsEncrypted = true;
            await setupContext.SaveChangesAsync();
        }

        _fixture.SetKeys(masterKey: null, oldKey: null);

        await using var dbContext = await _fixture.CreateMigratedContextAsync();
        using var encryptionWithoutKey = new ConfigEncryptionService();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => StartupEncryptionCheck.RunAsync(dbContext, encryptionWithoutKey));

        Assert.Contains("Found encrypted config but NZBDAV_MASTER_KEY is not set", ex.Message);
    }

    [Fact]
    public async Task RunAsync_DoesNotDuplicateMigrationMarker_WhenItAlreadyExists()
    {
        await _fixture.ResetAsync();
        _fixture.SetKeys(masterKey: _fixture.CreateKey(), oldKey: null);

        await using (var setupContext = await _fixture.CreateMigratedContextAsync())
        {
            setupContext.ConfigItems.Add(new ConfigItem
            {
                ConfigName = "encryption.migration-completed-at",
                ConfigValue = "2026-04-07T12:00:00.0000000Z",
                IsEncrypted = false
            });
            setupContext.ConfigItems.Add(new ConfigItem
            {
                ConfigName = "arr.instances",
                ConfigValue = "{}",
                IsEncrypted = false
            });
            await setupContext.SaveChangesAsync();
        }

        await using (var dbContext = await _fixture.CreateMigratedContextAsync())
        using (var encryption = new ConfigEncryptionService())
        {
            await StartupEncryptionCheck.RunAsync(dbContext, encryption);
        }

        await using var verifyContext = await _fixture.CreateMigratedContextAsync();
        var markers = await verifyContext.ConfigItems
            .Where(x => x.ConfigName == "encryption.migration-completed-at")
            .CountAsync();
        Assert.Equal(1, markers);
    }
}

public sealed class ConfigEncryptionDatabaseFixture : IAsyncLifetime
{
    private readonly string _configPath = Path.Join(Path.GetTempPath(), "nzbdav-tests", "config-encryption");
    private readonly string? _previousConfigPath = Environment.GetEnvironmentVariable("CONFIG_PATH");
    private readonly string? _previousMasterKey = Environment.GetEnvironmentVariable("NZBDAV_MASTER_KEY");
    private readonly string? _previousOldKey = Environment.GetEnvironmentVariable("NZBDAV_MASTER_KEY_OLD");

    public ConfigEncryptionDatabaseFixture()
    {
        Environment.SetEnvironmentVariable("CONFIG_PATH", _configPath);
    }

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_configPath);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("CONFIG_PATH", _previousConfigPath);
        Environment.SetEnvironmentVariable("NZBDAV_MASTER_KEY", _previousMasterKey);
        Environment.SetEnvironmentVariable("NZBDAV_MASTER_KEY_OLD", _previousOldKey);
        return ResetAsync();
    }

    public async Task ResetAsync()
    {
        await Task.Yield();
        SqliteConnection.ClearAllPools();
        DeleteIfExists(DavDatabaseContext.DatabaseFilePath);
        DeleteIfExists(DavDatabaseContext.DatabaseFilePath + "-wal");
        DeleteIfExists(DavDatabaseContext.DatabaseFilePath + "-shm");
    }

    public async Task<DavDatabaseContext> CreateMigratedContextAsync()
    {
        Directory.CreateDirectory(_configPath);
        var dbContext = new DavDatabaseContext();
        await dbContext.Database.MigrateAsync();
        return dbContext;
    }

    public void SetKeys(string? masterKey, string? oldKey)
    {
        Environment.SetEnvironmentVariable("NZBDAV_MASTER_KEY", masterKey);
        Environment.SetEnvironmentVariable("NZBDAV_MASTER_KEY_OLD", oldKey);
    }

    public string CreateKey() => Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}

[CollectionDefinition(nameof(ConfigEncryptionDatabaseCollection), DisableParallelization = true)]
public sealed class ConfigEncryptionDatabaseCollection : ICollectionFixture<ConfigEncryptionDatabaseFixture>
{
}
