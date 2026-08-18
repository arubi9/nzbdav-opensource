using System.Buffers;
using System.Text;
using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.Controllers.Setup;

namespace NzbWebDAV.Tests.Api.Controllers.Setup;

public sealed class SetupGrantRequestTests
{
    [Theory]
    [InlineData("handoff")]
    [InlineData("renew")]
    [InlineData("recovery")]
    public async Task CapturedFrontendUrlSearchParamsBytesAndHeadersCrossProductionParser(string operation)
    {
        // Captured from the frontend URLSearchParams contract. Keep this
        // boundary test on real bytes/headers rather than mocking form parsing.
        const string contentType = "application/x-www-form-urlencoded; charset=UTF-8";
        var capturedBytes = Encoding.UTF8.GetBytes("username=Admin%2B&password=p%26ss%3Dsecret");
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.ContentType = contentType;
        context.Request.ContentLength = capturedBytes.Length;
        context.Request.Body = new MemoryStream(capturedBytes);

        var requestOperation = operation switch
        {
            "handoff" => SetupGrantOperation.Handoff,
            "renew" => SetupGrantOperation.Renew,
            "recovery" => SetupGrantOperation.Recovery,
            _ => throw new InvalidOperationException(),
        };
        var request = await SetupGrantRequest.ParseAsync(context, requestOperation, CancellationToken.None);

        Assert.Equal("admin+", request.Username);
        Assert.Equal("p&ss=secret", request.Password);
        Assert.Equal(contentType, context.Request.ContentType);
    }

    [Theory]
    [InlineData("handoff")]
    [InlineData("renew")]
    [InlineData("recovery")]
    public async Task CapturedFrontendBoundaryRejectsDuplicateMalformedAndBounds(string operation)
    {
        var requestOperation = operation switch
        {
            "handoff" => SetupGrantOperation.Handoff,
            "renew" => SetupGrantOperation.Renew,
            "recovery" => SetupGrantOperation.Recovery,
            _ => throw new InvalidOperationException(),
        };
        var capturedBodies = new[]
        {
            "username=admin&username=other&password=secret",
            "username=admin%ZZ&password=secret",
            $"username=admin&password={new string('p', 4096)}",
        };

        foreach (var body in capturedBodies)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            var context = new DefaultHttpContext();
            context.Request.Method = "POST";
            context.Request.ContentType = "application/x-www-form-urlencoded; charset=UTF-8";
            context.Request.ContentLength = bytes.Length;
            context.Request.Body = new MemoryStream(bytes);

            await Assert.ThrowsAsync<BadHttpRequestException>(() =>
                SetupGrantRequest.ParseAsync(context, requestOperation, CancellationToken.None));
        }
    }

    [Fact]
    public async Task ReadAsync_ParsesValidUrlEncodedForm()
    {
        var context = CreateContext(
            "application/x-www-form-urlencoded; charset=UTF-8",
            "username=Admin&password=secret");

        var result = await SetupGrantRequest.ReadAsync(context, CancellationToken.None);

        Assert.Equal("admin", result.Username);
        Assert.Equal("secret", result.Password);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("")]
    [InlineData(null)]
    public async Task ReadAsync_RejectsInvalidContentType(string? contentType)
    {
        var context = CreateContext(contentType, "username=Admin&password=secret");

        var exception = await Assert.ThrowsAsync<BadHttpRequestException>(() =>
            SetupGrantRequest.ReadAsync(context, CancellationToken.None));

        Assert.Contains("content type", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAsync_RejectsOversizedContentLengthBody()
    {
        var context = CreateContext(
            "application/x-www-form-urlencoded",
            "username=admin&password=secret",
            contentLength: SetupGrantRequest.DefaultMaxRequestBodySizeBytes + 1);

        var exception = await Assert.ThrowsAsync<BadHttpRequestException>(() =>
            SetupGrantRequest.ReadAsync(context, CancellationToken.None));

        Assert.Contains("too large", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAsync_RejectsOversizedChunkedBody()
    {
        var oversizedPassword = new string('p', (int)SetupGrantRequest.DefaultMaxRequestBodySizeBytes);
        var body = $"username=admin&password={oversizedPassword}";
        var context = CreateContext("application/x-www-form-urlencoded", body);

        var exception = await Assert.ThrowsAsync<BadHttpRequestException>(() =>
            SetupGrantRequest.ReadAsync(context, CancellationToken.None));

        Assert.Contains("too large", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAsync_RejectsMalformedFields()
    {
        var context = CreateContext(
            "application/x-www-form-urlencoded",
            "username=admin&password=secret&role=admin");

        var exception = await Assert.ThrowsAsync<BadHttpRequestException>(() =>
            SetupGrantRequest.ReadAsync(context, CancellationToken.None));

        Assert.Equal("Invalid setup credentials payload.", exception.Message);
    }

    [Fact]
    public async Task ReadAsync_RejectsDuplicateUsernameField()
    {
        var context = CreateContext(
            "application/x-www-form-urlencoded",
            "username=admin&username=evil&password=secret");

        var exception = await Assert.ThrowsAsync<BadHttpRequestException>(() =>
            SetupGrantRequest.ReadAsync(context, CancellationToken.None));

        Assert.Equal("Invalid setup credentials payload.", exception.Message);
    }

    [Fact]
    public async Task ReadAsync_RejectsOverlongUsername()
    {
        var longUsername = new string('u', 256);
        var context = CreateContext(
            "application/x-www-form-urlencoded",
            $"username={longUsername}&password=secret");

        var exception = await Assert.ThrowsAsync<BadHttpRequestException>(() =>
            SetupGrantRequest.ReadAsync(context, CancellationToken.None));

        Assert.Equal("Invalid setup credentials payload.", exception.Message);
    }

    [Fact]
    public async Task ReadAsync_ReturnsArrayPoolBuffersWithClearArrayFlag()
    {
        var pool = new TrackingArrayPool();
        var originalFactory = SetupGrantRequest.BufferPoolFactory;
        SetupGrantRequest.BufferPoolFactory = () => pool;

        try
        {
            var longPassword = new string('p', 9000);
            var body = $"username=admin&password={longPassword}";
            var context = CreateContext("application/x-www-form-urlencoded", body);
            var limits = new SetupGrantRequest.Limits(10_000, 2, 255, 10_000);

            var result = await SetupGrantRequest.ReadAsync(context, limits, CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal("admin", result.Username);
            Assert.Equal(longPassword, result.Password);
            Assert.Equal(0, pool.ReturnedWithoutClearCount);
            Assert.True(pool.ReturnCalls > 0);
        }
        finally
        {
            SetupGrantRequest.BufferPoolFactory = originalFactory;
        }
    }

    [Fact]
    public async Task ReadAsync_RespectsRequestAborted()
    {
        var context = CreateContext("application/x-www-form-urlencoded", "username=admin&password=secret");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SetupGrantRequest.ReadAsync(context, cancellation.Token));
    }

    private static DefaultHttpContext CreateContext(string? contentType, string body, long? contentLength = null)
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = contentType;

        if (contentLength.HasValue)
            context.Request.ContentLength = contentLength.Value;
        else
            context.Request.Headers.ContentLength = null;

        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return context;
    }

    private sealed class TrackingArrayPool : ArrayPool<byte>
    {
        public int ReturnCalls { get; private set; }
        public int ReturnedWithoutClearCount { get; private set; }

        public override byte[] Rent(int minimumLength)
        {
            var array = new byte[minimumLength];
            Array.Fill(array, (byte)0xAA);
            return array;
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            ReturnCalls++;
            if (!clearArray)
                ReturnedWithoutClearCount++;
        }
    }
}
