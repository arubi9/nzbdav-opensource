using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using NzbWebDAV.Api.Controllers;
using NzbWebDAV.Setup.Core;

namespace NzbWebDAV.Api.Filters;

public sealed class SetupGrantAuthFilter(SetupGrantService setupGrantService) : IAsyncActionFilter
{
    private readonly SetupGrantService _setupGrantService = setupGrantService;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // This filter is attached only to setup mutation actions. Reject
        // inherited GET/HEAD mappings before grant validation or any setup
        // work, with the same safe JSON contract as the controller boundary.
        if (!HttpMethods.IsPost(context.HttpContext.Request.Method))
        {
            context.HttpContext.Response.Headers.Allow = HttpMethods.Post;
            context.Result = new ObjectResult(new BaseApiResponse
            {
                Status = false,
                Error = "Setup mutations require POST."
            }) { StatusCode = StatusCodes.Status405MethodNotAllowed };
            return;
        }

        var provided = context.HttpContext.Request.Headers[SetupGrantConstants.HeaderName].FirstOrDefault();
        var scope = await _setupGrantService.ValidateScopeAsync(provided, context.HttpContext.RequestAborted).ConfigureAwait(false);
        if (scope is null)
        {
            context.Result = new UnauthorizedObjectResult(new BaseApiResponse
            {
                Status = false,
                Error = "Invalid setup grant."
            });
            return;
        }

        // Retain only the request's already-present bearer for the durable
        // orchestration lease binding. It is never logged or copied to a
        // response.
        context.HttpContext.Items["nzbdav.setup.grant"] = provided;
        context.HttpContext.Items["nzbdav.setup.scope"] = scope.Value;

        // Every recovery representation is fail-closed: the DB candidate,
        // emergency-only candidate, revocation marker, or completion phase-A
        // row. HasRecoveryPending is a bounded fenced boolean and never
        // exposes token/operation material to this filter.
        if (scope != SetupGrantScope.Repair
            && await _setupGrantService.HasRecoveryPendingAsync(context.HttpContext.RequestAborted).ConfigureAwait(false))
        {
            context.Result = new ConflictObjectResult(new BaseApiResponse
            {
                Status = false,
                Error = "Setup session recovery is pending; recover cleanup before continuing."
            });
            return;
        }

        await next().ConfigureAwait(false);
    }
}
