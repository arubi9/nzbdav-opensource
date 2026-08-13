using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Api.Filters;
using NzbWebDAV.Setup.Core;
using NzbWebDAV.Setup.Orchestration;

namespace NzbWebDAV.Api.Controllers.Setup;

[ApiController]
[ServiceFilter(typeof(SetupGrantAuthFilter))]
[Route("api/setup/configuration")]
[Route("api/setup/configure")]
public sealed class SetupConfigurationController(SetupOrchestrationService orchestration) : BaseApiController
{
    protected override bool AllowGet => false;
    protected override async Task<IActionResult> HandleRequest()
    {
        var request = await SetupConfigurationData.ParseAsync(HttpContext, HttpContext.RequestAborted).ConfigureAwait(false);
        var scope = HttpContext.Items["nzbdav.setup.scope"] is SetupGrantScope value
            ? value
            : SetupGrantScope.Normal;
        if (scope == SetupGrantScope.Repair)
            throw new BadHttpRequestException("Repair grants cannot run normal setup configuration.");
        var grant = HttpContext.Items["nzbdav.setup.grant"] as string ?? string.Empty;
        var response = await orchestration.ConfigureAndRunAsync(
                request,
                grant,
                "setup",
                HttpContext.RequestAborted)
            .ConfigureAwait(false);
        return Ok(response);
    }
}
