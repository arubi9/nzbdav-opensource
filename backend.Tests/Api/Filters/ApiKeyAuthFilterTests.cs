using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using NzbWebDAV.Api.Controllers;
using NzbWebDAV.Api.Filters;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Api.Filters;

public class ApiKeyAuthFilterTests
{
    [Fact]
    public async Task OnAuthorizationAsync_AllowsMatchingHeaderApiKey()
    {
        var filter = CreateFilter();
        var context = CreateContext();
        context.HttpContext.Request.Headers["x-api-key"] = "test-api-key";

        await filter.OnAuthorizationAsync(context);

        Assert.Null(context.Result);
    }

    [Fact]
    public async Task OnAuthorizationAsync_AllowsMatchingQueryApiKey()
    {
        var filter = CreateFilter();
        var context = CreateContext();
        context.HttpContext.Request.QueryString = new QueryString("?apikey=test-api-key");

        await filter.OnAuthorizationAsync(context);

        Assert.Null(context.Result);
    }

    [Fact]
    public async Task OnAuthorizationAsync_RejectsMissingApiKey()
    {
        var filter = CreateFilter();
        var context = CreateContext();

        await filter.OnAuthorizationAsync(context);

        var result = Assert.IsType<UnauthorizedObjectResult>(context.Result);
        var response = Assert.IsType<BaseApiResponse>(result.Value);
        Assert.False(response.Status);
        Assert.Equal("API Key Required", response.Error);
    }

    private static ApiKeyAuthFilter CreateFilter()
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

        return new ApiKeyAuthFilter(configManager);
    }

    private static AuthorizationFilterContext CreateContext()
    {
        var httpContext = new DefaultHttpContext();
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        return new AuthorizationFilterContext(actionContext, []);
    }
}
