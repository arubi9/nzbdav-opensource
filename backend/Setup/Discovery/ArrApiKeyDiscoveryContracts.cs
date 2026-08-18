namespace NzbWebDAV.Setup.Discovery;

public enum ArrService
{
    Sonarr,
    Radarr,
    Prowlarr
}

public enum ArrConfigDiscoveryFailure
{
    InvalidOptions,
    InvalidPath,
    Missing,
    TooLarge,
    Unreadable,
    PlatformNotSupported,
    Malformed,
    MissingApiKey,
    EmptyApiKey
}

/// <summary>
/// Discovery's production locations are deliberately not configurable. This
/// internal type is only a seam for tests of file and parser behavior.
/// </summary>
internal sealed class ArrConfigDiscoveryPaths
{
    internal ArrConfigDiscoveryPaths()
    {
    }

    internal ArrConfigDiscoveryPaths(string sonarrConfigPath, string radarrConfigPath, string prowlarrConfigPath)
    {
        SonarrConfigPath = sonarrConfigPath;
        RadarrConfigPath = radarrConfigPath;
        ProwlarrConfigPath = prowlarrConfigPath;
    }

    internal string SonarrConfigPath { get; init; } = string.Empty;
    internal string RadarrConfigPath { get; init; } = string.Empty;
    internal string ProwlarrConfigPath { get; init; } = string.Empty;

    internal string GetPath(ArrService service) => service switch
    {
        ArrService.Sonarr => SonarrConfigPath,
        ArrService.Radarr => RadarrConfigPath,
        ArrService.Prowlarr => ProwlarrConfigPath,
        _ => throw new ArgumentOutOfRangeException(nameof(service))
    };
}

/// <summary>
/// Internal test-only options. No public API accepts arbitrary filesystem
/// paths or read limits.
/// </summary>
internal sealed class ArrConfigDiscoveryOptions
{
    internal ArrConfigDiscoveryOptions()
    {
    }

    internal ArrConfigDiscoveryOptions(ArrConfigDiscoveryPaths paths, long maxFileBytes = DefaultMaxFileBytes)
    {
        Paths = paths;
        MaxFileBytes = maxFileBytes;
    }

    internal const long DefaultMaxFileBytes = 1024 * 1024;
    internal const long AbsoluteMaxFileBytes = 4 * 1024 * 1024;

    internal ArrConfigDiscoveryPaths Paths { get; init; } = new();
    internal long MaxFileBytes { get; init; } = DefaultMaxFileBytes;

    // Deterministic synchronization points used by the security tests. They
    // are intentionally not part of the production configuration surface.
    internal Action? AfterInitialLengthObserved { get; init; }
    internal Action? BeforeFinalOpen { get; init; }
    internal Action? ParseStarted { get; init; }

    // Explicit parser-only seam for tests. The stream must already be opened by
    // the caller; production discovery never accepts a filesystem path on
    // unsupported platforms.
    internal Func<ArrService, FileStream>? TestOpenedFileFactory { get; init; }

    internal static ArrConfigDiscoveryOptions Production { get; } = new(
        new ArrConfigDiscoveryPaths(
            "/bootstrap/sonarr/config.xml",
            "/bootstrap/radarr/config.xml",
            "/bootstrap/prowlarr/config.xml"));
}

/// <summary>
/// The result of discovery. The API key is intentionally omitted from the
/// diagnostic representation; callers must use the property to access it.
/// </summary>
public sealed class ArrApiKeyDiscoveryResult
{
    public ArrApiKeyDiscoveryResult(ArrService service, string apiKey)
    {
        Service = service;
        ApiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
    }

    public ArrService Service { get; }
    public string ApiKey { get; }

    public override string ToString() => $"{nameof(ArrApiKeyDiscoveryResult)} {{ Service = {Service} }}";
}

/// <summary>
/// Deliberately omits both the configured path and the file contents from its
/// message. Callers can use the typed service and failure instead.
/// </summary>
public sealed class ArrConfigDiscoveryException : Exception
{
    public ArrConfigDiscoveryException(
        ArrService service,
        ArrConfigDiscoveryFailure failure)
        : base($"Unable to discover the {service} API key ({failure}).")
    {
        Service = service;
        Failure = failure;
    }

    public ArrService Service { get; }
    public ArrConfigDiscoveryFailure Failure { get; }
}
