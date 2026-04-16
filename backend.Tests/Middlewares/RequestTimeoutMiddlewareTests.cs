using Microsoft.AspNetCore.Http;
using NzbWebDAV.Middlewares;

namespace backend.Tests.Middlewares;

public sealed class RequestTimeoutMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_StreamingRequest_PreservesOriginalAbortToken()
    {
        var seenToken = CancellationToken.None;
        var middleware = new RequestTimeoutMiddleware(ctx =>
        {
            seenToken = ctx.RequestAborted;
            return Task.CompletedTask;
        });

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/stream/123";
        using var original = new CancellationTokenSource();
        context.RequestAborted = original.Token;

        await middleware.InvokeAsync(context);

        Assert.Equal(original.Token, seenToken);
    }

    [Fact]
    public async Task InvokeAsync_MetadataRequest_ReplacesAbortTokenWithTimeoutToken()
    {
        var seenToken = CancellationToken.None;
        var middleware = new RequestTimeoutMiddleware(ctx =>
        {
            seenToken = ctx.RequestAborted;
            return Task.CompletedTask;
        });

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/meta/123";
        using var original = new CancellationTokenSource();
        context.RequestAborted = original.Token;

        await middleware.InvokeAsync(context);

        Assert.NotEqual(original.Token, seenToken);
    }
}
