using System.Collections.Frozen;

namespace NzbWebDAV.Config;

public static class SensitiveConfigKeys
{
    // This is the single source of truth for managed names. The key comparer
    // accepts legacy casing, while every value is the one spelling allowed at
    // the cache, event, and persistence boundaries.
    private static readonly FrozenDictionary<string, string> CanonicalMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["usenet.providers"] = "usenet.providers",
            ["arr.instances"] = "arr.instances",
            ["api.key"] = "api.key",
            ["auth.admin-password"] = "auth.admin-password",
            ["api.strm-key"] = "api.strm-key",
            ["api.strm-key-previous"] = "api.strm-key-previous",
            ["api.strm-key-previous-expires-at"] = "api.strm-key-previous-expires-at",
            ["api.strm-key-previous-ring"] = "api.strm-key-previous-ring",
            ["cache.l2.access-key"] = "cache.l2.access-key",
            ["cache.l2.secret-key"] = "cache.l2.secret-key",
            ["webdav.pass"] = "webdav.pass",
            ["setup.indexers"] = "setup.indexers",
            ["setup.jellyfin-api-key"] = "setup.jellyfin-api-key",
            ["setup.plugin-api-key"] = "setup.plugin-api-key",
            ["setup.plugin-api-key-previous"] = "setup.plugin-api-key-previous",
            ["setup.plugin-api-key-previous-expires-at"] = "setup.plugin-api-key-previous-expires-at",
            ["setup.stream-token-legacy-migration-start"] = "setup.stream-token-legacy-migration-start",
            ["setup.run-progress"] = "setup.run-progress",
            ["setup.run-progress-reasons"] = "setup.run-progress-reasons",
            ["setup.repair-required"] = "setup.repair-required",
            ["setup.repair-checked-at-utc"] = "setup.repair-checked-at-utc",
            ["setup.sonarr-repair-reason"] = "setup.sonarr-repair-reason",
            ["setup.radarr-repair-reason"] = "setup.radarr-repair-reason",
            ["setup.repair-reasons"] = "setup.repair-reasons",
            // AAD-only names for encrypted completion-operation payloads.
            ["setup.completion.active"] = "setup.completion.active",
            ["setup.completion.revocation"] = "setup.completion.revocation",
            ["setup.completion.candidate"] = "setup.completion.candidate",
            ["setup.completion.candidate-operation"] = "setup.completion.candidate-operation",
            ["setup.completion.emergency"] = "setup.completion.emergency",
            ["setup.completion.emergency-operation"] = "setup.completion.emergency-operation",
            ["setup.grant.repair-session"] = "setup.grant.repair-session",
            ["setup.revocation-pending"] = "setup.revocation-pending",
            ["setup.revocation-pending-token"] = "setup.revocation-pending-token",
            ["setup.candidate-session-token"] = "setup.candidate-session-token",
            ["setup.candidate-session-operation"] = "setup.candidate-session-operation",
            ["setup.candidate-session-intent"] = "setup.candidate-session-intent",
            ["setup.completed"] = "setup.completed",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> SensitiveNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "usenet.providers",
        "arr.instances",
        "api.key",
        "auth.admin-password",
        "api.strm-key",
        "api.strm-key-previous",
        "api.strm-key-previous-expires-at",
        "api.strm-key-previous-ring",
        "cache.l2.access-key",
        "cache.l2.secret-key",
        "webdav.pass",
        "setup.indexers",
        "setup.jellyfin-api-key",
        "setup.plugin-api-key",
        "setup.plugin-api-key-previous",
        "setup.plugin-api-key-previous-expires-at",
        // Progress, repair, migration, and revocation values are durable
        // non-secret markers. They must remain canonical plaintext so direct
        // status/boolean readers cannot reopen setup after a key rotation.
        "setup.completion.active",
        "setup.completion.revocation",
        "setup.completion.candidate",
        "setup.completion.candidate-operation",
        "setup.completion.emergency",
        "setup.completion.emergency-operation",
        "setup.revocation-pending",
        "setup.revocation-pending-token",
        "setup.candidate-session-token",
        "setup.candidate-session-operation",
        "setup.candidate-session-intent",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlySet<string> Keys => SensitiveNames;
    public static IReadOnlyDictionary<string, string> CanonicalNames => CanonicalMap;

    public static bool IsSensitive(string configName)
        => TryGetCanonicalKey(configName, out var canonicalName) && SensitiveNames.Contains(canonicalName);

    public static bool TryGetCanonicalKey(string configName, out string canonicalName)
        => CanonicalMap.TryGetValue(configName, out canonicalName!);

    public static bool TryGetCanonicalSetupKey(string configName, out string canonicalName)
        => TryGetCanonicalKey(configName, out canonicalName)
           && (canonicalName.StartsWith("setup.", StringComparison.Ordinal)
               || canonicalName.Equals("usenet.providers", StringComparison.Ordinal));
}
