using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Tasks;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Api.Controllers.BackfillStrmFiles;

[ApiController]
[Route("api/backfill-strm-files")]
public class BackfillStrmFilesController(
    ConfigManager configManager,
    DavDatabaseClient dbClient,
    WebsocketManager websocketManager
) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var task = new BackfillStrmFilesTask(configManager, dbClient, websocketManager);
        var executed = await task.Execute().ConfigureAwait(false);
        return Ok(executed);
    }
}
