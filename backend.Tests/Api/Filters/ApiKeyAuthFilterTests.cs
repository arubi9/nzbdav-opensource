using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using NzbWebDAV.Api.Filters;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Api.Filters;

public class ApiKeyAuthFilterTests
{
    [Fact]
    public async Task OnActionExecutionAsync_AllowsMatchingHeaderApiKey()
    {
        var filter = CreateFilter();
        var context = CreateContext();
        context.HttpContext.Request.Headers["x-api-key"] = "test-api-key";

        var nextCalled = false;
        await filter.OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult<ActionExecutedContext>(null!);
        });

        Assert.Null(context.Result);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task OnActionExecutionAsync_AllowsMatchingQueryApiKey()
    {
        var filter = CreateFilter();
        var context = CreateContext();
        context.HttpContext.Request.QueryString = new QueryString("?apikey=test-api-key");

        var nextCalled = false;
        await filter.OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult<ActionExecutedContext>(null!);
        });

        Assert.Null(context.Result);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task OnActionExecutionAsync_AllowsValidSignedToken()
    {
        var configManager = CreateConfigManager();
        var filter = new ApiKeyAuthFilter(configManager, new AuthFailureTracker());
        var context = CreateContext();
        context.HttpContext.Request.Method = "GET";
        context.HttpContext.Request.Path = "/api/stream/11111111-1111-1111-1111-111111111111";
        var token = StreamTokenService.GenerateToken(context.HttpContext.Request.Path, configManager, method: "GET");
        context.HttpContext.Request.QueryString = new QueryString($"?token={token}");

        var nextCalled = false;
        await filter.OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult<ActionExecutedContext>(null!);
        });

        Assert.Null(context.Result);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task OnActionExecutionAsync_AllowsValidSignedHeadToken()
    {
        var configManager = CreateConfigManager();
        var filter = new ApiKeyAuthFilter(configManager, new AuthFailureTracker());
        var context = CreateContext();
        context.HttpContext.Request.Method = "HEAD";
        context.HttpContext.Request.Path = "/api/stream/11111111-1111-1111-1111-111111111111";
        var token = StreamTokenService.GenerateToken(context.HttpContext.Request.Path, configManager, method: "GET");
        context.HttpContext.Request.QueryString = new QueryString($"?token={token}");

        var nextCalled = false;
        await filter.OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult<ActionExecutedContext>(null!);
        });

        Assert.Null(context.Result);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task OnActionExecutionAsync_AllowsSetupPluginKey_ForManifestRoute()
    {
        var filter = CreateFilter(pluginKey: "plugin-key");
        var context = CreateContext("/api/manifest");
        context.HttpContext.Request.Method = "GET";
        context.HttpContext.Request.Headers["x-api-key"] = "plugin-key";

        var nextCalled = false;
        await filter.OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult<ActionExecutedContext>(null!);
        });

        Assert.Null(context.Result);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task OnActionExecutionAsync_RejectsSetupPluginKey_ForNonPluginRoute()
    {
        var filter = CreateFilter(pluginKey: "plugin-key");
        var context = CreateContext("/api/test");
        context.HttpContext.Request.Headers["x-api-key"] = "plugin-key";

        await filter.OnActionExecutionAsync(context, () => Task.FromResult<ActionExecutedContext>(null!));

        Assert.IsType<UnauthorizedObjectResult>(context.Result);
    }

    [Fact]
    public async Task OnActionExecutionAsync_RejectsSetupPluginKey_ForMutatingPluginRoute()
    {
        var filter = CreateFilter(pluginKey: "plugin-key");
        var context = CreateContext("/api/probe/11111111-1111-1111-1111-111111111111");
        context.HttpContext.Request.Method = "POST";
        context.HttpContext.Request.Headers["x-api-key"] = "plugin-key";

        await filter.OnActionExecutionAsync(context, () => Task.FromResult<ActionExecutedContext>(null!));

        Assert.IsType<UnauthorizedObjectResult>(context.Result);
    }

    [Fact]
    public async Task OnActionExecutionAsync_AllowsSetupPluginPreviousKey_ForManifestRouteWithinWindow()
    {
        var filter = CreateFilter(
            pluginKey: "plugin-key",
            pluginPreviousKey: "previous-plugin-key",
            pluginPreviousExpiresAt: DateTime.UtcNow.AddDays(1));
        var context = CreateContext("/api/manifest");
        context.HttpContext.Request.Method = "GET";
        context.HttpContext.Request.Headers["x-api-key"] = "previous-plugin-key";

        var nextCalled = false;
        await filter.OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult<ActionExecutedContext>(null!);
        });

        Assert.Null(context.Result);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task OnActionExecutionAsync_RejectsFutureSetupPluginPreviousKey_ForManifestRoute()
    {
        var filter = CreateFilter(
            pluginKey: "plugin-key",
            pluginPreviousKey: "previous-plugin-key",
            pluginPreviousExpiresAt: DateTime.UtcNow.AddDays(1),
            pluginPreviousExpiresAtValueOverride: DateTime.UtcNow.AddDays(15).ToString("O"));
        var context = CreateContext("/api/manifest");
        context.HttpContext.Request.Method = "GET";
        context.HttpContext.Request.Headers["x-api-key"] = "previous-plugin-key";

        await filter.OnActionExecutionAsync(context, () => Task.FromResult<ActionExecutedContext>(null!));

        Assert.IsType<UnauthorizedObjectResult>(context.Result);
    }

    [Fact]
    public async Task OnActionExecutionAsync_RejectsMalformedSetupPluginPreviousKeyExpiry_ForManifestRoute()
    {
        var filter = CreateFilter(
            pluginKey: "plugin-key",
            pluginPreviousKey: "previous-plugin-key",
            pluginPreviousExpiresAtValueOverride: "not-a-timestamp");
        var context = CreateContext("/api/manifest");
        context.HttpContext.Request.Method = "GET";
        context.HttpContext.Request.Headers["x-api-key"] = "previous-plugin-key";

        await filter.OnActionExecutionAsync(context, () => Task.FromResult<ActionExecutedContext>(null!));

        Assert.IsType<UnauthorizedObjectResult>(context.Result);
    }

    [Fact]
    public async Task OnActionExecutionAsync_RejectsExpiredSetupPluginPreviousKey()
    {
        var filter = CreateFilter(
            pluginKey: "plugin-key",
            pluginPreviousKey: "previous-plugin-key",
            pluginPreviousExpiresAt: DateTime.UtcNow.AddSeconds(-1));
        var context = CreateContext("/api/meta/11111111-1111-1111-1111-111111111111");
        context.HttpContext.Request.Headers["x-api-key"] = "previous-plugin-key";

        await filter.OnActionExecutionAsync(context, () => Task.FromResult<ActionExecutedContext>(null!));

        Assert.IsType<UnauthorizedObjectResult>(context.Result);
    }

    [Fact]
    public async Task OnActionExecutionAsync_RejectsSetupPluginPreviousKey_ForNonPluginRoute()
    {
        var filter = CreateFilter(
            pluginKey: "plugin-key",
            pluginPreviousKey: "previous-plugin-key",
            pluginPreviousExpiresAt: DateTime.UtcNow.AddHours(1));
        var context = CreateContext("/api/stream");
        context.HttpContext.Request.Headers["x-api-key"] = "previous-plugin-key";

        await filter.OnActionExecutionAsync(context, () => Task.FromResult<ActionExecutedContext>(null!));

        Assert.IsType<UnauthorizedObjectResult>(context.Result);
    }

    [Fact]
    public async Task OnActionExecutionAsync_RejectsMissingApiKey()
    {
        var filter = CreateFilter();
        var context = CreateContext();

        await filter.OnActionExecutionAsync(context, () => Task.FromResult<ActionExecutedContext>(null!));

        var result = Assert.IsType<UnauthorizedObjectResult>(context.Result);
        var error = result.Value!.GetType().GetProperty("error")!.GetValue(result.Value);
        Assert.Equal("Invalid or missing API key", error);
    }

    [Fact]
    public async Task OnActionExecutionAsync_AllowsInternalKey_ForEncryptionStatusRoute()
    {
        var filter = CreateFilter(apiKey: "internal-key");
        var context = CreateContext("/api/encryption-status");
        context.HttpContext.Request.Headers["x-api-key"] = "internal-key";

        var nextCalled = false;
        await filter.OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult<ActionExecutedContext>(null!);
        });

        Assert.Null(context.Result);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task OnActionExecutionAsync_AllowsInternalKey_ForNormalApiRoute()
    {
        var filter = CreateFilter(apiKey: "internal-key");
        var context = CreateContext("/api/stream/11111111-1111-1111-1111-111111111111");
        context.HttpContext.Request.Headers["x-api-key"] = "internal-key";

        var nextCalled = false;
        await filter.OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult<ActionExecutedContext>(null!);
        });

        Assert.Null(context.Result);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task OnActionExecutionAsync_RejectsWrongKey_WhenInternalAndUserKeysDiffer()
    {
        var filter = CreateFilter(apiKey: "independent-internal-key");
        var context = CreateContext("/api/test");
        context.HttpContext.Request.Headers["x-api-key"] = "wrong-key";

        await filter.OnActionExecutionAsync(context, () => Task.FromResult<ActionExecutedContext>(null!));

        Assert.IsType<UnauthorizedObjectResult>(context.Result);
    }

    private static ApiKeyAuthFilter CreateFilter(
        string? apiKey = null,
        string? pluginKey = null,
        string? pluginPreviousKey = null,
        DateTime? pluginPreviousExpiresAt = null,
        string? pluginPreviousExpiresAtValueOverride = null)
        => new(
            CreateConfigManager(apiKey, pluginKey, pluginPreviousKey, pluginPreviousExpiresAt, pluginPreviousExpiresAtValueOverride),
            new AuthFailureTracker());

    private static ConfigManager CreateConfigManager(
        string? apiKey = null,
        string? pluginKey = null,
        string? pluginPreviousKey = null,
        DateTime? pluginPreviousExpiresAt = null,
        string? pluginPreviousExpiresAtValueOverride = null)
    {
        // These tests only exercise the filter's read-only ConfigManager view.
        // Populate that view directly so parallel tests do not share or reset
        // the process-wide SQLite database (or mutate process-wide environment).
        var configManager = new ConfigManager();
        var configItems = new List<ConfigItem>
        {
            new()
            {
                ConfigName = "api.key",
                ConfigValue = apiKey ?? "test-api-key"
            },
            new()
            {
                ConfigName = "api.strm-key",
                ConfigValue = "test-stream-key"
            }
        };

        var setupItems = new List<ConfigItem>();
        if (pluginKey is not null)
            setupItems.Add(new ConfigItem
            {
                ConfigName = "setup.plugin-api-key",
                ConfigValue = pluginKey
            });

        if (pluginPreviousKey is not null)
        {
            setupItems.Add(new ConfigItem
            {
                ConfigName = "setup.plugin-api-key-previous",
                ConfigValue = pluginPreviousKey
            });

            setupItems.Add(new ConfigItem
            {
                ConfigName = "setup.plugin-api-key-previous-expires-at",
                ConfigValue = pluginPreviousExpiresAtValueOverride
                              ?? (pluginPreviousExpiresAt ?? DateTime.UtcNow.AddHours(1)).ToString("O")
            });
        }

        configManager.UpdateValues(configItems);
        if (setupItems.Count > 0)
            configManager.ApplySetupValues(setupItems);
        return configManager;
    }

    private static ActionExecutingContext CreateContext(string path = "/api/test")
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = path;
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        return new ActionExecutingContext(actionContext, [], new Dictionary<string, object?>(), controller: new object());
    }
}
