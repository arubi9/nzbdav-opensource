namespace NzbWebDAV.Config;

public static class SensitiveConfigKeys
{
    // These keys either contain secrets directly or values that give
    // equivalent access. `webdav.pass` is included even though it is stored as
    // a hash because possession of the hash enables offline cracking attempts.
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
