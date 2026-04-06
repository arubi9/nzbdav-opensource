using System.Text;
using Microsoft.AspNetCore.Http;
using NzbWebDAV.Services;

namespace backend.Tests.Services;

public sealed class StreamExecutionServiceTests
{
    [Fact]
    public void ResolveContentType_UsesSpecialCasesAndFallback()
    {
        var service = new StreamExecutionService();

        Assert.Equal("text/plain", service.ResolveContentType("README"));
        Assert.Equal("video/webm", service.ResolveContentType("movie.mkv"));
        Assert.Equal("text/plain", service.ResolveContentType("notes.nfo"));
        Assert.Equal("application/octet-stream", service.ResolveContentType("archive.unknownext"));
    }

    [Fact]
    public void BeginActiveStream_TracksOpenLeases()
    {
        var service = new StreamExecutionService();

        Assert.Equal(0, service.ActiveStreamCount);

        using (service.BeginActiveStream())
        {
            Assert.Equal(1, service.ActiveStreamCount);
        }

        Assert.Equal(0, service.ActiveStreamCount);
    }

    [Fact]
    public async Task ExecuteAsync_WritesRangeBodyAndHeadersForGetRequest()
    {
        var service = new StreamExecutionService();
        var context = CreateContext(HttpMethods.Get, "bytes=2-5");

        await service.ExecuteAsync(
            context,
            _ => Task.FromResult<Stream>(new MemoryStream(Encoding.ASCII.GetBytes("0123456789"))),
            "movie.mkv"
        );

        Assert.Equal(StatusCodes.Status206PartialContent, context.Response.StatusCode);
        Assert.Equal("video/webm", context.Response.ContentType);
        Assert.Equal("bytes", context.Response.Headers.AcceptRanges.ToString());
        Assert.Equal(4, context.Response.ContentLength);
        Assert.Equal("bytes 2-5 / 10", context.Response.Headers.ContentRange.ToString());
        Assert.Equal("2345", Encoding.ASCII.GetString(((MemoryStream)context.Response.Body).ToArray()));
    }

    [Fact]
    public async Task ExecuteAsync_SkipsBodyForHeadRequest()
    {
        var service = new StreamExecutionService();
        var context = CreateContext(HttpMethods.Head, "bytes=2-5");

        await service.ExecuteAsync(
            context,
            _ => Task.FromResult<Stream>(new MemoryStream(Encoding.ASCII.GetBytes("0123456789"))),
            "movie.mkv"
        );

        Assert.Equal(StatusCodes.Status206PartialContent, context.Response.StatusCode);
        Assert.Equal("video/webm", context.Response.ContentType);
        Assert.Equal("bytes", context.Response.Headers.AcceptRanges.ToString());
        Assert.Equal(4, context.Response.ContentLength);
        Assert.Equal("bytes 2-5 / 10", context.Response.Headers.ContentRange.ToString());
        Assert.Equal(0, ((MemoryStream)context.Response.Body).Length);
    }

    private static DefaultHttpContext CreateContext(string method, string rangeHeader)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Headers.Range = rangeHeader;
        context.Response.Body = new MemoryStream();
        return context;
    }
}
