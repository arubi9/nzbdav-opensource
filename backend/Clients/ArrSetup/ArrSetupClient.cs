using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NzbWebDAV.Clients;

namespace NzbWebDAV.Clients.ArrSetup;

/// <summary>Inputs for configuring the SABnzbd-compatible NZBDAV client in Sonarr and Radarr.</summary>
public sealed record ArrSetupOptions(
    string SonarrUrl,
    string SonarrApiKey,
    string RadarrUrl,
    string RadarrApiKey,
    string NzbdavUrl,
    string NzbdavApiKey,
    string TvCategory = "tv",
    string MovieCategory = "movies",
    Func<CancellationToken, Task>? AssertMutationLeaseAsync = null);

public sealed record ArrSetupResult(int Created, int Updated, int Unchanged)
{
    public static ArrSetupResult Empty { get; } = new(0, 0, 0);

    public static ArrSetupResult operator +(ArrSetupResult left, ArrSetupResult right) =>
        new(left.Created + right.Created, left.Updated + right.Updated, left.Unchanged + right.Unchanged);
}

public sealed record ArrManagedResourcesVerification(bool SonarrReady, bool RadarrReady);

public sealed class ArrSetupConflictException(string message) : InvalidOperationException(message);

/// <summary>
/// Isolated setup client for the pinned Sonarr/Radarr v3 APIs. It deliberately
/// only creates or updates download clients and creates missing root folders;
/// root folders have no update operation in the v3 contract.
/// </summary>
public sealed class ArrSetupClient
{
    private const string ApiPrefix = "api/v3";
    private const string ApiKeyHeader = "X-Api-Key";
    private const string DownloadImplementation = "Sabnzbd";
    private const string DownloadContract = "SabnzbdSettings";
    private const string SonarrRootPath = "/data/completed-downloads/tv";
    private const string RadarrRootPath = "/data/completed-downloads/movies";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int MaxResponseBytes = 4 * 1024 * 1024;
    private const int MaxResponseJsonDepth = 64;

    private readonly HttpClient _httpClient;
    private readonly TimeSpan _timeout;

    public ArrSetupClient(TimeSpan? timeout = null)
        : this(SetupHttpClientFactory.Create(), timeout)
    {
    }

    public ArrSetupClient(HttpClient httpClient, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
        _timeout = timeout ?? DefaultTimeout;
        if (_timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    // Kept as a convenient injection-compatible overload for callers that
    // construct setup clients in the same way as other setup clients. The URL
    // and key are not used: each Arr has its own URL and key in the options.
    public ArrSetupClient(HttpClient httpClient, Uri unusedBaseUrl, string unusedApiKey, TimeSpan? timeout = null)
        : this(httpClient, timeout)
    {
        ArgumentNullException.ThrowIfNull(unusedBaseUrl);
        if (!unusedBaseUrl.IsAbsoluteUri || unusedBaseUrl.Scheme is not ("http" or "https"))
            throw new ArgumentException("Base URL must be an absolute HTTP URL.", nameof(unusedBaseUrl));
        if (string.IsNullOrWhiteSpace(unusedApiKey))
            throw new ArgumentException("An API key is required.", nameof(unusedApiKey));
    }

    public ArrSetupClient(HttpClient httpClient, string unusedBaseUrl, string unusedApiKey, TimeSpan? timeout = null)
        : this(httpClient, new Uri(unusedBaseUrl, UriKind.Absolute), unusedApiKey, timeout) { }

    /// <summary>Verifies the concrete managed SAB client and root folder independently in both Arrs.</summary>
    public async Task<ArrManagedResourcesVerification> VerifyManagedResourcesDetailedAsync(
        ArrSetupOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var nzbdav = ValidateOptions(options);
        var targets = new[]
        {
            new Target("Sonarr", "NZBDAV Sonarr", options.SonarrUrl, options.SonarrApiKey, "tvCategory", options.TvCategory, SonarrRootPath),
            new Target("Radarr", "NZBDAV Radarr", options.RadarrUrl, options.RadarrApiKey, "movieCategory", options.MovieCategory, RadarrRootPath),
        };

        var sonarrReady = await VerifyTargetAsync(targets[0], nzbdav, options.AssertMutationLeaseAsync, cancellationToken).ConfigureAwait(false);
        var radarrReady = await VerifyTargetAsync(targets[1], nzbdav, options.AssertMutationLeaseAsync, cancellationToken).ConfigureAwait(false);
        return new ArrManagedResourcesVerification(sonarrReady, radarrReady);
    }

    public async Task<bool> VerifyManagedResourcesAsync(ArrSetupOptions options, CancellationToken cancellationToken = default)
    {
        var result = await VerifyManagedResourcesDetailedAsync(options, cancellationToken).ConfigureAwait(false);
        return result.SonarrReady && result.RadarrReady;
    }

    private async Task<bool> VerifyTargetAsync(
        Target target,
        NzbdavSettings nzbdav,
        Func<CancellationToken, Task>? assertMutationLeaseAsync,
        CancellationToken cancellationToken)
    {
        try
        {
            // Pinned Servarr v3 does not serve the API-prefix root. Its
            // authenticated system-status endpoint is the stable liveness
            // probe before inspecting managed resources.
            _ = await GetAsync(target.Url, target.ApiKey, "system/status", cancellationToken).ConfigureAwait(false);
            var clients = (await GetAsync(target.Url, target.ApiKey, "downloadclient", cancellationToken).ConfigureAwait(false)).AsArray();
            var roots = (await GetAsync(target.Url, target.ApiKey, "rootfolder", cancellationToken).ConfigureAwait(false)).AsArray();
            var matches = FindByName(clients, target.Name);
            if (matches.Count != 1 || !HasImplementation(matches[0], DownloadImplementation)
                || !IsManagedClient(matches[0], target, nzbdav))
                return false;
            var rootMatches = FindByPath(roots, target.RootPath);
            // Servarr's real root-folder GET payload identifies this resource
            // by its path and omits a display name. A returned inaccessible
            // root is still not usable, but a missing non-contract name must
            // not invalidate an otherwise exact managed path.
            if (rootMatches.Count != 1
                || rootMatches[0]["accessible"]?.GetValue<bool?>() == false)
                return false;

            // Servarr masks API-key fields in GET responses. A matching shape
            // is therefore insufficient: test the persisted resource through
            // the pinned v3 testall endpoint so an operator-rotated/stale
            // secret cannot be reported Ready.
            var id = matches[0]["id"]?.GetValue<int?>();
            return id is not null
                && await TestPersistedDownloadClientAsync(target.Url, target.ApiKey, id.Value, assertMutationLeaseAsync, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    public async Task<ArrSetupResult> SetupAsync(
        ArrSetupOptions options,
        CancellationToken cancellationToken = default,
        Func<CancellationToken, Task>? assertMutationLeaseAsync = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var mutationLease = assertMutationLeaseAsync ?? options.AssertMutationLeaseAsync;
        var nzbdav = ValidateOptions(options);

        var targets = new[]
        {
            new Target("Sonarr", "NZBDAV Sonarr", options.SonarrUrl, options.SonarrApiKey, "tvCategory", options.TvCategory, SonarrRootPath),
            new Target("Radarr", "NZBDAV Radarr", options.RadarrUrl, options.RadarrApiKey, "movieCategory", options.MovieCategory, RadarrRootPath)
        };

        // Complete the read phase before any write. This is important both for
        // collision safety and to ensure an invalid second target cannot leave
        // the first target partially configured.
        var states = new List<TargetState>(targets.Length);
        foreach (var target in targets)
        {
            var schema = await GetAsync(target.Url, target.ApiKey, "downloadclient/schema", cancellationToken).ConfigureAwait(false);
            var clients = await GetAsync(target.Url, target.ApiKey, "downloadclient", cancellationToken).ConfigureAwait(false);
            var roots = await GetAsync(target.Url, target.ApiKey, "rootfolder", cancellationToken).ConfigureAwait(false);
            states.Add(new TargetState(target, FindSchema(schema), clients.AsArray(), roots.AsArray()));
        }

        var operations = new List<Operation>();
        var unchanged = 0;
        foreach (var state in states)
        {
            var clients = FindByName(state.Clients, state.Target.Name);
            ValidateClientCollisions(clients, state.Target.Name);
            var existingClient = clients.SingleOrDefault(x => HasImplementation(x, DownloadImplementation));
            var clientPayload = BuildDownloadClient(state.Schema, state.Target, nzbdav, existingClient);
            AddOperation(operations, ref unchanged, state.Target.Url, state.Target.ApiKey, "downloadclient", existingClient, clientPayload);

            var roots = FindByPath(state.Roots, state.Target.RootPath);
            if (roots.Count > 1)
                throw new ArrSetupConflictException($"Arr root folder path '{state.Target.RootPath}' is duplicated.");
            if (roots.Count == 0)
                operations.Add(new Operation(state.Target.Url, state.Target.ApiKey, "rootfolder", null,
                    new JsonObject { ["path"] = state.Target.RootPath }));
            else
                unchanged++;
        }

        var created = 0;
        var updated = 0;
        foreach (var operation in operations)
        {
            var path = operation.Id is null ? operation.Resource : $"{operation.Resource}/{operation.Id.Value}";
            await SendJsonAsync(operation.Id is null ? HttpMethod.Post : HttpMethod.Put,
                operation.Url, operation.ApiKey, path, operation.Payload, cancellationToken,
                mutationLease).ConfigureAwait(false);
            if (operation.Resource == "rootfolder" || operation.Id is null) created++;
            else updated++;
        }

        return new ArrSetupResult(created, updated, unchanged);
    }

    private static NzbdavSettings ValidateOptions(ArrSetupOptions options)
    {
        ValidateHttpUrl(options.SonarrUrl, nameof(options.SonarrUrl));
        ValidateHttpUrl(options.RadarrUrl, nameof(options.RadarrUrl));
        var nzbdavUri = ValidateHttpUrl(options.NzbdavUrl, nameof(options.NzbdavUrl));
        Require(options.SonarrApiKey, nameof(options.SonarrApiKey));
        Require(options.RadarrApiKey, nameof(options.RadarrApiKey));
        Require(options.NzbdavApiKey, nameof(options.NzbdavApiKey));
        ValidateText(options.TvCategory, nameof(options.TvCategory));
        ValidateText(options.MovieCategory, nameof(options.MovieCategory));

        var urlBase = nzbdavUri.AbsolutePath.TrimEnd('/');
        if (urlBase == "/") urlBase = string.Empty;
        return new NzbdavSettings(nzbdavUri.Host, nzbdavUri.IsDefaultPort ? (nzbdavUri.Scheme == "https" ? 443 : 80) : nzbdavUri.Port,
            nzbdavUri.Scheme == "https", urlBase, options.NzbdavApiKey);
    }

    private static Uri ValidateHttpUrl(string value, string name)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException($"{name} must be an absolute HTTP URL without credentials or query parameters.", name);
        return uri;
    }

    private static Uri ParseUrl(string value, string name) => ValidateHttpUrl(value, name);

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A value is required.", name);
    }

    private static void ValidateText(string value, string name)
    {
        Require(value, name);
        if (value.Any(char.IsControl)) throw new ArgumentException("Control characters are not allowed.", name);
    }

    private static JsonObject BuildDownloadClient(JsonObject schema, Target target, NzbdavSettings nzbdav, JsonObject? existing)
    {
        var schemaFields = SchemaFields(schema);
        var requested = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase)
        {
            ["host"] = JsonValue.Create(nzbdav.Host),
            ["port"] = JsonValue.Create(nzbdav.Port),
            ["useSsl"] = JsonValue.Create(nzbdav.UseSsl),
            ["urlBase"] = JsonValue.Create(nzbdav.UrlBase),
            ["apiKey"] = JsonValue.Create(nzbdav.ApiKey),
            [target.CategoryField] = JsonValue.Create(target.Category)
        };
        foreach (var field in requested.Keys)
            if (!schemaFields.ContainsKey(field))
                throw new InvalidOperationException($"{target.Kind} SABnzbd schema does not contain field '{field}'.");

        var existingFields = existing is null ? null : ResourceFields(existing);
        var fields = new JsonArray();
        foreach (var (name, schemaValue) in schemaFields)
        {
            var value = requested.TryGetValue(name, out var desired)
                ? desired?.DeepClone()
                : existingFields?.GetValueOrDefault(name)?.DeepClone() ?? schemaValue?.DeepClone();
            fields.Add(new JsonObject { ["name"] = name, ["value"] = value });
        }

        var payload = existing?.DeepClone().AsObject() ?? new JsonObject();
        payload["name"] = target.Name;
        payload["implementation"] = Text(schema["implementation"]) ?? DownloadImplementation;
        payload["implementationName"] = Text(schema["implementationName"]) ?? "SABnzbd";
        payload["configContract"] = Text(schema["configContract"]) ?? DownloadContract;
        payload["protocol"] = "usenet";
        payload["enable"] = true;
        payload["priority"] ??= 1;
        payload["fields"] = fields;
        return payload;
    }

    private static JsonObject FindSchema(JsonNode root)
    {
        var schema = root.AsArray().Select(x => x?.AsObject()).FirstOrDefault(x => x is not null &&
            string.Equals(Text(x["configContract"]), DownloadContract, StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(Text(x["implementation"]), DownloadImplementation, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(Text(x["implementationName"]), "SABnzbd", StringComparison.OrdinalIgnoreCase)));
        return schema ?? throw new InvalidOperationException("Sonarr/Radarr SABnzbdSettings schema is unavailable.");
    }

    private static List<JsonObject> FindByName(JsonArray resources, string name) => resources
        .Select(x => x?.AsObject()).Where(x => x is not null && string.Equals(Text(x["name"]), name, StringComparison.Ordinal))
        .Cast<JsonObject>().ToList();

    private static List<JsonObject> FindByPath(JsonArray resources, string path) => resources
        .Select(x => x?.AsObject()).Where(x => x is not null && string.Equals(NormalizePath(Text(x["path"])), path, StringComparison.Ordinal))
        .Cast<JsonObject>().ToList();

    private static void ValidateClientCollisions(IEnumerable<JsonObject> matches, string name)
    {
        var list = matches.ToList();
        if (list.Count > 1 || list.Any(x => !HasImplementation(x, DownloadImplementation)))
            throw new ArrSetupConflictException($"Arr download client name '{name}' is already used by another resource.");
    }

    private static void AddOperation(List<Operation> operations, ref int unchanged, Uri url, string key,
        string resource, JsonObject? existing, JsonObject payload)
    {
        if (existing is not null && IsEquivalent(existing, payload)) { unchanged++; return; }
        int? id = existing?["id"] is JsonValue value && value.TryGetValue<int>(out var parsed) ? parsed : null;
        if (existing is not null && id is null) throw new InvalidOperationException("Arr download client has no usable id.");
        operations.Add(new Operation(url, key, resource, id, payload));
    }

    private static bool IsEquivalent(JsonObject old, JsonObject desired)
    {
        if (!string.Equals(Text(old["name"]), Text(desired["name"]), StringComparison.Ordinal) ||
            !string.Equals(Text(old["implementation"]) ?? Text(old["implementationName"]), Text(desired["implementation"]), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Text(old["configContract"]), Text(desired["configContract"]), StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var field in desired["fields"]!.AsArray().OfType<JsonObject>())
        {
            var name = Text(field["name"]);
            if (name is not null && !JsonNode.DeepEquals(ResourceFields(old).GetValueOrDefault(name), field["value"])) return false;
        }
        return JsonNode.DeepEquals(old["enable"], desired["enable"]) && JsonNode.DeepEquals(old["protocol"], desired["protocol"]);
    }

    private static bool IsManagedClient(JsonObject resource, Target target, NzbdavSettings expected)
    {
        var fields = ResourceFields(resource);
        var uriBase = fields.GetValueOrDefault("urlBase")?.GetValue<string>() ?? string.Empty;
        var host = fields.GetValueOrDefault("host")?.GetValue<string>();
        var port = fields.GetValueOrDefault("port")?.GetValue<int?>();
        var category = fields.GetValueOrDefault(target.CategoryField)?.GetValue<string>();
        var apiKey = fields.GetValueOrDefault("apiKey")?.GetValue<string>();
        return string.Equals(Text(resource["configContract"]), DownloadContract, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Text(resource["protocol"]), "usenet", StringComparison.OrdinalIgnoreCase)
            && JsonValueEquals(resource["enable"], true)
            && JsonValueEquals(fields.GetValueOrDefault("useSsl"), expected.UseSsl)
            && string.Equals(host, expected.Host, StringComparison.OrdinalIgnoreCase)
            && port == expected.Port
            && string.Equals(uriBase.TrimEnd('/'), expected.UrlBase.TrimEnd('/'), StringComparison.Ordinal)
            && string.Equals(category, target.Category, StringComparison.Ordinal)
            // A masked value proves that the resource has a secret without
            // exposing it; blank/missing values are never ready.
            && (!string.IsNullOrWhiteSpace(apiKey)
                && (string.Equals(apiKey, expected.ApiKey, StringComparison.Ordinal) || apiKey == "********"));
    }

    private static bool JsonValueEquals(JsonNode? value, bool expected)
        => value is JsonValue json && json.TryGetValue<bool>(out var actual) && actual == expected;

    private static bool HasImplementation(JsonObject resource, string implementation) =>
        string.Equals(Text(resource["implementation"]) ?? Text(resource["implementationName"]), implementation, StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, JsonNode?> SchemaFields(JsonObject schema) => Fields(schema["fields"]);
    private static Dictionary<string, JsonNode?> ResourceFields(JsonObject resource) => Fields(resource["fields"]);
    private static Dictionary<string, JsonNode?> Fields(JsonNode? node)
    {
        var fields = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in node?.AsArray() ?? [])
            if (field is JsonObject item && Text(item["name"]) is { } name) fields[name] = item["value"]?.DeepClone();
        return fields;
    }

    private async Task<bool> TestPersistedDownloadClientAsync(
        Uri url,
        string key,
        int id,
        Func<CancellationToken, Task>? assertMutationLeaseAsync,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            url,
            key,
            "downloadclient/testall",
            null,
            cancellationToken,
            assertMutationLeaseAsync).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return false;

        var result = await ReadJsonAsync(response, "downloadclient/testall", cancellationToken).ConfigureAwait(false);
        foreach (var item in result.AsArray())
        {
            if (item is not JsonObject objectItem
                || objectItem["id"]?.GetValue<int?>() != id)
                continue;
            return objectItem["isValid"]?.GetValue<bool?>() == true;
        }

        return false;
    }

    private async Task<JsonNode> GetAsync(Uri url, string key, string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, url, key, path, null, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync(response, path, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendJsonAsync(HttpMethod method, Uri url, string key, string path, JsonObject payload, CancellationToken cancellationToken, Func<CancellationToken, Task>? assertMutationLeaseAsync)
    {
        using var response = await SendAsync(method, url, key, path, payload, cancellationToken, assertMutationLeaseAsync).ConfigureAwait(false);
        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Accepted or HttpStatusCode.Created or HttpStatusCode.NoContent))
            throw new ArrSetupHttpException(path, response.StatusCode);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string key, string path, JsonObject? payload, CancellationToken cancellationToken, Func<CancellationToken, Task>? assertMutationLeaseAsync = null)
        => await SendAsync(method, ParseUrl(url, "Arr URL"), key, path, payload, cancellationToken, assertMutationLeaseAsync).ConfigureAwait(false);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri url, string key, string path, JsonObject? payload, CancellationToken cancellationToken, Func<CancellationToken, Task>? assertMutationLeaseAsync = null)
    {
        using var request = new HttpRequestMessage(method, Endpoint(url, path));
        request.Headers.TryAddWithoutValidation(ApiKeyHeader, key);
        if (payload is not null) request.Content = new StringContent(payload.ToJsonString(JsonOptions), Encoding.UTF8, "application/json");
        var isMutation = assertMutationLeaseAsync is not null
            && (method == HttpMethod.Post || method == HttpMethod.Put || method == HttpMethod.Delete);
        if (isMutation)
            await assertMutationLeaseAsync!(cancellationToken).ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        HttpResponseMessage? response = null;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (SetupHttpClientFactory.IsRedirect(response))
            {
                var status = response.StatusCode;
                response.Dispose();
                response = null;
                throw new ArrSetupHttpException(path, status);
            }

            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException($"Arr request to /{ApiPrefix}/{path} timed out."); }
        finally
        {
            if (isMutation)
            {
                try
                {
                    // A lease takeover must be observed even when the request
                    // failed or the caller cancelled while it was in flight.
                    await assertMutationLeaseAsync!(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    response?.Dispose();
                    throw;
                }
            }
        }
    }

    private static async Task<JsonNode> ReadJsonAsync(HttpResponseMessage response, string path, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode) throw new ArrSetupHttpException(path, response.StatusCode);

        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
            throw new InvalidOperationException($"Arr response to /{ApiPrefix}/{path} is too large.");

        var content = await ReadResponseTextAsync(response, path, cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = MaxResponseJsonDepth });
            return JsonNode.Parse(document.RootElement.GetRawText()) ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"Arr returned invalid JSON for /{ApiPrefix}/{path}.");
        }
    }

    private static async Task<string> ReadResponseTextAsync(HttpResponseMessage response, string path, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream(Math.Min((int)Math.Min(4096, MaxResponseBytes), MaxResponseBytes));
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > MaxResponseBytes)
                throw new InvalidOperationException($"Arr response to /{ApiPrefix}/{path} is too large.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static Uri Endpoint(Uri baseUrl, string path) => new($"{baseUrl.GetLeftPart(UriPartial.Path).TrimEnd('/')}/{ApiPrefix}/{path.TrimStart('/')}");
    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static string NormalizePath(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().Replace('\\', '/');
        while (normalized.Contains("//", StringComparison.Ordinal)) normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        return normalized.Length > 1 ? normalized.TrimEnd('/') : normalized;
    }

    private sealed record Target(string Kind, string Name, Uri Url, string ApiKey, string CategoryField, string Category, string RootPath)
    {
        public Target(string kind, string name, string url, string apiKey, string categoryField, string category, string rootPath)
            : this(kind, name, ParseUrl(url, $"{kind} URL"), apiKey, categoryField, category, rootPath) { }
    }
    private sealed record NzbdavSettings(string Host, int Port, bool UseSsl, string UrlBase, string ApiKey);
    private sealed record TargetState(Target Target, JsonObject Schema, JsonArray Clients, JsonArray Roots);
    private sealed record Operation(Uri Url, string ApiKey, string Resource, int? Id, JsonObject Payload);
}

public sealed class ArrSetupHttpException : HttpRequestException
{
    public ArrSetupHttpException(string path, HttpStatusCode statusCode)
        : base(
            $"Arr request to /api/v3/{path.TrimStart('/')} failed ({(int)statusCode} {statusCode}).",
            inner: null,
            statusCode)
    {
    }
}
