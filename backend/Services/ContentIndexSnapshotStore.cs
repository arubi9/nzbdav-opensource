using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Services;

/// <summary>
/// Reads and writes the /content recovery snapshot.
///
/// The snapshot is dominated by DavNzbFile.SegmentIds: one usenet article id per
/// 700KB of media, or roughly 43 snapshot bytes per segment. A 40TB library is a
/// ~2.4GB snapshot, so neither the writer nor the reader may hold the document -- or
/// the rows it is built from -- in memory. Everything here therefore streams:
/// the writer pages the database and emits JSON as it goes, and the reader parses
/// incrementally so callers can consume rows in bounded batches.
/// </summary>
public static class ContentIndexSnapshotStore
{
    public const int CurrentVersion = 1;

    // Items carry no segment data, so they page cheaply.
    internal const int DefaultItemPageSize = 500;

    // File rows are paged in two phases. DavItems has no index on Path, so every
    // keyset page costs a scan and small pages turn the write quadratic (measured:
    // 40k files took 51s at 16 rows per page and 2.8s at 500). The cursor page is
    // therefore large but projects only (Path, Id) -- no SegmentIds -- and the rows
    // themselves are then fetched by primary key in small chunks, so the resident
    // set stays bounded by the chunk size times the worst row rather than by the
    // number of files.
    internal const int DefaultFilePageSize = 500;
    internal const int DefaultFileChunkSize = 25;

    private const int WriterFlushThresholdBytes = 64 * 1024;

    private const string VersionProperty = "Version";
    private const string GeneratedAtUtcProperty = "GeneratedAtUtc";
    private const string ItemsProperty = "Items";
    private const string NzbFilesProperty = "NzbFiles";
    private const string RarFilesProperty = "RarFiles";
    private const string MultipartFilesProperty = "MultipartFiles";

    private const string SnapshotFileName = "content-index.snapshot.json";
    private const string BackupSnapshotFileName = "content-index.snapshot.backup.json";
    private static readonly SemaphoreSlim Mutex = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    public static string SnapshotFilePath => Path.Join(DavDatabaseContext.ConfigPath, SnapshotFileName);
    public static string BackupSnapshotFilePath => Path.Join(DavDatabaseContext.ConfigPath, BackupSnapshotFileName);

    public static Task WriteAsync(DavDatabaseContext dbContext, CancellationToken cancellationToken)
    {
        return WriteAsync(dbContext, DefaultItemPageSize, DefaultFilePageSize, cancellationToken);
    }

    internal static async Task WriteAsync
    (
        DavDatabaseContext dbContext,
        int itemPageSize,
        int filePageSize,
        CancellationToken cancellationToken,
        int fileChunkSize = DefaultFileChunkSize
    )
    {
        await Mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(DavDatabaseContext.ConfigPath);
            var tempFilePath = SnapshotFilePath + ".tmp";

            try
            {
                await using (var stream = File.Create(tempFilePath))
                {
                    await WriteSnapshotAsync(
                        dbContext,
                        stream,
                        itemPageSize,
                        filePageSize,
                        cancellationToken,
                        fileChunkSize
                    ).ConfigureAwait(false);
                }

                File.Move(tempFilePath, SnapshotFilePath, overwrite: true);
                File.Copy(SnapshotFilePath, BackupSnapshotFilePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempFilePath))
                    File.Delete(tempFilePath);
            }
        }
        finally
        {
            Mutex.Release();
        }
    }

    internal static async Task WriteSnapshotAsync
    (
        DavDatabaseContext dbContext,
        Stream stream,
        int itemPageSize,
        int filePageSize,
        CancellationToken cancellationToken,
        int fileChunkSize = DefaultFileChunkSize
    )
    {
        // Like the query-per-table implementation this replaces, the four arrays
        // are read outside a single transaction, so a row committed midway can be
        // captured in one array but not the other. Such a snapshot is rejected on
        // read and replaced by the next debounced write.
        await using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });
        writer.WriteStartObject();
        writer.WriteNumber(VersionProperty, CurrentVersion);
        writer.WritePropertyName(GeneratedAtUtcProperty);
        JsonSerializer.Serialize(writer, DateTimeOffset.UtcNow, JsonOptions);

        await WriteArrayAsync(
            writer,
            ItemsProperty,
            PageAsync(cursor => ContentItemPage(dbContext, cursor, itemPageSize), itemPageSize, cancellationToken),
            cancellationToken
        ).ConfigureAwait(false);

        await WriteArrayAsync(
            writer,
            NzbFilesProperty,
            PageFileRowsAsync(
                cursor => NzbFileIdPage(dbContext, cursor, filePageSize),
                chunk => dbContext.NzbFiles.AsNoTracking().Where(x => chunk.Contains(x.Id)),
                filePageSize,
                fileChunkSize,
                cancellationToken
            ),
            cancellationToken
        ).ConfigureAwait(false);

        await WriteArrayAsync(
            writer,
            RarFilesProperty,
            PageFileRowsAsync(
                cursor => RarFileIdPage(dbContext, cursor, filePageSize),
                chunk => dbContext.RarFiles.AsNoTracking().Where(x => chunk.Contains(x.Id)),
                filePageSize,
                fileChunkSize,
                cancellationToken
            ),
            cancellationToken
        ).ConfigureAwait(false);

        await WriteArrayAsync(
            writer,
            MultipartFilesProperty,
            PageFileRowsAsync(
                cursor => MultipartFileIdPage(dbContext, cursor, filePageSize),
                chunk => dbContext.MultipartFiles.AsNoTracking().Where(x => chunk.Contains(x.Id)),
                filePageSize,
                fileChunkSize,
                cancellationToken
            ),
            cancellationToken
        ).ConfigureAwait(false);

        writer.WriteEndObject();
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteArrayAsync<T>
    (
        Utf8JsonWriter writer,
        string propertyName,
        IAsyncEnumerable<T> rows,
        CancellationToken cancellationToken
    )
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();
        await foreach (var row in rows.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            JsonSerializer.Serialize(writer, row, JsonOptions);

            // Hand the bytes to the file as we go. Without this the writer's own
            // buffer would grow to hold the whole document.
            if (writer.BytesPending >= WriterFlushThresholdBytes)
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        writer.WriteEndArray();
    }

    // Keyset pagination on DavItem.Path. Path is unique -- (ParentId, Name) is a
    // unique index and Path is the concatenation of the two -- and unlike Guid it
    // orders identically in SQLite and Postgres, so it is a complete sort key on
    // both backends. It also guarantees parents are written before their children,
    // because a parent path is always a strict prefix of its children's paths.
    private static IQueryable<PagedRow<DavItem>> ContentItemPage(DavDatabaseContext dbContext, string cursor, int pageSize)
    {
        return dbContext.Items
            .AsNoTracking()
            .Where(x => x.Path.StartsWith("/content/"))
            .Where(x => cursor == "" || string.Compare(x.Path, cursor) > 0)
            .OrderBy(x => x.Path)
            .Select(x => new PagedRow<DavItem> { Cursor = x.Path, Row = x })
            .Take(pageSize);
    }

    // Phase one of the file paging: the ids of the file rows under /content/, in
    // the same item order. Joining to DavItems keeps the /content/ filter out of a
    // Contains() over every content item id, which past SQLite's 32766 variable
    // cap fails outright.
    private static IQueryable<PagedRow<Guid>> NzbFileIdPage(DavDatabaseContext dbContext, string cursor, int pageSize)
    {
        return (from file in dbContext.NzbFiles
                join item in dbContext.Items on file.Id equals item.Id
                where item.Path.StartsWith("/content/")
                where cursor == "" || string.Compare(item.Path, cursor) > 0
                orderby item.Path
                select new PagedRow<Guid> { Cursor = item.Path, Row = file.Id })
            .AsNoTracking()
            .Take(pageSize);
    }

    private static IQueryable<PagedRow<Guid>> RarFileIdPage(DavDatabaseContext dbContext, string cursor, int pageSize)
    {
        return (from file in dbContext.RarFiles
                join item in dbContext.Items on file.Id equals item.Id
                where item.Path.StartsWith("/content/")
                where cursor == "" || string.Compare(item.Path, cursor) > 0
                orderby item.Path
                select new PagedRow<Guid> { Cursor = item.Path, Row = file.Id })
            .AsNoTracking()
            .Take(pageSize);
    }

    private static IQueryable<PagedRow<Guid>> MultipartFileIdPage(DavDatabaseContext dbContext, string cursor, int pageSize)
    {
        return (from file in dbContext.MultipartFiles
                join item in dbContext.Items on file.Id equals item.Id
                where item.Path.StartsWith("/content/")
                where cursor == "" || string.Compare(item.Path, cursor) > 0
                orderby item.Path
                select new PagedRow<Guid> { Cursor = item.Path, Row = file.Id })
            .AsNoTracking()
            .Take(pageSize);
    }

    // Phase two: materialize at most chunkSize file rows -- and therefore at most
    // chunkSize SegmentIds arrays -- at a time, by primary key.
    private static async IAsyncEnumerable<T> PageFileRowsAsync<T>
    (
        Func<string, IQueryable<PagedRow<Guid>>> idPageQuery,
        Func<Guid[], IQueryable<T>> rowQuery,
        int pageSize,
        int chunkSize,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var cursor = string.Empty;
        while (true)
        {
            var page = await idPageQuery(cursor).ToListAsync(cancellationToken).ConfigureAwait(false);
            if (page.Count == 0) yield break;

            foreach (var chunk in page.Select(x => x.Row).Chunk(chunkSize))
            {
                var rows = await rowQuery(chunk).ToListAsync(cancellationToken).ConfigureAwait(false);
                foreach (var row in rows)
                    yield return row;
            }

            cursor = page[^1].Cursor;
            if (page.Count < pageSize) yield break;
        }
    }

    private static async IAsyncEnumerable<T> PageAsync<T>
    (
        Func<string, IQueryable<PagedRow<T>>> pageQuery,
        int pageSize,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var cursor = string.Empty;
        while (true)
        {
            var page = await pageQuery(cursor).ToListAsync(cancellationToken).ConfigureAwait(false);
            if (page.Count == 0) yield break;

            foreach (var row in page)
                yield return row.Row;

            cursor = page[^1].Cursor;
            if (page.Count < pageSize) yield break;
        }
    }

    /// <summary>
    /// Reads the snapshot header and validates it, without ever holding the
    /// document -- or any SegmentIds -- in memory. The returned summary carries
    /// only the per-item parent/type data recovery needs; the rows themselves are
    /// streamed separately through <see cref="ReadItemsAsync"/> and friends.
    /// </summary>
    public static async Task<SnapshotReadResult> ReadAsync(CancellationToken cancellationToken)
    {
        await Mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var warnings = new List<string>();
            var primaryResult = await TryReadPathAsync(SnapshotFilePath, cancellationToken).ConfigureAwait(false);
            if (primaryResult.Summary != null)
                return primaryResult;
            if (primaryResult.Warning != null)
                warnings.Add(primaryResult.Warning);

            var backupResult = await TryReadPathAsync(BackupSnapshotFilePath, cancellationToken).ConfigureAwait(false);
            if (backupResult.Summary != null)
            {
                warnings.AddRange(backupResult.Warnings);
                return new SnapshotReadResult
                {
                    Summary = backupResult.Summary,
                    SourcePath = backupResult.SourcePath,
                    Warnings = warnings,
                };
            }

            if (backupResult.Warning != null)
                warnings.Add(backupResult.Warning);

            return new SnapshotReadResult { Warnings = warnings };
        }
        finally
        {
            Mutex.Release();
        }
    }

    public static IAsyncEnumerable<DavItem> ReadItemsAsync(string path, CancellationToken cancellationToken)
    {
        return ReadArrayAsync<DavItem>(path, ItemsProperty, cancellationToken);
    }

    public static IAsyncEnumerable<DavNzbFile> ReadNzbFilesAsync(string path, CancellationToken cancellationToken)
    {
        return ReadArrayAsync<DavNzbFile>(path, NzbFilesProperty, cancellationToken);
    }

    public static IAsyncEnumerable<DavRarFile> ReadRarFilesAsync(string path, CancellationToken cancellationToken)
    {
        return ReadArrayAsync<DavRarFile>(path, RarFilesProperty, cancellationToken);
    }

    public static IAsyncEnumerable<DavMultipartFile> ReadMultipartFilesAsync(string path, CancellationToken cancellationToken)
    {
        return ReadArrayAsync<DavMultipartFile>(path, MultipartFilesProperty, cancellationToken);
    }

    private static async IAsyncEnumerable<T> ReadArrayAsync<T>
    (
        string path,
        string propertyName,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        // Held for the duration of the enumeration so the atomic replace in
        // WriteAsync cannot swap the file out from under an open reader.
        await Mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var stream = File.OpenRead(path);
            await foreach (var row in ReadArrayAsync<T>(stream, propertyName, cancellationToken).ConfigureAwait(false))
                yield return row;
        }
        finally
        {
            Mutex.Release();
        }
    }

    internal static async IAsyncEnumerable<T> ReadArrayAsync<T>
    (
        Stream stream,
        string propertyName,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        using var reader = new JsonStreamReader(stream);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) yield break;
        if (reader.TokenType == JsonTokenType.Null) yield break;
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("The snapshot root is not a JSON object.");

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.TokenType == JsonTokenType.EndObject) yield break;
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("Unexpected token in the snapshot root object.");

            if (!string.Equals(reader.PropertyName, propertyName, StringComparison.Ordinal))
            {
                await reader.SkipValueAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                || reader.TokenType != JsonTokenType.StartArray)
                throw new JsonException($"Snapshot property '{propertyName}' is not an array.");

            while (true)
            {
                var element = await reader.ReadArrayElementAsync<T>(cancellationToken).ConfigureAwait(false);
                if (!element.HasValue) yield break;
                if (element.Value != null) yield return element.Value;
            }
        }
    }

    private static async Task<SnapshotReadResult> TryReadPathAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return new SnapshotReadResult();

        try
        {
            await using var stream = File.OpenRead(path);
            var scan = await ScanAsync(stream, cancellationToken).ConfigureAwait(false);

            if (scan == null)
            {
                return new SnapshotReadResult
                {
                    Warning = $"Ignored /content recovery snapshot at '{path}' because it deserialized to null."
                };
            }

            if (!TryValidate(scan, out var validationError))
            {
                return new SnapshotReadResult
                {
                    Warning = $"Ignored /content recovery snapshot at '{path}': {validationError}"
                };
            }

            return new SnapshotReadResult
            {
                Summary = scan.ToSummary(),
                SourcePath = path,
            };
        }
        catch (Exception ex)
        {
            return new SnapshotReadResult
            {
                Warning = $"Ignored /content recovery snapshot at '{path}': {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Single streaming pass over the snapshot that keeps only the data needed to
    /// validate referential integrity: per-item parent/type, and the ids of the
    /// file tables. SegmentIds are parsed and discarded, never accumulated.
    /// </summary>
    private static async Task<SnapshotScan?> ScanAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new JsonStreamReader(stream);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new JsonException("The snapshot is empty.");
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("The snapshot root is not a JSON object.");

        var scan = new SnapshotScan();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.TokenType == JsonTokenType.EndObject) break;
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("Unexpected token in the snapshot root object.");

            switch (reader.PropertyName)
            {
                case VersionProperty:
                    scan.Version = await reader.ReadValueAsync<int>(cancellationToken).ConfigureAwait(false);
                    // An unsupported snapshot is rejected outright, so there is no
                    // point streaming the rest of a multi-gigabyte file.
                    if (scan.Version != CurrentVersion) return scan;
                    break;

                case GeneratedAtUtcProperty:
                    scan.GeneratedAtUtc = await reader.ReadValueAsync<DateTimeOffset>(cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case ItemsProperty:
                    await ScanArrayAsync<DavItem>(reader, scan.AddItem, cancellationToken).ConfigureAwait(false);
                    break;

                case NzbFilesProperty:
                    await ScanArrayAsync<IdOnly>(reader, x => scan.AddNzbFile(x.Id), cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case RarFilesProperty:
                    await ScanArrayAsync<IdOnly>(reader, x => scan.AddRarFile(x.Id), cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case MultipartFilesProperty:
                    await ScanArrayAsync<IdOnly>(reader, x => scan.AddMultipartFile(x.Id), cancellationToken)
                        .ConfigureAwait(false);
                    break;

                default:
                    await reader.SkipValueAsync(cancellationToken).ConfigureAwait(false);
                    break;
            }
        }

        return scan;
    }

    private static async Task ScanArrayAsync<T>
    (
        JsonStreamReader reader,
        Action<T> onElement,
        CancellationToken cancellationToken
    )
    {
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            || reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("Expected a JSON array in the snapshot.");

        while (true)
        {
            var element = await reader.ReadArrayElementAsync<T>(cancellationToken).ConfigureAwait(false);
            if (!element.HasValue) return;
            if (element.Value != null) onElement(element.Value);
        }
    }

    private static bool TryValidate(SnapshotScan scan, out string error)
    {
        if (scan.Version != CurrentVersion)
        {
            error = $"unsupported version {scan.Version}";
            return false;
        }

        if (scan.DuplicateItemId != null)
        {
            error = $"duplicate DavItem id {scan.DuplicateItemId}";
            return false;
        }

        if (scan.NonContentPathError != null)
        {
            error = scan.NonContentPathError;
            return false;
        }

        foreach (var (id, item) in scan.ItemsById)
        {
            if (item.ParentId != DavItem.ContentFolder.Id && !scan.ItemsById.ContainsKey(item.ParentId!.Value))
            {
                error = $"item '{id}' references missing parent '{item.ParentId}'";
                return false;
            }
        }

        if (scan.HasDuplicateNzbFileId)
        {
            error = "duplicate DavNzbFile ids";
            return false;
        }

        if (scan.HasDuplicateRarFileId)
        {
            error = "duplicate DavRarFile ids";
            return false;
        }

        if (scan.HasDuplicateMultipartFileId)
        {
            error = "duplicate DavMultipartFile ids";
            return false;
        }

        foreach (var (id, item) in scan.ItemsById)
        {
            var hasRequiredMetadata = item.Type switch
            {
                DavItem.ItemType.Directory => true,
                DavItem.ItemType.NzbFile => scan.NzbFileIds.Contains(id),
                DavItem.ItemType.RarFile => scan.RarFileIds.Contains(id),
                DavItem.ItemType.MultipartFile => scan.MultipartFileIds.Contains(id),
                _ => false
            };

            if (!hasRequiredMetadata)
            {
                error = $"item '{id}' is missing required metadata";
                return false;
            }
        }

        if (scan.NzbFileIds.Any(x => !scan.ItemsById.TryGetValue(x, out var item) || item.Type != DavItem.ItemType.NzbFile))
        {
            error = "snapshot contains DavNzbFile rows without matching NzbFile items";
            return false;
        }

        if (scan.RarFileIds.Any(x => !scan.ItemsById.TryGetValue(x, out var item) || item.Type != DavItem.ItemType.RarFile))
        {
            error = "snapshot contains DavRarFile rows without matching RarFile items";
            return false;
        }

        if (scan.MultipartFileIds.Any(x => !scan.ItemsById.TryGetValue(x, out var item) || item.Type != DavItem.ItemType.MultipartFile))
        {
            error = "snapshot contains DavMultipartFile rows without matching MultipartFile items";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private sealed class PagedRow<T>
    {
        public string Cursor { get; set; } = string.Empty;
        public T Row { get; set; } = default!;
    }

    // Binds only the id of a file row. System.Text.Json skips the unmatched
    // SegmentIds/RarParts/Metadata payload instead of materializing it.
    private sealed class IdOnly
    {
        public Guid Id { get; set; }
    }

    private sealed class SnapshotScan
    {
        public int Version { get; set; }
        public DateTimeOffset GeneratedAtUtc { get; set; }
        public Dictionary<Guid, SnapshotItem> ItemsById { get; } = new();
        public HashSet<Guid> NzbFileIds { get; } = [];
        public HashSet<Guid> RarFileIds { get; } = [];
        public HashSet<Guid> MultipartFileIds { get; } = [];
        public Guid? DuplicateItemId { get; private set; }
        public string? NonContentPathError { get; private set; }
        public bool HasDuplicateNzbFileId { get; private set; }
        public bool HasDuplicateRarFileId { get; private set; }
        public bool HasDuplicateMultipartFileId { get; private set; }

        public void AddItem(DavItem item)
        {
            if (!ItemsById.TryAdd(item.Id, new SnapshotItem(item.ParentId, item.Type)))
                DuplicateItemId ??= item.Id;

            if (NonContentPathError == null && !item.Path.StartsWith("/content/", StringComparison.Ordinal))
                NonContentPathError = $"item '{item.Id}' has non-content path '{item.Path}'";
        }

        public void AddNzbFile(Guid id)
        {
            if (!NzbFileIds.Add(id)) HasDuplicateNzbFileId = true;
        }

        public void AddRarFile(Guid id)
        {
            if (!RarFileIds.Add(id)) HasDuplicateRarFileId = true;
        }

        public void AddMultipartFile(Guid id)
        {
            if (!MultipartFileIds.Add(id)) HasDuplicateMultipartFileId = true;
        }

        public ContentIndexSnapshotSummary ToSummary()
        {
            return new ContentIndexSnapshotSummary
            {
                Version = Version,
                GeneratedAtUtc = GeneratedAtUtc,
                ItemsById = ItemsById,
                NzbFileCount = NzbFileIds.Count,
                RarFileCount = RarFileIds.Count,
                MultipartFileCount = MultipartFileIds.Count,
            };
        }
    }

    public sealed class SnapshotReadResult
    {
        public ContentIndexSnapshotSummary? Summary { get; init; }
        public string? SourcePath { get; init; }
        public string? Warning { get; init; }
        public List<string> Warnings { get; init; } = [];
    }

    /// <summary>
    /// The cheap part of a snapshot: enough to plan a recovery, small enough to
    /// hold for a library of any size. Roughly 40 bytes per item, and crucially
    /// nothing that scales with the number of usenet segments.
    /// </summary>
    public sealed class ContentIndexSnapshotSummary
    {
        public int Version { get; init; }
        public DateTimeOffset GeneratedAtUtc { get; init; }
        public Dictionary<Guid, SnapshotItem> ItemsById { get; init; } = new();
        public int ItemCount => ItemsById.Count;
        public int NzbFileCount { get; init; }
        public int RarFileCount { get; init; }
        public int MultipartFileCount { get; init; }
    }

    public readonly record struct SnapshotItem(Guid? ParentId, DavItem.ItemType Type);

    /// <summary>
    /// Pull-based JSON reader over a stream. Only the bytes of the value being
    /// read are buffered, so the resident set is bounded by the largest single
    /// row rather than by the document.
    /// </summary>
    private sealed class JsonStreamReader(Stream stream, int initialBufferSize = 32 * 1024) : IDisposable
    {
        private byte[] _buffer = ArrayPool<byte>.Shared.Rent(initialBufferSize);
        private int _start;
        private int _length;
        private JsonReaderState _state = new();
        private bool _isFinalBlock;

        public JsonTokenType TokenType { get; private set; } = JsonTokenType.None;
        public string? PropertyName { get; private set; }

        public async ValueTask<bool> ReadAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                if (TryReadToken(out var read)) return read;
                await FillAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public async ValueTask<T?> ReadValueAsync<T>(CancellationToken cancellationToken)
        {
            var element = await ReadElementAsync<T>(stopOnEndArray: false, cancellationToken).ConfigureAwait(false);
            return element.Value;
        }

        public ValueTask<ElementResult<T>> ReadArrayElementAsync<T>(CancellationToken cancellationToken)
        {
            return ReadElementAsync<T>(stopOnEndArray: true, cancellationToken);
        }

        public async ValueTask SkipValueAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                if (TrySkipValue()) return;
                await FillAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private async ValueTask<ElementResult<T>> ReadElementAsync<T>(bool stopOnEndArray, CancellationToken cancellationToken)
        {
            while (true)
            {
                if (TryReadElement<T>(stopOnEndArray, out var result)) return result;
                await FillAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private bool TryReadToken(out bool read)
        {
            var reader = new Utf8JsonReader(_buffer.AsSpan(_start, _length), _isFinalBlock, _state);
            if (!reader.Read())
            {
                read = false;
                return _isFinalBlock;
            }

            TokenType = reader.TokenType;
            PropertyName = reader.TokenType == JsonTokenType.PropertyName ? reader.GetString() : null;
            Commit(ref reader);
            read = true;
            return true;
        }

        private bool TryReadElement<T>(bool stopOnEndArray, out ElementResult<T> result)
        {
            result = default;
            var reader = new Utf8JsonReader(_buffer.AsSpan(_start, _length), _isFinalBlock, _state);
            if (!reader.Read()) return false;

            if (stopOnEndArray && reader.TokenType == JsonTokenType.EndArray)
            {
                TokenType = reader.TokenType;
                PropertyName = null;
                Commit(ref reader);
                result = new ElementResult<T>(false, default);
                return true;
            }

            var valueStart = (int)reader.TokenStartIndex;
            if (!reader.TrySkip()) return false;

            var valueEnd = (int)reader.BytesConsumed;
            var value = JsonSerializer.Deserialize<T>(
                _buffer.AsSpan(_start + valueStart, valueEnd - valueStart),
                JsonOptions
            );

            TokenType = reader.TokenType;
            PropertyName = null;
            Commit(ref reader);
            result = new ElementResult<T>(true, value);
            return true;
        }

        private bool TrySkipValue()
        {
            var reader = new Utf8JsonReader(_buffer.AsSpan(_start, _length), _isFinalBlock, _state);
            if (!reader.Read()) return false;
            if (!reader.TrySkip()) return false;

            TokenType = reader.TokenType;
            PropertyName = null;
            Commit(ref reader);
            return true;
        }

        private void Commit(ref Utf8JsonReader reader)
        {
            var consumed = (int)reader.BytesConsumed;
            _start += consumed;
            _length -= consumed;
            _state = reader.CurrentState;
        }

        private async ValueTask FillAsync(CancellationToken cancellationToken)
        {
            if (_isFinalBlock)
                throw new JsonException("The snapshot ended in the middle of a JSON value.");

            if (_start > 0)
            {
                Array.Copy(_buffer, _start, _buffer, 0, _length);
                _start = 0;
            }

            if (_length == _buffer.Length)
            {
                var grown = ArrayPool<byte>.Shared.Rent(_buffer.Length * 2);
                Array.Copy(_buffer, grown, _length);
                ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = grown;
            }

            var read = await stream.ReadAsync(_buffer.AsMemory(_length), cancellationToken).ConfigureAwait(false);
            if (read == 0) _isFinalBlock = true;
            else _length += read;
        }

        public void Dispose()
        {
            var buffer = _buffer;
            _buffer = [];
            if (buffer.Length > 0) ArrayPool<byte>.Shared.Return(buffer);
        }

        public readonly record struct ElementResult<T>(bool HasValue, T? Value);
    }
}
