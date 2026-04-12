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
    private static readonly SemaphoreSlim EnvLock = new(1, 1);

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
    public async Task OnActionExecutionAsync_AllowsInternalKey_ForEncryptionStatusRoute()
    {
        await EnvLock.WaitAsync();
        try
        {
            var previous = Environment.GetEnvironmentVariable("FRONTEND_BACKEND_API_KEY");
            try
            {
                Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", "internal-key");

                var filter = CreateFilter();
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
            finally
            {
                Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", previous);
            }
        }
        finally
        {
            EnvLock.Release();
        }
    }

    [Fact]
    public async Task OnActionExecutionAsync_RejectsInternalKey_ForStreamRoute()
    {
        await EnvLock.WaitAsync();
        try
        {
            var previous = Environment.GetEnvironmentVariable("FRONTEND_BACKEND_API_KEY");
            try
            {
                Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", "internal-key");

                var filter = CreateFilter();
                var context = CreateContext("/api/stream/11111111-1111-1111-1111-111111111111");
                context.HttpContext.Request.Headers["x-api-key"] = "internal-key";

                await filter.OnActionExecutionAsync(context, () => Task.FromResult<ActionExecutedContext>(null!));

                var result = Assert.IsType<UnauthorizedObjectResult>(context.Result);
                var error = result.Value!.GetType().GetProperty("error")!.GetValue(result.Value);
                Assert.Equal("Invalid or missing API key", error);
            }
            finally
            {
                Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", previous);
            }
        }
        finally
        {
            EnvLock.Release();
        }
    }

    private static ApiKeyAuthFilter CreateFilter()
        => new(CreateConfigManager(), new AuthFailureTracker());

    private static ConfigManager CreateConfigManager()
    {
        var configManager = new ConfigManager();
        configManager.UpdateValues(
            [
                new ConfigItem
                {
                    ConfigName = "api.key",
                    ConfigValue = "test-api-key"
                }
            ]
        );
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
