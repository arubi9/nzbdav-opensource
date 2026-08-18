using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;
using NzbWebDAV.Api.Controllers;
using NzbWebDAV.Api.Controllers.UpdateConfig;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Api.Controllers.UpdateConfig;

[Collection(nameof(backend.Tests.Config.EnvironmentVariableCollection))]
public sealed class UpdateConfigControllerTests
{
    [Theory]
    [InlineData("setup.plugin-api-key")]
    [InlineData("USENET.PROVIDERS")]
    [InlineData("setup.completed")]
    [InlineData("api.strm-key-previous-ring")]
    [InlineData("API.STRM-KEY-PREVIOUS-RING")]
    public async Task HandleRequest_RejectsSetupManagedKeys(string setupKey)
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"nzbdav-tests/update-config-setup-{Guid.NewGuid():N}");
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("CONFIG_PATH", configPath),
            ("DATABASE_URL", null),
            ("FRONTEND_BACKEND_API_KEY", "unit-api-key"),
            ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
        Directory.CreateDirectory(configPath);

        await using var dbContext = new DavDatabaseContext();
        await dbContext.Database.MigrateAsync();
        var controller = new UpdateConfigController(new DavDatabaseClient(dbContext), new ConfigManager(new ConfigEncryptionService()))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = CreateContext("unit-api-key", setupKey, "secret")
            }
        };

        var result = await controller.HandleApiRequest();
        var error = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, error.StatusCode);
        var response = Assert.IsType<BaseApiResponse>(error.Value);
        Assert.False(response.Status);
        Assert.Contains("config", response.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenericWriter_RejectsEverySensitiveRegistryAliasBeforeWrite()
    {
        using var test = await CreateStreamEnvironmentAsync();
        var before = await CaptureSnapshotAsync(test.Manager);
        var events = 0;
        test.Manager.OnConfigChanged += (_, _) => events++;

        foreach (var key in SensitiveConfigKeys.CanonicalNames.Keys)
        {
            if (string.Equals(key, "api.strm-key", StringComparison.OrdinalIgnoreCase))
                continue;

            var result = await UpdateAsync(test.Manager, key.ToUpperInvariant(), "generic-marker");
            Assert.Equal(500, Assert.IsType<ObjectResult>(result).StatusCode);
        }

        Assert.Equal(0, events);
        await AssertSnapshotAsync(test.Manager, before);
    }

    [Fact]
    public async Task GenericWriter_AllowsApiStrmKeyRotationViaUpdateConfig()
    {
        using var test = await CreateStreamEnvironmentAsync();
        var desired = "rotation-key-8f3c";
        var before = await CaptureSnapshotAsync(test.Manager);
        var beforeRow = before.Rows.TryGetValue("api.strm-key", out var existing)
            ? existing
            : throw new InvalidOperationException("api.strm-key must exist before stream-key rotation test.");

        var result = await UpdateAsync(test.Manager, "api.strm-key", desired);
        Assert.IsType<OkObjectResult>(result);

        await using var db = new DavDatabaseContext();
        var row = await db.ConfigItems.AsNoTracking().SingleAsync(x => x.ConfigName == "api.strm-key");
        Assert.NotEqual(beforeRow.Value, row.ConfigValue);
        using var encryption = new ConfigEncryptionService();
        var restored = row.IsEncrypted
            ? encryption.Decrypt("api.strm-key", row.ConfigValue).plaintext
            : row.ConfigValue;
        Assert.Equal(desired, restored);
    }

    [Fact]
    public async Task GenericWriter_RejectsSensitiveClearOnlyFormsBeforeWrite()
    {
        using var test = await CreateStreamEnvironmentAsync();
        var before = await CaptureSnapshotAsync(test.Manager);
        var values = new Dictionary<string, StringValues>
        {
            ["arr.instances.__clear"] = "true",
            ["public.setting"] = "value",
        };
        var result = await UpdateAsync(test.Manager, values);
        Assert.Equal(500, Assert.IsType<ObjectResult>(result).StatusCode);
        await AssertSnapshotAsync(test.Manager, before);
    }

    [Fact]
    public async Task GenericWriter_StillAllowsNonSensitiveSettings()
    {
        using var test = await CreateStreamEnvironmentAsync();
        var result = await UpdateAsync(test.Manager, CreateContext("unit-api-key", "public.setting", "public-value"));
        Assert.IsType<OkObjectResult>(result);

        await using var db = new DavDatabaseContext();
        var row = await db.ConfigItems.AsNoTracking().SingleAsync(x => x.ConfigName == "public.setting");
        Assert.Equal("public-value", row.ConfigValue);
        Assert.False(row.IsEncrypted);
    }

    private static DefaultHttpContext CreateContext(string apiKey, string key, string value)
        => CreateContext(apiKey, new Dictionary<string, StringValues> { [key] = value });

    private static DefaultHttpContext CreateContext(string apiKey, Dictionary<string, StringValues> values)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Headers["x-api-key"] = apiKey;
        context.Request.Form = new FormCollection(values);
        return context;
    }

    private sealed class StreamEnvironment : IDisposable
    {
        public required backend.Tests.Config.TemporaryEnvironment Variables { get; init; }
        public required ConfigManager Manager { get; init; }
        public required string ConfigPath { get; init; }
        public void Dispose()
        {
            Variables.Dispose();
            TryDeleteDirectory(ConfigPath);
        }
    }

    private static async Task<StreamEnvironment> CreateStreamEnvironmentAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nzbdav-tests/update-generic-{Guid.NewGuid():N}");
        var variables = new backend.Tests.Config.TemporaryEnvironment(
            ("CONFIG_PATH", path),
            ("DATABASE_URL", null),
            ("FRONTEND_BACKEND_API_KEY", "unit-api-key"),
            ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
        Directory.CreateDirectory(path);
        await using (var db = new DavDatabaseContext())
        {
            await db.Database.MigrateAsync();
            var apiKey = await db.ConfigItems.SingleAsync(x => x.ConfigName == "api.key");
            apiKey.ConfigValue = "unit-api-key";
            apiKey.IsEncrypted = false;
            await db.SaveChangesAsync();
        }
        var manager = new ConfigManager(new ConfigEncryptionService());
        await manager.LoadConfig();
        return new StreamEnvironment { Variables = variables, Manager = manager, ConfigPath = path };
    }

    private static async Task<IActionResult> UpdateAsync(ConfigManager manager, string key, string value)
        => await UpdateAsync(manager, CreateContext("unit-api-key", key, value));

    private static async Task<IActionResult> UpdateAsync(ConfigManager manager, Dictionary<string, StringValues> values)
        => await UpdateAsync(manager, CreateContext("unit-api-key", values));

    private static async Task<IActionResult> UpdateAsync(ConfigManager manager, DefaultHttpContext context)
    {
        await using var db = new DavDatabaseContext();
        var controller = new UpdateConfigController(new DavDatabaseClient(db), manager)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
        return await controller.HandleApiRequest();
    }

    private sealed record Snapshot(Dictionary<string, (string Value, bool IsEncrypted)> Rows);

    private static async Task<Snapshot> CaptureSnapshotAsync(ConfigManager manager)
    {
        await using var db = new DavDatabaseContext();
        return new Snapshot(await db.ConfigItems.AsNoTracking().ToDictionaryAsync(
            x => x.ConfigName, x => (x.ConfigValue, x.IsEncrypted)));
    }

    private static async Task AssertSnapshotAsync(ConfigManager manager, Snapshot expected)
        => Assert.Equal(expected.Rows, (await CaptureSnapshotAsync(manager)).Rows);

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
