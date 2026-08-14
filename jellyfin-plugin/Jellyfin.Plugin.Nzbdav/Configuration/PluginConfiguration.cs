using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Nzbdav.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public string NzbdavBaseUrl { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Upper bound on the number of items accepted while assembling the paged manifest.
    /// It is the guard against an unbounded read from the backend, so it is never
    /// removed; operators with larger libraries raise it. Values of zero or less are
    /// ignored in favour of the default, because a cap of zero rejects every manifest.
    /// </summary>
    public int MaxManifestItems { get; set; } = 250000;

    /// <summary>
    /// Local directory where .strm files are written.
    /// Add this path as a Jellyfin library (Movies or TV).
    /// </summary>
    public string LibraryPath { get; set; } = "/media/nzbdav";
}
