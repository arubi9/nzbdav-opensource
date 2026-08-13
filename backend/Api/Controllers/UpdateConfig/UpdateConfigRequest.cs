using Microsoft.AspNetCore.Http;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.Controllers.UpdateConfig;

public class UpdateConfigRequest
{
    public List<ConfigItem> ConfigItems { get; init; }
    public HashSet<string> ClearSecretKeys { get; init; }

    public UpdateConfigRequest(HttpContext context)
    {
        var form = context.Request.Form;
        ClearSecretKeys = form
            .Where(pair => pair.Key.EndsWith(".__clear", StringComparison.Ordinal)
                           && pair.Value.Count == 1
                           && string.Equals(pair.Value[0], "true", StringComparison.Ordinal))
            .Select(pair => pair.Key[..^".__clear".Length])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        ConfigItems = form
            .Where(x => !x.Key.EndsWith(".__clear", StringComparison.Ordinal))
            .Select(x => new ConfigItem()
            {
                ConfigName = x.Key,
                ConfigValue = x.Value.FirstOrDefault() ?? ""
            })
            .Select(x => x.ConfigName != "webdav.pass" ? x : new ConfigItem()
            {
                ConfigName = x.ConfigName,
                ConfigValue = PasswordUtil.Hash(x.ConfigValue)
            })
            .ToList();
    }
}