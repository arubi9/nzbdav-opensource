using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Api.Filters;
using NzbWebDAV.Setup.Core;

namespace NzbWebDAV.Api.Controllers.Setup;

[ApiController]
[Route("api/setup/grant/revoke")]
[ServiceFilter(typeof(SetupGrantAuthFilter))]
public class SetupGrantRevokeController(SetupGrantService setupGrantService) : BaseApiController
{
    protected override bool AllowGet => false;
    private readonly SetupGrantService _setupGrantService = setupGrantService;

    protected override async Task<IActionResult> HandleRequest()
    {
        await SetupHttpContracts.RequireEmptyJsonPostAsync(HttpContext).ConfigureAwait(false);
        if (HttpContext.Items["nzbdav.setup.scope"] is SetupGrantScope.Repair)
            await _setupGrantService.RecoverRepairAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        else
            await _setupGrantService.RevokeAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        return Ok(new BaseApiResponse());
    }
}
