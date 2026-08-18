using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Setup.Core;

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
        Assert.StartsWith("v2:", secretRow.ConfigValue);
        Assert.False(markerRow.IsEncrypted);
    }

    [Fact]
    public async Task RunAsync_EncryptsCandidateSessionIntent_SessionIntentIsEncryptedAtRest()
    {
        await _fixture.ResetAsync();
        _fixture.SetKeys(masterKey: _fixture.CreateKey(), oldKey: null);

        await using (var setupContext = await _fixture.CreateMigratedContextAsync())
        {
            setupContext.ConfigItems.Add(new ConfigItem
            {
                ConfigName = SetupConfigKeys.CandidateSessionIntent,
                ConfigValue = "issue:plain-intent",
                IsEncrypted = false,
            });
            await setupContext.SaveChangesAsync();
        }

        await using (var dbContext = await _fixture.CreateMigratedContextAsync())
        using (var encryption = new ConfigEncryptionService())
        {
            await StartupEncryptionCheck.RunAsync(dbContext, encryption);
        }

        await using var verifyContext = await _fixture.CreateMigratedContextAsync();
        var intentRow = await verifyContext.ConfigItems.SingleAsync(x => x.ConfigName == SetupConfigKeys.CandidateSessionIntent);

        Assert.True(intentRow.IsEncrypted);
        Assert.StartsWith("v2:", intentRow.ConfigValue);
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
            ciphertext = encryption.Encrypt("api.key", "secret-value");

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

    [Theory]
    [InlineData("v1", true)]
    [InlineData("v1", false)]
    [InlineData("v2", true)]
    [InlineData("v2", false)]
    public async Task RunAsync_EncryptedCompletionMarkerIsMigratedToCanonicalPlaintext(
        string version,
        bool completed)
    {
        await _fixture.ResetAsync();
        var key = _fixture.CreateKey();
        _fixture.SetKeys(masterKey: key, oldKey: null);

        string ciphertext;
        using (var encryption = new ConfigEncryptionService())
        {
            ciphertext = version == "v1"
                ? CreateLegacyV1Ciphertext(SetupConfigKeys.Completed, completed ? "true" : "false", key)
                : encryption.Encrypt(SetupConfigKeys.Completed, completed ? "true" : "false");
        }

        await using (var setupContext = await _fixture.CreateMigratedContextAsync())
        {
            setupContext.ConfigItems.Add(new ConfigItem
            {
                ConfigName = SetupConfigKeys.Completed,
                ConfigValue = ciphertext,
                IsEncrypted = true,
            });
            await setupContext.SaveChangesAsync();
        }

        await using (var dbContext = await _fixture.CreateMigratedContextAsync())
        using (var encryption = new ConfigEncryptionService())
            await StartupEncryptionCheck.RunAsync(dbContext, encryption);

        await using var verifyContext = await _fixture.CreateMigratedContextAsync();
        var marker = await verifyContext.ConfigItems.SingleAsync(row => row.ConfigName == SetupConfigKeys.Completed);
        Assert.False(marker.IsEncrypted);
        Assert.Equal(completed ? "true" : "false", marker.ConfigValue);

        using var managerEncryption = new ConfigEncryptionService();
        var manager = new ConfigManager(managerEncryption);
        await manager.LoadConfig();
        Assert.Equal(completed, manager.IsSetupCompleted());

        if (completed)
        {
            var healthPersistence = new SetupConfigPersistence(manager, verifyContext);
            await healthPersistence.SaveLiveHealthStateAsync(
                ready: false,
                checkedAtUtc: DateTime.UtcNow,
                sonarrReason: SetupReasonCodes.SonarrFailed,
                radarrReason: null,
                reasonsJson: "{}",
                CancellationToken.None);
            Assert.True(manager.IsSetupCompleted());

            var grantService = new SetupGrantService(
                verifyContext,
                manager,
                TimeProvider.System,
                () => new HttpClient(new ThrowingAuthenticationHandler()));
            await Assert.ThrowsAsync<BadHttpRequestException>(() => grantService.IssueAsync("admin", "password"));
        }
    }

    [Fact]
    public async Task RunAsync_CompletionMarkerRotatesOldKeyAndRemainsPlaintextAfterOldKeyRetirement()
    {
        await _fixture.ResetAsync();
        var oldKey = _fixture.CreateKey();
        var newKey = _fixture.CreateKey();
        _fixture.SetKeys(masterKey: oldKey, oldKey: null);

        string ciphertext;
        using (var oldEncryption = new ConfigEncryptionService())
            ciphertext = oldEncryption.Encrypt(SetupConfigKeys.Completed, "true");

        await using (var setupContext = await _fixture.CreateMigratedContextAsync())
        {
            setupContext.ConfigItems.Add(new ConfigItem
            {
                ConfigName = SetupConfigKeys.Completed,
                ConfigValue = ciphertext,
                IsEncrypted = true,
            });
            await setupContext.SaveChangesAsync();
        }

        _fixture.SetKeys(masterKey: newKey, oldKey: oldKey);
        await using (var dbContext = await _fixture.CreateMigratedContextAsync())
        using (var rotationEncryption = new ConfigEncryptionService())
            await StartupEncryptionCheck.RunAsync(dbContext, rotationEncryption);

        _fixture.SetKeys(masterKey: newKey, oldKey: null);
        await using var verifyContext = await _fixture.CreateMigratedContextAsync();
        var marker = await verifyContext.ConfigItems.SingleAsync(row => row.ConfigName == SetupConfigKeys.Completed);
        Assert.False(marker.IsEncrypted);
        Assert.Equal("true", marker.ConfigValue);

        using var managerEncryption = new ConfigEncryptionService();
        var manager = new ConfigManager(managerEncryption);
        await manager.LoadConfig();
        Assert.True(manager.IsSetupCompleted());
    }

    [Fact]
    public async Task RunAsync_InvalidEncryptedCompletionMarkerRollsBackWithoutDeletingMarker()
    {
        await _fixture.ResetAsync();
        var key = _fixture.CreateKey();
        _fixture.SetKeys(masterKey: key, oldKey: null);

        string ciphertext;
        using (var encryption = new ConfigEncryptionService())
            ciphertext = encryption.Encrypt(SetupConfigKeys.Completed, "not-a-boolean");

        await using (var setupContext = await _fixture.CreateMigratedContextAsync())
        {
            setupContext.ConfigItems.Add(new ConfigItem
            {
                ConfigName = SetupConfigKeys.Completed,
                ConfigValue = ciphertext,
                IsEncrypted = true,
            });
            await setupContext.SaveChangesAsync();
        }

        await using var dbContext = await _fixture.CreateMigratedContextAsync();
        using var startupEncryption = new ConfigEncryptionService();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            StartupEncryptionCheck.RunAsync(dbContext, startupEncryption));

        await using var verifyContext = await _fixture.CreateMigratedContextAsync();
        var marker = await verifyContext.ConfigItems.SingleAsync(row => row.ConfigName == SetupConfigKeys.Completed);
        Assert.True(marker.IsEncrypted);
        Assert.Equal(ciphertext, marker.ConfigValue);
    }

    [Fact]
    public async Task RunAsync_RotatesRowsEncryptedWithOldKey()
    {
        await _fixture.ResetAsync();
        var oldKey = _fixture.CreateKey();
        var newKey = _fixture.CreateKey();
        _fixture.SetKeys(masterKey: oldKey, oldKey: null);

        string oldCiphertext;
        using (var oldEncryption = new ConfigEncryptionService())
            oldCiphertext = oldEncryption.Encrypt("api.key", "rotate-this-real-row");

        await using (var setupContext = await _fixture.CreateMigratedContextAsync())
        {
            var apiKeyRow = await setupContext.ConfigItems.SingleAsync(x => x.ConfigName == "api.key");
            apiKeyRow.ConfigValue = oldCiphertext;
            apiKeyRow.IsEncrypted = true;
            await setupContext.SaveChangesAsync();
        }

        _fixture.SetKeys(masterKey: newKey, oldKey: oldKey);
        await using (var dbContext = await _fixture.CreateMigratedContextAsync())
        using (var rotationEncryption = new ConfigEncryptionService())
        {
            await StartupEncryptionCheck.RunAsync(dbContext, rotationEncryption);
        }

        await using var verifyContext = await _fixture.CreateMigratedContextAsync();
        var rotated = await verifyContext.ConfigItems.SingleAsync(x => x.ConfigName == "api.key");
        _fixture.SetKeys(masterKey: newKey, oldKey: null);
        using (var newEncryption = new ConfigEncryptionService())
            Assert.Equal("rotate-this-real-row", newEncryption.Decrypt("api.key", rotated.ConfigValue).plaintext);

        _fixture.SetKeys(masterKey: oldKey, oldKey: null);
        using var oldOnlyEncryption = new ConfigEncryptionService();
        Assert.ThrowsAny<Exception>(() => oldOnlyEncryption.Decrypt("api.key", rotated.ConfigValue));
    }

    [Fact]
    public async Task RunAsync_RotatesCandidateSessionIntentWithOldKey()
    {
        await _fixture.ResetAsync();
        var oldKey = _fixture.CreateKey();
        var newKey = _fixture.CreateKey();
        _fixture.SetKeys(masterKey: oldKey, oldKey: null);

        string oldCiphertext;
        using (var oldEncryption = new ConfigEncryptionService())
            oldCiphertext = oldEncryption.Encrypt(SetupConfigKeys.CandidateSessionIntent, "issue:rotate-me");

        await using (var setupContext = await _fixture.CreateMigratedContextAsync())
        {
            var intentRow = await setupContext.ConfigItems.SingleAsync(x => x.ConfigName == "api.key");
            setupContext.ConfigItems.Remove(intentRow);
            await setupContext.SaveChangesAsync();

            setupContext.ConfigItems.Add(new ConfigItem
            {
                ConfigName = SetupConfigKeys.CandidateSessionIntent,
                ConfigValue = oldCiphertext,
                IsEncrypted = true,
            });
            await setupContext.SaveChangesAsync();
        }

        _fixture.SetKeys(masterKey: newKey, oldKey: oldKey);
        await using (var dbContext = await _fixture.CreateMigratedContextAsync())
        using (var rotationEncryption = new ConfigEncryptionService())
        {
            await StartupEncryptionCheck.RunAsync(dbContext, rotationEncryption);
        }

        await using var verifyContext = await _fixture.CreateMigratedContextAsync();
        var rotated = await verifyContext.ConfigItems.SingleAsync(x => x.ConfigName == SetupConfigKeys.CandidateSessionIntent);
        _fixture.SetKeys(masterKey: newKey, oldKey: null);
        using (var newEncryption = new ConfigEncryptionService())
            Assert.Equal("issue:rotate-me", newEncryption.Decrypt(SetupConfigKeys.CandidateSessionIntent, rotated.ConfigValue).plaintext);

        _fixture.SetKeys(masterKey: oldKey, oldKey: null);
        using var oldOnlyEncryption = new ConfigEncryptionService();
        Assert.ThrowsAny<Exception>(() => oldOnlyEncryption.Decrypt(SetupConfigKeys.CandidateSessionIntent, rotated.ConfigValue));
    }

    [Fact]
    public async Task RunAsync_RejectsTamperedCandidateSessionIntent_CannotDecrypt()
    {
        await _fixture.ResetAsync();
        _fixture.SetKeys(masterKey: _fixture.CreateKey(), oldKey: null);

        string ciphertext;
        using (var encryption = new ConfigEncryptionService())
            ciphertext = encryption.Encrypt(SetupConfigKeys.CandidateSessionIntent, "issue:tampered");

        var tampered = ciphertext.Length > 10
            ? ciphertext.Substring(0, ciphertext.Length - 1) + (ciphertext[^1] == 'A' ? 'B' : 'A')
            : ciphertext + "A";

        await using (var setupContext = await _fixture.CreateMigratedContextAsync())
        {
            var row = await setupContext.ConfigItems.SingleAsync(x => x.ConfigName == "api.key");
            setupContext.ConfigItems.Remove(row);
            await setupContext.SaveChangesAsync();

            setupContext.ConfigItems.Add(new ConfigItem
            {
                ConfigName = SetupConfigKeys.CandidateSessionIntent,
                ConfigValue = tampered,
                IsEncrypted = true,
            });
            await setupContext.SaveChangesAsync();
        }

        await using var dbContext = await _fixture.CreateMigratedContextAsync();
        using var startupEncryption = new ConfigEncryptionService();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => StartupEncryptionCheck.RunAsync(dbContext, startupEncryption));
    }

    [Fact]
    public async Task RunAsync_DetectsCaseDuplicateSensitiveKeys_BeforeAnyMutation()
    {
        await _fixture.ResetAsync();
        var legacyKey = _fixture.CreateKey();
        var primaryKey = _fixture.CreateKey();

        // A legacy sensitive row is intentionally stored with v1 formatting plus a
        // second mixed-case plaintext alias; detection should fail early and block
        // startup migration before any writes occur.
        var legacyCiphertext = CreateLegacyV1Ciphertext(
            "setup.plugin-api-key",
            "legacy-plugin-secret",
            legacyKey);

        _fixture.SetKeys(masterKey: primaryKey, oldKey: legacyKey);

        await using (var setupContext = await _fixture.CreateMigratedContextAsync())
        {
            setupContext.ConfigItems.Add(new ConfigItem
            {
                ConfigName = "setup.plugin-api-key",
                ConfigValue = legacyCiphertext,
                IsEncrypted = true,
            });
            setupContext.ConfigItems.Add(new ConfigItem
            {
                ConfigName = "SeTuP.Plugin-ApI-KeY",
                ConfigValue = "legacy-plaintext",
                IsEncrypted = false,
            });
            await setupContext.SaveChangesAsync();
        }

        List<ConfigSnapshot> before;
        await using (var preContext = await _fixture.CreateMigratedContextAsync())
        {
            before = await preContext.ConfigItems
                .Select(row => new ConfigSnapshot(row.ConfigName, row.ConfigValue, row.IsEncrypted))
                .ToListAsync();
        }

        await using var checkContext = await _fixture.CreateMigratedContextAsync();
        using var encryption = new ConfigEncryptionService();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => StartupEncryptionCheck.RunAsync(checkContext, encryption));

        Assert.Contains("Duplicate managed config key 'setup.plugin-api-key' exists with multiple casings.", ex.Message);

        await using (var postContext = await _fixture.CreateMigratedContextAsync())
        {
            var after = await postContext.ConfigItems
                .Select(row => new ConfigSnapshot(row.ConfigName, row.ConfigValue, row.IsEncrypted))
                .ToListAsync();

            Assert.Equal(before.Count, after.Count);
            Assert.Equal(
                before.OrderBy(row => row.Name),
                after.OrderBy(row => row.Name));
        }
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

    private static string CreateLegacyV1Ciphertext(string configName, string plaintext, string masterKey)
    {
        var key = Convert.FromBase64String(masterKey);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var input = System.Text.Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[input.Length];
        var tag = new byte[16];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, input, ciphertext, tag);
            var packed = nonce.Concat(ciphertext).Concat(tag).ToArray();
            return "v1:" + Convert.ToBase64String(packed).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(input);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
        }
    }

    private sealed class ThrowingAuthenticationHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Authentication must not be reached after completion.");
    }

    private sealed record ConfigSnapshot(string Name, string Value, bool IsEncrypted);
}

public sealed class ConfigEncryptionDatabaseFixture : IAsyncLifetime
{
    // DavDatabaseContext resolves CONFIG_PATH every time it creates options. Keep an
    // immutable path for this fixture so cleanup never follows another test's env.
    private readonly string _configPath = Path.Join(Path.GetTempPath(), "nzbdav-tests", "config-encryption", Guid.NewGuid().ToString("N"));
    private readonly string _databaseFilePath;
    private readonly bool _acquireProcessEnvironmentGate;
    private bool _processEnvironmentGateHeld;
    private readonly string? _previousConfigPath = Environment.GetEnvironmentVariable("CONFIG_PATH");
    private readonly string? _previousMasterKey = Environment.GetEnvironmentVariable("NZBDAV_MASTER_KEY");
    private readonly string? _previousOldKey = Environment.GetEnvironmentVariable("NZBDAV_MASTER_KEY_OLD");

    public ConfigEncryptionDatabaseFixture()
        : this(acquireProcessEnvironmentGate: true)
    {
    }

    internal ConfigEncryptionDatabaseFixture(bool acquireProcessEnvironmentGate)
    {
        _acquireProcessEnvironmentGate = acquireProcessEnvironmentGate;
        _databaseFilePath = Path.Join(_configPath, "db.sqlite");
    }

    internal string ConfigPath => _configPath;
    internal string DatabaseFilePath => _databaseFilePath;

    public async ValueTask InitializeAsync()
    {
        if (_acquireProcessEnvironmentGate)
        {
            await backend.Tests.Config.ProcessEnvironmentGate.Instance.WaitAsync();
            _processEnvironmentGateHeld = true;
        }

        Environment.SetEnvironmentVariable("CONFIG_PATH", _configPath);
        Directory.CreateDirectory(_configPath);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await ResetAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("CONFIG_PATH", _previousConfigPath);
            Environment.SetEnvironmentVariable("NZBDAV_MASTER_KEY", _previousMasterKey);
            Environment.SetEnvironmentVariable("NZBDAV_MASTER_KEY_OLD", _previousOldKey);
            if (_processEnvironmentGateHeld)
            {
                backend.Tests.Config.ProcessEnvironmentGate.Instance.Release();
                _processEnvironmentGateHeld = false;
            }
        }
    }

    public async Task ResetAsync()
    {
        // All contexts returned by this fixture are disposed by their callers before
        // reset. Windows can release a pooled SQLite handle just after disposal, so
        // retry only this fixture's own immutable files after clearing the pool.
        SqliteConnection.ClearAllPools();
        await DeleteIfExistsAsync(_databaseFilePath);
        await DeleteIfExistsAsync(_databaseFilePath + "-wal");
        await DeleteIfExistsAsync(_databaseFilePath + "-shm");
    }

    public async Task<DavDatabaseContext> CreateMigratedContextAsync()
    {
        Directory.CreateDirectory(_configPath);
        var dbContext = new DavDatabaseContext();
        try
        {
            await dbContext.Database.MigrateAsync();
            return dbContext;
        }
        catch
        {
            await dbContext.DisposeAsync();
            throw;
        }
    }

    public void SetKeys(string? masterKey, string? oldKey)
    {
        Environment.SetEnvironmentVariable("NZBDAV_MASTER_KEY", masterKey);
        Environment.SetEnvironmentVariable("NZBDAV_MASTER_KEY_OLD", oldKey);
    }

    public string CreateKey() => Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    private static async Task DeleteIfExistsAsync(string path)
    {
        if (!File.Exists(path))
            return;

        const int maxAttempts = 10;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (IOException) when (attempt < maxAttempts - 1)
            {
                SqliteConnection.ClearAllPools();
                await Task.Delay(50);
            }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts - 1)
            {
                SqliteConnection.ClearAllPools();
                await Task.Delay(50);
            }
        }
    }
}

[Collection(nameof(ConfigEncryptionDatabaseCollection))]
public sealed class ConfigEncryptionDatabaseFixtureIsolationTests(ConfigEncryptionDatabaseFixture collectionFixture)
{
    [Fact]
    public async Task ResetAsync_DoesNotTouchAnUnrelatedActiveDatabase()
    {
        var isolatedFixture = new ConfigEncryptionDatabaseFixture(acquireProcessEnvironmentGate: false);
        var unrelatedPath = Path.Join(Path.GetTempPath(), "nzbdav-tests", "unrelated-context", $"{Guid.NewGuid():N}.sqlite");
        Directory.CreateDirectory(Path.GetDirectoryName(unrelatedPath)!);

        try
        {
            await isolatedFixture.InitializeAsync();

            var options = new DbContextOptionsBuilder<DavDatabaseContext>()
                .UseSqlite($"Data Source={unrelatedPath}")
                .Options;
            await using var unrelatedContext = new DavDatabaseContext(options);
            await unrelatedContext.Database.EnsureCreatedAsync();

            await Task.WhenAll(
                isolatedFixture.ResetAsync(),
                unrelatedContext.Database.CanConnectAsync());

            Assert.NotEqual(unrelatedPath, isolatedFixture.DatabaseFilePath);
            Assert.True(File.Exists(unrelatedPath));
            Assert.True(await unrelatedContext.Database.CanConnectAsync());
        }
        finally
        {
            await isolatedFixture.DisposeAsync();
            DeleteIfExists(unrelatedPath);
        }

        Assert.Equal(collectionFixture.ConfigPath, Environment.GetEnvironmentVariable("CONFIG_PATH"));
    }

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
