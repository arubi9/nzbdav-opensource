using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Nzbdav.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public string NzbdavBaseUrl { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 30;
}
