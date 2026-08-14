using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Api.Filters;
using NzbWebDAV.Clients.Usenet.Caching;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

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

    // Unpaged callers still get one document, so they keep the original ceiling.
    // Paged callers reuse the same number as a page size: every per-response bound
    // below is a property of the response, not of the library, so a page that
    // respects them is servable no matter how large the tree grows.
    private const int MaxManifestItems = 10_000;
    internal const int MaxCursorLength = 2_048;
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

    /// <param name="paged">
    /// Opt-in. An older plugin that does not understand <c>nextCursor</c> would treat
    /// every item beyond the first page as deleted and quarantine it, so pagination is
    /// never applied unless the caller asks for it.
    /// </param>
    /// <param name="after">Opaque cursor from the previous page's <c>nextCursor</c>.</param>
    [HttpGet]
    public async Task<IActionResult> GetManifest(
        [FromQuery] bool paged,
        [FromQuery] string? after,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!paged && !string.IsNullOrEmpty(after))
            return InvalidManifest("Manifest cursors require paged=true.");

        var afterPath = string.Empty;
        if (!string.IsNullOrEmpty(after))
        {
            if (!TryDecodeCursor(after, out afterPath))
                return InvalidManifest("Manifest cursor is malformed.");
        }

        // Paging keys on Path alone. Path is already required to be unique -- the
        // duplicate check below rejects the manifest outright otherwise -- so it is a
        // complete sort key, and unlike Id it orders identically in .NET, SQLite and
        // Postgres. Guid ordering does not agree across those three.
        var hasCursor = afterPath.Length > 0;

        // Do not materialize entities or an unbounded query. The projection is
        // deliberately kept here so the database only returns manifest data.
        var manifestRows = dbClient.Ctx.Items
            .AsNoTracking()
            .Where(x => x.Path.StartsWith("/content/"))
            .Where(x => !hasCursor || string.Compare(x.Path, afterPath) > 0)
            .OrderBy(x => x.Path)
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

        // Set when a bound stopped the page early rather than the tree running out.
        var hasMore = false;

        // The cursor must be the last path in *database* order. The sort below reorders
        // this page ordinally for stable output bytes, and the database collation need
        // not agree with ordinal comparison, so reading the cursor off the sorted list
        // would skip or repeat rows at every page boundary.
        var cursorPath = string.Empty;

        await foreach (var row in manifestRows.WithCancellation(ct).ConfigureAwait(false))
        {
            if (items.Count == MaxManifestItems)
            {
                if (!paged) return TooLarge("Manifest contains too many items.");
                hasMore = true;
                break;
            }

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
            {
                // A single row that cannot fit an empty page is unservable at any page
                // size. Failing here is what stops the caller looping on a page that
                // can never make progress.
                if (!paged || items.Count == 0)
                    return TooLarge("Manifest contains too much string data.");
                hasMore = true;
                break;
            }
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
            cursorPath = row.Path;
        }

        // Database ordering is not part of the contract. Sort the bounded set
        // using an ordinal key so equivalent databases produce identical bytes.
        items.Sort(static (left, right) =>
        {
            var pathComparison = StringComparer.Ordinal.Compare(left.Path, right.Path);
            return pathComparison != 0 ? pathComparison : left.Id.CompareTo(right.Id);
        });

        var response = new ManifestResponse
        {
            ItemCount = items.Count,
            Items = items,
            // Null in unpaged mode, and both properties are omitted when null, so an
            // unpaged response is byte-identical to the one served before paging
            // existed. That keeps existing ETags valid across this change.
            NextCursor = hasMore && cursorPath.Length > 0 ? EncodeCursor(cursorPath) : null,
            Version = paged ? ManifestVersion.Token : null
        };
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

        // Unpaged: hash the bytes clients actually receive. In particular, this keeps
        // ETags coupled to JSON escaping and property ordering.
        //
        // Paged: hashing one page would only prove that page unchanged, so a 304 on it
        // would wrongly imply the rest of the tree is unchanged too. Use the tree
        // version instead, which moves on any content or probe change.
        var etag = paged
            ? $"\"{ManifestVersion.Token}\""
            : $"\"{Convert.ToHexString(SHA256.HashData(serialized))}\"";
        Response.Headers.ETag = etag;
        Response.Headers.CacheControl = "private, max-age=30";

        // Only the first page may be revalidated. Honouring If-None-Match on a later
        // page would answer "the tree is unchanged" to a question about one slice of it,
        // leaving the caller with a truncated walk.
        var mayRevalidate = !hasCursor;
        if (mayRevalidate && Request.Headers.IfNoneMatch.Any(value => string.Equals((value ?? string.Empty).Trim(), etag, StringComparison.Ordinal)))
        {
            Response.StatusCode = StatusCodes.Status304NotModified;
            return new EmptyResult();
        }

        return File(serialized, "application/json");
    }

    /// <summary>Cursors are opaque to clients; the encoding only has to round-trip.</summary>
    private static string EncodeCursor(string path)
        => Convert.ToBase64String(Utf8.GetBytes(path));

    private static bool TryDecodeCursor(string cursor, out string path)
    {
        path = string.Empty;
        if (cursor.Length > MaxCursorLength) return false;

        Span<byte> decoded = new byte[MaxCursorLength];
        if (!Convert.TryFromBase64String(cursor, decoded, out var written)) return false;

        try
        {
            // Throws on malformed UTF-8 because Utf8 is constructed to.
            path = Utf8.GetString(decoded[..written]);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        // A cursor is a path the caller was previously given. Anything else is either a
        // client bug or someone probing, and both deserve the same flat rejection.
        return path.Length is > 0 and <= MaxPathLength
               && path.StartsWith("/content/", StringComparison.Ordinal);
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

    /// <summary>Cursor for the next page, or null when this is the last page.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NextCursor { get; init; }

    /// <summary>
    /// Identifies the tree these pages were read from. A caller that sees this change
    /// mid-walk has assembled a torn view and must start over.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; init; }
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
