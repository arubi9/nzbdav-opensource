using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Api.Filters;
using NzbWebDAV.Setup.Core;

namespace NzbWebDAV.Api.Controllers.Setup;

[ApiController]
[Route("api/setup/handoff")]
[Route("api/setup/admin-handoff")]
public sealed class SetupHandoffController(
    SetupGrantService setupGrantService,
    IAuthFailureTracker? failureTracker = null) : BaseApiController
{
    protected override bool AllowGet => false;
    private readonly SetupGrantService _setupGrantService = setupGrantService;
    private readonly IAuthFailureTracker? _failureTracker = failureTracker;

    protected override async Task<IActionResult> HandleRequest()
    {
        var blocked = await SetupAuthFailureGuard.RejectIfBlockedAsync(HttpContext, _failureTracker).ConfigureAwait(false);
        if (blocked is not null)
            return blocked;

        var request = await SetupGrantRequest.ParseAsync(HttpContext, SetupGrantOperation.Handoff, HttpContext.RequestAborted)
            .ConfigureAwait(false);
        SetupGrantResult result;
        try
        {
            result = await _setupGrantService.IssueAsync(request.Username, request.Password, HttpContext.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException)
        {
            await SetupAuthFailureGuard.RecordFailureAsync(HttpContext, _failureTracker).ConfigureAwait(false);
            return Unauthorized(new BaseApiResponse { Status = false, Error = "Invalid setup credentials." });
        }

        return Ok(new SetupGrantResponse
        {
            Grant = result.Grant,
            ExpiresAtUtc = result.ExpiresAtUtc,
            RevocationPending = result.RevocationPending,
            Warning = result.Warning,
            Scope = result.Scope,
        });
    }
}
