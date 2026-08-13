using Microsoft.AspNetCore.Http;

namespace NzbWebDAV.Api.Controllers.Setup;

internal static class SetupHttpContracts
{
    internal static async Task RequireEmptyJsonPostAsync(HttpContext context)
    {
        var type = context.Request.ContentType;
        if (string.IsNullOrWhiteSpace(type)
            || !type.Split(';', 2)[0].Trim().Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || type.Contains(';') && !type[(type.IndexOf(';') + 1)..].Trim().Equals("charset=utf-8", StringComparison.OrdinalIgnoreCase))
            throw new BadHttpRequestException("Setup command requires application/json.");
        var buffer = new byte[1];
        if (await context.Request.Body.ReadAsync(buffer, context.RequestAborted).ConfigureAwait(false) != 0)
            throw new BadHttpRequestException("Setup command does not accept a request body.");
    }
}
