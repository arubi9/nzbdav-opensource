using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Clients.RadarrSonarr;

namespace NzbWebDAV.Api.Controllers.TestArrConnection;

[ApiController]
[Route("api/test-arr-connection")]
public class TestArrConnectionController() : BaseApiController
{
    private async Task<TestArrConnectionResponse> TestArrConnection(TestArrConnectionRequest request)
    {
        try
        {
            var client = new ArrClient(request.Host, request.ApiKey);
            var apiInfo = await client.GetApiInfo(HttpContext.RequestAborted).ConfigureAwait(false);
            return new TestArrConnectionResponse
            {
                Status = true,
                Connected = apiInfo.Current?.Length > 0
            };
        }
        catch (ArrClientException exception)
        {
            return new TestArrConnectionResponse
            {
                Status = true,
                Connected = false,
                Error = exception.Failure switch
                {
                    ArrClientFailure.Unauthorized => "unauthorized",
                    ArrClientFailure.Redirect => "redirect-rejected",
                    ArrClientFailure.Timeout => "timeout",
                    ArrClientFailure.ResponseTooLarge => "response-too-large",
                    ArrClientFailure.InvalidResponse => "invalid-response",
                    _ => "unreachable",
                }
            };
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new TestArrConnectionResponse { Status = true, Connected = false, Error = "unreachable" };
        }
    }

    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new TestArrConnectionRequest(HttpContext);
        var response = await TestArrConnection(request).ConfigureAwait(false);
        return Ok(response);
    }
}