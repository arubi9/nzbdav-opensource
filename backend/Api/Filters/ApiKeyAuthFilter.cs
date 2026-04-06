using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using NzbWebDAV.Api.Controllers;
using NzbWebDAV.Config;
using NzbWebDAV.Extensions;

namespace NzbWebDAV.Api.Filters;

public sealed class ApiKeyAuthFilter(ConfigManager configManager) : IAsyncAuthorizationFilter
{
    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var requestApiKey = context.HttpContext.GetRequestApiKey();
        if (requestApiKey == null)
        {
            context.Result = new UnauthorizedObjectResult(new BaseApiResponse
            {
                Status = false,
                Error = "API Key Required"
            });
            return Task.CompletedTask;
        }

        if (!IsValidApiKey(requestApiKey))
        {
            context.Result = new UnauthorizedObjectResult(new BaseApiResponse
            {
                Status = false,
                Error = "API Key Incorrect"
            });
        }

        return Task.CompletedTask;
    }

    private bool IsValidApiKey(string requestApiKey)
    {
        var expectedApiKey = configManager.GetApiKey();
        var expectedBytes = Encoding.UTF8.GetBytes(expectedApiKey);
        var requestBytes = Encoding.UTF8.GetBytes(requestApiKey);

        return expectedBytes.Length == requestBytes.Length
               && CryptographicOperations.FixedTimeEquals(expectedBytes, requestBytes);
    }
}
