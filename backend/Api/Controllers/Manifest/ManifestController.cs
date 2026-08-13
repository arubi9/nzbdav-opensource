using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Api.Filters;
using NzbWebDAV.Clients.Usenet.Caching;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Api.Controllers.Manifest;

/// <summary>Returns the entire /content tree as one bounded, ETag-versioned JSON document.</summary>
[ApiController]
[Route("api/manifest")]
[ServiceFilter(typeof(ApiKeyAuthFilter))]
public class ManifestController(DavDatabaseClient dbClient, LiveSegmentCache liveSegmentCache) : ControllerBase
{
    // Keep this below the plugin's 8 MiB input cap. The response is built in a
    // fixed-size buffer, so serialization can never grow beyond this limit.
    internal const int MaxManifestResponseBytes = 7 * 1024 * 1024;
    private const int MaxManifestItems = 10_000;
    private const int MaxManifestInputUtf16Chars = 2 * 1024 * 1024;
    private const int MaxManifestInputUtf8Bytes = 4 * 1024 * 1024;
    private const int MaxPathLength = 1_024;
    private const int MaxNameLength = 255;
    private const int MaxTypeLength = 32;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [HttpGet]
    public async Task<IActionResult> GetManifest(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Do not materialize entities or an unbounded query. The projection is
        // deliberately kept here so the database only returns manifest data.
        var manifestRows = dbClient.Ctx.Items
            .AsNoTracking()
            .Where(x => x.Path.StartsWith("/content/"))
            .Select(x => new
            {
                x.Id,
                x.ParentId,
                x.Name,
                x.Path,
                Type = x.Type == DavItem.ItemType.Directory ? "directory"
                    : x.Type == DavItem.ItemType.NzbFile ? "nzb_file"
                    : x.Type == DavItem.ItemType.RarFile ? "rar_file"
                    : x.Type == DavItem.ItemType.MultipartFile ? "multipart_file" : "unknown",
                x.FileSize,
                x.CreatedAt
            })
            .Take(MaxManifestItems + 1)
            .AsAsyncEnumerable();

        var items = new List<ManifestItem>(Math.Min(MaxManifestItems, 256));
        var ids = new HashSet<Guid>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inputUtf16Chars = 0L;
        var inputUtf8Bytes = 0L;

        await foreach (var row in manifestRows.WithCancellation(ct).ConfigureAwait(false))
        {
            if (items.Count == MaxManifestItems)
                return TooLarge("Manifest contains too many items.");

            if (row.Name is null || row.Path is null || row.Type is null)
                return InvalidManifest("Manifest contains an item with a missing string field.");
            if (row.Name.Length > MaxNameLength || row.Path.Length > MaxPathLength || row.Type.Length > MaxTypeLength)
                return InvalidManifest("Manifest contains an item with an oversized string field.");
            if (!row.Path.StartsWith("/content/", StringComparison.Ordinal))
                return InvalidManifest("Manifest contains an item with an invalid path.");
            if (!ids.Add(row.Id) || !paths.Add(row.Path))
                return InvalidManifest("Manifest contains duplicate IDs or final destinations.");

            var rowUtf16Chars = (long)row.Name.Length + row.Path.Length + row.Type.Length;
            int rowUtf8Bytes;
            try
            {
                rowUtf8Bytes = GetUtf8ByteCount(row.Name) + GetUtf8ByteCount(row.Path) + GetUtf8ByteCount(row.Type);
            }
            catch (InvalidManifestStringException)
            {
                return InvalidManifest("Manifest contains invalid UTF-16 string data.");
            }
            if (inputUtf16Chars > MaxManifestInputUtf16Chars - rowUtf16Chars
                || inputUtf8Bytes > MaxManifestInputUtf8Bytes - rowUtf8Bytes)
                return TooLarge("Manifest contains too much string data.");
            inputUtf16Chars += rowUtf16Chars;
            inputUtf8Bytes += rowUtf8Bytes;

            var hasProbeData = System.IO.File.Exists(
                Path.Combine(liveSegmentCache.CacheDirectory, $"probe-{row.Id:N}.json"));
            items.Add(new ManifestItem
            {
                Id = row.Id,
                ParentId = row.ParentId,
                Name = row.Name,
                Path = row.Path,
                Type = row.Type,
                FileSize = row.FileSize,
                CreatedAt = row.CreatedAt,
                HasProbeData = hasProbeData
            });
        }

        // Database ordering is not part of the contract. Sort the bounded set
        // using an ordinal key so equivalent databases produce identical bytes.
        items.Sort(static (left, right) =>
        {
            var pathComparison = StringComparer.Ordinal.Compare(left.Path, right.Path);
            return pathComparison != 0 ? pathComparison : left.Id.CompareTo(right.Id);
        });

        var response = new ManifestResponse { ItemCount = items.Count, Items = items };
        byte[] serialized;
        try
        {
            using var output = new BoundedMemoryStream(MaxManifestResponseBytes);
            using (var writer = new Utf8JsonWriter(output))
            {
                JsonSerializer.Serialize(writer, response, JsonOptions);
                writer.Flush();
            }
            serialized = output.ToArray();
        }
        catch (ManifestResponseTooLargeException)
        {
            return TooLarge("Manifest JSON is too large.");
        }

        // Hash the bytes clients actually receive. In particular, this keeps
        // ETags coupled to JSON escaping and property ordering.
        var etag = $"\"{Convert.ToHexString(SHA256.HashData(serialized))}\"";
        Response.Headers.ETag = etag;
        Response.Headers.CacheControl = "private, max-age=30";
        if (Request.Headers.IfNoneMatch.Any(value => string.Equals((value ?? string.Empty).Trim(), etag, StringComparison.Ordinal)))
        {
            Response.StatusCode = StatusCodes.Status304NotModified;
            return new EmptyResult();
        }

        return File(serialized, "application/json");
    }

    private static int GetUtf8ByteCount(string value)
    {
        try
        {
            return Utf8.GetByteCount(value);
        }
        catch (EncoderFallbackException)
        {
            throw new InvalidManifestStringException();
        }
    }

    private ObjectResult TooLarge(string message)
        => StatusCode(StatusCodes.Status413PayloadTooLarge, new { error = message });

    private ObjectResult InvalidManifest(string message)
        => StatusCode(StatusCodes.Status422UnprocessableEntity, new { error = message });

    private sealed class InvalidManifestStringException : Exception { }

    private sealed class ManifestResponseTooLargeException : Exception { }

    /// <summary>A fixed-size stream which rejects a write before it can exceed its cap.</summary>
    private sealed class BoundedMemoryStream : Stream
    {
        private readonly byte[] _buffer;
        private int _position;

        public BoundedMemoryStream(int capacity) => _buffer = new byte[capacity];

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _position;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public byte[] ToArray() => _buffer.AsSpan(0, _position).ToArray();

        public override void Flush() { }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length > _buffer.Length - _position)
                throw new ManifestResponseTooLargeException();
            buffer.CopyTo(_buffer.AsSpan(_position));
            _position += buffer.Length;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }
    }
}

public class ManifestResponse
{
    public required int ItemCount { get; init; }
    public required List<ManifestItem> Items { get; init; }
}

public class ManifestItem
{
    public required Guid Id { get; init; }
    public Guid? ParentId { get; init; }
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string Type { get; init; }
    public long? FileSize { get; init; }
    public required DateTime CreatedAt { get; init; }
    public bool HasProbeData { get; set; }
}
