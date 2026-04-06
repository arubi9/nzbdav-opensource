using System.Net.Http.Json;
using Jellyfin.Plugin.Nzbdav.Configuration;

namespace Jellyfin.Plugin.Nzbdav.Api;

public sealed class NzbdavApiClient
{
    private static readonly HttpClient SharedHttp = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    });

    private readonly PluginConfiguration _config;

    public NzbdavApiClient(PluginConfiguration config)
    {
        _config = config;
    }

    public Task<BrowseResponse?> BrowseAsync(string path, CancellationToken ct)
    {
        var url = $"{BaseUrl}/api/browse/{path.TrimStart('/')}";
        var request = CreateRequest(HttpMethod.Get, url);
        return SendJsonAsync<BrowseResponse>(request, ct);
    }

    public Task<MetaResponse?> GetMetaAsync(Guid id, CancellationToken ct)
    {
        var url = $"{BaseUrl}/api/meta/{id}";
        var request = CreateRequest(HttpMethod.Get, url);
        return SendJsonAsync<MetaResponse>(request, ct);
    }

    public string GetSignedStreamUrl(Guid id, string streamToken)
        => $"{BaseUrl}/api/stream/{id}?token={streamToken}";

    private string BaseUrl => _config.NzbdavBaseUrl.TrimEnd('/');

    private HttpRequestMessage CreateRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Api-Key", _config.ApiKey);
        return request;
    }

    private static async Task<T?> SendJsonAsync<T>(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await SharedHttp.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(ct).ConfigureAwait(false);
    }
}
