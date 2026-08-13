using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.GetConfig;

[ApiController]
[Route("api/get-config")]
public class GetConfigController(
    DavDatabaseClient dbClient,
    ConfigEncryptionService encryptionService) : BaseApiController
{
    private async Task<GetConfigResponse> GetConfig(GetConfigRequest request)
    {
        // Sensitive keys are not a redacted representation of configuration.
        // Refuse them before querying/decrypting, using the canonical key map so
        // casing and legacy aliases cannot bypass this boundary.
        if (request.ConfigKeys.Any(key =>
                SensitiveConfigKeys.IsSensitive(key)
                || SensitiveConfigKeys.TryGetCanonicalSetupKey(key, out _)
                || key.StartsWith("setup.", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Sensitive settings cannot be requested through this endpoint.");
        }

        var configKeys = request.ConfigKeys.ToHashSet(StringComparer.Ordinal);
        var configItems = await dbClient.Ctx.ConfigItems
            .Where(x => configKeys.Contains(x.ConfigName))
            .ToListAsync(HttpContext.RequestAborted).ConfigureAwait(false);

        return new GetConfigResponse
        {
            ConfigItems = configItems.Select(ToSafeItem).ToList()
        };
    }

    private SafeConfigItem ToSafeItem(ConfigItem item)
    {
        // This is a second boundary check for legacy rows and callers that
        // construct a controller request directly.
        if (SensitiveConfigKeys.IsSensitive(item.ConfigName)
            || SensitiveConfigKeys.TryGetCanonicalSetupKey(item.ConfigName, out _))
            throw new InvalidOperationException("Sensitive settings cannot be returned by this endpoint.");
        if (item.IsEncrypted)
            throw new InvalidOperationException("Encrypted config value has no safe settings representation.");
        return new SafeConfigItem { ConfigName = item.ConfigName, ConfigValue = item.ConfigValue };
    }

    // This endpoint is a read-only settings DTO. It must remain available to
    // authenticated GET callers; setup *mutations* are the POST-only routes.
    protected override bool AllowGet => true;

    protected override async Task<IActionResult> HandleRequest()
    {
        if (!HttpContext.Request.HasFormContentType)
            throw new BadHttpRequestException("Config reads require form content.");
        var request = new GetConfigRequest(HttpContext);
        var response = await GetConfig(request).ConfigureAwait(false);
        return Ok(response);
    }
}
