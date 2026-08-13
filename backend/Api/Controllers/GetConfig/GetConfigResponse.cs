namespace NzbWebDAV.Api.Controllers.GetConfig;

public sealed class SafeConfigItem
{
    public string ConfigName { get; init; } = string.Empty;
    public string ConfigValue { get; init; } = string.Empty;
    public bool HasSecret { get; init; }
    public string? SecretKind { get; init; }
}

public class GetConfigResponse : BaseApiResponse
{
    public List<SafeConfigItem> ConfigItems { get; init; } = new();
}
