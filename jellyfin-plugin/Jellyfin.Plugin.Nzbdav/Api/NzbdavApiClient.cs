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

    public async Task<string?> GetProbeDataAsync(Guid id, CancellationToken ct)
    {
        var request = CreateRequest(HttpMethod.Get, $"{BaseUrl}/api/probe/{id}");
        try
        {
            using (request)
            {
                using var response = await SharedHttp.SendAsync(request, ct).ConfigureAwait(false);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return null;
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
        }
        catch { return null; }
    }

    /// <summary>
    /// Fetch the entire /content tree in one request. ETag-cached — pass the
    /// previous ETag and get 304 Not Modified if nothing changed.
    /// </summary>
    public async Task<(ManifestResponse? Manifest, string? ETag)> GetManifestAsync(
        string? ifNoneMatch, CancellationToken ct)
    {
        var request = CreateRequest(HttpMethod.Get, $"{BaseUrl}/api/manifest");
        if (!string.IsNullOrEmpty(ifNoneMatch))
            request.Headers.IfNoneMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue(ifNoneMatch));

        using (request)
        {
            using var response = await SharedHttp.SendAsync(request, ct).ConfigureAwait(false);
            var etag = response.Headers.ETag?.Tag;

            if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
                return (null, etag);

            response.EnsureSuccessStatusCode();
            var manifest = await response.Content.ReadFromJsonAsync<ManifestResponse>(ct).ConfigureAwait(false);
            return (manifest, etag);
        }
    }

    private string BaseUrl => _config.NzbdavBaseUrl.TrimEnd('/');

    private HttpRequestMessage CreateRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Api-Key", _config.ApiKey);
        return request;
    }

    private static async Task<T?> SendJsonAsync<T>(HttpRequestMessage request, CancellationToken ct)
    {
        using (request)
        {
            using var response = await SharedHttp.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<T>(ct).ConfigureAwait(false);
        }
    }
}
