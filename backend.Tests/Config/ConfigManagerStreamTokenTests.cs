using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;
using NzbWebDAV.Api.Controllers.UpdateConfig;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Setup.Core;
using NzbWebDAV.Services;
using Xunit;
using Xunit.Sdk;

namespace backend.Tests.Config;

[Collection(nameof(backend.Tests.Services.ConfigEncryptionDatabaseCollection))]
public sealed class ConfigManagerStreamTokenTests
{
    private readonly backend.Tests.Services.ConfigEncryptionDatabaseFixture _fixture;

    public ConfigManagerStreamTokenTests(backend.Tests.Services.ConfigEncryptionDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task LoadConfig_InitializesStreamTokenLegacyMigrationStartWhenMissing()
    {
        await _fixture.ResetAsync();
        _fixture.SetKeys(masterKey: _fixture.CreateKey(), oldKey: null);

        using var setupContext = new DavDatabaseContext();
        setupContext.Database.EnsureDeleted();
        setupContext.Database.EnsureCreated();

        if (await setupContext.ConfigItems.AnyAsync(x => x.ConfigName == SetupConfigKeys.StreamTokenLegacyMigrationStart))
            throw new XunitException("Marker unexpectedly already exists.");

        var configManager = new ConfigManager(new ConfigEncryptionService());
        await configManager.LoadConfig();

        using var verifyContext = new DavDatabaseContext();
        verifyContext.Database.EnsureCreated();
        var marker = await verifyContext.ConfigItems
            .SingleAsync(x => x.ConfigName == SetupConfigKeys.StreamTokenLegacyMigrationStart);

        Assert.True(DateTimeOffset.TryParse(marker.ConfigValue, out _));
    }

    [Fact]
    public async Task LoadConfig_PreservesExistingStreamTokenLegacyMigrationStart()
    {
        await _fixture.ResetAsync();
        _fixture.SetKeys(masterKey: _fixture.CreateKey(), oldKey: null);

        var fixedAt = DateTimeOffset.UtcNow.AddDays(-42).ToString("O");
        using (var setupContext = new DavDatabaseContext())
        {
            setupContext.Database.EnsureDeleted();
            setupContext.Database.EnsureCreated();
            setupContext.ConfigItems.Add(new ConfigItem
            {
                ConfigName = SetupConfigKeys.StreamTokenLegacyMigrationStart,
                ConfigValue = fixedAt,
                IsEncrypted = false
            });
            await setupContext.SaveChangesAsync();
        }

        var configManager = new ConfigManager(new ConfigEncryptionService());
        await configManager.LoadConfig();

        using var verifyContext = new DavDatabaseContext();
        verifyContext.Database.EnsureCreated();
        var markerRows = await verifyContext.ConfigItems
            .Where(x => x.ConfigName == SetupConfigKeys.StreamTokenLegacyMigrationStart)
            .ToListAsync();

        Assert.Single(markerRows);
        Assert.Equal(fixedAt, markerRows[0].ConfigValue);
    }

    [Fact]
    public async Task GetStreamTokenState_ProvidesCurrentPreviousAndLegacyStartAtomicSnapshot()
    {
        await _fixture.ResetAsync();
        _fixture.SetKeys(masterKey: _fixture.CreateKey(), oldKey: null);

        var expectedExpiry = DateTimeOffset.UtcNow.AddDays(2).ToString("O");
        var legacyStart = DateTimeOffset.UtcNow.AddDays(-7).ToString("O");

        using (var setupContext = new DavDatabaseContext())
        {
            setupContext.Database.EnsureDeleted();
            setupContext.Database.EnsureCreated();
            setupContext.ConfigItems.Add(new ConfigItem
            {
                ConfigName = "api.strm-key",
                ConfigValue = "next-key",
                IsEncrypted = false
            });
            setupContext.ConfigItems.Add(new ConfigItem
            {
                ConfigName = "api.strm-key-previous",
                ConfigValue = "previous-key",
                IsEncrypted = false
            });
            setupContext.ConfigItems.Add(new ConfigItem
            {
                ConfigName = "api.strm-key-previous-expires-at",
                ConfigValue = expectedExpiry,
                IsEncrypted = false
            });
            setupContext.ConfigItems.Add(new ConfigItem
            {
                ConfigName = SetupConfigKeys.StreamTokenLegacyMigrationStart,
                ConfigValue = legacyStart,
                IsEncrypted = false
            });
            await setupContext.SaveChangesAsync();
        }

        var configManager = new ConfigManager(new ConfigEncryptionService());
        await configManager.LoadConfig();

        var state = configManager.GetStreamTokenState();

        Assert.Equal("next-key", state.Current);
        Assert.Equal("previous-key", state.Previous);
        Assert.Equal(DateTimeOffset.Parse(expectedExpiry), state.PreviousExpiresAt);
        Assert.Equal(DateTimeOffset.Parse(legacyStart), state.LegacyMigrationStart);
    }

    [Fact]
    public async Task LoadConfig_LoadsStreamSigningPreviousKeyAndExpiry()
    {
        await _fixture.ResetAsync();
        _fixture.SetKeys(masterKey: _fixture.CreateKey(), oldKey: null);

        var expectedExpiry = DateTimeOffset.UtcNow.AddDays(2).ToString("O");

        using (var setupContext = new DavDatabaseContext())
        {
            setupContext.Database.EnsureDeleted();
            setupContext.Database.EnsureCreated();
            setupContext.ConfigItems.Add(new ConfigItem
            {
                ConfigName = "api.strm-key",
                ConfigValue = "next-key",
                IsEncrypted = false
            });
            setupContext.ConfigItems.Add(new ConfigItem
            {
                ConfigName = "api.strm-key-previous",
                ConfigValue = "previous-key",
                IsEncrypted = false
            });
            setupContext.ConfigItems.Add(new ConfigItem
            {
                ConfigName = "api.strm-key-previous-expires-at",
                ConfigValue = expectedExpiry,
                IsEncrypted = false
            });
            await setupContext.SaveChangesAsync();
        }

        var configManager = new ConfigManager(new ConfigEncryptionService());
        await configManager.LoadConfig();

        Assert.Equal("previous-key", configManager.GetStrmKeyPrevious());
        Assert.Equal(
            DateTimeOffset.Parse(expectedExpiry),
            configManager.GetStrmKeyPreviousExpiresAtUtc());
    }

    [Fact]
    public async Task LoadConfig_IgnoresMalformedStreamKeyPreviousExpiry()
    {
        await _fixture.ResetAsync();
        _fixture.SetKeys(masterKey: _fixture.CreateKey(), oldKey: null);

        using (var setupContext = new DavDatabaseContext())
        {
            setupContext.Database.EnsureDeleted();
            setupContext.Database.EnsureCreated();
            setupContext.ConfigItems.Add(new ConfigItem
            {
                ConfigName = "api.strm-key-previous",
                ConfigValue = "previous-key",
                IsEncrypted = false
            });
            setupContext.ConfigItems.Add(new ConfigItem
            {
                ConfigName = "api.strm-key-previous-expires-at",
                ConfigValue = "not-a-timestamp",
                IsEncrypted = false
            });
            await setupContext.SaveChangesAsync();
        }

        var configManager = new ConfigManager(new ConfigEncryptionService());
        await configManager.LoadConfig();

        Assert.Equal("previous-key", configManager.GetStrmKeyPrevious());
        Assert.Null(configManager.GetStrmKeyPreviousExpiresAtUtc());
    }

    [Fact]
    public async Task UpdateConfig_ConcurrentStreamKeyRotations_KeepCurrentAndPreviousWithinHistoryWindow()
    {
        await _fixture.ResetAsync();
        _fixture.SetKeys(masterKey: _fixture.CreateKey(), oldKey: null);

        var initialKey = "initial-key";
        var updatedOne = "rotation-one";
        var updatedTwo = "rotation-two";

        await using (var setupContext = await _fixture.CreateMigratedContextAsync())
        {
            var apiKeyRow = await setupContext.ConfigItems
                .SingleOrDefaultAsync(x => x.ConfigName == "api.key");
            if (apiKeyRow is null)
                setupContext.ConfigItems.Add(new ConfigItem
                {
                    ConfigName = "api.key",
                    ConfigValue = "unit-api-key",
                    IsEncrypted = false
                });
            else
                apiKeyRow.ConfigValue = "unit-api-key";

            var strmKeyRow = await setupContext.ConfigItems
                .SingleOrDefaultAsync(x => x.ConfigName == "api.strm-key");
            if (strmKeyRow is null)
                setupContext.ConfigItems.Add(new ConfigItem
                {
                    ConfigName = "api.strm-key",
                    ConfigValue = initialKey,
                    IsEncrypted = false
                });
            else
                strmKeyRow.ConfigValue = initialKey;

            await setupContext.SaveChangesAsync();
        }

        using var _ = new TemporaryEnvironment(("FRONTEND_BACKEND_API_KEY", "unit-api-key"));
        var configManager = new ConfigManager(new ConfigEncryptionService());
        await configManager.LoadConfig();

        var ready = 0;
        var readyGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task SendUpdateAsync(string newKey)
        {
            await using var updateContext = await _fixture.CreateMigratedContextAsync();
            if (Interlocked.Increment(ref ready) == 2)
                readyGate.TrySetResult();

            // Release both controllers only after their independent database
            // contexts are ready, so both mutations contend for the common
            // ConfigManager gate rather than merely concurrent migration work.
            await readyGate.Task.ConfigureAwait(false);

            var controller = new UpdateConfigController(
                new NzbWebDAV.Database.DavDatabaseClient(updateContext),
                configManager)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = CreateUpdateRequestContext(newKey)
                }
            };

            var response = await controller.HandleApiRequest().ConfigureAwait(false);
            var ok = Assert.IsType<OkObjectResult>(response);
            var body = Assert.IsType<UpdateConfigResponse>(ok.Value);
            Assert.True(body.Status);
        }

        await Task.WhenAll(
            SendUpdateAsync(updatedOne),
            SendUpdateAsync(updatedTwo));

        await using var verifyContext = await _fixture.CreateMigratedContextAsync();
        var verifyManager = new ConfigManager(new ConfigEncryptionService());
        await verifyManager.LoadConfig();

        var current = verifyManager.GetStrmKey();
        var state = verifyManager.GetStreamTokenState();
        var allKeys = state.PreviousKeys.Select(entry => entry.Key).Append(current).ToArray();

        var expectedKeys = new[] { updatedOne, updatedTwo };
        Assert.Contains(current, expectedKeys);
        Assert.Contains(initialKey, allKeys);
        Assert.Contains(updatedOne, allKeys);
        Assert.Contains(updatedTwo, allKeys);
    }

    private static DefaultHttpContext CreateUpdateRequestContext(string streamKey)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Headers["x-api-key"] = "unit-api-key";
        context.Request.Form = new FormCollection(
            new Dictionary<string, StringValues>
            {
                ["api.strm-key"] = new StringValues(streamKey)
            });

        return context;
    }
}
