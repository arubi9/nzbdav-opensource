using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using NzbWebDAV.Extensions;
using NzbWebDAV.Metrics;

namespace NzbWebDAV.Services;

public class StreamExecutionService
{
    private static readonly FileExtensionContentTypeProvider MimeTypeProvider = new();

    public async Task ServeStreamAsync(
        Stream stream,
        string fileName,
        HttpResponse response,
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        response.ContentType = GetContentType(fileName);
        response.Headers.AcceptRanges = "bytes";

        if (!stream.CanSeek)
        {
            NzbdavMetricsCollector.IncrementActiveStreams();
            try
            {
                await stream.CopyToPooledAsync(response.Body, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                NzbdavMetricsCollector.DecrementActiveStreams();
            }
            return;
        }

        var totalLength = stream.Length;
        var rangeHeader = request.Headers.Range.FirstOrDefault();
        long start = 0;
        long? end = null;

        if (!string.IsNullOrEmpty(rangeHeader) && TryParseRange(rangeHeader, totalLength, out var parsedStart, out var parsedEnd))
        {
            start = parsedStart;
            end = parsedEnd;
            var length = end.Value - start + 1;

            response.StatusCode = StatusCodes.Status206PartialContent;
            response.ContentLength = length;
            response.Headers.ContentRange = $"bytes {start}-{end}/{totalLength}";
        }
        else
        {
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentLength = totalLength;
        }

        if (HttpMethods.IsHead(request.Method))
            return;

        NzbdavMetricsCollector.IncrementActiveStreams();
        try
        {
            await stream.CopyRangeToPooledAsync(
                response.Body,
                start,
                end,
                bufferSize: 256 * 1024,
                cancellationToken: cancellationToken
            ).ConfigureAwait(false);
        }
        finally
        {
            NzbdavMetricsCollector.DecrementActiveStreams();
        }
    }

    public void SetFileHeaders(string fileName, long? fileSize, HttpResponse response)
    {
        response.ContentType = GetContentType(fileName);
        response.Headers.AcceptRanges = "bytes";
        if (fileSize.HasValue)
            response.ContentLength = fileSize.Value;
    }

    private static bool TryParseRange(string rangeHeader, long totalLength, out long start, out long end)
    {
        start = 0;
        end = totalLength - 1;

        if (!rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            return false;

        var rangeSpec = rangeHeader["bytes=".Length..];
        var parts = rangeSpec.Split('-', 2);
        if (parts.Length != 2) return false;

        if (!string.IsNullOrEmpty(parts[0]))
            start = long.TryParse(parts[0], out var parsedStart) ? parsedStart : 0;
        else
        {
            if (long.TryParse(parts[1], out var suffix))
            {
                start = Math.Max(0, totalLength - suffix);
                end = totalLength - 1;
                return true;
            }
            return false;
        }

        if (!string.IsNullOrEmpty(parts[1]))
            end = long.TryParse(parts[1], out var parsedEnd) ? Math.Min(parsedEnd, totalLength - 1) : totalLength - 1;

        return start <= end && start < totalLength;
    }

    public static string GetContentType(string fileName)
    {
        if (MimeTypeProvider.TryGetContentType(fileName, out var contentType))
            return contentType;
        var ext = Path.GetExtension(fileName)?.ToLowerInvariant();
        return ext switch
        {
            ".mkv" => "video/x-matroska",
            ".nfo" => "text/plain",
            ".rclonelink" => "text/plain",
            _ => "application/octet-stream"
        };
    }
}
