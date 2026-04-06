using Microsoft.AspNetCore.Http;

namespace NzbWebDAV.Middlewares;

public class RequestTimeoutMiddleware(RequestDelegate next)
{
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StreamTimeout = TimeSpan.FromMinutes(5);

    public async Task InvokeAsync(HttpContext context)
    {
        var timeout = IsStreamingRequest(context) ? StreamTimeout : MetadataTimeout;
        var originalAbortToken = context.RequestAborted;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(originalAbortToken);
        cts.CancelAfter(timeout);
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
