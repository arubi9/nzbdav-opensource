using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Api.Filters;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Api.Controllers.Browse;

[ApiController]
[Route("api/browse")]
[ServiceFilter(typeof(ApiKeyAuthFilter))]
public class BrowseController(DavDatabaseClient dbClient) : ControllerBase
{
    [HttpGet("{*path}")]
    public async Task<IActionResult> Browse(string? path, CancellationToken ct)
    {
        var normalizedPath = "/" + (path?.Trim('/') ?? "");
        var directory = await ResolvePath(normalizedPath, ct).ConfigureAwait(false);
        if (directory is null) return NotFound(new { error = $"Path not found: {normalizedPath}" });

        var children = await dbClient.GetDirectoryChildrenAsync(directory.Id, ct).ConfigureAwait(false);

        return Ok(new BrowseResponse
        {
            Path = normalizedPath,
            Items = children.Select(BrowseItem.FromDavItem).ToArray()
        });
    }

    [HttpGet]
    public Task<IActionResult> BrowseRoot(CancellationToken ct) => Browse(null, ct);

    private async Task<DavItem?> ResolvePath(string path, CancellationToken ct)
    {
        if (path == "/") return DavItem.Root;

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = DavItem.Root;
        foreach (var part in parts)
        {
            var child = await dbClient.GetDirectoryChildAsync(current.Id, part, ct).ConfigureAwait(false);
            if (child is null) return null;
            current = child;
        }

        return current;
    }
}
