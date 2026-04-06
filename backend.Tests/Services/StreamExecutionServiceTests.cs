using System.Text;
using Microsoft.AspNetCore.Http;
using NzbWebDAV.Services;

namespace backend.Tests.Services;

public sealed class StreamExecutionServiceTests
{
    [Fact]
    public void GetContentType_UsesSpecialCasesAndFallback()
    {
        Assert.Equal("application/octet-stream", StreamExecutionService.GetContentType("README"));
        Assert.Equal("video/x-matroska", StreamExecutionService.GetContentType("movie.mkv"));
        Assert.Equal("text/plain", StreamExecutionService.GetContentType("notes.nfo"));
        Assert.Equal("application/octet-stream", StreamExecutionService.GetContentType("archive.unknownext"));
    }

    [Fact]
    public void SetFileHeaders_SetsContentTypeAcceptRangesAndLength()
    {
        var service = new StreamExecutionService();
        var context = new DefaultHttpContext();

        service.SetFileHeaders("movie.mkv", 1234, context.Response);

        Assert.Equal("video/x-matroska", context.Response.ContentType);
        Assert.Equal("bytes", context.Response.Headers.AcceptRanges.ToString());
        Assert.Equal(1234, context.Response.ContentLength);
    }

    [Fact]
    public async Task ServeStreamAsync_WritesRangeBodyAndHeadersForGetRequest()
    {
        var service = new StreamExecutionService();
        var context = CreateContext(HttpMethods.Get, "bytes=2-5");
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes("0123456789"));

        await service.ServeStreamAsync(stream, "movie.mkv", context.Response, context.Request, CancellationToken.None);

        Assert.Equal(StatusCodes.Status206PartialContent, context.Response.StatusCode);
        Assert.Equal("video/x-matroska", context.Response.ContentType);
        Assert.Equal("bytes", context.Response.Headers.AcceptRanges.ToString());
        Assert.Equal(4, context.Response.ContentLength);
        Assert.Equal("bytes 2-5/10", context.Response.Headers.ContentRange.ToString());
        Assert.Equal("2345", Encoding.ASCII.GetString(((MemoryStream)context.Response.Body).ToArray()));
    }

    [Fact]
    public async Task ServeStreamAsync_SkipsBodyForHeadRequest()
    {
        var service = new StreamExecutionService();
        var context = CreateContext(HttpMethods.Head, "bytes=2-5");
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes("0123456789"));

        await service.ServeStreamAsync(stream, "movie.mkv", context.Response, context.Request, CancellationToken.None);

        Assert.Equal(StatusCodes.Status206PartialContent, context.Response.StatusCode);
        Assert.Equal("video/x-matroska", context.Response.ContentType);
        Assert.Equal("bytes", context.Response.Headers.AcceptRanges.ToString());
        Assert.Equal(4, context.Response.ContentLength);
        Assert.Equal("bytes 2-5/10", context.Response.Headers.ContentRange.ToString());
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
