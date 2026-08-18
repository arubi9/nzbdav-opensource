using Jellyfin.Plugin.Nzbdav.Configuration;

namespace Jellyfin.Plugin.Nzbdav;

/// <summary>
/// Immutable configuration lease for one NZBDAV operation. Configuration can
/// be edited while Jellyfin awaits I/O, so callers must capture this once at
/// their operation boundary rather than retaining <see cref="PluginConfiguration"/>.
/// </summary>
internal sealed record NzbdavOperationConfiguration(
    string NzbdavBaseUrl,
    string LibraryPath,
    string ApiKey,
    int TimeoutSeconds)
{
    public bool IsValid
        => !string.IsNullOrWhiteSpace(NzbdavBaseUrl)
           && !string.IsNullOrWhiteSpace(LibraryPath)
           && TryParseBackendBaseUri(NzbdavBaseUrl, out _);

    public static NzbdavOperationConfiguration? Capture(PluginConfiguration? configuration)
    {
        if (configuration is null
            || string.IsNullOrWhiteSpace(configuration.NzbdavBaseUrl)
            || string.IsNullOrWhiteSpace(configuration.LibraryPath)
            || !TryParseBackendBaseUri(configuration.NzbdavBaseUrl, out var baseUri))
        {
            return null;
        }

        try
        {
            return new NzbdavOperationConfiguration(
                baseUri.AbsoluteUri.TrimEnd('/'),
                GetCanonicalLibraryRoot(configuration.LibraryPath),
                configuration.ApiKey ?? string.Empty,
                configuration.TimeoutSeconds);
        }
        catch
        {
            // Bad path spellings must not escape the operation boundary and
            // leave a scheduled task with a partially validated configuration.
            return null;
        }
    }

    public PluginConfiguration ToPluginConfiguration() => new()
    {
        NzbdavBaseUrl = NzbdavBaseUrl,
        LibraryPath = LibraryPath,
        ApiKey = ApiKey,
        TimeoutSeconds = TimeoutSeconds
    };

    private static bool TryParseBackendBaseUri(string value, out Uri baseUri)
    {
        baseUri = null!;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed)
            || parsed.Scheme is not ("http" or "https")
            || string.IsNullOrEmpty(parsed.Host)
            || !string.IsNullOrEmpty(parsed.UserInfo)
            || !string.IsNullOrEmpty(parsed.Fragment)
            || !string.IsNullOrEmpty(parsed.Query))
        {
            return false;
        }

        baseUri = parsed;
        return true;
    }

    private static string GetCanonicalLibraryRoot(string libraryRoot)
    {
        var fullRoot = Path.GetFullPath(libraryRoot);
        var pathRoot = Path.GetPathRoot(fullRoot);
        if (string.IsNullOrWhiteSpace(pathRoot))
            throw new ArgumentException("Cannot determine library path root.", nameof(libraryRoot));

        var trimmed = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var trimmedRoot = pathRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(trimmed, trimmedRoot, StringComparison.Ordinal)
            ? pathRoot
            : string.IsNullOrEmpty(trimmed) ? pathRoot : trimmed;
    }
}

/// <summary>
/// The only production bridge from Jellyfin's mutable plugin configuration to
/// an operation-local configuration lease.
/// </summary>
internal static class NzbdavOperationConfigurationAccessor
{
    public static NzbdavOperationConfiguration? CaptureFromPlugin()
        => NzbdavOperationConfiguration.Capture(Plugin.Instance?.Configuration);
}
