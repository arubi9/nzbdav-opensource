using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using NzbWebDAV.Config;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Filters;

public class ApiKeyAuthFilter(ConfigManager configManager, AuthFailureTracker failureTracker) : IAsyncActionFilter
{
    // Thread-safe: immutable record swapped atomically via volatile reference.
    // Singleton filter accessed by concurrent requests.
    private volatile CachedKeyData? _cachedKey;
    private sealed record CachedKeyData(string Source, byte[] Bytes);

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var request = context.HttpContext.Request;
        var ip = context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        // Check if this IP is blocked from too many failed attempts
        if (failureTracker.IsBlocked(ip))
        {
            context.HttpContext.Response.Headers.RetryAfter = "60";
            context.Result = new ObjectResult(new { error = "Too many failed attempts. Retry after 60 seconds." })
            {
                StatusCode = 429
            };
            return;
        }

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
            failureTracker.RecordFailure(ip);
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
            var cached = _cachedKey;
            if (cached is null || cached.Source != expectedKey)
            {
                cached = new CachedKeyData(expectedKey, System.Text.Encoding.UTF8.GetBytes(expectedKey));
                _cachedKey = cached;
            }

            var providedBytes = System.Text.Encoding.UTF8.GetBytes(providedKey);
            // FixedTimeEquals returns false immediately for different-length spans.
            // This leaks key length, which is acceptable for API keys (length is not secret).
            return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                providedBytes, cached.Bytes);
        }
        catch
        {
            return false;
        }
    }
}
