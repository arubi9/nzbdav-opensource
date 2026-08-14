using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Api.Controllers.Manifest;
using NzbWebDAV.Clients.Usenet.Caching;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Api.Controllers.Manifest;

[Collection(nameof(backend.Tests.Services.ConfigEncryptionDatabaseCollection))]
public sealed class ManifestControllerTests
{
    private readonly backend.Tests.Services.ConfigEncryptionDatabaseFixture _fixture;

    public ManifestControllerTests(backend.Tests.Services.ConfigEncryptionDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetManifest_ReturnsStableETag_ForUnchangedManifest()
    {
        var (context, cacheDir) = await SeedItemsAsync(CreateBaseItems);

        await using (context)
        using (var liveSegmentCache = new LiveSegmentCache(cacheDir))
        {
            var first = await GetManifestAsync(context, liveSegmentCache);
            var firstResponse = Assert.IsType<FileContentResult>(first.Result);
            var manifest = JsonSerializer.Deserialize<ManifestResponse>(firstResponse.FileContents, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            Assert.NotNull(manifest);

            var firstEtag = first.Context.Response.Headers.ETag.ToString();
            Assert.False(string.IsNullOrWhiteSpace(firstEtag));
            Assert.Equal($"\"{Convert.ToHexString(SHA256.HashData(firstResponse.FileContents))}\"", firstEtag);
            Assert.Equal("/content/movies", manifest.Items[0].Path);
            Assert.Equal("directory", manifest.Items[0].Type);
            Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000002"), manifest.Items[0].ParentId);

            var requestContext = new DefaultHttpContext();
            requestContext.Request.Headers["If-None-Match"] = firstEtag;

            var second = await GetManifestAsync(context, liveSegmentCache, requestContext);
            Assert.IsType<EmptyResult>(second.Result);
            Assert.Equal(StatusCodes.Status304NotModified, second.Context.Response.StatusCode);
        }
    }

    [Fact]
    public async Task GetManifest_ETagChanges_WhenItemPathMoves()
    {
        var (context, cacheDir) = await SeedItemsAsync(CreateBaseItems);

        var item = context.Items.Single(i => i.Name == "A.mkv");
        item.Path = "/content/movies/B.mkv";
        item.Name = "B.mkv";
        await context.SaveChangesAsync();

        await using (context)
        using (var liveSegmentCache = new LiveSegmentCache(cacheDir))
        {
            var first = await GetManifestAsync(context, liveSegmentCache);
            var firstEtag = first.Context.Response.Headers.ETag.ToString();

            item.Path = "/content/movies/A.mkv";
            item.Name = "A.mkv";
            await context.SaveChangesAsync();

            var second = await GetManifestAsync(context, liveSegmentCache);
            var secondEtag = second.Context.Response.Headers.ETag.ToString();

            Assert.NotEqual(firstEtag, secondEtag);
        }
    }

    [Fact]
    public async Task GetManifest_ETagChanges_WhenItemNamesSwap()
    {
        var (context, cacheDir) = await SeedItemsAsync(CreateSwappedNameItems);

        await using (context)
        using (var liveSegmentCache = new LiveSegmentCache(cacheDir))
        {
            var first = await GetManifestAsync(context, liveSegmentCache);
            var firstEtag = first.Context.Response.Headers.ETag.ToString();

            var fileA = context.Items.Single(i => i.Name == "A.mkv");
            var fileB = context.Items.Single(i => i.Name == "B.mkv");

            fileA.Name = "_tmp.mkv";
            fileA.Path = "/content/media/_tmp.mkv";
            await context.SaveChangesAsync();

            fileB.Name = "A.mkv";
            fileB.Path = "/content/media/A.mkv";
            await context.SaveChangesAsync();

            fileA.Name = "B.mkv";
            fileA.Path = "/content/media/B.mkv";
            await context.SaveChangesAsync();

            var second = await GetManifestAsync(context, liveSegmentCache);
            var secondEtag = second.Context.Response.Headers.ETag.ToString();

            Assert.NotEqual(firstEtag, secondEtag);
        }
    }

    [Fact]
    public async Task GetManifest_ETagChanges_WhenParentOrTypeChanges()
    {
        var (context, cacheDir) = await SeedItemsAsync(CreateParentAndTypeItems);

        await using (context)
        using (var liveSegmentCache = new LiveSegmentCache(cacheDir))
        {
            var first = await GetManifestAsync(context, liveSegmentCache);
            var firstEtag = first.Context.Response.Headers.ETag.ToString();

            var file = context.Items.Single(i => i.Name == "episode.mkv");
            var season2 = context.Items.Single(i => i.Name == "season-2");
            context.Items.Remove(file);
            context.Items.Add(new DavItem
            {
                Id = file.Id,
                IdPrefix = file.IdPrefix,
                CreatedAt = file.CreatedAt,
                ParentId = season2.Id,
                Name = file.Name,
                FileSize = file.FileSize,
                Type = DavItem.ItemType.NzbFile,
                Path = "/content/series/season-2/episode.mkv"
            });
            await context.SaveChangesAsync();

            var second = await GetManifestAsync(context, liveSegmentCache);
            var secondEtag = second.Context.Response.Headers.ETag.ToString();

            Assert.NotEqual(firstEtag, secondEtag);
        }
    }

    [Fact]
    public async Task GetManifest_ReturnsTooLarge_WhenManifestItemCountExceedsLimit()
    {
        await _fixture.ResetAsync();
        _fixture.SetKeys(_fixture.CreateKey(), null);

        await using var context = await _fixture.CreateMigratedContextAsync();
        var cacheDir = CreateCacheDirectory();

        var items = new List<DavItem>(50_001);
        for (var i = 0; i < 50_001; i++)
        {
            items.Add(
                CreateItem(
                    Guid.NewGuid(),
                    null,
                    $"video-{i}.mkv",
                    DavItem.ItemType.MultipartFile,
                    $"/content/library/video-{i}.mkv",
                    fileSize: 1_024));        }

        context.Items.AddRange(items);
        await context.SaveChangesAsync();

        using var liveSegmentCache = new LiveSegmentCache(cacheDir);
        var result = await GetManifestAsync(context, liveSegmentCache);
        var response = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task GetManifest_ReturnsUnprocessableEntity_WhenStringFieldExceedsBound()
    {
        var (context, cacheDir) = await SeedItemsAsync(db => db.Items.Add(CreateItem(
            Guid.NewGuid(), null, new string('n', 256), DavItem.ItemType.MultipartFile,
            "/content/library/too-long.mkv")));

        await using (context)
        using (var liveSegmentCache = new LiveSegmentCache(cacheDir))
        {
            var result = await GetManifestAsync(context, liveSegmentCache);
            var response = Assert.IsType<ObjectResult>(result.Result);
            Assert.Equal(StatusCodes.Status422UnprocessableEntity, response.StatusCode);
        }
    }

    [Fact]
    public async Task GetManifest_ReturnsTooLarge_WhenMultibyteInputBudgetIsExceeded()
    {
        var (context, cacheDir) = await SeedItemsAsync(_ => { });
        var items = new List<DavItem>(6_000);
        var multibyteName = new string('\u0800', 255);
        for (var i = 0; i < 6_000; i++)
        {
            items.Add(CreateItem(Guid.NewGuid(), null, multibyteName,
                DavItem.ItemType.MultipartFile, $"/content/library/{i}.mkv"));
        }

        context.Items.AddRange(items);
        await context.SaveChangesAsync();

        await using (context)
        using (var liveSegmentCache = new LiveSegmentCache(cacheDir))
        {
            var result = await GetManifestAsync(context, liveSegmentCache);
            var response = Assert.IsType<ObjectResult>(result.Result);
            Assert.Equal(StatusCodes.Status413PayloadTooLarge, response.StatusCode);
        }
    }

    [Fact]
    public async Task GetManifest_ReturnsTooLarge_WhenSerializedJsonExceedsHardCap()
    {
        var (context, cacheDir) = await SeedItemsAsync(_ => { });
        var items = new List<DavItem>(6_000);
        var escapedName = new string('\u0001', 255);
        for (var i = 0; i < 6_000; i++)
        {
            items.Add(CreateItem(Guid.NewGuid(), null, escapedName,
                DavItem.ItemType.MultipartFile, $"/content/library/{i}.mkv"));
        }

        context.Items.AddRange(items);
        await context.SaveChangesAsync();

        await using (context)
        using (var liveSegmentCache = new LiveSegmentCache(cacheDir))
        {
            var result = await GetManifestAsync(context, liveSegmentCache);
            var response = Assert.IsType<ObjectResult>(result.Result);
            Assert.Equal(StatusCodes.Status413PayloadTooLarge, response.StatusCode);
        }
    }

    [Fact]
    public async Task GetManifest_ReturnsUnprocessableEntity_WhenPathIsDuplicated()
    {
        var (context, cacheDir) = await SeedItemsAsync(db =>
        {
            db.Items.Add(CreateItem(Guid.NewGuid(), null, "one.mkv", DavItem.ItemType.MultipartFile,
                "/content/library/duplicate.mkv"));
            db.Items.Add(CreateItem(Guid.NewGuid(), null, "two.mkv", DavItem.ItemType.MultipartFile,
                "/content/library/duplicate.mkv"));
        });

        await using (context)
        using (var liveSegmentCache = new LiveSegmentCache(cacheDir))
        {
            var result = await GetManifestAsync(context, liveSegmentCache);
            var response = Assert.IsType<ObjectResult>(result.Result);
            Assert.Equal(StatusCodes.Status422UnprocessableEntity, response.StatusCode);
        }
    }

    [Fact]
    public async Task GetManifest_PropagatesCancellationFromRequest()
    {
        var (context, cacheDir) = await SeedItemsAsync(CreateBaseItems);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await using (context)
        using (var liveSegmentCache = new LiveSegmentCache(cacheDir))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                GetManifestAsync(context, liveSegmentCache, cancellationToken: cancellation.Token));
        }
    }

    private async Task<(DavDatabaseContext Context, string CacheDir)> SeedItemsAsync(Action<DavDatabaseContext> seed)
    {
        await _fixture.ResetAsync();
        _fixture.SetKeys(_fixture.CreateKey(), null);

        var context = await _fixture.CreateMigratedContextAsync();
        seed(context);
        await context.SaveChangesAsync();
        return (context, CreateCacheDirectory());
    }

    private static async Task<ManifestControllerResult> GetManifestAsync(
        DavDatabaseContext context,
        LiveSegmentCache liveSegmentCache,
        DefaultHttpContext? requestContext = null,
        CancellationToken cancellationToken = default)
    {
        var request = requestContext ?? new DefaultHttpContext();
        var controller = new ManifestController(new DavDatabaseClient(context), liveSegmentCache)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = request
            }
        };

        var result = await controller.GetManifest(paged: false, after: null, cancellationToken);
        return new ManifestControllerResult(result, request);
    }

    private static void CreateBaseItems(DavDatabaseContext context)
    {
        var parent = CreateItem(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("00000000-0000-0000-0000-000000000002"),
            "movies",
            DavItem.ItemType.Directory,
            "/content/movies");

        context.Items.Add(parent);
        context.Items.Add(CreateItem(
            Guid.Parse("11111111-1111-1111-1111-111111111112"),
            parent.Id,
            "A.mkv",
            DavItem.ItemType.MultipartFile,
            "/content/movies/A.mkv",
            10_000));
    }

    private static void CreateSwappedNameItems(DavDatabaseContext context)
    {
        var parent = CreateItem(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Guid.Parse("00000000-0000-0000-0000-000000000002"),
            "media",
            DavItem.ItemType.Directory,
            "/content/media");

        context.Items.Add(parent);
        context.Items.Add(CreateItem(
            Guid.Parse("22222222-2222-2222-2222-222222222223"),
            parent.Id,
            "A.mkv",
            DavItem.ItemType.MultipartFile,
            "/content/media/A.mkv", 1));
        context.Items.Add(CreateItem(
            Guid.Parse("22222222-2222-2222-2222-222222222224"),
            parent.Id,
            "B.mkv",
            DavItem.ItemType.MultipartFile,
            "/content/media/B.mkv", 1));
    }

    private static void CreateParentAndTypeItems(DavDatabaseContext context)
    {
        var series = CreateItem(
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            Guid.Parse("00000000-0000-0000-0000-000000000002"),
            "series",
            DavItem.ItemType.Directory,
            "/content/series");

        var seasonOne = CreateItem(
            Guid.Parse("33333333-3333-3333-3333-333333333334"),
            series.Id,
            "season-1",
            DavItem.ItemType.Directory,
            "/content/series/season-1");

        var seasonTwo = CreateItem(
            Guid.Parse("33333333-3333-3333-3333-333333333335"),
            series.Id,
            "season-2",
            DavItem.ItemType.Directory,
            "/content/series/season-2");

        context.Items.Add(series);
        context.Items.Add(seasonOne);
        context.Items.Add(seasonTwo);
        context.Items.Add(CreateItem(
            Guid.Parse("33333333-3333-3333-3333-333333333336"),
            seasonOne.Id,
            "episode.mkv",
            DavItem.ItemType.MultipartFile,
            "/content/series/season-1/episode.mkv",
            77));
    }

    private static DavItem CreateItem(
        Guid id,
        Guid? parentId,
        string name,
        DavItem.ItemType type,
        string path,
        long? fileSize = null)
    {
        return new DavItem
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

    private static string CreateCacheDirectory()
    {
        var cacheDirectory = Path.Combine(Path.GetTempPath(), "nzbdav-manifest-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cacheDirectory);
        return cacheDirectory;
    }
}

internal readonly record struct ManifestControllerResult(IActionResult Result, DefaultHttpContext Context);
