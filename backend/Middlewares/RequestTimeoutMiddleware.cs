using Microsoft.AspNetCore.Http;

namespace NzbWebDAV.Middlewares;

public class RequestTimeoutMiddleware(RequestDelegate next, TimeProvider? timeProvider = null)
{
    public static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan SetupTimeout = TimeSpan.FromMinutes(10);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task InvokeAsync(HttpContext context)
    {
        if (IsStreamingRequest(context))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var originalAbortToken = context.RequestAborted;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(originalAbortToken);
        using var timer = _timeProvider.CreateTimer(
            static state => ((CancellationTokenSource)state!).Cancel(),
            cts,
            IsLongRunningSetupRequest(context) ? SetupTimeout : MetadataTimeout,
            Timeout.InfiniteTimeSpan);
        context.RequestAborted = cts.Token;

        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested
                                                   && !originalAbortToken.IsCancellationRequested)
        {
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
                await context.Response.WriteAsync("Request timed out.").ConfigureAwait(false);
            }
        }
    }

    internal static bool IsLongRunningSetupRequest(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        return path.Equals("/api/setup/handoff", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/setup/admin-handoff", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/setup/grant/renew", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/setup/renew-grant", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/setup/grant/recovery", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/setup/grant/recover", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/setup/grant/revoke", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/setup/revoke-grant", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/setup/recover-grant", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/setup/configuration", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/setup/configure", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/setup/run", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/setup/retry", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStreamingRequest(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        if (path.StartsWith("/api/stream/", StringComparison.OrdinalIgnoreCase)) return true;
        if (context.Request.Method == HttpMethods.Get
            && path.StartsWith("/content/", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.StartsWith("/view/", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
