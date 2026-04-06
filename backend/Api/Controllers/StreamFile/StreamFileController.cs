using Microsoft.AspNetCore.Mvc;
using NWebDav.Server.Stores;
using NzbWebDAV.Api.Filters;
using NzbWebDAV.Database;
using NzbWebDAV.Services;
using NzbWebDAV.WebDav;

namespace NzbWebDAV.Api.Controllers.StreamFile;

[ApiController]
[Route("api/stream")]
[ServiceFilter(typeof(ApiKeyAuthFilter))]
public class StreamFileController(
    DatabaseStore store,
    DavDatabaseClient dbClient,
    StreamExecutionService streamService
) : ControllerBase
{
    [HttpGet("{id:guid}")]
    [HttpHead("{id:guid}")]
    public async Task HandleStream(Guid id, CancellationToken ct)
    {
        var davItem = await dbClient.Ctx.Items.FindAsync(new object[] { id }, ct).ConfigureAwait(false);
        if (davItem is null)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (HttpMethods.IsHead(Request.Method))
        {
            streamService.SetFileHeaders(davItem.Name, davItem.FileSize, Response);
            return;
        }

        var storeItem = await store.GetItemAsync(davItem.Path, ct).ConfigureAwait(false);
        if (storeItem is null || storeItem is IStoreCollection)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var stream = await storeItem.GetReadableStreamAsync(ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            await streamService.ServeStreamAsync(stream, davItem.Name, Response, Request, ct)
                .ConfigureAwait(false);
        }
    }
}
