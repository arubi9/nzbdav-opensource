using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.Filters;

/// <summary>
/// Shared source binding for credential endpoints. A browser-provided source
/// is trusted only on loopback traffic authenticated with the private frontend
/// key; direct callers cannot spoof it.
/// </summary>
public static class TrustedAuthSource
{
    public static string For(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        var forwarded = context.Request.Headers["X-Frontend-Client-Source"].FirstOrDefault();
        var internalKey = EnvironmentUtil.GetEnvironmentVariable("FRONTEND_BACKEND_API_KEY") ?? string.Empty;
        var providedKey = context.Request.Headers["X-Api-Key"].FirstOrDefault() ?? string.Empty;
        var frontendPeer = ApiKeyUtil.MatchesConfiguredKeys(providedKey, string.Empty, internalKey);

        // The private frontend/backend key authenticates the reverse proxy
        // across both loopback and container-network deployments. Public
        // callers cannot choose a throttle bucket by sending this header; IP
        // literals remain accepted for backwards-compatible direct requests.
        if (frontendPeer && IsBoundedOpaqueSource(forwarded))
            return "frontend:" + forwarded!.Trim();

        return remote?.ToString() ?? "unknown";
    }

    private static bool IsBoundedOpaqueSource(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
            return false;
        return value.All(character => character is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9'
            or '-' or '_' or '.' or ':' or '@');
    }

}

public static class SetupAuthFailureGuard
{
    public static async Task<IActionResult?> RejectIfBlockedAsync(
        HttpContext context,
        IAuthFailureTracker? tracker)
    {
        if (tracker is null)
            return null;

        if (!await tracker.IsBlockedAsync(TrustedAuthSource.For(context)).ConfigureAwait(false))
            return null;

        context.Response.Headers.RetryAfter = "60";
        return new ObjectResult(new
        {
            status = false,
            error = "Too many failed authentication attempts."
        })
        {
            StatusCode = StatusCodes.Status429TooManyRequests,
        };
    }

    public static async Task RecordFailureAsync(HttpContext context, IAuthFailureTracker? tracker)
    {
        if (tracker is null)
            return;

        try
        {
            await tracker.RecordFailureAsync(TrustedAuthSource.For(context)).ConfigureAwait(false);
        }
        catch
        {
            // The tracker is a protective control, not an authentication
            // dependency. A tracker outage must not expose credentials or
            // replace the normal closed-set unauthorized response.
        }
    }
}
