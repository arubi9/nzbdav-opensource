namespace NzbWebDAV.Config;

public static class SensitiveConfigKeys
{
    public static readonly HashSet<string> Keys = new(StringComparer.Ordinal)
    {
        "usenet.providers",
        "arr.instances",
        "api.key",
        "api.strm-key",
        "webdav.pass",
    };

    public static bool IsSensitive(string configName) => Keys.Contains(configName);
}
