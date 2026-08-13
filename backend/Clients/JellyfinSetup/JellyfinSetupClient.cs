using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NzbWebDAV.Clients;

namespace NzbWebDAV.Clients.JellyfinSetup;

/// <summary>
/// Small, stateful setup client for the pinned Jellyfin 10.11 API. It never
/// writes a library without first reading and checking its existing path.
/// </summary>
public sealed class JellyfinSetupClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = 64 };
    private const int MaxResponseBytes = 1024 * 1024;
    private readonly HttpClient _httpClient;
    private readonly JellyfinSetupOptions _options;
    private string? _sessionToken;

    public JellyfinSetupClient(JellyfinSetupOptions options)
        : this(SetupHttpClientFactory.Create(), options)
    {
    }

    public JellyfinSetupClient(HttpClient httpClient, JellyfinSetupOptions options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public Task<bool> ReadinessAsync(CancellationToken cancellationToken = default) => IsReadyAsync(cancellationToken);
    public Task<JellyfinAuthenticationResult> AuthenticateAsync(CancellationToken cancellationToken = default, Func<CancellationToken, Task>? assertMutationLeaseAsync = null)
        => AuthenticateByNameAsync(cancellationToken, assertMutationLeaseAsync: assertMutationLeaseAsync);
    public Task<JellyfinAuthenticationResult> AuthenticateWithAdministratorPolicyAsync(CancellationToken cancellationToken = default, Func<CancellationToken, Task>? assertMutationLeaseAsync = null)
        => AuthenticateByNameAsync(cancellationToken, requireAdministrator: true, assertMutationLeaseAsync: assertMutationLeaseAsync);
    public Task<string> CreateOrReuseApiKeyAsync(CancellationToken cancellationToken = default, Func<CancellationToken, Task>? assertMutationLeaseAsync = null) => EnsureApiKeyAsync(cancellationToken, assertMutationLeaseAsync);
    public Task ConfigurePluginAsync(JsonObject configuration, CancellationToken cancellationToken = default, Func<CancellationToken, Task>? assertMutationLeaseAsync = null) => EnsurePluginConfigurationAsync(configuration, cancellationToken, assertMutationLeaseAsync);
    public Task UpsertLibrariesAsync(CancellationToken cancellationToken = default, Func<CancellationToken, Task>? assertMutationLeaseAsync = null) => EnsureLibrariesAsync(cancellationToken, assertMutationLeaseAsync);
    public Task TriggerSyncAsync(CancellationToken cancellationToken = default, Func<CancellationToken, Task>? assertMutationLeaseAsync = null) => TriggerInitialSyncAsync(cancellationToken, assertMutationLeaseAsync);
    public Task<JellyfinVerificationResult> VerifySetupAsync(string apiKey, CancellationToken cancellationToken = default) => VerifyAsync(apiKey, cancellationToken);
    public void UseAccessToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("A Jellyfin access token is required.", nameof(token));

        _sessionToken = token;
    }

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "/health", null, false, "readiness", cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    public Task<JellyfinAuthenticationResult> AuthenticateByNameAsync(CancellationToken cancellationToken = default, Func<CancellationToken, Task>? assertMutationLeaseAsync = null)
        => AuthenticateByNameAsync(cancellationToken, requireAdministrator: false, assertMutationLeaseAsync: assertMutationLeaseAsync);

    /// <summary>
    /// Performs only the bounded Jellyfin authentication transport. Policy is
    /// returned as data and is deliberately not interpreted here: callers that
    /// need to make a policy decision must first durably journal the token.
    /// </summary>
    public async Task<JellyfinAuthenticationResult> AuthenticateTransportAsync(
        CancellationToken cancellationToken = default,
        Func<CancellationToken, Task>? assertMutationLeaseAsync = null)
    {
        var body = JsonSerializer.Serialize(new { Username = _options.Username, Pw = _options.Password }, JsonOptions);
        using var response = await SendAsync(HttpMethod.Post, "/Users/AuthenticateByName", body, false, "authentication", cancellationToken, assertMutationLeaseAsync, checkAfterResponse: false).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new JellyfinSetupException(JellyfinSetupFailure.Unauthorized, "authentication", (int)response.StatusCode);
        EnsureSuccess(response, "authentication");

        var json = await ReadJsonAsync(response, "authentication", cancellationToken).ConfigureAwait(false);
        var token = GetString(json, "AccessToken");
        if (string.IsNullOrWhiteSpace(token))
            throw new JellyfinSetupException(JellyfinSetupFailure.InvalidResponse, "authentication");

        var policy = TryGetProperty(json, "User", out var user) && TryGetProperty(user, "Policy", out var policyObject)
            ? TryGetBoolean(policyObject, "IsAdministrator")
            : null;
        _sessionToken = token;
        return new JellyfinAuthenticationResult(token, policy is true);
    }

    public async Task<JellyfinAuthenticationResult> AuthenticateByNameAsync(CancellationToken cancellationToken, bool requireAdministrator, Func<CancellationToken, Task>? assertMutationLeaseAsync = null)
    {
        var result = await AuthenticateTransportAsync(cancellationToken, assertMutationLeaseAsync).ConfigureAwait(false);
        // Preserve the legacy policy-wrapper fence contract. The transport
        // method intentionally omits this check so the grant service can
        // journal the token before any policy decision.
        if (assertMutationLeaseAsync is not null
            && !await InvokeLeaseAsync(assertMutationLeaseAsync, CancellationToken.None).ConfigureAwait(false))
            throw new JellyfinSetupException(JellyfinSetupFailure.UpstreamFailure, "authentication");
        if (requireAdministrator && !result.IsAdministrator)
        {
            try
            {
                await RevokeSessionAsync(result.AccessToken, CancellationToken.None, assertMutationLeaseAsync).ConfigureAwait(false);
            }
            catch (JellyfinSetupException ex)
            {
                throw new JellyfinSetupException(JellyfinSetupFailure.UpstreamFailure, "session logout", inner: ex);
            }

            throw new JellyfinSetupException(JellyfinSetupFailure.InvalidResponse, "authentication", sessionCreated: true);
        }

        return result;
    }

    public async Task<string> EnsureApiKeyAsync(CancellationToken cancellationToken = default, Func<CancellationToken, Task>? assertMutationLeaseAsync = null)
    {
        var keys = await GetApiKeysAsync(cancellationToken).ConfigureAwait(false);
        var existing = keys.FirstOrDefault(k => string.Equals(k.AppName, _options.ApiKeyAppName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null && !string.IsNullOrWhiteSpace(existing.AccessToken))
            return existing.AccessToken;

        // The key creation below is the first Jellyfin mutation in this
        // operation. Compatibility is checked immediately before it so a
        // missing/incompatible plugin can never leave a partially configured
        // server.
        await CheckCompatibilityAsync(cancellationToken).ConfigureAwait(false);
        var app = Uri.EscapeDataString(_options.ApiKeyAppName);
        using (var response = await SendAsync(HttpMethod.Post, $"/Auth/Keys?app={app}", null, true, "api-key creation", cancellationToken, assertMutationLeaseAsync).ConfigureAwait(false))
        {
            if (response.StatusCode != HttpStatusCode.NoContent)
                EnsureSuccess(response, "api-key creation");
        }

        keys = await GetApiKeysAsync(cancellationToken).ConfigureAwait(false);
        existing = keys.FirstOrDefault(k => string.Equals(k.AppName, _options.ApiKeyAppName, StringComparison.OrdinalIgnoreCase));
        if (existing is null || string.IsNullOrWhiteSpace(existing.AccessToken))
            throw new JellyfinSetupException(JellyfinSetupFailure.InvalidResponse, "api-key creation");
        return existing.AccessToken;
    }

    public async Task RevokeApiKeyAsync(string accessToken, CancellationToken cancellationToken = default, Func<CancellationToken, Task>? assertMutationLeaseAsync = null)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) throw new ArgumentException("An API key is required.", nameof(accessToken));
        await CheckCompatibilityAsync(cancellationToken).ConfigureAwait(false);
        using var response = await SendAsync(HttpMethod.Delete, "/Auth/Keys/" + Uri.EscapeDataString(accessToken), null, true, "api-key revocation", cancellationToken, assertMutationLeaseAsync).ConfigureAwait(false);
        EnsureNoContent(response, "api-key revocation");
    }

    public async Task RevokeSessionAsync(string accessToken, CancellationToken cancellationToken = default, Func<CancellationToken, Task>? assertMutationLeaseAsync = null)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) throw new ArgumentException("A session token is required.", nameof(accessToken));

        _sessionToken = accessToken;
        try
        {
            using var response = await SendAsync(HttpMethod.Post, "/Sessions/Logout", null, true, "session logout", cancellationToken, assertMutationLeaseAsync).ConfigureAwait(false);
            if ((int)response.StatusCode is > 299 and < 400)
                throw new JellyfinSetupException(JellyfinSetupFailure.UpstreamFailure, "session logout", (int)response.StatusCode);
            // Jellyfin returns 404 when the session has already disappeared.
            // Logout is deliberately idempotent so retries/restarts are safe.
            if (response.StatusCode != HttpStatusCode.NotFound)
                EnsureNoContent(response, "session logout");
        }
        finally
        {
            // A session that was explicitly logged out must never remain the
            // client's implicit authentication state. The caller may still
            // have another token, but it must opt into it again explicitly.
            _sessionToken = null;
        }
    }

    public async Task<JellyfinCompatibilityResult> CheckCompatibilityAsync(CancellationToken cancellationToken = default)
    {
        using var infoResponse = await SendAsync(HttpMethod.Get, "/System/Info", null, true, "compatibility", cancellationToken).ConfigureAwait(false);
        EnsureSuccess(infoResponse, "compatibility");
        var info = await ReadJsonAsync(infoResponse, "compatibility", cancellationToken).ConfigureAwait(false);
        var version = GetString(info, "Version");
        if (string.IsNullOrWhiteSpace(version) || !version.StartsWith(_options.RequiredJellyfinVersionPrefix, StringComparison.Ordinal))
            throw new JellyfinSetupException(JellyfinSetupFailure.IncompatibleVersion, "compatibility");

        using var pluginsResponse = await SendAsync(HttpMethod.Get, "/Plugins", null, true, "plugin compatibility", cancellationToken).ConfigureAwait(false);
        EnsureSuccess(pluginsResponse, "plugin compatibility");
        var plugins = await ReadJsonAsync(pluginsResponse, "plugin compatibility", cancellationToken).ConfigureAwait(false);
        var plugin = plugins.ValueKind == JsonValueKind.Array
            ? plugins.EnumerateArray().FirstOrDefault(p => SamePluginId(GetString(p, "Id"), _options.PluginId))
            : default;
        // Jellyfin 10.11 serializes the installed plugin identity as Id on
        // some builds and Guid on others, and its public Plugins route emits
        // the Guid's compact N form. Accept equivalent fixed GUID forms, but
        // still require the exact NZBDAV name below.
        if (plugin.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            plugin = plugins.ValueKind == JsonValueKind.Array
                ? plugins.EnumerateArray().FirstOrDefault(p => SamePluginId(GetString(p, "Guid"), _options.PluginId))
                : default;
        }
        if (plugin.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ||
            (!string.IsNullOrWhiteSpace(_options.PluginName) && !string.Equals(GetString(plugin, "Name"), _options.PluginName, StringComparison.OrdinalIgnoreCase)))
            throw new JellyfinSetupException(JellyfinSetupFailure.PluginMissing, "plugin compatibility");
        return new JellyfinCompatibilityResult(version, true, GetString(plugin, "Version"));
    }

    public async Task EnsurePluginConfigurationAsync(JsonObject configuration, CancellationToken cancellationToken = default, Func<CancellationToken, Task>? assertMutationLeaseAsync = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        await CheckCompatibilityAsync(cancellationToken).ConfigureAwait(false);
        var route = $"/Plugins/{Uri.EscapeDataString(_options.PluginId)}/Configuration";
        using var get = await SendAsync(HttpMethod.Get, route, null, true, "plugin configuration read", cancellationToken).ConfigureAwait(false);
        EnsureSuccess(get, "plugin configuration read");
        var current = await ReadJsonObjectAsync(get, "plugin configuration read", cancellationToken).ConfigureAwait(false);
        var merged = (JsonObject)current.DeepClone();
        Merge(merged, configuration);
        if (string.Equals(merged.ToJsonString(JsonOptions), current.ToJsonString(JsonOptions), StringComparison.Ordinal))
            return;

        using (var post = await SendAsync(HttpMethod.Post, route, merged.ToJsonString(JsonOptions), true, "plugin configuration write", cancellationToken, assertMutationLeaseAsync).ConfigureAwait(false))
            EnsureNoContent(post, "plugin configuration write");

        // Jellyfin 10.11 accepts the configuration request before its plugin
        // serializer has durably applied the values. Read back immediately;
        // this is a contract assertion, not a readiness relaxation.
        using var verify = await SendAsync(HttpMethod.Get, route, null, true, "plugin configuration verification", cancellationToken).ConfigureAwait(false);
        EnsureSuccess(verify, "plugin configuration verification");
        var persisted = await ReadJsonObjectAsync(verify, "plugin configuration verification", cancellationToken).ConfigureAwait(false);
        var compatible = ContainsConfiguration(persisted, configuration);
        // The installed 10.11 plugin contract calls this NzbdavBaseUrl;
        // Jellyfin silently drops the legacy NzbdavUrl compatibility alias.
        // Verify the retained alias rather than rejecting a successful write.
        if (!compatible && !persisted.ContainsKey("NzbdavUrl") && configuration.ContainsKey("NzbdavBaseUrl"))
        {
            var compatibleConfiguration = (JsonObject)configuration.DeepClone();
            compatibleConfiguration.Remove("NzbdavUrl");
            compatible = ContainsConfiguration(persisted, compatibleConfiguration);
        }
        if (!compatible)
            throw new JellyfinSetupException(JellyfinSetupFailure.InvalidResponse, "plugin configuration verification");
    }

    public async Task EnsureLibrariesAsync(CancellationToken cancellationToken = default, Func<CancellationToken, Task>? assertMutationLeaseAsync = null)
    {
        ValidateLibraryPaths();
        await CheckCompatibilityAsync(cancellationToken).ConfigureAwait(false);
        var folders = await ReadLibrariesAsync(cancellationToken).ConfigureAwait(false);

        await EnsureLibraryAsync(folders, "NZBDAV Movies", "movies", _options.MoviesLibraryPath, cancellationToken, assertMutationLeaseAsync).ConfigureAwait(false);
        // Jellyfin updates VirtualFolders asynchronously on some 10.11
        // builds. Always refresh the snapshot before creating the second
        // category; never make a decision using the first GET response.
        folders = await ReadLibrariesAsync(cancellationToken).ConfigureAwait(false);
        await EnsureLibraryAsync(folders, "NZBDAV TV", "tvshows", _options.TvLibraryPath, cancellationToken, assertMutationLeaseAsync).ConfigureAwait(false);
    }

    public async Task TriggerInitialSyncAsync(CancellationToken cancellationToken = default, Func<CancellationToken, Task>? assertMutationLeaseAsync = null)
    {
        await CheckCompatibilityAsync(cancellationToken).ConfigureAwait(false);
        using var response = await SendAsync(HttpMethod.Get, "/ScheduledTasks", null, true, "sync task read", cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, "sync task read");
        var tasks = await ReadJsonAsync(response, "sync task read", cancellationToken).ConfigureAwait(false);
        if (tasks.ValueKind != JsonValueKind.Array)
            throw new JellyfinSetupException(JellyfinSetupFailure.InvalidResponse, "sync task read");
        var task = tasks.EnumerateArray().FirstOrDefault(t => string.Equals(GetString(t, "Key"), _options.SyncTaskKey, StringComparison.Ordinal));
        var id = task.ValueKind == JsonValueKind.Undefined ? null : GetString(task, "Id");
        if (string.IsNullOrWhiteSpace(id))
            throw new JellyfinSetupException(JellyfinSetupFailure.SyncTaskMissing, "sync task read");
        using var start = await SendAsync(HttpMethod.Post, "/ScheduledTasks/Running/" + Uri.EscapeDataString(id), null, true, "sync trigger", cancellationToken, assertMutationLeaseAsync).ConfigureAwait(false);
        EnsureNoContent(start, "sync trigger");
    }

    public async Task<JellyfinVerificationResult> VerifyAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        var ready = await IsReadyAsync(cancellationToken).ConfigureAwait(false);
        var keys = await GetApiKeysAsync(cancellationToken).ConfigureAwait(false);
        var active = keys.Any(k => string.Equals(k.AccessToken, apiKey, StringComparison.Ordinal));
        var compatibility = await CheckCompatibilityAsync(cancellationToken).ConfigureAwait(false);
        var route = $"/Plugins/{Uri.EscapeDataString(_options.PluginId)}/Configuration";
        using var pluginResponse = await SendAsync(HttpMethod.Get, route, null, true, "verification", cancellationToken).ConfigureAwait(false);
        EnsureSuccess(pluginResponse, "verification");
        var current = await ReadJsonObjectAsync(pluginResponse, "verification", cancellationToken).ConfigureAwait(false);
        var expectedPlugin = BuildExpectedPluginConfiguration(apiKey);
        Merge(expectedPlugin, _options.PluginConfiguration);
        expectedPlugin["ApiKey"] = apiKey;
        var pluginConfigured = ContainsConfiguration(current, expectedPlugin);
        // Older plugin binaries ignore the renamed NzbdavUrl property but
        // retain the compatibility alias. This still proves the URL, key and
        // library path without treating a missing URL as configured.
        if (!pluginConfigured && !current.ContainsKey("NzbdavUrl") && expectedPlugin.ContainsKey("NzbdavBaseUrl"))
        {
            expectedPlugin.Remove("NzbdavUrl");
            pluginConfigured = ContainsConfiguration(current, expectedPlugin);
        }
        using var libraryResponse = await SendAsync(HttpMethod.Get, "/Library/VirtualFolders", null, true, "verification", cancellationToken).ConfigureAwait(false);
        EnsureSuccess(libraryResponse, "verification");
        var libraries = await ReadJsonAsync(libraryResponse, "verification", cancellationToken).ConfigureAwait(false);
        var librariesPresent = libraries.ValueKind == JsonValueKind.Array && LibrariesMatch(libraries);
        using var taskResponse = await SendAsync(HttpMethod.Get, "/ScheduledTasks", null, true, "verification", cancellationToken).ConfigureAwait(false);
        EnsureSuccess(taskResponse, "verification");
        var tasks = await ReadJsonAsync(taskResponse, "verification", cancellationToken).ConfigureAwait(false);
        var sync = tasks.ValueKind == JsonValueKind.Array && tasks.EnumerateArray().Any(x => string.Equals(GetString(x, "Key"), _options.SyncTaskKey, StringComparison.Ordinal));
        return new JellyfinVerificationResult(ready, active, pluginConfigured, librariesPresent, sync);
    }

    public async Task<JellyfinSetupResult> SetupAsync(CancellationToken cancellationToken = default, Func<CancellationToken, Task>? assertMutationLeaseAsync = null)
    {
        if (!await IsReadyAsync(cancellationToken).ConfigureAwait(false))
            throw new JellyfinSetupException(JellyfinSetupFailure.NotReady, "readiness");
        await AuthenticateByNameAsync(cancellationToken, requireAdministrator: false, assertMutationLeaseAsync: assertMutationLeaseAsync).ConfigureAwait(false);
        var compatibility = await CheckCompatibilityAsync(cancellationToken).ConfigureAwait(false);
        var key = await EnsureApiKeyAsync(cancellationToken, assertMutationLeaseAsync).ConfigureAwait(false);
        var pluginConfiguration = BuildExpectedPluginConfiguration(key);
        Merge(pluginConfiguration, _options.PluginConfiguration);
        pluginConfiguration["ApiKey"] = key;
        await EnsurePluginConfigurationAsync(pluginConfiguration, cancellationToken, assertMutationLeaseAsync).ConfigureAwait(false);
        await EnsureLibrariesAsync(cancellationToken, assertMutationLeaseAsync).ConfigureAwait(false);
        await TriggerInitialSyncAsync(cancellationToken, assertMutationLeaseAsync).ConfigureAwait(false);
        var verification = await VerifyAsync(key, cancellationToken).ConfigureAwait(false);
        if (!verification.Ready || !verification.ApiKeyActive || !verification.PluginConfigured || !verification.LibrariesPresent || !verification.SyncTaskPresent)
            throw new JellyfinSetupException(JellyfinSetupFailure.InvalidResponse, "verification");
        return new JellyfinSetupResult(key, compatibility);
    }

    private async Task EnsureLibraryAsync(JsonElement folders, string name, string collectionType, string path, CancellationToken cancellationToken, Func<CancellationToken, Task>? assertMutationLeaseAsync)
    {
        var existing = folders.EnumerateArray().FirstOrDefault(f => string.Equals(GetString(f, "Name"), name, StringComparison.Ordinal));
        var pathOwners = folders.EnumerateArray()
            .Where(folder => GetLocations(folder).Contains(path, StringComparer.Ordinal))
            .ToArray();
        if (pathOwners.Length > 1 || (pathOwners.Length == 1 && existing.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null))
            throw new JellyfinSetupException(JellyfinSetupFailure.LibraryCollision, "library read");

        if (existing.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
        {
            var locations = GetLocations(existing);
            if (locations.Length != 1 || !string.Equals(locations[0], path, StringComparison.Ordinal))
                throw new JellyfinSetupException(JellyfinSetupFailure.LibraryCollision, "library read");
            var actualType = GetString(existing, "CollectionType");
            if (!string.Equals(actualType, collectionType, StringComparison.OrdinalIgnoreCase))
                throw new JellyfinSetupException(JellyfinSetupFailure.LibraryTypeMismatch, "library read");
            return;
        }

        var query = $"name={Uri.EscapeDataString(name)}&collectionType={collectionType}&paths={Uri.EscapeDataString(path)}&refreshLibrary=false";
        var body = JsonSerializer.Serialize(new { LibraryOptions = new { PathInfos = new[] { new { Path = path } } } }, JsonOptions);
        using var response = await SendAsync(HttpMethod.Post, "/Library/VirtualFolders?" + query, body, true, "library write", cancellationToken, assertMutationLeaseAsync).ConfigureAwait(false);
        EnsureNoContent(response, "library write");

        // Validate the server's fresh representation after every create. This
        // catches Jellyfin accepting a colliding path or normalising a type.
        var refreshed = await ReadLibrariesAsync(cancellationToken).ConfigureAwait(false);
        EnsureLibraryPresent(refreshed, name, collectionType, path);
    }

    private async Task<JsonElement> ReadLibrariesAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, "/Library/VirtualFolders", null, true, "library read", cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, "library read");
        var folders = await ReadJsonAsync(response, "library read", cancellationToken).ConfigureAwait(false);
        if (folders.ValueKind != JsonValueKind.Array)
            throw new JellyfinSetupException(JellyfinSetupFailure.InvalidResponse, "library read");
        return folders;
    }

    private void ValidateLibraryPaths()
    {
        if (string.IsNullOrWhiteSpace(_options.MoviesLibraryPath)
            || string.IsNullOrWhiteSpace(_options.TvLibraryPath)
            || string.Equals(_options.MoviesLibraryPath, _options.TvLibraryPath, StringComparison.Ordinal))
            throw new JellyfinSetupException(JellyfinSetupFailure.LibraryCollision, "library validation");
    }

    private bool LibrariesMatch(JsonElement folders)
    {
        ValidateLibraryPaths();
        var movie = folders.EnumerateArray().FirstOrDefault(f => string.Equals(GetString(f, "Name"), "NZBDAV Movies", StringComparison.Ordinal));
        var tv = folders.EnumerateArray().FirstOrDefault(f => string.Equals(GetString(f, "Name"), "NZBDAV TV", StringComparison.Ordinal));
        if (!LibraryMatches(movie, "movies", _options.MoviesLibraryPath)
            || !LibraryMatches(tv, "tvshows", _options.TvLibraryPath))
            return false;

        var paths = folders.EnumerateArray().SelectMany(GetLocations).ToArray();
        return paths.Count(path => string.Equals(path, _options.MoviesLibraryPath, StringComparison.Ordinal)) == 1
            && paths.Count(path => string.Equals(path, _options.TvLibraryPath, StringComparison.Ordinal)) == 1;
    }

    private static bool LibraryMatches(JsonElement library, string type, string path)
        => library.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null
            && string.Equals(GetString(library, "CollectionType"), type, StringComparison.OrdinalIgnoreCase)
            && GetLocations(library).Length == 1
            && string.Equals(GetLocations(library)[0], path, StringComparison.Ordinal);

    private static void EnsureLibraryPresent(JsonElement folders, string name, string collectionType, string path)
    {
        var library = folders.EnumerateArray().FirstOrDefault(f => string.Equals(GetString(f, "Name"), name, StringComparison.Ordinal));
        var pathUses = folders.EnumerateArray().SelectMany(GetLocations)
            .Count(location => string.Equals(location, path, StringComparison.Ordinal));
        if (!LibraryMatches(library, collectionType, path) || pathUses != 1)
            throw new JellyfinSetupException(JellyfinSetupFailure.LibraryCollision, "library verification");
    }

    private static string[] GetLocations(JsonElement folder)
        => TryGetProperty(folder, "Locations", out var locationElement) && locationElement.ValueKind == JsonValueKind.Array
            ? locationElement.EnumerateArray().Select(x => x.GetString()).Where(x => x is not null).Cast<string>().ToArray()
            : [];

    private JsonObject BuildExpectedPluginConfiguration(string apiKey)
        => new()
        {
            ["NzbdavUrl"] = _options.NzbdavUrl,
            // Compatibility with plugin binaries that still deserialize the
            // pre-contract property name. Both point at the backend listener.
            ["NzbdavBaseUrl"] = _options.NzbdavUrl,
            ["ApiKey"] = apiKey,
            ["LibraryPath"] = _options.LibraryPath,
        };

    private async Task<IReadOnlyList<ApiKeyInfo>> GetApiKeysAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, "/Auth/Keys", null, true, "api-key read", cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, "api-key read");
        var json = await ReadJsonAsync(response, "api-key read", cancellationToken).ConfigureAwait(false);
        var source = json.ValueKind == JsonValueKind.Object && TryGetProperty(json, "Items", out var items) ? items : json;
        if (source.ValueKind != JsonValueKind.Array) throw new JellyfinSetupException(JellyfinSetupFailure.InvalidResponse, "api-key read");
        return source.EnumerateArray().Select(x => new ApiKeyInfo(GetString(x, "AppName"), GetString(x, "AccessToken"))).ToArray();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? body, bool authenticated, string operation, CancellationToken cancellationToken, Func<CancellationToken, Task>? assertMutationLeaseAsync = null, bool checkAfterResponse = true)
    {
        if (_options.RequestTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(_options.RequestTimeout), "The request timeout must be positive.");
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);
        using var request = new HttpRequestMessage(method, new Uri(_options.BaseUrl + path));
        var legacyLease = assertMutationLeaseAsync ?? _options.AssertMutationLeaseAsync;
        var mutationLease = _options.AssertMutationLeaseResultAsync
            ?? (legacyLease is null ? null : async token => { await legacyLease(token).ConfigureAwait(false); return true; });
        var isMutation = mutationLease is not null
            && (method == HttpMethod.Post || method == HttpMethod.Put || method == HttpMethod.Delete);
        if (isMutation && !await mutationLease!(cancellationToken).ConfigureAwait(false))
            throw new JellyfinSetupException(JellyfinSetupFailure.UpstreamFailure, operation);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        if (authenticated && !string.IsNullOrWhiteSpace(_sessionToken)) request.Headers.TryAddWithoutValidation("X-Emby-Token", _sessionToken);
        if (!authenticated && path == "/Users/AuthenticateByName")
            request.Headers.TryAddWithoutValidation("X-Emby-Authorization", $"MediaBrowser Client=\"{_options.ClientName}\", Device=\"{_options.DeviceName}\", DeviceId=\"{_options.DeviceId}\", Version=\"{_options.ClientVersion}\"");
        HttpResponseMessage? response = null;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (SetupHttpClientFactory.IsRedirect(response))
            {
                var status = (int)response.StatusCode;
                response.Dispose();
                response = null;
                throw new JellyfinSetupException(JellyfinSetupFailure.UpstreamFailure, operation, status);
            }

            // Keep the linked timeout alive through response-body consumption.
            // ResponseHeadersRead otherwise leaves ReadAsStreamAsync outside
            // the request deadline.
            response.Content = new TimeoutBoundedContent(response.Content, timeout, MaxResponseBytes);
            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new JellyfinSetupException(JellyfinSetupFailure.UpstreamFailure, operation); }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException) { throw new JellyfinSetupException(JellyfinSetupFailure.UpstreamFailure, operation); }
        finally
        {
            if (response is null)
                timeout.Dispose();
            if (isMutation && checkAfterResponse)
            {
                try
                {
                    // Fence again after every modifying request, including
                    // failures and lost acknowledgements.
                    if (!await mutationLease!(CancellationToken.None).ConfigureAwait(false))
                        throw new JellyfinSetupException(JellyfinSetupFailure.UpstreamFailure, operation);
                }
                catch
                {
                    response?.Dispose();
                    throw;
                }
            }
        }
    }

    private static async Task<bool> InvokeLeaseAsync(Func<CancellationToken, Task> lease, CancellationToken cancellationToken)
    {
        await lease(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        try
        {
            var content = await ReadResponseTextAsync(response, operation, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(content, new JsonDocumentOptions
            {
                MaxDepth = JsonOptions.MaxDepth
            });
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new JellyfinSetupException(JellyfinSetupFailure.InvalidResponse, operation);
        }
    }

    private static async Task<JsonObject> ReadJsonObjectAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        var content = await ReadResponseTextAsync(response, operation, cancellationToken).ConfigureAwait(false);
        return ParseObject(content, operation);
    }

    private static JsonObject ParseObject(string content, string operation)
    {
        try
        {
            return JsonNode.Parse(content) as JsonObject
                ?? throw new JellyfinSetupException(JellyfinSetupFailure.InvalidResponse, operation);
        }
        catch (JsonException)
        {
            throw new JellyfinSetupException(JellyfinSetupFailure.InvalidResponse, operation);
        }
    }

    private static async Task<string> ReadResponseTextAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
            throw new JellyfinSetupException(JellyfinSetupFailure.InvalidResponse, operation);

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await ReadResponseTextFromStreamAsync(stream, operation, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new JellyfinSetupException(JellyfinSetupFailure.UpstreamFailure, operation);
        }
    }

    private static async Task<string> ReadResponseTextFromStreamAsync(Stream stream, string operation, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream(Math.Min((int)Math.Min(4096, MaxResponseBytes), MaxResponseBytes));
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;

            if (output.Length + read > MaxResponseBytes)
                throw new JellyfinSetupException(JellyfinSetupFailure.InvalidResponse, operation);

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static bool SamePluginId(string? actual, string expected)
        => Guid.TryParse(actual, out var parsed) && Guid.TryParse(expected, out var expectedGuid)
            ? parsed == expectedGuid
            : string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static string? GetString(JsonElement element, string property) => TryGetProperty(element, property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool? TryGetBoolean(JsonElement element, string property)
    {
        if (!TryGetProperty(element, property, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static bool TryGetProperty(JsonElement element, string property, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var candidate in element.EnumerateObject())
            {
                if (string.Equals(candidate.Name, property, StringComparison.OrdinalIgnoreCase))
                {
                    value = candidate.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }
    private static void EnsureSuccess(HttpResponseMessage response, string operation) { if (!response.IsSuccessStatusCode) throw new JellyfinSetupException(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? JellyfinSetupFailure.Unauthorized : JellyfinSetupFailure.UpstreamFailure, operation, (int)response.StatusCode); }
    private static void EnsureNoContent(HttpResponseMessage response, string operation) { if (response.StatusCode != HttpStatusCode.NoContent) EnsureSuccess(response, operation); }
    private sealed record ApiKeyInfo(string? AppName, string? AccessToken);

    private static void Merge(JsonObject target, JsonObject patch)
    {
        foreach (var property in patch)
        {
            var key = target.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, property.Key, StringComparison.OrdinalIgnoreCase)) ?? property.Key;
            if (property.Value is JsonObject patchObject && target[key] is JsonObject targetObject) Merge(targetObject, patchObject);
            else target[key] = property.Value?.DeepClone();
        }
    }

    private sealed class TimeoutBoundedContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly CancellationTokenSource _timeout;
        private readonly int _maxBytes;

        public TimeoutBoundedContent(HttpContent inner, CancellationTokenSource timeout, int maxBytes)
        {
            _inner = inner;
            _timeout = timeout;
            _maxBytes = maxBytes;
            foreach (var header in inner.Headers)
                Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        protected override async Task<Stream> CreateContentReadStreamAsync()
        {
            var stream = await _inner.ReadAsStreamAsync(_timeout.Token).ConfigureAwait(false);
            return new BoundedReadStream(stream, _maxBytes);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => throw new NotSupportedException();

        protected override bool TryComputeLength(out long length)
        {
            length = _inner.Headers.ContentLength ?? -1;
            return length >= 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _timeout.Dispose();
            }
            base.Dispose(disposing);
        }

        private sealed class BoundedReadStream(Stream inner, int maxBytes) : Stream
        {
            private long _read;
            public override bool CanRead => inner.CanRead;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => _read; set => throw new NotSupportedException(); }
            public override void Flush() => throw new NotSupportedException();
            public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();
            public override int Read(Span<byte> buffer)
            {
                var read = inner.Read(buffer);
                _read += read;
                if (_read > maxBytes)
                    throw new JellyfinSetupException(JellyfinSetupFailure.InvalidResponse, "response");
                return read;
            }
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                _read += read;
                if (_read > maxBytes)
                    throw new JellyfinSetupException(JellyfinSetupFailure.InvalidResponse, "response");
                return read;
            }
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
            protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    private static bool ContainsConfiguration(JsonObject current, JsonObject expected)
    {
        foreach (var property in expected)
        {
            var key = current.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, property.Key, StringComparison.OrdinalIgnoreCase));
            if (key is null) return false;
            if (property.Value is JsonObject expectedObject)
            {
                if (current[key] is not JsonObject currentObject || !ContainsConfiguration(currentObject, expectedObject)) return false;
            }
            else if (!JsonNode.DeepEquals(current[key], property.Value)) return false;
        }
        return true;
    }
}
