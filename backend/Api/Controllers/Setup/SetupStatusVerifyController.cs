using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Setup.Orchestration;

namespace NzbWebDAV.Api.Controllers.Setup;

/// <summary>Explicit live setup verification. GET status remains pure.</summary>
/// <remarks>This POST uses BaseApiController's internal API-key boundary and deliberately requires no setup grant.</remarks>
[ApiController]
[Route("api/setup/status/verify")]
[Route("api/setup-status/verify")]
public sealed class SetupStatusVerifyController(SetupOrchestrationService orchestration) : BaseApiController
{
    protected override bool AllowGet => false;

    protected override async Task<IActionResult> HandleRequest()
    {
        await SetupHttpContracts.RequireEmptyJsonPostAsync(HttpContext).ConfigureAwait(false);
        return Ok(await orchestration.VerifyStatusAsync(HttpContext.RequestAborted).ConfigureAwait(false));
    }
}
