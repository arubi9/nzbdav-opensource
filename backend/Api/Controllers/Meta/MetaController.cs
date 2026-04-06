using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Api.Filters;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.Meta;

[ApiController]
[Route("api/meta")]
[ServiceFilter(typeof(ApiKeyAuthFilter))]
public class MetaController(DavDatabaseClient dbClient, ConfigManager configManager) : ControllerBase
{
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetMeta(Guid id, CancellationToken ct)
    {
        var item = await dbClient.Ctx.Items.FindAsync(new object[] { id }, ct).ConfigureAwait(false);
        if (item is null) return NotFound(new { error = "Item not found" });

        var streamPath = $"/api/stream/{id}";
        var token = StreamTokenService.GenerateToken(streamPath, configManager);

        return Ok(new MetaResponse
        {
            Id = item.Id,
            Name = item.Name,
            Path = item.Path,
            Type = item.Type.ToString(),
            FileSize = item.FileSize,
            CreatedAt = item.CreatedAt,
            ParentId = item.ParentId,
            ContentType = StreamExecutionService.GetContentType(item.Name),
            StreamToken = token
        });
    }
}
