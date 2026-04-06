using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using NzbWebDAV.Config;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Filters;

public class ApiKeyAuthFilter(ConfigManager configManager) : IAsyncActionFilter
{
    private byte[]? _cachedKeyBytes;
    private string? _cachedKeySource;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var request = context.HttpContext.Request;
        var providedKey = request.Headers["X-Api-Key"].FirstOrDefault()
                          ?? request.Query["apikey"].FirstOrDefault();

        if (string.IsNullOrEmpty(providedKey))
        {
            var token = request.Query["token"].FirstOrDefault();
            if (!string.IsNullOrEmpty(token)
                && StreamTokenService.ValidateToken(token, request.Path, configManager, request.Method))
            {
                await next().ConfigureAwait(false);
                return;
            }
        }

        if (string.IsNullOrEmpty(providedKey) || !ValidateApiKey(providedKey))
        {
            context.Result = new UnauthorizedObjectResult(new { error = "Invalid or missing API key" });
            return;
        }

        await next().ConfigureAwait(false);
    }

    private bool ValidateApiKey(string providedKey)
    {
        try
        {
            var expectedKey = configManager.GetApiKey();
            if (_cachedKeySource != expectedKey)
            {
                _cachedKeySource = expectedKey;
                _cachedKeyBytes = System.Text.Encoding.UTF8.GetBytes(expectedKey);
            }

            var providedBytes = System.Text.Encoding.UTF8.GetBytes(providedKey);
            if (_cachedKeyBytes == null)
                return false;

            // No length pre-check — FixedTimeEquals handles mismatched lengths
            // without leaking key length via timing.
            return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(providedBytes, _cachedKeyBytes);
        }
        catch
        {
            return false;
        }
    }
}
