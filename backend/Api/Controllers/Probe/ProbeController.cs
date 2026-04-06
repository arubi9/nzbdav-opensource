using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Api.Filters;
using NzbWebDAV.Clients.Usenet.Caching;

namespace NzbWebDAV.Api.Controllers.Probe;

/// <summary>
/// Returns pre-generated FFmpeg probe data for a file, if available.
/// Used by the Jellyfin sync task to write .nfo sidecars that prevent
/// Jellyfin from probing streams via NNTP during library scans.
/// </summary>
[ApiController]
[Route("api/probe")]
[ServiceFilter(typeof(ApiKeyAuthFilter))]
public class ProbeController(LiveSegmentCache liveSegmentCache) : ControllerBase
{
    [HttpGet("{id:guid}")]
    public IActionResult GetProbe(Guid id)
    {
        var probePath = Path.Combine(liveSegmentCache.CacheDirectory, $"probe-{id:N}.json");
        if (!System.IO.File.Exists(probePath))
            return NotFound(new { error = "No probe data available for this file" });

        var json = System.IO.File.ReadAllText(probePath);
        return Content(json, "application/json");
    }
}
