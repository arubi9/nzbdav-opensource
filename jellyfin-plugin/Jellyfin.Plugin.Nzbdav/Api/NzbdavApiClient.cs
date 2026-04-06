using System.Net.Http.Json;
using Jellyfin.Plugin.Nzbdav.Configuration;

namespace Jellyfin.Plugin.Nzbdav.Api;

public sealed class NzbdavApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly PluginConfiguration _config;

    public NzbdavApiClient(PluginConfiguration config)
    {
        _config = config;
        _http = new HttpClient
        {
            BaseAddress = new Uri(config.NzbdavBaseUrl.TrimEnd('/')),
            Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds)
        };
        _http.DefaultRequestHeaders.Add("X-Api-Key", config.ApiKey);
    }

    public Task<BrowseResponse?> BrowseAsync(string path, CancellationToken ct)
        => _http.GetFromJsonAsync<BrowseResponse>($"/api/browse/{path.TrimStart('/')}", ct);

    public Task<MetaResponse?> GetMetaAsync(Guid id, CancellationToken ct)
        => _http.GetFromJsonAsync<MetaResponse>($"/api/meta/{id}", ct);

    public string GetSignedStreamUrl(Guid id, string streamToken)
        => $"{_config.NzbdavBaseUrl.TrimEnd('/')}/api/stream/{id}?token={streamToken}";

    public void Dispose() => _http.Dispose();
}
