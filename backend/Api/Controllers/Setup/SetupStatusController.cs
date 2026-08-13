using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Setup.Orchestration;

namespace NzbWebDAV.Api.Controllers.Setup;

[ApiController]
[Route("api/setup-status")]
[Route("api/setup/status")]
public sealed class SetupStatusController(SetupOrchestrationService orchestration) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        if (!HttpMethods.IsGet(HttpContext.Request.Method))
        {
            HttpContext.Response.Headers.Allow = HttpMethods.Get;
            return StatusCode(StatusCodes.Status405MethodNotAllowed, new BaseApiResponse
            {
                Status = false,
                Error = "Setup status reads require GET."
            });
        }
        return Ok(await orchestration.GetStatusAsync(HttpContext.RequestAborted).ConfigureAwait(false));
    }
}
