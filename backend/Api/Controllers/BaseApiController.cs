using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Extensions;
using NzbWebDAV.Setup.Core;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.Controllers;

public abstract class BaseApiController : ControllerBase
{
    protected virtual bool RequiresAuthentication => true;
    /// <summary>Setup mutation controllers override this to reject inherited GET mapping.</summary>
    protected virtual bool AllowGet => true;
    protected abstract Task<IActionResult> HandleRequest();

    [HttpGet]
    [HttpPost]
    public async Task<IActionResult> HandleApiRequest()
    {
        try
        {
            if (!AllowGet && !HttpMethods.IsPost(HttpContext.Request.Method))
            {
                HttpContext.Response.Headers.Allow = HttpMethods.Post;
                return StatusCode(StatusCodes.Status405MethodNotAllowed, new BaseApiResponse
                {
                    Status = false,
                    Error = "Setup mutations require POST."
                });
            }

            if (RequiresAuthentication)
            {
                var apiKey = HttpContext.GetRequestApiKey();
                if (apiKey == null)
                    throw new UnauthorizedAccessException("API Key Required");
                if (!ApiKeyUtil.MatchesConfiguredKeys(
                        apiKey,
                        string.Empty,
                        EnvironmentUtil.GetRequiredVariable("FRONTEND_BACKEND_API_KEY")))
                    throw new UnauthorizedAccessException("API Key Incorrect");
            }

            return await HandleRequest().ConfigureAwait(false);
        }
        catch (SetupRunBusyException e)
        {
            // The lease holder and its grant are intentionally never exposed.
            // Retry-After is bounded by SetupRunBusyException's public contract.
            HttpContext.Response.Headers["Retry-After"] = e.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Conflict(new BaseApiResponse
            {
                Status = false,
                Error = SetupRunBusyException.SafeMessage,
                Code = e.Code,
            });
        }
        catch (BadHttpRequestException e)
        {
            var error = e.Message;
            if (string.Equals(
                    Environment.GetEnvironmentVariable("BACKEND_TEST_DEBUG"),
                    "1",
                    StringComparison.Ordinal))
            {
                error = e.ToString();
            }

            return BadRequest(new BaseApiResponse()
            {
                Status = false,
                Error = error
            });
        }
        catch (UnauthorizedAccessException e)
        {
            return Unauthorized(new BaseApiResponse()
            {
                Status = false,
                Error = e.Message
            });
        }
        catch (ConcurrencyConflictException e)
        {
            return Conflict(new BaseApiResponse()
            {
                Status = false,
                Error = e.Message
            });
        }
        catch (Exception e) when (e is not OperationCanceledException ||
                                  !HttpContext.RequestAborted.IsCancellationRequested)
        {
            // Setup progress and upstream clients intentionally discard
            // exception text. Never turn a provider response, SQL diagnostic,
            // or credential-bearing operation detail into an API body.
            var safeError = HttpContext.Request.Path.StartsWithSegments("/api/setup")
                ? "Setup operation failed."
                : e.Message;
            return StatusCode(500, new BaseApiResponse()
            {
                Status = false,
                Error = safeError
            });
        }
    }
}