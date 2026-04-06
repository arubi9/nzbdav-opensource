using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using NWebDav.Server;
using NWebDav.Server.Helpers;
using NzbWebDAV.Extensions;

namespace NzbWebDAV.Services;

public sealed class StreamExecutionService
{
    private static readonly FileExtensionContentTypeProvider MimeTypeProvider = new();

    private int _activeStreams;

    public int ActiveStreamCount => Volatile.Read(ref _activeStreams);

    public IDisposable BeginActiveStream()
    {
        Interlocked.Increment(ref _activeStreams);
        return new ActiveStreamLease(this);
    }

    public string ResolveContentType(string fileName, string? explicitContentType = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitContentType))
            return explicitContentType;

        var normalizedFileName = Path.GetFileName(fileName);
        if (string.Equals(normalizedFileName, "README", StringComparison.OrdinalIgnoreCase))
            return "text/plain";

        return Path.GetExtension(normalizedFileName).ToLowerInvariant() switch
        {
            ".mkv" => "video/webm",
            ".rclonelink" => "text/plain",
            ".nfo" => "text/plain",
            _ when MimeTypeProvider.TryGetContentType(normalizedFileName, out var mimeType) => mimeType,
            _ => "application/octet-stream",
        };
    }

    public async Task ExecuteAsync(
        HttpContext httpContext,
        Func<CancellationToken, Task<Stream>> openStreamAsync,
        string fileName,
        string? explicitContentType = null)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(openStreamAsync);

        using var activeStreamLease = BeginActiveStream();

        var request = httpContext.Request;
        var response = httpContext.Response;
        var isHeadRequest = request.Method == HttpMethods.Head;
        var range = request.GetRange();

        var stream = await openStreamAsync(httpContext.RequestAborted).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            if (stream == Stream.Null)
            {
                response.SetStatus(DavStatusCode.NoContent);
                return;
            }

            response.SetStatus(DavStatusCode.Ok);
            response.ContentType = ResolveContentType(fileName, explicitContentType);

            try
            {
                if (stream.CanSeek)
                {
                    response.Headers.AcceptRanges = "bytes";

                    var length = stream.Length;
                    if (range != null)
                    {
                        var start = range.Start ?? 0;
                        var end = Math.Min(range.End ?? long.MaxValue, length - 1);
                        length = end - start + 1;

                        response.Headers.ContentRange = $"bytes {start}-{end} / {stream.Length}";
                        if (length < stream.Length)
                            response.SetStatus(DavStatusCode.PartialContent);
                    }

                    response.ContentLength = length;
                }
            }
            catch (NotSupportedException)
            {
                // If the content length is not supported, then we just skip it.
            }

            if (isHeadRequest)
                return;

            await stream.CopyRangeToPooledAsync(
                response.Body,
                range?.Start ?? 0,
                range?.End,
                cancellationToken: httpContext.RequestAborted
            ).ConfigureAwait(false);
        }
    }

    private void ReleaseActiveStream()
    {
        Interlocked.Decrement(ref _activeStreams);
    }

    private sealed class ActiveStreamLease(StreamExecutionService owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return;

            owner.ReleaseActiveStream();
        }
    }
}
