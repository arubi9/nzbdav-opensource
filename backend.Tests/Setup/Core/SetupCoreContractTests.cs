using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Setup.Core;
using NzbWebDAV.Clients.JellyfinSetup;

namespace NzbWebDAV.Tests.Setup.Core;

[Collection(nameof(backend.Tests.Config.EnvironmentVariableCollection))]
public sealed class SetupCoreContractTests
{
    [Fact]
    public void EnvironmentOptions_DefaultToDisabled_AndTrimSafeOverrides()
    {
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("NZBDAV_FULL_STACK", null),
            ("SETUP_NZBDAV_URL", "  https://nzbdav.example.test  "));

        var options = SetupEnvironmentOptions.FromEnvironment();

        Assert.False(options.FullStackEnabled);
        Assert.Equal("https://nzbdav.example.test", options.NzbdavUrl);
        Assert.Null(options.ValidationError);
    }

    [Fact]
    public void InternalNzbdavDefaultTargetsBackendListenerNotBrowserListener()
    {
        Assert.Equal("http://nzbdav:8080", SetupEnvironmentOptions.DefaultNzbdavUrl);
        Assert.Equal("http://nzbdav:8080", new JellyfinSetupOptions("http://jellyfin:8096", "u", "p").NzbdavUrl);
    }

    [Fact]
    public void EnvironmentOptions_InvalidUrlFailsClosedWithSafeError()
    {
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("NZBDAV_FULL_STACK", "true"),
            ("SETUP_SONARR_URL", "https://user:password@sonarr.example.test/?apiKey=secret#fragment"));

        var options = SetupEnvironmentOptions.FromEnvironment();

        Assert.False(options.FullStackEnabled);
        Assert.Equal(SetupEnvironmentOptions.DefaultSonarrUrl, options.SonarrUrl);
        Assert.NotNull(options.ValidationError);
        Assert.DoesNotContain("password", options.ValidationError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("apiKey", options.ValidationError, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("https:///missing-host")]
    [InlineData("https://user@example.test")]
    [InlineData("https://example.test/admin")]
    [InlineData("https://example.test?token=secret")]
    [InlineData("https://example.test#fragment")]
    [InlineData("https://example.test\\admin")]
    [InlineData("https://example.test/%2fadmin")]
    [InlineData("https://example.test/%0d%0a")]
    [InlineData("https://example.test/.")]
    [InlineData("https://example.test/./")]
    [InlineData("https://example.test/a/..")]
    public void EnvironmentOptions_RejectsEachUnsafeOriginComponent(string invalidUrl)
    {
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("NZBDAV_FULL_STACK", "true"),
            ("SETUP_SONARR_URL", invalidUrl));

        var options = SetupEnvironmentOptions.FromEnvironment();

        Assert.False(options.FullStackEnabled);
        Assert.Equal(SetupEnvironmentOptions.DefaultSonarrUrl, options.SonarrUrl);
        Assert.NotNull(options.ValidationError);
    }

    [Theory]
    [InlineData("https://xn--fsqu00a.xn--0zwm56d:8443/", "https://xn--fsqu00a.xn--0zwm56d:8443")]
    [InlineData("http://[2001:db8::1]:8080/", "http://[2001:db8::1]:8080")]
    [InlineData("http://192.0.2.10:8080", "http://192.0.2.10:8080")]
    [InlineData("https://example.test:443", "https://example.test")]
    [InlineData("http://example.test:80", "http://example.test")]
    public void EnvironmentOptions_AcceptsAndCanonicalizesValidOrigins(string input, string expected)
    {
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("SETUP_SONARR_URL", input));

        Assert.Equal(expected, SetupEnvironmentOptions.FromEnvironment().SonarrUrl);
    }

    [Theory]
    [InlineData("https://127.1")]
    [InlineData("http://2130706433")]
    [InlineData("http://0x7f000001")]
    [InlineData("http://010.000.000.001")]
    [InlineData("https://0x7F000001")]
    public void EnvironmentOptions_RejectsAmbiguousIpv4Forms(string input)
    {
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("SETUP_NZBDAV_URL", input));

        var options = SetupEnvironmentOptions.FromEnvironment();

        Assert.Equal(SetupEnvironmentOptions.DefaultNzbdavUrl, options.NzbdavUrl);
        Assert.NotNull(options.ValidationError);
        Assert.False(options.FullStackEnabled);
    }

    [Theory]
    [InlineData("http://example.test:0")]
    [InlineData("http://example.test:65536")]
    [InlineData("http://example.test/%2e%2e")]
    [InlineData("http://example.test/../")]
    [InlineData("http://example.test/\u202e")]
    [InlineData("http://exa\u200Dmple.test")]
    [InlineData("http://例子.测试")]
    [InlineData("http://раураl.example")]
    [InlineData("http://example_test")]
    [InlineData("http://-bad.example")]
    [InlineData("http://bad-.example")]
    [InlineData("http://example..test")]
    [InlineData("http://EXAMPLE.test")]
    [InlineData("http://xn--.example")]
    public void EnvironmentOptions_RejectsAmbiguousPortsEncodedSeparatorsAndUnsafeHostCharacters(string input)
    {
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("NZBDAV_FULL_STACK", "true"),
            ("SETUP_SONARR_URL", input));

        var options = SetupEnvironmentOptions.FromEnvironment();

        Assert.False(options.FullStackEnabled);
        Assert.Equal(SetupEnvironmentOptions.DefaultSonarrUrl, options.SonarrUrl);
    }

    [Fact]
    public void EnvironmentOptions_CanonicalizesRootOrigin()
    {
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("SETUP_SONARR_URL", "  https://sonarr.example.test/  "));

        var options = SetupEnvironmentOptions.FromEnvironment();

        Assert.Equal("https://sonarr.example.test", options.SonarrUrl);
    }

    [Fact]
    public void ManagedNames_AreStable()
    {
        Assert.Equal("NZBDAV", SetupManagedNames.Nzbdav);
        Assert.Equal("NZBDAV Sonarr", SetupManagedNames.Sonarr);
        Assert.Equal("NZBDAV Radarr", SetupManagedNames.Radarr);
        Assert.Equal("NZBDAV Movies", SetupManagedNames.MoviesRootDirectory);
        Assert.Equal("NZBDAV TV", SetupManagedNames.TvRootDirectory);
    }

    [Fact]
    public void SensitiveKeys_AreCaseInsensitiveAndNotExternallyMutable()
    {
        Assert.True(SensitiveConfigKeys.IsSensitive("SETUP.PLUGIN-API-KEY"));
        Assert.True(SensitiveConfigKeys.Keys.Contains("SETUP.PLUGIN-API-KEY"));
        Assert.False(typeof(ISet<string>).IsAssignableFrom(typeof(SensitiveConfigKeys).GetProperty("Keys")!.PropertyType));
    }

    [Fact]
    public void SetupStatus_SnapshotsStepsAndRejectsMutation()
    {
        var steps = new List<SetupStepStatus> { new("services", SetupStepState.Pending) };
        var status = new SetupStatus(true, false, steps);

        steps[0] = new SetupStepStatus("services", SetupStepState.Complete);

        Assert.Equal(SetupStepState.Pending, status.Steps[0].State);
        Assert.Throws<NotSupportedException>(() => ((IList<SetupStepStatus>)status.Steps)[0] =
            new SetupStepStatus("services", SetupStepState.Failed));
    }

    [Fact]
    public void QueueDefaults_AreEmptyAndDoNothing()
    {
        var defaults = SetupQueueDefaults.Create();

        Assert.Empty(defaults);
        Assert.Equal(ArrConfig.QueueAction.DoNothing, SetupQueueDefaults.Action);
    }

    [Fact]
    public void Status_UsesClosedStates_AndNeverSerializesSecrets()
    {
        var status = new SetupStatus(
            Enabled: true,
            Completed: false,
            Steps: [new SetupStepStatus("services", SetupStepState.Pending)]);

        var json = JsonSerializer.Serialize(status);

        Assert.Contains("\"State\":\"pending\"", json);
        Assert.DoesNotContain("api-key", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-value", json, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SetupStepStatus>(
            "{\"Name\":\"services\",\"State\":\"arbitrary\"}"));
    }

    [Fact]
    public void GenericSetupWrites_AreRejectedWithOrWithoutMasterKey()
    {
        foreach (var masterKey in new string?[] { null, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) })
        {
            using var environment = new backend.Tests.Config.TemporaryEnvironment(("NZBDAV_MASTER_KEY", masterKey));
            using var encryption = new ConfigEncryptionService();
            var manager = new ConfigManager(encryption);

            foreach (var key in new[]
                     {
                         SetupConfigKeys.Indexers,
                         SetupConfigKeys.JellyfinApiKey,
                         SetupConfigKeys.PluginApiKey,
                         SetupConfigKeys.Completed,
                         "SETUP.PLUGIN-API-KEY",
                     })
            {
                Assert.Throws<InvalidOperationException>(() => manager.UpdateValues(
                    [new ConfigItem { ConfigName = key, ConfigValue = "secret" }]));
                Assert.Throws<InvalidOperationException>(() => manager.PrepareForStorage(
                    [new ConfigItem { ConfigName = key, ConfigValue = "secret" }]));
            }
        }
    }

    [Fact]
    public async Task ConfigEvents_ThrowingSubscriberDoesNotUndoCommittedSetupSave()
    {
        var configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-throwing-event-{Guid.NewGuid():N}");
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("CONFIG_PATH", configPath),
            ("DATABASE_URL", null),
            ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
        Directory.CreateDirectory(configPath);
        await using var context = new DavDatabaseContext();
        await context.Database.MigrateAsync();

        using var encryption = new ConfigEncryptionService();
        var manager = new ConfigManager(encryption);
        var otherSubscriberNotified = false;
        manager.OnConfigChanged += (_, _) => throw new InvalidOperationException("test subscriber failure");
        manager.OnConfigChanged += (_, _) => otherSubscriberNotified = true;

        await new SetupConfigPersistence(manager, context).SaveAsync(new SetupSecretValues
        {
            Indexers = "[]",
            JellyfinApiKey = "jellyfin-event-secret",
            PluginApiKey = "plugin-event-secret",
        }, completed: true);

        Assert.True(otherSubscriberNotified);
        Assert.True(manager.IsSetupCompleted());
        Assert.Equal("true", await context.ConfigItems
            .Where(row => row.ConfigName == SetupConfigKeys.Completed)
            .Select(row => row.ConfigValue)
            .SingleAsync());
    }

    [Fact]
    public async Task ConfigEvents_DedicatedSaveAndHotReloadRedactSetupSecrets()
    {
        var configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-events-{Guid.NewGuid():N}");
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("CONFIG_PATH", configPath),
            ("DATABASE_URL", null),
            ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
        Directory.CreateDirectory(configPath);
        await using var context = new DavDatabaseContext();
        await context.Database.MigrateAsync();

        using var encryption = new ConfigEncryptionService();
        var manager = new ConfigManager(encryption);
        ConfigManager.ConfigEventArgs? saved = null;
        manager.OnConfigChanged += (_, args) => saved = args;
        await new SetupConfigPersistence(manager, context).SaveAsync(new SetupSecretValues
        {
            Indexers = "[]",
            JellyfinApiKey = "jellyfin-event-secret",
            PluginApiKey = "plugin-event-secret",
        }, completed: false);

        Assert.NotNull(saved);
        Assert.Equal("[redacted]", saved!.ChangedConfig[SetupConfigKeys.Indexers]);
        Assert.Equal("[redacted]", saved.ChangedConfig[SetupConfigKeys.JellyfinApiKey]);
        Assert.Equal("[redacted]", saved.ChangedConfig[SetupConfigKeys.PluginApiKey]);

        var reloaded = new ConfigManager(new ConfigEncryptionService());
        ConfigManager.ConfigEventArgs? hotReloaded = null;
        reloaded.OnConfigChanged += (_, args) => hotReloaded = args;
        await reloaded.LoadConfig();

        Assert.NotNull(hotReloaded);
        Assert.Equal("[redacted]", hotReloaded!.ChangedConfig[SetupConfigKeys.PluginApiKey]);
        Assert.Equal("plugin-event-secret", reloaded.GetPluginApiKey());
    }

    [Fact]
    public void ConfigEvents_RedactEverySensitiveRegistryKey()
    {
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
        using var encryption = new ConfigEncryptionService();
        var manager = new ConfigManager(encryption);
        ConfigManager.ConfigEventArgs? observed = null;
        manager.OnConfigChanged += (_, args) => observed = args;

        manager.UpdateValues([
            new ConfigItem { ConfigName = "api.key", ConfigValue = "frontend-secret" },
            new ConfigItem { ConfigName = "cache.l2.secret-key", ConfigValue = "object-secret" },
            new ConfigItem { ConfigName = "webdav.pass", ConfigValue = "password-hash" },
        ]);

        Assert.NotNull(observed);
        Assert.All(observed!.ChangedConfig,
            pair => Assert.Equal("[redacted]", pair.Value));
        Assert.Equal("frontend-secret", manager.GetApiKey());
    }

    [Fact]
    public void AllManagedSensitiveKeys_CanonicalizeBeforeEncryptionAndRejectCaseDuplicates()
    {
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
        using var encryption = new ConfigEncryptionService();
        var manager = new ConfigManager(encryption);
        var item = new ConfigItem { ConfigName = "API.KEY", ConfigValue = "secret" };

        var prepared = manager.PrepareForStorage([item]);

        Assert.Equal("api.key", item.ConfigName);
        Assert.Equal("api.key", prepared[0].ConfigName);
        Assert.True(prepared[0].IsEncrypted);
        Assert.Throws<InvalidOperationException>(() => manager.PrepareForStorage([
            new ConfigItem { ConfigName = "api.key", ConfigValue = "one" },
            new ConfigItem { ConfigName = "API.KEY", ConfigValue = "two" },
        ]));
    }

    [Fact]
    public void SetupKeys_CanonicalizeAndRejectUnknownOrAmbiguousReservedNames()
    {
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
        using var encryption = new ConfigEncryptionService();
        var manager = new ConfigManager(encryption);

        var mixedCase = new ConfigItem { ConfigName = "SeTuP.PlUgIn-ApI-KeY", ConfigValue = "secret" };
        var prepared = manager.PrepareSetupForStorage([mixedCase]);
        Assert.Equal(SetupConfigKeys.PluginApiKey, mixedCase.ConfigName);
        Assert.Equal(SetupConfigKeys.PluginApiKey, prepared[0].ConfigName);

        var usenetAlias = new ConfigItem { ConfigName = "USENET.Providers", ConfigValue = "[{\"name\":\"provider\"}]" };
        var preparedUsenet = manager.PrepareSetupForStorage([usenetAlias]);
        Assert.Equal(SetupConfigKeys.UsenetProviders, usenetAlias.ConfigName);
        Assert.Equal(SetupConfigKeys.UsenetProviders, preparedUsenet[0].ConfigName);

        Assert.Throws<InvalidOperationException>(() => manager.UpdateValues(
            [new ConfigItem { ConfigName = "setup.unknown", ConfigValue = "value" }]));
        Assert.Throws<InvalidOperationException>(() => manager.PrepareForStorage(
            [new ConfigItem { ConfigName = "SETUP.UNKNOWN", ConfigValue = "value" }]));
        Assert.Throws<InvalidOperationException>(() => manager.PrepareForStorage([
            new ConfigItem { ConfigName = SetupConfigKeys.PluginApiKey, ConfigValue = "a" },
            new ConfigItem { ConfigName = "SETUP.PLUGIN-API-KEY", ConfigValue = "a" },
        ]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GenericWrites_CannotMutateSetupCompletionMarker(bool keyConfigured)
    {
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("NZBDAV_MASTER_KEY", keyConfigured ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) : null));
        using var encryption = new ConfigEncryptionService();
        var manager = new ConfigManager(encryption);
        var marker = new ConfigItem { ConfigName = SetupConfigKeys.Completed, ConfigValue = "true" };

        Assert.Throws<InvalidOperationException>(() => manager.UpdateValues([marker]));
        Assert.Throws<InvalidOperationException>(() => manager.PrepareForStorage([marker]));
        Assert.False(manager.IsSetupCompleted());
    }

    [Theory]
    [InlineData("1")]
    [InlineData("yes")]
    [InlineData("")]
    public void SetupCompletion_RequiresPlaintextBoolean(string invalidValue)
    {
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
        using var encryption = new ConfigEncryptionService();
        var manager = new ConfigManager(encryption);

        Assert.Throws<InvalidOperationException>(() => manager.PrepareSetupForStorage([
            new ConfigItem
            {
                ConfigName = "SETUP.COMPLETED",
                ConfigValue = invalidValue,
                IsEncrypted = false,
            },
        ]));

        var ciphertext = encryption.Encrypt(SetupConfigKeys.Completed, "true");
        Assert.Throws<InvalidOperationException>(() => manager.PrepareSetupForStorage([
            new ConfigItem
            {
                ConfigName = SetupConfigKeys.Completed,
                ConfigValue = ciphertext,
                IsEncrypted = true,
            },
        ]));
    }

    [Fact]
    public void SetupPersistence_PersistedKeyNames_IsReadOnlyAndContainsUsenetProviders()
    {
        Assert.IsAssignableFrom<IReadOnlySet<string>>(SetupConfigPersistence.PersistedKeyNames);
        Assert.True(SetupConfigPersistence.PersistedKeyNames.Contains("usenet.providers"));
        Assert.True(SetupConfigPersistence.PersistedKeyNames.Contains(SetupConfigKeys.UsenetProviders));
    }

    [Fact]
    public async Task SetupPersistence_DoesNotMutateSecretsAfterCompletion()
    {
        var configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-completed-no-mutate-{Guid.NewGuid():N}");
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("CONFIG_PATH", configPath),
            ("DATABASE_URL", null),
            ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))),
            ("FRONTEND_BACKEND_API_KEY", null));
        Directory.CreateDirectory(configPath);

        await using var context = new DavDatabaseContext();
        await context.Database.MigrateAsync();
        using var encryption = new ConfigEncryptionService();
        var setupItem = "[{\"name\":\"one\"}]";
        context.ConfigItems.AddRange(
        [
            new ConfigItem
            {
                ConfigName = SetupConfigKeys.Indexers,
                ConfigValue = encryption.Encrypt(SetupConfigKeys.Indexers, "[]"),
                IsEncrypted = true
            },
            new ConfigItem
            {
                ConfigName = SetupConfigKeys.JellyfinApiKey,
                ConfigValue = encryption.Encrypt(SetupConfigKeys.JellyfinApiKey, "jellyfin"),
                IsEncrypted = true
            },
            new ConfigItem
            {
                ConfigName = SetupConfigKeys.PluginApiKey,
                ConfigValue = encryption.Encrypt(SetupConfigKeys.PluginApiKey, "plugin"),
                IsEncrypted = true
            },
            new ConfigItem
            {
                ConfigName = SetupConfigKeys.UsenetProviders,
                ConfigValue = encryption.Encrypt(SetupConfigKeys.UsenetProviders, setupItem),
                IsEncrypted = true
            },
            new ConfigItem { ConfigName = SetupConfigKeys.Completed, ConfigValue = "true", IsEncrypted = false },
        ]);
        await context.SaveChangesAsync();

        var manager = new ConfigManager(encryption);
        await new SetupConfigPersistence(manager, context).SaveAsync(new SetupSecretValues
        {
            Indexers = "[{\"name\":\"two\"}]",
            JellyfinApiKey = "new-jellyfin",
            PluginApiKey = "new-plugin",
            UsenetProviders = "[{\"name\":\"new\"}]",
        }, completed: false);

        var persisted = await context.ConfigItems
            .Where(row => row.ConfigName != SetupConfigKeys.Completed
                && SetupConfigPersistence.PersistedKeyNames.Contains(row.ConfigName))
            .OrderBy(row => row.ConfigName)
            .ToListAsync();

        Assert.Equal(3, persisted.Count);
        Assert.DoesNotContain(persisted, row => row.ConfigName == SetupConfigKeys.JellyfinApiKey);
        Assert.Equal("[]", encryption.Decrypt(SetupConfigKeys.Indexers, persisted.Single(row => row.ConfigName == SetupConfigKeys.Indexers).ConfigValue).plaintext);
        Assert.Equal("plugin", encryption.Decrypt(SetupConfigKeys.PluginApiKey, persisted.Single(row => row.ConfigName == SetupConfigKeys.PluginApiKey).ConfigValue).plaintext);
        Assert.Equal(setupItem, encryption.Decrypt(SetupConfigKeys.UsenetProviders, persisted.Single(row => row.ConfigName == SetupConfigKeys.UsenetProviders).ConfigValue).plaintext);
        Assert.Equal("true", await context.ConfigItems
            .Where(row => row.ConfigName == SetupConfigKeys.Completed)
            .Select(row => row.ConfigValue)
            .SingleAsync());
    }

    [Fact]
    public void SetupPersistence_AuthenticatesExistingCiphertext()
    {
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
        using var encryption = new ConfigEncryptionService();
        var manager = new ConfigManager(encryption);

        Assert.Throws<InvalidOperationException>(() => manager.PrepareSetupForStorage(
            [new ConfigItem { ConfigName = SetupConfigKeys.PluginApiKey, ConfigValue = "v1:not-base64", IsEncrypted = true }]));

        var ciphertext = encryption.Encrypt(SetupConfigKeys.PluginApiKey, "secret");
        var tampered = ciphertext[..3] + (ciphertext[3] == 'A' ? 'B' : 'A') + ciphertext[4..];
        Assert.Throws<CryptographicException>(() => manager.PrepareSetupForStorage(
            [new ConfigItem { ConfigName = SetupConfigKeys.PluginApiKey, ConfigValue = tampered, IsEncrypted = true }]));

        using var wrongKeyEnvironment = new backend.Tests.Config.TemporaryEnvironment(
            ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
        using var wrongKeyEncryption = new ConfigEncryptionService();
        var wrongKeyCiphertext = wrongKeyEncryption.Encrypt(SetupConfigKeys.PluginApiKey, "secret");
        Assert.Throws<CryptographicException>(() => manager.PrepareSetupForStorage(
            [new ConfigItem { ConfigName = SetupConfigKeys.PluginApiKey, ConfigValue = wrongKeyCiphertext, IsEncrypted = true }]));
    }

    [Fact]
    public void SetupPersistence_RetryWithOldKeyReencryptsWithPrimary()
    {
        var oldKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        string oldCiphertext;
        using (var oldEnvironment = new backend.Tests.Config.TemporaryEnvironment(
                   ("NZBDAV_MASTER_KEY", oldKey), ("NZBDAV_MASTER_KEY_OLD", null)))
        using (var oldEncryption = new ConfigEncryptionService())
            oldCiphertext = oldEncryption.Encrypt(SetupConfigKeys.PluginApiKey, "secret");

        var primaryKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("NZBDAV_MASTER_KEY", primaryKey), ("NZBDAV_MASTER_KEY_OLD", oldKey));
        using var encryption = new ConfigEncryptionService();
        var manager = new ConfigManager(encryption);

        var retry = manager.PrepareSetupForStorage(
            [new ConfigItem { ConfigName = SetupConfigKeys.PluginApiKey, ConfigValue = oldCiphertext, IsEncrypted = true }]);

        Assert.NotEqual(oldCiphertext, retry[0].ConfigValue);
        Assert.Equal("secret", encryption.Decrypt(SetupConfigKeys.PluginApiKey, retry[0].ConfigValue).plaintext);
    }

    [Fact]
    public void SetupSecrets_AreEncryptedAndCompletionMarkerIsPlaintext()
    {
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
        using var encryption = new ConfigEncryptionService();
        var manager = new ConfigManager(encryption);
        var rows = manager.PrepareSetupForStorage([
            new ConfigItem { ConfigName = SetupConfigKeys.Indexers, ConfigValue = "[]" },
            new ConfigItem { ConfigName = SetupConfigKeys.JellyfinApiKey, ConfigValue = "secret-jellyfin-key" },
            new ConfigItem { ConfigName = SetupConfigKeys.PluginApiKey, ConfigValue = "secret-plugin-key" },
            new ConfigItem { ConfigName = SetupConfigKeys.UsenetProviders, ConfigValue = "[{\"name\":\"provider\"}]" },
            new ConfigItem { ConfigName = SetupConfigKeys.Completed, ConfigValue = "true" },
        ]);

        Assert.Equal(4, rows.Count(row => row.IsEncrypted));
        Assert.All(rows.Where(row => row.IsEncrypted), row => Assert.StartsWith("v2:", row.ConfigValue));
        Assert.DoesNotContain(rows, row => row.ConfigValue == "[]");
        Assert.DoesNotContain(rows, row => row.ConfigValue == "secret-jellyfin-key");
        Assert.DoesNotContain(rows, row => row.ConfigValue == "secret-plugin-key");
        Assert.DoesNotContain(rows, row => row.ConfigValue == "[{\"name\":\"provider\"}]");

        var marker = Assert.Single(rows, row => row.ConfigName == SetupConfigKeys.Completed);
        Assert.False(marker.IsEncrypted);
        Assert.Equal("true", marker.ConfigValue);
        Assert.DoesNotContain("secret", marker.ConfigValue, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SetupPersistence_UsesSQLiteAndRoundTripsSecretsIdempotently()
    {
        var configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-{Guid.NewGuid():N}");
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("CONFIG_PATH", configPath),
            ("DATABASE_URL", null),
            ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))),
            ("FRONTEND_BACKEND_API_KEY", null));

        Directory.CreateDirectory(configPath);
        await using var context = new DavDatabaseContext();
        await context.Database.MigrateAsync();
        context.ConfigItems.RemoveRange(await context.ConfigItems.ToListAsync());
        await context.SaveChangesAsync();
        using var encryption = new ConfigEncryptionService();
        var manager = new ConfigManager(encryption);
        var secrets = new SetupSecretValues
        {
            Indexers = "[]",
            JellyfinApiKey = "secret-jellyfin-key",
            PluginApiKey = "secret-plugin-key",
            UsenetProviders = "[{\"name\":\"primary\"}]",
        };
        context.ConfigItems.Add(new ConfigItem
        {
            ConfigName = "UsEnEt.PRoViDeRs",
            ConfigValue = "[{\"name\":\"legacy\"}]",
            IsEncrypted = false,
        });
        await context.SaveChangesAsync();

        var persistence = new SetupConfigPersistence(manager, context);
        await persistence.SaveAsync(secrets, completed: true);
        await persistence.SaveAsync(new SetupSecretValues(), completed: false);
        Assert.Equal("true", await context.ConfigItems
            .Where(row => row.ConfigName == SetupConfigKeys.Completed)
            .Select(row => row.ConfigValue)
            .SingleAsync());

        var frontendRow = manager.PrepareForStorage(
        [new ConfigItem { ConfigName = "api.key", ConfigValue = "frontend-key" }]);
        context.ConfigItems.AddRange(frontendRow);
        await context.SaveChangesAsync();

        var storedRows = await context.ConfigItems.ToListAsync();
        var storedSecrets = storedRows.Where(row => row.ConfigName != SetupConfigKeys.Completed && row.ConfigName != "api.key").ToList();
        Assert.Equal(3, storedSecrets.Count);
        Assert.All(storedSecrets, row =>
        {
            Assert.True(row.IsEncrypted);
            Assert.StartsWith("v2:", row.ConfigValue);
        });
        Assert.Equal("frontend-key", encryption.Decrypt("api.key", storedRows.Single(row => row.ConfigName == "api.key").ConfigValue).plaintext);
        Assert.Equal("true", storedRows.Single(row => row.ConfigName == SetupConfigKeys.Completed).ConfigValue);
        Assert.False(storedRows.Single(row => row.ConfigName == SetupConfigKeys.Completed).IsEncrypted);

        var reloaded = new ConfigManager(new ConfigEncryptionService());
        await reloaded.LoadConfig();
        Assert.Equal("secret-plugin-key", reloaded.GetPluginApiKey());
        Assert.Equal("frontend-key", reloaded.GetApiKey());
        Assert.True(reloaded.IsSetupCompleted());
        var indexerRows = storedRows.Where(row => row.ConfigName == SetupConfigKeys.Indexers).ToList();
        Assert.Single(indexerRows);
        Assert.Equal("[]", encryption.Decrypt(SetupConfigKeys.Indexers, indexerRows[0].ConfigValue).plaintext);

        var jellyfinRows = storedRows.Where(row => row.ConfigName == SetupConfigKeys.JellyfinApiKey).ToList();
        Assert.Empty(jellyfinRows);

        var usenetRows = storedRows.Where(row => row.ConfigName.Contains(SetupConfigKeys.UsenetProviders, StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Single(usenetRows);
        Assert.Equal("[{\"name\":\"primary\"}]", encryption.Decrypt(SetupConfigKeys.UsenetProviders, usenetRows[0].ConfigValue).plaintext);

        var ciphertextBeforeRetry = storedSecrets.Where(row => row.ConfigName != SetupConfigKeys.UsenetProviders)
            .ToDictionary(row => row.ConfigName, row => row.ConfigValue);
        await persistence.SaveAsync(secrets, completed: true);
        var retryStoredRows = (await context.ConfigItems.ToListAsync())
            .Where(row => row.ConfigName.StartsWith("setup.", StringComparison.OrdinalIgnoreCase)
                         || row.ConfigName.Contains(SetupConfigKeys.UsenetProviders, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var expectedRetryRows = ciphertextBeforeRetry;
        var actualRetryRows = retryStoredRows.Where(row => row.IsEncrypted
                && !row.ConfigName.Contains(SetupConfigKeys.UsenetProviders, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(row => row.ConfigName, row => row.ConfigValue);

        foreach (var kv in expectedRetryRows)
        {
            if (string.Equals(kv.Key, SetupConfigKeys.UsenetProviders, StringComparison.OrdinalIgnoreCase))
                continue;

            Assert.True(actualRetryRows.TryGetValue(kv.Key, out var actualRetryValue),
                $"Missing expected retry secret '{kv.Key}'.");
            Assert.Equal(kv.Value, actualRetryValue);
        }

        context.ChangeTracker.Clear();
        context.ConfigItems.Update(new ConfigItem { ConfigName = SetupConfigKeys.Completed, ConfigValue = "not-a-boolean", IsEncrypted = false });
        await context.SaveChangesAsync();
        var malformedMarkerManager = new ConfigManager(new ConfigEncryptionService());
        await malformedMarkerManager.LoadConfig();
        Assert.False(malformedMarkerManager.IsSetupCompleted());
    }

    [Fact]
    public async Task SetupPersistence_UsenetProviders_CaseInsensitiveAliasesAreMergedOrRejected()
    {
        var configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-usenet-alias-{Guid.NewGuid():N}");
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("CONFIG_PATH", configPath),
            ("DATABASE_URL", null),
            ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))),
            ("FRONTEND_BACKEND_API_KEY", null));
        Directory.CreateDirectory(configPath);

        await using var context = new DavDatabaseContext();
        await context.Database.MigrateAsync();
        context.ConfigItems.Add(new ConfigItem
        {
            ConfigName = "USENET.PROVIDERS",
            ConfigValue = "[{\"name\":\"legacy\"}]",
            IsEncrypted = false,
        });
        context.ConfigItems.Add(new ConfigItem
        {
            ConfigName = "uSeNeT.Providers",
            ConfigValue = "[{\"name\":\"dup\"}]",
            IsEncrypted = false,
        });
        await context.SaveChangesAsync();

        using var encryption = new ConfigEncryptionService();
        var manager = new ConfigManager(encryption);
        var persistence = new SetupConfigPersistence(manager, context);

        await Assert.ThrowsAsync<InvalidOperationException>(() => persistence.SaveAsync(
            new SetupSecretValues { UsenetProviders = "[{\"name\":\"incoming\"}]" },
            completed: false));

        var rows = await context.ConfigItems
            .Where(row => row.ConfigName == "USENET.PROVIDERS" || row.ConfigName == "uSeNeT.Providers")
            .OrderBy(row => row.ConfigName)
            .ToListAsync();

        Assert.Equal(2, rows.Count);
        Assert.Equal("[{\"name\":\"legacy\"}]", rows[0].ConfigValue);
        Assert.Equal("[{\"name\":\"dup\"}]", rows[1].ConfigValue);
    }

    [Fact]
    public async Task SetupPersistence_PartialSaveCanonicalizesLegacyRowsAndPreservesSecrets()
    {
        var configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-partial-{Guid.NewGuid():N}");
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("CONFIG_PATH", configPath),
            ("DATABASE_URL", null),
            ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))),
            ("FRONTEND_BACKEND_API_KEY", null));
        Directory.CreateDirectory(configPath);

        using var encryption = new ConfigEncryptionService();
        var legacyIndexerCiphertext = encryption.Encrypt(SetupConfigKeys.Indexers, "[]");
        await using (var initialize = new DavDatabaseContext())
        {
            await initialize.Database.MigrateAsync();
            initialize.ConfigItems.AddRange([
                new ConfigItem { ConfigName = "SETUP.INDEXERS", ConfigValue = legacyIndexerCiphertext, IsEncrypted = true },
                new ConfigItem { ConfigName = "setup.jellyfin-api-key", ConfigValue = "legacy-jellyfin", IsEncrypted = false },
                new ConfigItem { ConfigName = "SeTuP.Plugin-ApI-KeY", ConfigValue = "legacy-plugin", IsEncrypted = false },
                new ConfigItem { ConfigName = "SETUP.COMPLETED", ConfigValue = "false", IsEncrypted = false },
            ]);
            await initialize.SaveChangesAsync();
        }

        await using var context = new DavDatabaseContext();
        var manager = new ConfigManager(encryption);
        await new SetupConfigPersistence(manager, context).SaveAsync(
            new SetupSecretValues(), completed: false);

        var rows = await context.ConfigItems
            .Where(row => row.ConfigName.StartsWith("setup."))
            .ToListAsync();
        Assert.Equal(
            new[] { SetupConfigKeys.Completed, SetupConfigKeys.Indexers, SetupConfigKeys.JellyfinApiKey, SetupConfigKeys.PluginApiKey },
            rows.Select(row => row.ConfigName).OrderBy(name => name));
        Assert.All(rows.Where(row => row.ConfigName != SetupConfigKeys.Completed), row => Assert.True(row.IsEncrypted));
        Assert.Equal(legacyIndexerCiphertext, rows.Single(row => row.ConfigName == SetupConfigKeys.Indexers).ConfigValue);
        Assert.Equal("[]", encryption.Decrypt(SetupConfigKeys.Indexers, rows.Single(row => row.ConfigName == SetupConfigKeys.Indexers).ConfigValue).plaintext);
        Assert.Equal("legacy-jellyfin", encryption.Decrypt(SetupConfigKeys.JellyfinApiKey, rows.Single(row => row.ConfigName == SetupConfigKeys.JellyfinApiKey).ConfigValue).plaintext);
        Assert.Equal("legacy-plugin", encryption.Decrypt(SetupConfigKeys.PluginApiKey, rows.Single(row => row.ConfigName == SetupConfigKeys.PluginApiKey).ConfigValue).plaintext);
    }

    [Fact]
    public async Task SetupPersistence_ConcurrentFirstSavesAndChangesRemainConsistent()
    {
        var configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-concurrent-{Guid.NewGuid():N}");
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("CONFIG_PATH", configPath),
            ("DATABASE_URL", null),
            ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))),
            ("FRONTEND_BACKEND_API_KEY", null));
        Directory.CreateDirectory(configPath);

        await using (var initialize = new DavDatabaseContext())
            await initialize.Database.MigrateAsync();

        await using var context1 = new DavDatabaseContext();
        await using var context2 = new DavDatabaseContext();
        using var encryption1 = new ConfigEncryptionService();
        using var encryption2 = new ConfigEncryptionService();
        var first = new SetupConfigPersistence(new ConfigManager(encryption1), context1);
        var second = new SetupConfigPersistence(new ConfigManager(encryption2), context2);

        await Task.WhenAll(
            first.SaveAsync(new SetupSecretValues
            {
                Indexers = "[]",
                JellyfinApiKey = "jellyfin-a",
                PluginApiKey = "plugin-a",
            }, completed: false),
            second.SaveAsync(new SetupSecretValues
            {
                Indexers = "[]",
                JellyfinApiKey = "jellyfin-b",
                PluginApiKey = "plugin-b",
            }, completed: false));

        await using var verify = new DavDatabaseContext();
        var rows = await verify.ConfigItems
            .Where(row => row.ConfigName.StartsWith("setup."))
            .ToListAsync();
        Assert.Equal(4, rows.Count);
        Assert.Equal(4, rows.Select(row => row.ConfigName).Distinct(StringComparer.Ordinal).Count());
        Assert.All(rows.Where(row => row.ConfigName != SetupConfigKeys.Completed), row => Assert.True(row.IsEncrypted));

        await first.SaveAsync(new SetupSecretValues { PluginApiKey = "plugin-changed" }, completed: false);
        verify.ChangeTracker.Clear();
        var changed = await verify.ConfigItems.SingleAsync(row => row.ConfigName == SetupConfigKeys.PluginApiKey);
        Assert.True(changed.IsEncrypted);
        Assert.Equal("plugin-changed", encryption1.Decrypt(SetupConfigKeys.PluginApiKey, changed.ConfigValue).plaintext);
    }

    [Fact]
    public void SetupPersistence_ExistingRowDiagnosticsNeverContainPlaintext()
    {
        var rowType = typeof(SetupConfigPersistence).GetNestedType(
            "ExistingSetupRow", BindingFlags.NonPublic);
        Assert.NotNull(rowType);
        Assert.True(rowType!.IsSealed);
        Assert.Null(rowType.GetMethod("<Clone>$", BindingFlags.Instance | BindingFlags.NonPublic));

        var row = Activator.CreateInstance(
            rowType,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null,
            args: [
                SetupConfigKeys.PluginApiKey,
                new ConfigItem
                {
                    ConfigName = SetupConfigKeys.PluginApiKey,
                    ConfigValue = "diagnostic-secret",
                    IsEncrypted = false,
                },
                "diagnostic-secret",
                false,
            ],
            culture: null);

        Assert.NotNull(row);
        Assert.DoesNotContain("diagnostic-secret", row!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetupPersistence_RetrySeamRetriesTransientPostgresStatesAndStopsAtBound()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<PostgresException>(() =>
            SetupConfigPersistence.ExecuteWithRetriesForTestsAsync(attempt =>
            {
                attempts = attempt;
                throw new PostgresException("serialization failure", "ERROR", "ERROR", "40001");
            }));

        Assert.Equal(4, attempts);
    }

    [Fact]
    public void SetupPersistence_RequiresMasterKeyForSecrets()
    {
        using var environment = new backend.Tests.Config.TemporaryEnvironment(("NZBDAV_MASTER_KEY", null));
        using var encryption = new ConfigEncryptionService();
        var manager = new ConfigManager(encryption);

        Assert.Throws<InvalidOperationException>(() => manager.PrepareSetupForStorage([
            new ConfigItem { ConfigName = SetupConfigKeys.PluginApiKey, ConfigValue = "secret-plugin-key" },
            new ConfigItem { ConfigName = SetupConfigKeys.Completed, ConfigValue = "false" },
        ]));
    }

    [Fact]
    public async Task SetupGrantMigration_DiscoveredAndTableIsAvailableAtRuntime()
    {
        var configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-migration-{Guid.NewGuid():N}");
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("CONFIG_PATH", configPath),
            ("DATABASE_URL", null),
            ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))),
            ("FRONTEND_BACKEND_API_KEY", null));
        Directory.CreateDirectory(configPath);

        await using var context = new DavDatabaseContext();
        var migrations = context.Database.GetMigrations().ToList();
        Assert.Contains("20260809120000_AddSetupGrants", migrations);

        await context.Database.MigrateAsync();

        var pending = context.Database.GetPendingMigrations().ToList();
        Assert.DoesNotContain("20260809120000_AddSetupGrants", pending);

        var tablePresent = await context.SetupGrants.AnyAsync();
        Assert.False(tablePresent);
    }

    [Fact]
    public async Task SQLiteModelMatchesSnapshotWithoutPendingChanges()
    {
        var configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-model-parity-{Guid.NewGuid():N}");
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("CONFIG_PATH", configPath),
            ("DATABASE_URL", null),
            ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
        Directory.CreateDirectory(configPath);

        await using var context = new DavDatabaseContext();
        await context.Database.MigrateAsync();

        Assert.False(context.Database.HasPendingModelChanges());
    }
}
