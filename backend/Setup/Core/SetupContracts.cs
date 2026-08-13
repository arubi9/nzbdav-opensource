using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using NzbWebDAV.Config;

namespace NzbWebDAV.Setup.Core;

public static class SetupConfigKeys
{
    public const string Indexers = "setup.indexers";
    public const string UsenetProviders = "usenet.providers";
    public const string JellyfinApiKey = "setup.jellyfin-api-key";
    public const string PluginApiKey = "setup.plugin-api-key";
    public const string PluginApiKeyPrevious = "setup.plugin-api-key-previous";
    public const string PluginApiKeyPreviousExpiresAt = "setup.plugin-api-key-previous-expires-at";
    public const string StreamTokenLegacyMigrationStart = "setup.stream-token-legacy-migration-start";
    public const string RunProgress = "setup.run-progress";
    public const string RunProgressReasons = "setup.run-progress-reasons";
    // Live completion checks persist a repair latch and independent subsystem
    // diagnostics. These rows are intentionally separate from progress so a
    // completed marker can never masquerade as a live health assertion.
    public const string RepairRequired = "setup.repair-required";
    public const string RepairCheckedAtUtc = "setup.repair-checked-at-utc";
    public const string SonarrRepairReason = "setup.sonarr-repair-reason";
    public const string RadarrRepairReason = "setup.radarr-repair-reason";
    public const string RepairReasons = "setup.repair-reasons";
    // Durable phase marker for external Jellyfin logout followed by local
    // recovery-token deletion. It is never exposed through generic config APIs.
    public const string RevocationPending = "setup.revocation-pending";
    // During grant rotation the new session is the usable session and this
    // encrypted value is the previous session awaiting retirement. Keeping it
    // separate from JellyfinApiKey prevents recovery from logging out the new
    // session after a crash.
    public const string RevocationPendingToken = "setup.revocation-pending-token";
    // A single encrypted candidate journal protects an authenticated Jellyfin
    // session across the gap before the grant handoff transaction commits.
    public const string CandidateSessionToken = "setup.candidate-session-token";
    public const string CandidateToken = CandidateSessionToken;
    public const string CandidateSessionOperation = "setup.candidate-session-operation";
    public const string CandidateSessionIntent = "setup.candidate-session-intent";
    public const string CandidateOperationId = CandidateSessionOperation;
    // Compatibility alias for callers that name the value by its purpose.
    public const string CandidateSessionOperationId = CandidateSessionOperation;
    public const string CandidateSessionId = CandidateSessionOperation;
    public const string Completed = "setup.completed";
}

public sealed class SetupEnvironmentOptions
{
    // Internal service-to-service traffic targets the backend listener. Port
    // 3000 is the browser/frontend listener and requires a browser session.
    public const string DefaultNzbdavUrl = "http://nzbdav:8080";
    public const string DefaultJellyfinUrl = "http://jellyfin:8096";
    public const string DefaultSonarrUrl = "http://sonarr:8989";
    public const string DefaultRadarrUrl = "http://radarr:7878";
    public const string DefaultProwlarrUrl = "http://prowlarr:9696";
    public const string DefaultSonarrConfigPath = "/bootstrap/sonarr/config.xml";
    public const string DefaultRadarrConfigPath = "/bootstrap/radarr/config.xml";
    public const string DefaultProwlarrConfigPath = "/bootstrap/prowlarr/config.xml";
    public const string DefaultLibraryPath = "/media/nzbdav";

    private SetupEnvironmentOptions(
        bool fullStackEnabled,
        string nzbdavUrl,
        string jellyfinUrl,
        string sonarrUrl,
        string radarrUrl,
        string prowlarrUrl,
        string? validationError)
    {
        FullStackEnabled = fullStackEnabled;
        NzbdavUrl = nzbdavUrl;
        JellyfinUrl = jellyfinUrl;
        SonarrUrl = sonarrUrl;
        RadarrUrl = radarrUrl;
        ProwlarrUrl = prowlarrUrl;
        ValidationError = validationError;
    }

    public bool FullStackEnabled { get; }
    public string NzbdavUrl { get; }
    public string JellyfinUrl { get; }
    public string SonarrUrl { get; }
    public string RadarrUrl { get; }
    public string ProwlarrUrl { get; }
    public string SonarrConfigPath => DefaultSonarrConfigPath;
    public string RadarrConfigPath => DefaultRadarrConfigPath;
    public string ProwlarrConfigPath => DefaultProwlarrConfigPath;
    public string LibraryPath => DefaultLibraryPath;

    /// <summary>
    /// A safe, non-secret explanation of why full-stack setup was disabled.
    /// Invalid environment values are never included in this message.
    /// </summary>
    public string? ValidationError { get; }

    public static SetupEnvironmentOptions FromEnvironment()
    {
        var errors = new List<string>();
        var (fullStackEnabled, booleanError) = ReadBoolean("NZBDAV_FULL_STACK");
        if (booleanError is not null)
            errors.Add(booleanError);

        var nzbdavUrl = ReadUrl("SETUP_NZBDAV_URL", DefaultNzbdavUrl, errors);
        var jellyfinUrl = ReadUrl("SETUP_JELLYFIN_URL", DefaultJellyfinUrl, errors);
        var sonarrUrl = ReadUrl("SETUP_SONARR_URL", DefaultSonarrUrl, errors);
        var radarrUrl = ReadUrl("SETUP_RADARR_URL", DefaultRadarrUrl, errors);
        var prowlarrUrl = ReadUrl("SETUP_PROWLARR_URL", DefaultProwlarrUrl, errors);

        var validationError = errors.Count == 0
            ? null
            : "Full-stack setup is disabled because its environment configuration is invalid.";

        return new SetupEnvironmentOptions(
            fullStackEnabled && validationError is null,
            nzbdavUrl,
            jellyfinUrl,
            sonarrUrl,
            radarrUrl,
            prowlarrUrl,
            validationError);
    }

    private static (bool Value, string? Error) ReadBoolean(string name)
    {
        var rawValue = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(rawValue))
            return (false, null);

        return bool.TryParse(rawValue.Trim(), out var value)
            ? (value, null)
            : (false, "NZBDAV_FULL_STACK must be a boolean value.");
    }

    private static string ReadUrl(string name, string fallback, ICollection<string> errors)
    {
        var rawValue = Environment.GetEnvironmentVariable(name);
        if (rawValue is null || rawValue.Length == 0)
            return fallback;

        if (!TryCanonicalOrigin(rawValue, out var canonicalOrigin))
        {
            errors.Add($"{name} is not a safe HTTP or HTTPS URL.");
            return fallback;
        }

        return canonicalOrigin;
    }

    private static bool TryCanonicalOrigin(string rawValue, out string canonicalOrigin)
    {
        canonicalOrigin = string.Empty;
        if (string.IsNullOrWhiteSpace(rawValue)
            || rawValue.Any(IsUnsafeUrlCharacter)
            || rawValue.Contains('\\')
            || rawValue.Contains('%'))
            return false;

        var value = rawValue.Trim();
        var separator = value.IndexOf("://", StringComparison.Ordinal);
        if (separator is not 4 and not 5)
            return false;

        var scheme = value[..separator];
        if (!scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;

        var remainder = value[(separator + 3)..];
        var pathStart = remainder.IndexOfAny(['/', '?', '#']);
        var authority = pathStart < 0 ? remainder : remainder[..pathStart];
        var pathAndAnything = pathStart < 0 ? string.Empty : remainder[pathStart..];
        if (authority.Length == 0
            || pathAndAnything.Contains('?')
            || pathAndAnything.Contains('#')
            || (pathAndAnything.Length != 0 && pathAndAnything != "/"))
            return false;

        string host;
        var port = -1;
        if (authority[0] == '[')
        {
            var close = authority.IndexOf(']');
            if (close < 0)
                return false;
            host = authority[1..close];
            var suffix = authority[(close + 1)..];
            if (suffix.Length != 0 && (suffix[0] != ':' || !TryReadPort(suffix[1..], out port)))
                return false;
            if (!IPAddress.TryParse(host, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
                return false;
            host = address.ToString();
        }
        else
        {
            if (authority.Count(character => character == ':') > 1)
                return false;
            var colon = authority.IndexOf(':');
            if (colon < 0)
                host = authority;
            else
            {
                host = authority[..colon];
                if (!TryReadPort(authority[(colon + 1)..], out port))
                    return false;
            }

            if (host.Length == 0 || host.Contains('@') || !TryCanonicalHost(host, out host))
                return false;
        }

        if (host.Length == 0 || host.Contains('@'))
            return false;
        if (port == 0 || port > 65535)
            return false;

        if (!Uri.TryCreate($"{scheme.ToLowerInvariant()}://{(host.Contains(':') ? $"[{host}]" : host)}{(port >= 0 ? $":{port}" : string.Empty)}", UriKind.Absolute, out var uri)
            || uri.Host.Length == 0
            || !string.IsNullOrEmpty(uri.UserInfo))
            return false;

        var isDefaultPort = (scheme.Equals("http", StringComparison.OrdinalIgnoreCase) && port is -1 or 80)
                            || (scheme.Equals("https", StringComparison.OrdinalIgnoreCase) && port is -1 or 443);
        canonicalOrigin = $"{scheme.ToLowerInvariant()}://{(host.Contains(':') ? $"[{host}]" : host)}{(!isDefaultPort && port >= 0 ? $":{port}" : string.Empty)}";
        return true;
    }

    private static bool TryCanonicalHost(string host, out string canonicalHost)
    {
        canonicalHost = string.Empty;
        if (host.Length is 0 or > 253
            || host.Any(character => character > 0x7f)
            || host.Any(IsUnsafeUrlCharacter)
            || host.Contains('@'))
            return false;

        // A dotted decimal address is accepted only when it is a canonical
        // IPv4 literal. Do not let legacy numeric URI parsing convert
        // alternative numeric forms.
        if (LooksLikePotentialIpv4Literal(host))
        {
            if (IPAddress.TryParse(host, out var address)
                && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                && TryCanonicalDottedDecimal(host, out canonicalHost))
            {
                return true;
            }

            return false;
        }

        // Hexadecimal-only IPv4 notations (`0x...`) are legacy and must remain
        // rejected even when they are syntactically DNS-safe.
        if (IsLegacyHexIpv4(host))
            return false;

        var labels = host.Split('.');
        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63
                || label[0] == '-'
                || label[^1] == '-'
                || !label.All(IsAsciiLdhCharacter))
                return false;
        }

        // Unicode hostnames are intentionally not accepted. The only IDN form
        // allowed is a caller-supplied, lowercase ASCII A-label which survives
        // an explicit IDNA Unicode -> ASCII round trip unchanged.
        if (labels.Any(label => label.StartsWith("xn--", StringComparison.Ordinal)))
        {
            try
            {
                var idn = new IdnMapping();
                var unicode = idn.GetUnicode(host);
                if (!string.Equals(idn.GetAscii(unicode), host, StringComparison.Ordinal))
                    return false;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        canonicalHost = host;
        return true;
    }

    private static bool LooksLikePotentialIpv4Literal(string host)
    {
        if (host.Contains('.'))
        {
            foreach (var character in host)
            {
                if (character != '.' && !IsAsciiDigit(character))
                    return false;
            }

            return true;
        }

        foreach (var character in host)
        {
            if (!IsAsciiDigit(character))
                return false;
        }

        return true;
    }

    private static bool TryCanonicalDottedDecimal(string host, out string canonicalHost)
    {
        canonicalHost = string.Empty;

        var labels = host.Split('.');
        if (labels.Length != 4)
            return false;

        foreach (var label in labels)
        {
            if (label.Length is 0 or > 3)
                return false;

            if (label.Length > 1 && label[0] == '0')
                return false;

            foreach (var character in label)
            {
                if (!IsAsciiDigit(character))
                    return false;
            }

            var value = int.Parse(label);
            if (value is < 0 or > 255)
                return false;
        }

        canonicalHost = host;
        return true;
    }

    private static bool IsLegacyHexIpv4(string host)
    {
        if (!host.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return false;

        var tail = host.AsSpan(2);
        if (tail.Length == 0)
            return false;

        for (var i = 0; i < tail.Length; i++)
        {
            var character = tail[i];
            if (!IsAsciiDigit(character)
                && !(character >= 'a' && character <= 'f')
                && !(character >= 'A' && character <= 'F'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAsciiLdhCharacter(char character)
        => character is >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '-';

    private static bool IsAsciiDigit(char character)
        => character is >= '0' and <= '9';

    private static bool TryReadPort(string value, out int port)
    {
        port = 0;
        return value.Length > 0
               && value.All(character => character is >= '0' and <= '9')
               && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port)
               && port is >= 1 and <= 65535;
    }

    private static bool IsUnsafeUrlCharacter(char character)
    {
        var category = char.GetUnicodeCategory(character);
        return char.IsControl(character)
               || category == UnicodeCategory.Format;
    }
}

public static class SetupManagedNames
{
    public const string Nzbdav = "NZBDAV";
    public const string Sonarr = "NZBDAV Sonarr";
    public const string Radarr = "NZBDAV Radarr";
    public const string MoviesRootDirectory = "NZBDAV Movies";
    public const string TvRootDirectory = "NZBDAV TV";
}

public sealed class SetupSecretValues
{
    public string? Indexers { get; init; }
    public string? JellyfinApiKey { get; init; }
    /// <summary>Removes the temporary privileged Jellyfin session from durable setup state.</summary>
    public bool ClearJellyfinApiKey { get; init; }
    public string? PluginApiKey { get; init; }
    public string? PluginApiKeyPrevious { get; init; }
    public string? PluginApiKeyPreviousExpiresAt { get; init; }
    public string? RunProgress { get; init; }
    public string? RunProgressReasons { get; init; }
    public string? RepairRequiredState { get; init; }
    public string? RepairCheckedAtUtc { get; init; }
    public string? SonarrRepairReason { get; init; }
    public string? RadarrRepairReason { get; init; }
    public bool ClearRepairRequired { get; init; }
    public bool ClearRepairReasons { get; init; }
    public bool ClearSonarrRepairReason { get; init; }
    public bool ClearRadarrRepairReason { get; init; }
    public string? UsenetProviders { get; init; }
    public bool RevocationPending { get; init; }
    public string? RevocationPendingToken { get; init; }
    public bool ClearRevocationPending { get; init; }
    public bool ClearRevocationPendingToken { get; init; }
    public string? CandidateSessionToken { get; init; }
    public string? CandidateSessionOperation { get; init; }
    public string? CandidateSessionIntent { get; init; }
    public bool ClearCandidateSession { get; init; }
}

[JsonConverter(typeof(SetupStepStateJsonConverter))]
public enum SetupStepState
{
    Pending,
    Running,
    Complete,
    Warning,
    Failed,
}

/// <summary>Display-safe, bounded reason codes for a setup step.</summary>
public static class SetupReasonCodes
{
    public const string Unknown = "unknown";
    public const string ProviderFailed = "provider-failed";
    public const string ProviderAuthFailed = "provider-auth-failed";
    public const string IndexerFailed = "indexer-failed";
    public const string IndexerCapabilityFailed = "indexer-capability-failed";
    public const string ConfigurationFailed = "configuration-failed";
    public const string CompatibilityFailed = "compatibility-failed";
    public const string JellyfinVersionFailed = "jellyfin-version-failed";
    public const string LibraryCollision = "library-collision";
    public const string LibraryFailed = "library-failed";
    public const string TaskFailed = "task-failed";
    public const string PluginMismatch = "plugin-mismatch";
    public const string ArrFailed = "arr-failed";
    public const string ProwlarrFailed = "prowlarr-failed";
    public const string ValidationCanceled = "validation-canceled";
    public const string BackendUnavailable = "backend-unavailable";
    public const string NzbdavFailed = "nzbdav-failed";
    public const string SonarrFailed = "sonarr-failed";
    public const string RadarrFailed = "radarr-failed";
    public const string JellyfinFailed = "jellyfin-failed";
    // A completed marker remains authoritative while a live verification run
    // is fenced by another setup worker. This is distinct from a service
    // failure and is safe to expose to status callers.
    public const string SetupRunBusy = "setup-run-busy";
    public const string VerificationBusy = SetupRunBusy;
    public const string VerificationStale = "verification-stale";
}

public sealed record SetupStepStatus
{
    public SetupStepStatus(string name, SetupStepState state, string? reason = null, string? code = null, bool? serviceReady = null)
    {
        Name = SafeToken(name, "setup");
        State = state;
        Reason = SafeTokenOrNull(reason);
        Code = SafeTokenOrNull(code);
        ServiceReady = serviceReady;
    }

    public string Name { get; }
    public SetupStepState State { get; }
    public string? Reason { get; }
    public string? Code { get; }
    public bool? ServiceReady { get; }

    internal static string SafeToken(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64 || !value.All(IsSafeTokenCharacter))
            return fallback;
        return value.ToLowerInvariant();
    }

    private static string? SafeTokenOrNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : SafeToken(value, SetupReasonCodes.Unknown);

    private static bool IsSafeTokenCharacter(char value)
        => value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_';
}

public sealed record SetupServiceStatus
{
    public SetupServiceStatus(string name, bool ready, string? reason = null, string? code = null)
    {
        Name = SetupStepStatus.SafeToken(name, "service");
        Ready = ready;
        Reason = string.IsNullOrWhiteSpace(reason) ? null : SetupStepStatus.SafeToken(reason, SetupReasonCodes.Unknown);
        Code = string.IsNullOrWhiteSpace(code) ? null : SetupStepStatus.SafeToken(code, SetupReasonCodes.Unknown);
    }

    public string Name { get; }
    public bool Ready { get; }
    public string? Reason { get; }
    public string? Code { get; }
}

/// <summary>
/// Deliberately contains only display-safe setup state. Credentials and
/// encrypted values are never members of this response contract.
/// </summary>
public sealed record SetupStatus
{
    public SetupStatus(
        bool Enabled,
        bool Completed,
        IReadOnlyList<SetupStepStatus> Steps,
        bool RevocationPending = false,
        IReadOnlyList<SetupServiceStatus>? Services = null,
        bool RepairRequired = false)
    {
        this.Enabled = Enabled;
        this.Completed = Completed;
        this.Steps = Steps.ToList().AsReadOnly();
        this.RevocationPending = RevocationPending;
        this.Services = (Services ?? []).ToList().AsReadOnly();
        this.RepairRequired = RepairRequired;
    }

    public bool Enabled { get; }
    public bool Completed { get; }
    public IReadOnlyList<SetupStepStatus> Steps { get; }
    public bool RevocationPending { get; }
    public IReadOnlyList<SetupServiceStatus> Services { get; }
    public bool RepairRequired { get; }
}

internal sealed class SetupStepStateJsonConverter : JsonConverter<SetupStepState>
{
    public override SetupStepState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Setup step state must be one of pending, running, complete, warning, or failed.");

        return reader.GetString()?.ToLowerInvariant() switch
        {
            "pending" => SetupStepState.Pending,
            "running" => SetupStepState.Running,
            "complete" => SetupStepState.Complete,
            "warning" => SetupStepState.Warning,
            "failed" => SetupStepState.Failed,
            _ => throw new JsonException("Setup step state must be one of pending, running, complete, warning, or failed."),
        };
    }

    public override void Write(Utf8JsonWriter writer, SetupStepState value, JsonSerializerOptions options)
        => writer.WriteStringValue(value switch
        {
            SetupStepState.Pending => "pending",
            SetupStepState.Running => "running",
            SetupStepState.Complete => "complete",
            SetupStepState.Warning => "warning",
            SetupStepState.Failed => "failed",
            _ => throw new JsonException("Unknown setup step state."),
        });
}

public static class SetupQueueDefaults
{
    public const ArrConfig.QueueAction Action = ArrConfig.QueueAction.DoNothing;

    public static List<ArrConfig.QueueRule> Create() => [];
}
