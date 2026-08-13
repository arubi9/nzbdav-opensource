using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Api.Filters;
using NzbWebDAV.Setup.Core;

namespace NzbWebDAV.Api.Controllers.Setup;

[ApiController]
[Route("api/setup/grant/repair")]
[Route("api/setup/repair-grant")]
public sealed class SetupGrantRepairController(
    SetupGrantService setupGrantService,
    IAuthFailureTracker? failureTracker = null) : BaseApiController
{
    protected override bool AllowGet => false;
    protected override async Task<IActionResult> HandleRequest()
    {
        var blocked = await SetupAuthFailureGuard.RejectIfBlockedAsync(HttpContext, failureTracker).ConfigureAwait(false);
        if (blocked is not null) return blocked;

        var request = await SetupGrantRequest.ReadRepairAsync(HttpContext, HttpContext.RequestAborted).ConfigureAwait(false);
        try
        {
            var result = await setupGrantService.IssueRepairAsync(
                    request.Username,
                    request.Password,
                    HttpContext.RequestAborted)
                .ConfigureAwait(false);
            return Ok(new SetupGrantResponse
            {
                Status = true,
                Grant = result.Grant,
                ExpiresAtUtc = result.ExpiresAtUtc,
                Scope = result.Scope,
                Warning = result.Warning,
            });
        }
        catch (UnauthorizedAccessException)
        {
            await SetupAuthFailureGuard.RecordFailureAsync(HttpContext, failureTracker).ConfigureAwait(false);
            return Unauthorized(new BaseApiResponse { Status = false, Error = "Invalid setup credentials." });
        }
    }
}
