using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Nzbdav.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public string NzbdavBaseUrl { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Local directory where .strm files are written.
    /// Add this path as a Jellyfin library (Movies or TV).
    /// </summary>
    public string LibraryPath { get; set; } = "/media/nzbdav";
}
