using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Api.Filters;
using NzbWebDAV.Database;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.Controllers.Authenticate;

[ApiController]
[Route("api/authenticate")]
public class AuthenticateController(DavDatabaseClient dbClient, IAuthFailureTracker failureTracker) : BaseApiController
{
    // Keep an absent username on the same password-hash path as an existing
    // username so the authentication endpoint does not become an existence
    // oracle through its response timing.
    private static readonly string DummyPasswordHash = PasswordUtil.Hash("nzb-webdav-authentication-dummy");

    protected override async Task<IActionResult> HandleRequest()
    {
        var source = TrustedAuthSource.For(HttpContext);
        if (await failureTracker.IsBlockedAsync(source).ConfigureAwait(false))
        {
            HttpContext.Response.Headers.RetryAfter = "60";
            return StatusCode(429, new { status = false, error = "Too many failed authentication attempts." });
        }

        var request = new AuthenticateRequest(HttpContext);
        var account = await dbClient.Ctx.Accounts
            .Where(a => a.Type == request.Type && a.Username == request.Username)
            .FirstOrDefaultAsync().ConfigureAwait(false);
        var authenticated = account is not null
            ? PasswordUtil.Verify(account.PasswordHash, request.Password, account.RandomSalt)
            : PasswordUtil.Verify(DummyPasswordHash, request.Password);

        if (!authenticated)
            await failureTracker.RecordFailureAsync(source).ConfigureAwait(false);

        // Keep nonexistent-user and wrong-password responses identical. A
        // successful login is intentionally not treated as a failure.
        return Ok(new AuthenticateResponse { Authenticated = authenticated });
    }
}
