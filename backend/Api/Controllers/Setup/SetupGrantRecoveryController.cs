using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Api.Filters;
using NzbWebDAV.Setup.Core;

namespace NzbWebDAV.Api.Controllers.Setup;

/// <summary>
/// The only setup operation available after durable setup-session cleanup or
/// candidate recovery is pending. It requires the local administrator and a
/// fresh Jellyfin administrator authentication; no setup grant is accepted or
/// re-enabled here.
/// </summary>
[ApiController]
[Route("api/setup/grant/recover")]
[Route("api/setup/grant/recovery")]
[Route("api/setup/recover-grant")]
public sealed class SetupGrantRecoveryController(
    SetupGrantService setupGrantService,
    IAuthFailureTracker? failureTracker = null) : BaseApiController
{
    protected override bool AllowGet => false;
    protected override async Task<IActionResult> HandleRequest()
    {
        var blocked = await SetupAuthFailureGuard.RejectIfBlockedAsync(HttpContext, failureTracker).ConfigureAwait(false);
        if (blocked is not null)
            return blocked;

        var request = await SetupGrantRequest.ParseAsync(HttpContext, SetupGrantOperation.Recovery, HttpContext.RequestAborted)
            .ConfigureAwait(false);
        bool recovered;
        try
        {
            recovered = await setupGrantService.RecoverRevocationAsync(
                    request.Username,
                    request.Password,
                    HttpContext.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException)
        {
            await SetupAuthFailureGuard.RecordFailureAsync(HttpContext, failureTracker).ConfigureAwait(false);
            return Unauthorized(new BaseApiResponse { Status = false, Error = "Invalid setup credentials." });
        }
        // Recovery is an idempotent repair command. A replay after a committed
        // cleanup is success with safe state only; never return the existing
        // grant or turn an ambiguous/lost response into a user-visible 400.

        return Ok(new SetupGrantRecoveryResponse
        {
            Status = true,
            RevocationPending = await setupGrantService.IsRevocationPendingAsync(HttpContext.RequestAborted)
                .ConfigureAwait(false),
        });
    }
}

public sealed class SetupGrantRecoveryResponse : BaseApiResponse
{
    public bool RevocationPending { get; init; }
}
