using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Api.Filters;
using NzbWebDAV.Setup.Core;
using NzbWebDAV.Setup.Orchestration;

namespace NzbWebDAV.Api.Controllers.Setup;

[ApiController]
[ServiceFilter(typeof(SetupGrantAuthFilter))]
[Route("api/setup/run")]
public sealed class SetupRunController(SetupOrchestrationService orchestration) : BaseApiController
{
    protected override bool AllowGet => false;
    protected override async Task<IActionResult> HandleRequest()
    {
        await SetupHttpContracts.RequireEmptyJsonPostAsync(HttpContext).ConfigureAwait(false);
        var grant = HttpContext.Items["nzbdav.setup.grant"] as string ?? string.Empty;
        var scope = HttpContext.Items["nzbdav.setup.scope"] is SetupGrantScope value
            ? value
            : SetupGrantScope.Normal;
        var response = scope == SetupGrantScope.Repair
            ? await orchestration.RunRepairAsync(grant, HttpContext.RequestAborted).ConfigureAwait(false)
            : await orchestration.RunAsync(grant, SetupGrantConstants.NormalPurpose, HttpContext.RequestAborted).ConfigureAwait(false);
        return Ok(response);
    }
}
