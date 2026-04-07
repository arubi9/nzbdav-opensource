using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
        var configItems = await dbClient.Ctx.ConfigItems
            .Where(x => request.ConfigKeys.Contains(x.ConfigName))
            .ToListAsync(HttpContext.RequestAborted).ConfigureAwait(false);

        var response = new GetConfigResponse
        {
            ConfigItems = configItems.Select(item => new ConfigItem
            {
                ConfigName = item.ConfigName,
                ConfigValue = item.IsEncrypted
                    ? encryptionService.Decrypt(item.ConfigValue).plaintext
                    : item.ConfigValue,
                IsEncrypted = item.IsEncrypted,
            }).ToList()
        };
        return response;
    }

    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new GetConfigRequest(HttpContext);
        var response = await GetConfig(request).ConfigureAwait(false);
        return Ok(response);
    }
}
