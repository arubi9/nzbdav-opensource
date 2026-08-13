using System;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using NzbWebDAV.Config;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.Filters;

public class ApiKeyAuthFilter(ConfigManager configManager, IAuthFailureTracker failureTracker, TimeProvider? timeProvider = null) : IAsyncActionFilter
{
    private static readonly PathString[] PluginReadOnlyRoutes =
    [
        "/api/manifest",
        "/api/meta",
        "/api/probe",
        "/api/browse"
    ];

    // Thread-safe: immutable record swapped atomically via volatile reference.
    // Singleton filter accessed by concurrent requests.
    private static readonly int MaxPluginApiKeyOverlapSeconds = 14 * 24 * 60 * 60;

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private volatile CachedKeyData? _cachedUserKey;
    private sealed record CachedKeyData(string Source, byte[] Bytes);

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var request = context.HttpContext.Request;
        var ip = context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var providedKey = request.Headers["X-Api-Key"].FirstOrDefault()
                          ?? request.Query["apikey"].FirstOrDefault();
        var token = request.Query["token"].FirstOrDefault();

        // Credentials are checked before consulting the failure block. A
        // shared proxy address can be blocked because another caller sent
        // bad credentials, but it must not deny a valid caller.
        var validApiKey = ValidateApiKey(request, providedKey ?? string.Empty);
        var validStreamToken = !string.IsNullOrEmpty(token)
                               && StreamTokenService.ValidateToken(token, request.Path, configManager, request.Method);

        if (validApiKey | validStreamToken)
        {
            await next().ConfigureAwait(false);
            return;
        }

        // Only invalid attempts consult and update the IP failure tracker.
        if (await failureTracker.IsBlockedAsync(ip).ConfigureAwait(false))
        {
            context.HttpContext.Response.Headers.RetryAfter = "60";
            context.Result = new ObjectResult(new { error = "Too many failed attempts. Retry after 60 seconds." })
            {
                StatusCode = 429
            };
            return;
        }

        await failureTracker.RecordFailureAsync(ip).ConfigureAwait(false);
        context.Result = new UnauthorizedObjectResult(new { error = "Invalid or missing API key" });
    }

    private bool ValidateApiKey(HttpRequest request, string providedKey)
    {
        if (string.IsNullOrEmpty(providedKey))
            return false;

        var providedBytes = Encoding.UTF8.GetBytes(providedKey);

        // Evaluate all allowed keys on every request to preserve constant-time behavior.
        var cached = _cachedUserKey;
        if (cached is null || cached.Source != configManager.GetApiKey())
        {
            var source = configManager.GetApiKey();
            cached = new CachedKeyData(source, Encoding.UTF8.GetBytes(source));
            _cachedUserKey = cached;
        }

        var userBytes = cached.Bytes;
        var internalKey = NzbWebDAV.Utils.EnvironmentUtil.GetEnvironmentVariable("FRONTEND_BACKEND_API_KEY") ?? string.Empty;
        var internalBytes = Encoding.UTF8.GetBytes(internalKey);
        var pluginKey = configManager.GetPluginApiKey() ?? string.Empty;
        var pluginPreviousKey = configManager.GetSetupPluginApiKeyPrevious() ?? string.Empty;
        var pluginPreviousExpiresAt = configManager.GetSetupPluginApiKeyPreviousExpiresAtUtc();

        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var pluginPreviousActive = IsValidPluginPreviousKey(pluginPreviousKey, pluginPreviousExpiresAt, nowUtc);

        var pluginMatch = !string.IsNullOrEmpty(pluginKey)
            ? CryptographicOperations.FixedTimeEquals(providedBytes, Encoding.UTF8.GetBytes(pluginKey))
            : false;
        var pluginPreviousMatch = !string.IsNullOrEmpty(pluginPreviousKey)
            ? CryptographicOperations.FixedTimeEquals(
                providedBytes,
                Encoding.UTF8.GetBytes(pluginPreviousKey))
            : false;

        var pluginRouteMatch = IsPluginReadEndpoint(request.Path, request.Method)
            && (pluginMatch || (pluginPreviousActive && pluginPreviousMatch));

        var userMatch = CryptographicOperations.FixedTimeEquals(providedBytes, userBytes);
        var internalMatch = !string.IsNullOrEmpty(internalKey)
            ? CryptographicOperations.FixedTimeEquals(providedBytes, internalBytes)
            : false;

        return userMatch | internalMatch | pluginRouteMatch;
    }

    private static bool IsValidPluginPreviousKey(string previousKey, DateTime? previousExpiresAt, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(previousKey))
            return false;

        if (previousExpiresAt is null)
            return false;

        if (previousExpiresAt <= nowUtc)
            return false;

        if (previousExpiresAt.Value > nowUtc.AddSeconds(MaxPluginApiKeyOverlapSeconds))
            return false;

        return true;
    }

    private static bool IsPluginReadEndpoint(PathString path, string method)
    {
        if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method))
            return false;

        return PluginReadOnlyRoutes.Any(route => path.StartsWithSegments(route, StringComparison.OrdinalIgnoreCase));
    }
}
