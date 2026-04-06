using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Api.Filters;
using NzbWebDAV.Clients.Usenet.Caching;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Api.Controllers.Manifest;

/// <summary>
/// Returns the entire /content tree as a single JSON document.
/// ETag-versioned — the plugin caches locally and only re-fetches when content changes.
/// One HTTP request per sync cycle regardless of library size.
/// </summary>
[ApiController]
[Route("api/manifest")]
[ServiceFilter(typeof(ApiKeyAuthFilter))]
public class ManifestController(DavDatabaseClient dbClient, LiveSegmentCache liveSegmentCache) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetManifest(CancellationToken ct)
    {
        // Query all /content items in one DB call
        var items = await dbClient.Ctx.Items
            .AsNoTracking()
            .Where(x => x.Path.StartsWith("/content/"))
            .OrderBy(x => x.Path)
            .Select(x => new ManifestItem
            {
                Id = x.Id,
                ParentId = x.ParentId,
                Name = x.Name,
                Path = x.Path,
                Type = x.Type == DavItem.ItemType.Directory ? "directory"
                    : x.Type == DavItem.ItemType.NzbFile ? "nzb_file"
                    : x.Type == DavItem.ItemType.RarFile ? "rar_file"
                    : x.Type == DavItem.ItemType.MultipartFile ? "multipart_file"
                    : "unknown",
                FileSize = x.FileSize,
                CreatedAt = x.CreatedAt,
                HasProbeData = false // Set below after query
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Mark items that have pre-generated probe data
        foreach (var item in items)
        {
            var probePath = Path.Combine(liveSegmentCache.CacheDirectory, $"probe-{item.Id:N}.json");
            item.HasProbeData = System.IO.File.Exists(probePath);
        }

        // ETag based on item count + latest creation date
        var latestCreated = items.Count > 0
            ? items.Max(x => x.CreatedAt).Ticks.ToString()
            : "0";
        var etag = $"\"{items.Count}-{latestCreated}\"";

        // Check If-None-Match
        if (Request.Headers.IfNoneMatch.Contains(etag))
        {
            Response.StatusCode = StatusCodes.Status304NotModified;
            return new EmptyResult();
        }

        Response.Headers.ETag = etag;
        Response.Headers.CacheControl = "private, max-age=30";

        return Ok(new ManifestResponse
        {
            ItemCount = items.Count,
            Items = items
        });
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
