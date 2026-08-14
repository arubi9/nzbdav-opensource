using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Api.Controllers.Manifest;
using NzbWebDAV.Clients.Usenet.Caching;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Api.Controllers.Manifest;

/// <summary>
/// The manifest used to refuse any library larger than 10,000 items, and it refused it
/// by failing the whole request, so one oversized library stopped the Jellyfin sync
/// entirely. These cover the paged replacement and, just as importantly, that an
/// unpaged caller still sees exactly the old behaviour.
/// </summary>
[Collection(nameof(backend.Tests.Services.ConfigEncryptionDatabaseCollection))]
public sealed class ManifestPaginationTests
{
    private const int PageSize = 10_000;

    private readonly backend.Tests.Services.ConfigEncryptionDatabaseFixture _fixture;

    public ManifestPaginationTests(backend.Tests.Services.ConfigEncryptionDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task PagedWalkReturnsEveryItemExactlyOnceAcrossPages()
    {
        const int total = PageSize + 500;
        var (context, cacheDir) = await SeedItemsAsync(ctx => SeedBulk(ctx, total, pathPadding: 0));

        await using (context)
        using (var cache = new LiveSegmentCache(cacheDir))
        {
            var (items, pages) = await WalkAsync(context, cache);

            Assert.True(pages > 1, $"expected the walk to span multiple pages but it took {pages}");

            // The seeded tree is the bulk files plus their parent directory.
            Assert.Equal(total + 1, items.Count);
            Assert.Equal(items.Count, items.Select(x => x.Id).Distinct().Count());
            Assert.Equal(items.Count, items.Select(x => x.Path).Distinct().Count());

            // Not Select(BulkPath): that binds the (element, index) overload and feeds
            // the index in as padding.
            var expected = Enumerable.Range(0, total).Select(i => BulkPath(i)).ToHashSet(StringComparer.Ordinal);
            expected.Add("/content/bulk");
            Assert.Equal(expected, items.Select(x => x.Path).ToHashSet(StringComparer.Ordinal));
        }
    }

    /// <summary>
    /// The other bound that ends a page. With long paths the accumulated string budget
    /// runs out well before the item count does, so this exercises a different break
    /// path than the test above.
    /// </summary>
    [Fact]
    public async Task PagesAlsoBreakOnAccumulatedStringDataWithoutFailing()
    {
        const int total = 6_000;
        var (context, cacheDir) = await SeedItemsAsync(ctx => SeedBulk(ctx, total, pathPadding: 600));

        await using (context)
        using (var cache = new LiveSegmentCache(cacheDir))
        {
            var (items, pages) = await WalkAsync(context, cache);

            Assert.True(pages > 1, "long paths should have exhausted the string budget before the item count");
            Assert.All(items, item => Assert.True(item.Path.Length <= 1024));
            Assert.Equal(total + 1, items.Count);
            Assert.Equal(items.Count, items.Select(x => x.Path).Distinct().Count());
        }
    }

    /// <summary>
    /// The cursor has to be the last path in database order, but each page is sorted
    /// ordinally before it is serialized. Those two orders are not the same: SQLite
    /// compares UTF-8 bytes while .NET compares UTF-16 code units, so a supplementary
    /// plane character (encoded as a surrogate pair, first unit 0xD800) sorts *below*
    /// U+FF01 in .NET and *above* it in the database.
    ///
    /// The seed places that pair at the end of the first page, so reading the cursor off
    /// the sorted page hands back a path from the middle of the tree and the next page
    /// re-serves rows the caller already has. Postgres, whose collation is locale-driven,
    /// diverges from ordinal far more broadly than this.
    /// </summary>
    [Fact]
    public async Task CursorFollowsDatabaseOrderWhenItDisagreesWithOrdinalOrder()
    {
        var (context, cacheDir) = await SeedItemsAsync(SeedCollationDivergentTree);

        await using (context)
        using (var cache = new LiveSegmentCache(cacheDir))
        {
            var (items, pages) = await WalkAsync(context, cache);

            Assert.True(pages > 1, "the seed must span more than one page for the cursor to matter");

            var duplicates = items.GroupBy(x => x.Path, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToArray();
            Assert.Empty(duplicates);

            var expected = context.Items
                .Where(x => x.Path.StartsWith("/content/"))
                .Select(x => x.Path)
                .ToHashSet(StringComparer.Ordinal);
            Assert.Equal(expected, items.Select(x => x.Path).ToHashSet(StringComparer.Ordinal));
        }
    }

    /// <summary>
    /// A plugin that predates paging ignores nextCursor, so serving it a first page
    /// would look like every later item had been deleted and get them quarantined.
    /// Failing closed is the only safe answer for that caller.
    /// </summary>
    [Fact]
    public async Task UnpagedRequestStillFailsClosedBeyondTheItemCeiling()
    {
        var (context, cacheDir) = await SeedItemsAsync(ctx => SeedBulk(ctx, PageSize + 500, pathPadding: 0));

        await using (context)
        using (var cache = new LiveSegmentCache(cacheDir))
        {
            var result = await InvokeAsync(context, cache, paged: false);
            var status = Assert.IsType<ObjectResult>(result.Result);
            Assert.Equal(StatusCodes.Status413PayloadTooLarge, status.StatusCode);
        }
    }

    /// <summary>
    /// Existing clients cache an ETag computed over the response bytes, so an unpaged
    /// response must not gain fields just because paging exists.
    /// </summary>
    [Fact]
    public async Task UnpagedResponseBytesDoNotGainPagingFields()
    {
        var (context, cacheDir) = await SeedItemsAsync(ctx => SeedBulk(ctx, 3, pathPadding: 0));

        await using (context)
        using (var cache = new LiveSegmentCache(cacheDir))
        {
            var result = await InvokeAsync(context, cache, paged: false);
            var file = Assert.IsType<FileContentResult>(result.Result);

            using var document = JsonDocument.Parse(file.FileContents);
            Assert.False(document.RootElement.TryGetProperty("nextCursor", out _));
            Assert.False(document.RootElement.TryGetProperty("version", out _));
        }
    }

    [Fact]
    public async Task PagedFirstPageRevalidatesAgainstTheContentVersion()
    {
        var (context, cacheDir) = await SeedItemsAsync(ctx => SeedBulk(ctx, 3, pathPadding: 0));

        await using (context)
        using (var cache = new LiveSegmentCache(cacheDir))
        {
            var first = await InvokeAsync(context, cache, paged: true);
            var etag = first.Context.Response.Headers.ETag.ToString();
            Assert.False(string.IsNullOrWhiteSpace(etag));

            var repeat = new DefaultHttpContext();
            repeat.Request.Headers["If-None-Match"] = etag;
            var second = await InvokeAsync(context, cache, paged: true, requestContext: repeat);

            Assert.IsType<EmptyResult>(second.Result);
            Assert.Equal(StatusCodes.Status304NotModified, second.Context.Response.StatusCode);

            // A probe sidecar appearing changes the manifest without touching the
            // database, which is exactly the case a database-only version would miss.
            ManifestVersion.Bump();

            var afterChange = new DefaultHttpContext();
            afterChange.Request.Headers["If-None-Match"] = etag;
            var third = await InvokeAsync(context, cache, paged: true, requestContext: afterChange);
            Assert.IsType<FileContentResult>(third.Result);
        }
    }

    /// <summary>
    /// A 304 on a later page would answer "the tree is unchanged" to a question about
    /// one slice of it, leaving the caller holding a truncated walk it believes is whole.
    /// </summary>
    [Fact]
    public async Task LaterPagesRefuseToRevalidate()
    {
        var (context, cacheDir) = await SeedItemsAsync(ctx => SeedBulk(ctx, PageSize + 500, pathPadding: 0));

        await using (context)
        using (var cache = new LiveSegmentCache(cacheDir))
        {
            var first = await InvokeAsync(context, cache, paged: true);
            var etag = first.Context.Response.Headers.ETag.ToString();
            var cursor = ReadManifest(first.Result).NextCursor;
            Assert.False(string.IsNullOrEmpty(cursor));

            var conditional = new DefaultHttpContext();
            conditional.Request.Headers["If-None-Match"] = etag;
            var second = await InvokeAsync(context, cache, paged: true, after: cursor, requestContext: conditional);

            Assert.IsType<FileContentResult>(second.Result);
            Assert.NotEqual(StatusCodes.Status304NotModified, second.Context.Response.StatusCode);
        }
    }

    [Theory]
    [InlineData("not-base64!!")]
    [InlineData("")]                                  // decodes to an empty path
    [InlineData("L2V0Yy9wYXNzd2Q=")]                  // /etc/passwd — outside /content/
    public async Task MalformedCursorsAreRejected(string cursor)
    {
        var (context, cacheDir) = await SeedItemsAsync(ctx => SeedBulk(ctx, 3, pathPadding: 0));

        await using (context)
        using (var cache = new LiveSegmentCache(cacheDir))
        {
            var result = await InvokeAsync(context, cache, paged: true, after: cursor);

            // An empty cursor is indistinguishable from "no cursor" on the wire, so it
            // is served as a first page rather than rejected.
            if (cursor.Length == 0)
            {
                Assert.IsType<FileContentResult>(result.Result);
                return;
            }

            var status = Assert.IsType<ObjectResult>(result.Result);
            Assert.Equal(StatusCodes.Status422UnprocessableEntity, status.StatusCode);
        }
    }

    [Fact]
    public async Task CursorsAreRejectedWithoutTheOptIn()
    {
        var (context, cacheDir) = await SeedItemsAsync(ctx => SeedBulk(ctx, 3, pathPadding: 0));

        await using (context)
        using (var cache = new LiveSegmentCache(cacheDir))
        {
            var result = await InvokeAsync(context, cache, paged: false, after: "L2NvbnRlbnQvYnVsaw==");
            var status = Assert.IsType<ObjectResult>(result.Result);
            Assert.Equal(StatusCodes.Status422UnprocessableEntity, status.StatusCode);
        }
    }

    private static async Task<(List<ManifestItem> Items, int Pages)> WalkAsync(
        DavDatabaseContext context, LiveSegmentCache cache)
    {
        var items = new List<ManifestItem>();
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        string? version = null;
        var pages = 0;

        while (true)
        {
            var result = await InvokeAsync(context, cache, paged: true, after: cursor);
            var page = ReadManifest(result.Result);
            pages++;

            Assert.False(string.IsNullOrEmpty(page.Version));
            version ??= page.Version;
            Assert.Equal(version, page.Version);

            items.AddRange(page.Items);
            cursor = page.NextCursor;
            if (string.IsNullOrEmpty(cursor)) break;

            Assert.True(seenCursors.Add(cursor), "the walk revisited a cursor and would not terminate");
            Assert.True(pages < 100, "the walk did not terminate");
        }

        return (items, pages);
    }

    private static ManifestResponse ReadManifest(IActionResult result)
    {
        var file = Assert.IsType<FileContentResult>(result);
        var manifest = JsonSerializer.Deserialize<ManifestResponse>(
            file.FileContents, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(manifest);
        return manifest!;
    }

    private static async Task<ManifestControllerResult> InvokeAsync(
        DavDatabaseContext context,
        LiveSegmentCache cache,
        bool paged,
        string? after = null,
        DefaultHttpContext? requestContext = null)
    {
        var request = requestContext ?? new DefaultHttpContext();
        var controller = new ManifestController(new DavDatabaseClient(context), cache)
        {
            ControllerContext = new ControllerContext { HttpContext = request }
        };

        var result = await controller.GetManifest(paged, after, CancellationToken.None);
        return new ManifestControllerResult(result, request);
    }

    private async Task<(DavDatabaseContext Context, string CacheDir)> SeedItemsAsync(Action<DavDatabaseContext> seed)
    {
        await _fixture.ResetAsync();
        _fixture.SetKeys(_fixture.CreateKey(), null);

        var context = await _fixture.CreateMigratedContextAsync();
        seed(context);
        await context.SaveChangesAsync();

        var cacheDirectory = Path.Combine(Path.GetTempPath(), "nzbdav-manifest-paging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cacheDirectory);
        return (context, cacheDirectory);
    }

    // Padding goes in a directory segment, not the filename: names are capped at 255
    // characters and an over-long one is rejected as invalid rather than paged.
    private static string BulkPath(int index, int pathPadding = 0)
    {
        var padding = pathPadding == 0 ? string.Empty : new string('x', pathPadding) + "/";
        return $"/content/bulk/{padding}file-{index:D6}.mkv";
    }

    private static void SeedBulk(DavDatabaseContext context, int count, int pathPadding)
    {
        var parent = CreateItem(
            BuildId(0),
            Guid.Parse("00000000-0000-0000-0000-000000000002"),
            "bulk",
            DavItem.ItemType.Directory,
            "/content/bulk");
        context.Items.Add(parent);

        for (var i = 0; i < count; i++)
        {
            var path = BulkPath(i, pathPadding);
            context.Items.Add(CreateItem(
                BuildId(i + 1),
                parent.Id,
                Path.GetFileName(path),
                DavItem.ItemType.NzbFile,
                path,
                1024));
        }
    }

    /// <summary>
    /// Lays out exactly one full page whose database-order last entry is not its
    /// ordinal-order last entry. In UTF-8 byte order: ASCII &lt; U+FF01 &lt; U+10000.
    /// In .NET ordinal order U+10000 comes first of those two, because its leading
    /// UTF-16 code unit is a surrogate.
    /// </summary>
    private static void SeedCollationDivergentTree(DavDatabaseContext context)
    {
        const string fullWidth = "\uFF01";              // U+FF01, UTF-8 EF BC 81
        const string supplementary = "\U00010000";      // UTF-8 F0 90 80 80
        const string beyond = "\U00010100";             // sorts after both in UTF-8

        var parent = CreateItem(
            BuildId(0),
            Guid.Parse("00000000-0000-0000-0000-000000000002"),
            "bulk",
            DavItem.ItemType.Directory,
            "/content/bulk");
        context.Items.Add(parent);

        void Add(int id, string path) => context.Items.Add(
            CreateItem(BuildId(id), parent.Id, Path.GetFileName(path), DavItem.ItemType.NzbFile, path, 1024));

        // The parent directory occupies the first slot of the page.
        var filler = PageSize - 3;
        for (var i = 0; i < filler; i++) Add(i + 1, BulkPath(i));

        // Positions 9,999 and 10,000: ordinal order swaps this pair.
        Add(filler + 1, $"/content/bulk/{fullWidth}.mkv");
        Add(filler + 2, $"/content/bulk/{supplementary}.mkv");

        // Spill onto a second page so a wrong cursor has somewhere to go wrong.
        for (var i = 0; i < 200; i++)
            Add(filler + 3 + i, $"/content/bulk/{beyond}-{i:D4}.mkv");
    }

    // Deterministic and collision-free, which matters because the walk asserts on
    // distinct ids across pages.
    private static Guid BuildId(int index) => new($"44444444-4444-4444-4444-{index:D12}");

    private static DavItem CreateItem(
        Guid id, Guid? parentId, string name, DavItem.ItemType type, string path, long? fileSize = null)
        => new()
        {
            Id = id,
            IdPrefix = id.ToString("N")[..5],
            CreatedAt = new DateTime(2026, 4, 6, 12, 0, 0, DateTimeKind.Utc),
            ParentId = parentId,
            Name = name,
            FileSize = fileSize,
            Type = type,
            Path = path
        };
}
