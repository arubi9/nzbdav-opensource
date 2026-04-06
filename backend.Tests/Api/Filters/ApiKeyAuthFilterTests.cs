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
        var filter = new ApiKeyAuthFilter(configManager);
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

    private static ApiKeyAuthFilter CreateFilter()
        => new(CreateConfigManager());

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

    private static ActionExecutingContext CreateContext()
    {
        var httpContext = new DefaultHttpContext();
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        return new ActionExecutingContext(actionContext, [], new Dictionary<string, object?>(), controller: null);
    }
}
