using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Nzbdav;
using Jellyfin.Plugin.Nzbdav.Configuration;

namespace Jellyfin.Plugin.Nzbdav.Api;

public sealed class NzbdavApiClient
{
    private static readonly HttpClient SharedHttp = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    }) { Timeout = Timeout.InfiniteTimeSpan };

    // Bounds the assembled tree rather than a single response. Paging means the
    // server no longer caps what a library may contain, so this is the remaining
    // guard against an unbounded read; at roughly 200 bytes per item it is about
    // 50 MB, which a Jellyfin host can absorb.
    private const int MaxManifestItems = 250_000;

    private const int MaxManifestWalkRestarts = 3;
    private const int MaxManifestContentLength = 8 * 1024 * 1024;
    private const int MaxManifestJsonDepth = 64;
    private const int MaxManifestStringBytes = 8 * 1024 * 1024;
    private const int MaxBrowseContentLength = 2 * 1024 * 1024;
    private const int MaxMetaContentLength = 256 * 1024;
    private const int MaxProbeContentLength = 8 * 1024 * 1024;
    private const int MaxJsonDepth = 32;
    private const int MaxJsonArrayItems = 50_000;
    private const int MaxJsonObjectProperties = 256;
    private const int MaxJsonStringBytes = 1 * 1024 * 1024;
    private const int MaxManifestPathLength = 1_024;
    private const int MaxManifestPathSegments = 128;
    private const int MaxManifestNameLength = 255;

    private static readonly JsonSerializerOptions ManifestResponseJsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = MaxManifestJsonDepth };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = MaxJsonDepth };
    private readonly PluginConfiguration _config;
    private readonly HttpClient _http;
    private readonly int _maxManifestItems;
    private readonly int _maxManifestContentLength;

    public NzbdavApiClient(PluginConfiguration config, HttpMessageHandler? handler = null,
        int? maxManifestItems = null, int? maxManifestContentLength = null)
    {
        _config = config;
        // SharedHttp is configured once at type initialization. HttpClient
        // properties cannot be changed after the first request is sent.
        _http = handler is null ? SharedHttp : CreateHttpClient(handler);
        _maxManifestItems = maxManifestItems ?? MaxManifestItems;
        _maxManifestContentLength = maxManifestContentLength ?? MaxManifestContentLength;
    }

    private static HttpClient CreateHttpClient(HttpMessageHandler handler)
    {
        if (handler is SocketsHttpHandler sockets) sockets.AllowAutoRedirect = false;
        if (handler is HttpClientHandler http) http.AllowAutoRedirect = false;
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public Task<BrowseResponse?> BrowseAsync(string path, CancellationToken ct)
        => SendJsonAsync<BrowseResponse>(CreateRequest(HttpMethod.Get, $"{BaseUrl}/api/browse/{path.TrimStart('/')}"),
            MaxBrowseContentLength, ct);

    public async Task<MetaResponse?> GetMetaAsync(Guid id, CancellationToken ct)
    {
        var meta = await SendJsonAsync<MetaResponse>(
            CreateRequest(HttpMethod.Get, $"{BaseUrl}/api/meta/{id}"), MaxMetaContentLength, ct)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(meta?.StreamToken)
            && !IsCanonicalStreamToken(meta.StreamToken))
            throw new HttpRequestException("Metadata response contained a malformed stream token.");
        return meta;
    }

    private static bool IsCanonicalStreamToken(string token)
    {
        var separator = token.IndexOf('.');
        if (separator <= 0 || separator == token.Length - 1 || token.IndexOf('.', separator + 1) >= 0)
            return false;
        var expiry = token[..separator];
        if (expiry.Length > 19 || (expiry.Length > 1 && expiry[0] == '0')
            || !long.TryParse(expiry, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var value)
            || value <= 0)
            return false;
        var signature = token[(separator + 1)..];
        if (signature.Length != 43)
            return false;
        for (var index = 0; index < signature.Length; index++)
        {
            var character = signature[index];
            if (!(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_'))
                return false;
        }
        return (Base64UrlValue(signature[^1]) & 0b11) == 0;
    }

    private static int Base64UrlValue(char value)
        => value is >= 'A' and <= 'Z' ? value - 'A'
            : value is >= 'a' and <= 'z' ? value - 'a' + 26
            : value is >= '0' and <= '9' ? value - '0' + 52
            : value == '-' ? 62 : 63;

    public string GetSignedStreamUrl(Guid id, string streamToken)
        => $"{BaseUrl}/api/stream/{id}?token={Uri.EscapeDataString(streamToken)}";

    public async Task<string> GetProbeDataAsync(Guid id, CancellationToken ct)
    {
        var request = CreateRequest(HttpMethod.Get, $"{BaseUrl}/api/probe/{id}");
        using (request)
        using (var timeoutCts = CreateTimeoutToken(ct))
        using (var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false))
        {
            // A missing probe is a failed item, not an optional success: the
            // manifest advertised it and the sync worker must retry without
            // recording a successful marker/ETag.
            response.EnsureSuccessStatusCode();
            var body = await ReadBodyAsync(response.Content, MaxProbeContentLength, timeoutCts.Token, "Probe").ConfigureAwait(false);
            try
            {
                using var document = ParseJson(body, MaxJsonDepth);
                ValidateJsonLimits(document.RootElement, MaxJsonArrayItems, MaxJsonStringBytes);
                // Parse the provider DTO here, before returning raw bytes to the
                // sync writer. The provider repeats this same semantic parse
                // when it consumes the sidecar.
                _ = FfprobeJsonParser.Parse(body);
                return new UTF8Encoding(false, true).GetString(body);
            }
            catch (JsonException exception)
            {
                throw new HttpRequestException("Probe response was malformed JSON.", exception);
            }
            catch (DecoderFallbackException exception)
            {
                throw new HttpRequestException("Probe response was not valid UTF-8.", exception);
            }
        }
    }

    /// <summary>
    /// Retrieves the whole /content tree, following pages when the server serves them.
    /// Callers receive one assembled manifest either way.
    /// </summary>
    public async Task<(ManifestResponse? Manifest, string? ETag)> GetManifestAsync(string? ifNoneMatch, CancellationToken ct)
    {
        // A tree that changes mid-walk yields a torn view, so the walk restarts. Content
        // is added continuously during a large import, so a couple of restarts are
        // expected; an endless supply of them is a signal to give up and let the next
        // scheduled sync try, rather than to keep hammering the server.
        for (var attempt = 0; ; attempt++)
        {
            var walk = await TryWalkManifestAsync(ifNoneMatch, ct).ConfigureAwait(false);
            if (!walk.Torn) return (walk.Manifest, walk.ETag);
            if (attempt >= MaxManifestWalkRestarts)
                throw new HttpRequestException("Manifest changed on the server during every paged read attempt.");
        }
    }

    private async Task<(ManifestResponse? Manifest, string? ETag, bool Torn)> TryWalkManifestAsync(
        string? ifNoneMatch, CancellationToken ct)
    {
        var items = new List<ManifestItem>();
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        string? firstETag = null;
        string? version = null;
        var versionKnown = false;

        while (true)
        {
            // Only the first page is revalidated; the server refuses If-None-Match on
            // later pages precisely because a 304 there would mean nothing useful.
            var (page, etag, notModified) = await GetManifestPageAsync(
                cursor is null ? ifNoneMatch : null, cursor, ct).ConfigureAwait(false);

            if (notModified) return (null, etag, false);
            firstETag ??= etag;

            if (!versionKnown)
            {
                version = page.Version;
                versionKnown = true;
            }
            else if (!string.Equals(version, page.Version, StringComparison.Ordinal))
            {
                return (null, null, true);
            }

            items.AddRange(page.Items);
            if (items.Count > _maxManifestItems)
                throw new HttpRequestException("Manifest response contains too many items.");

            cursor = page.NextCursor;
            if (string.IsNullOrEmpty(cursor)) break;

            // A cursor that repeats would loop forever and grow items without bound.
            if (!seenCursors.Add(cursor))
                throw new HttpRequestException("Manifest paging did not advance.");
        }

        // Uniqueness has to be judged across the assembled set, not per page: two pages
        // could each be internally consistent and still collide with each other.
        var manifest = new ManifestResponse
        {
            ItemCount = items.Count,
            Items = items.ToArray()
        };
        ValidateManifest(manifest);
        return (manifest, firstETag, false);
    }

    private async Task<(ManifestResponse Page, string? ETag, bool NotModified)> GetManifestPageAsync(
        string? ifNoneMatch, string? after, CancellationToken ct)
    {
        // paged=true is an opt-in the server requires before it will paginate, so that
        // it never hands a truncated tree to a plugin that cannot follow cursors.
        var url = $"{BaseUrl}/api/manifest?paged=true";
        if (!string.IsNullOrEmpty(after)) url += $"&after={Uri.EscapeDataString(after)}";

        var request = CreateRequest(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(ifNoneMatch))
            request.Headers.IfNoneMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue(ifNoneMatch));

        using (request)
        using (var timeoutCts = CreateTimeoutToken(ct))
        using (var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false))
        {
            var etag = response.Headers.ETag?.Tag;
            if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
                return (new ManifestResponse(), etag, true);
            response.EnsureSuccessStatusCode();

            var body = await ReadBodyAsync(response.Content, _maxManifestContentLength, timeoutCts.Token, "Manifest").ConfigureAwait(false);
            if (body.Length == 0) throw new HttpRequestException("Manifest response body is empty.");
            using var document = ParseManifestJson(body);
            ValidateJsonLimits(document.RootElement, MaxJsonArrayItems, MaxManifestStringBytes);
            var page = JsonSerializer.Deserialize<ManifestResponse>(body, ManifestResponseJsonOptions)
                ?? throw new HttpRequestException("Manifest response was malformed.");
            if (page.Items is null || page.ItemCount != page.Items.Length)
                throw new HttpRequestException("Manifest item count is invalid.");
            return (page, etag, false);
        }
    }

    private string BaseUrl => _config.NzbdavBaseUrl.TrimEnd('/');

    private HttpRequestMessage CreateRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        // Keep the API key in the request header only; in particular, never
        // copy it into a query string that can be logged by a redirect target.
        request.Headers.Add("X-Api-Key", _config.ApiKey);
        return request;
    }

    private CancellationTokenSource CreateTimeoutToken(CancellationToken ct)
    {
        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _config.TimeoutSeconds)));
        return timeoutCts;
    }

    private async Task<T?> SendJsonAsync<T>(HttpRequestMessage request, int maxBytes, CancellationToken ct)
    {
        using (request)
        using (var timeoutCts = CreateTimeoutToken(ct))
        using (var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            var body = await ReadBodyAsync(response.Content, maxBytes, timeoutCts.Token, typeof(T).Name).ConfigureAwait(false);
            using var document = ParseJson(body, MaxJsonDepth);
            ValidateJsonLimits(document.RootElement, MaxJsonArrayItems, MaxJsonStringBytes);
            return JsonSerializer.Deserialize<T>(body, JsonOptions);
        }
    }

    private static JsonDocument ParseManifestJson(byte[] body)
    {
        try
        {
            return ParseJson(body, MaxManifestJsonDepth);
        }
        catch (JsonException exception)
        {
            // Keep malformed manifest failures on the documented JsonException
            // surface rather than exposing the reader's more specific type.
            throw new JsonException("Manifest response was malformed JSON.", exception);
        }
    }

    private static JsonDocument ParseJson(byte[] body, int maxDepth)
        => JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = maxDepth, CommentHandling = JsonCommentHandling.Disallow });

    private static void ValidateJsonLimits(JsonElement root, int maxArrayItems, int maxStringBytes)
    {
        var arrays = 0;
        var properties = 0;
        var strings = 0L;
        var stack = new Stack<JsonElement>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var element = stack.Pop();
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    if (++strings > maxStringBytes || Encoding.UTF8.GetByteCount(element.GetString() ?? string.Empty) > maxStringBytes)
                        throw new HttpRequestException("JSON contains oversized strings.");
                    break;
                case JsonValueKind.Array:
                    if (++arrays > maxArrayItems || element.GetArrayLength() > maxArrayItems)
                        throw new HttpRequestException("JSON contains too many array items.");
                    foreach (var child in element.EnumerateArray()) stack.Push(child);
                    break;
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        if (++properties > MaxJsonObjectProperties * maxArrayItems)
                            throw new HttpRequestException("JSON contains too many object properties.");
                        stack.Push(property.Value);
                    }
                    break;
            }
        }
    }

    private static async Task<byte[]> ReadBodyAsync(HttpContent content, int maxBytes, CancellationToken ct, string label)
    {
        if (maxBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        var declaredLength = content.Headers.ContentLength;
        if (declaredLength is < 0 or > int.MaxValue || declaredLength > maxBytes)
            throw new HttpRequestException($"{label} response is too large.");

        await using var source = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var destination = new MemoryStream(Math.Min(maxBytes + 1, 1024 * 1024));
        var buffer = new byte[16_384];
        var total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) break;
            if (read > maxBytes + 1 - total)
                throw new HttpRequestException($"{label} response is too large.");
            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            total += read;
            // max+1 is the bounded probe read: it detects an oversized body
            // without ever buffering an unbounded response.
            if (total > maxBytes)
                throw new HttpRequestException($"{label} response is too large.");
        }

        if (declaredLength is not null && declaredLength.Value != total)
            throw new HttpRequestException($"{label} response Content-Length did not match the body.");
        return destination.ToArray();
    }

    private void ValidateManifest(ManifestResponse manifest)
    {
        if (manifest.Items is null || manifest.ItemCount < 0 || manifest.ItemCount != manifest.Items.Length)
            throw new HttpRequestException("Manifest item count is invalid.");
        if (manifest.ItemCount > _maxManifestItems)
            throw new HttpRequestException("Manifest response contains too many items.");

        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var seenIds = new HashSet<Guid>(manifest.Items.Length);
        var seenPaths = new HashSet<string>(manifest.Items.Length, pathComparer);
        foreach (var item in manifest.Items)
        {
            if (string.IsNullOrWhiteSpace(item.Path) || string.IsNullOrWhiteSpace(item.Name) || string.IsNullOrWhiteSpace(item.Type))
                throw new HttpRequestException("Manifest item contains missing required fields.");
            if (item.Path.Length > MaxManifestPathLength || item.Name.Length > MaxManifestNameLength
                || item.Type.Length > 32)
                throw new HttpRequestException("Manifest item string is too long.");
            if (item.Path.Contains('\\') || item.Path.Contains("../") || item.Path.Contains("..\\"))
                throw new HttpRequestException("Manifest item path has invalid traversal characters.");
            var normalizedPath = item.Path.Trim();
            var segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0 || segments.Length > MaxManifestPathSegments || normalizedPath.Length != item.Path.Length)
                throw new HttpRequestException("Manifest item path is malformed.");
            if (!seenIds.Add(item.Id) || !seenPaths.Add(normalizedPath))
                throw new HttpRequestException("Manifest contains duplicate IDs or final destinations.");
        }
    }
}
