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

        var directory = normalizedPath == "/"
            ? DavItem.Root
            : await dbClient.GetItemByPathAsync(normalizedPath, ct).ConfigureAwait(false);

        if (directory is null)
            return NotFound(new { error = $"Path not found: {normalizedPath}" });

        var children = await dbClient.GetDirectoryChildrenAsync(directory.Id, ct).ConfigureAwait(false);

        return Ok(new BrowseResponse
        {
            Path = normalizedPath,
            Items = children.Select(BrowseItem.FromDavItem).ToArray()
        });
    }

    [HttpGet]
    public Task<IActionResult> BrowseRoot(CancellationToken ct) => Browse(null, ct);
}
