using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;
using NzbWebDAV.Api.Controllers;
using NzbWebDAV.Api.Controllers.GetConfig;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Setup.Core;

namespace NzbWebDAV.Tests.Api.Controllers.GetConfig;

[Collection(nameof(backend.Tests.Config.EnvironmentVariableCollection))]
public sealed class GetConfigControllerTests
{
    [Theory]
    [InlineData("setup.plugin-api-key")]
    [InlineData("SeTuP.JellyFin-API-KeY")]
    [InlineData("setup.indexers")]
    [InlineData("setup.completed")]
    [InlineData("SeTuP.future-secret")]
    [InlineData("USENET.PROVIDERS")]
    [InlineData("ArR.InStAnCeS")]
    [InlineData("aPi.KeY")]
    [InlineData("aPi.StRm-KeY")]
    [InlineData("WeBdAv.PaSs")]
    [InlineData("CaChE.L2.AcCeSs-KeY")]
    [InlineData("CaChE.L2.SeCrEt-KeY")]
    public async Task HandleRequest_RejectsCaseInsensitiveSensitiveKeys(string setupKey)
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"nzbdav-tests/get-config-setup-{Guid.NewGuid():N}");
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("CONFIG_PATH", configPath),
            ("DATABASE_URL", null),
            ("FRONTEND_BACKEND_API_KEY", "unit-api-key"));
        Directory.CreateDirectory(configPath);

        await using var dbContext = await CreateDatabaseContextWithSetupRowsAsync(configPath);
        using var encryption = new ConfigEncryptionService();

        var controller = new GetConfigController(new DavDatabaseClient(dbContext), encryption)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = CreateContext("unit-api-key", setupKey)
            }
        };

        var result = await controller.HandleApiRequest();
        var error = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, error.StatusCode);
        var response = Assert.IsType<BaseApiResponse>(error.Value);
        Assert.False(response.Status);
        Assert.Contains("settings", response.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleRequest_RejectsEveryRegisteredSensitiveAlias()
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"nzbdav-tests/get-config-registry-{Guid.NewGuid():N}");
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("CONFIG_PATH", configPath),
            ("DATABASE_URL", null),
            ("FRONTEND_BACKEND_API_KEY", "unit-api-key"));
        Directory.CreateDirectory(configPath);

        await using var dbContext = await CreateDatabaseContextWithSetupRowsAsync(configPath);
        using var encryption = new ConfigEncryptionService();
        foreach (var key in SensitiveConfigKeys.CanonicalNames.Keys)
        {
            var controller = new GetConfigController(new DavDatabaseClient(dbContext), encryption)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = CreateContext("unit-api-key", key.ToUpperInvariant())
                }
            };

            var result = await controller.HandleApiRequest();
            var error = Assert.IsType<ObjectResult>(result);
            Assert.Equal(500, error.StatusCode);
            Assert.False(Assert.IsType<BaseApiResponse>(error.Value).Status);
        }
    }

    [Fact]
    public async Task HandleRequest_RejectsSensitiveKeys()
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"nzbdav-tests/get-config-safe-{Guid.NewGuid():N}");
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("CONFIG_PATH", configPath),
            ("DATABASE_URL", null),
            ("FRONTEND_BACKEND_API_KEY", "unit-api-key"));
        Directory.CreateDirectory(configPath);

        await using var dbContext = await CreateDatabaseContextWithSetupRowsAsync(configPath);
        using var encryption = new ConfigEncryptionService();

        var controller = new GetConfigController(new DavDatabaseClient(dbContext), encryption)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = CreateContext("unit-api-key", "api.key")
            }
        };

        var result = await controller.HandleApiRequest();
        var error = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, error.StatusCode);
        var response = Assert.IsType<BaseApiResponse>(error.Value);
        Assert.False(response.Status);
        Assert.Contains("sensitive", response.Error, StringComparison.OrdinalIgnoreCase);
    }

    private static DefaultHttpContext CreateContext(string apiKey, params string[] configKeys)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["x-api-key"] = apiKey;
        context.Request.Form = new FormCollection(
            new Dictionary<string, StringValues>
            {
                ["config-keys"] = new StringValues(configKeys)
            });

        return context;
    }

    private static async Task<DavDatabaseContext> CreateDatabaseContextWithSetupRowsAsync(string configPath)
    {
        Environment.SetEnvironmentVariable("CONFIG_PATH", configPath);

        var dbPath = DavDatabaseContext.DatabaseFilePath;
        if (File.Exists(dbPath))
            File.Delete(dbPath);

        var context = new DavDatabaseContext();
        await context.Database.MigrateAsync();

        context.ConfigItems.AddRange(
        [
            new ConfigItem { ConfigName = SetupConfigKeys.PluginApiKey, ConfigValue = "plugin-secret", IsEncrypted = false },
            new ConfigItem { ConfigName = SetupConfigKeys.JellyfinApiKey, ConfigValue = "jf-secret", IsEncrypted = false },
            new ConfigItem { ConfigName = SetupConfigKeys.Indexers, ConfigValue = "[]", IsEncrypted = false },
            new ConfigItem { ConfigName = SetupConfigKeys.Completed, ConfigValue = "true", IsEncrypted = false },
            new ConfigItem { ConfigName = "SeTuP.future-secret", ConfigValue = "must-not-leak", IsEncrypted = false },
        ]);

        var frontendApiKey = await context.ConfigItems.SingleAsync(x => x.ConfigName == "api.key");
        frontendApiKey.ConfigValue = "frontend-api-key";

        await context.SaveChangesAsync();
        return context;
    }
}