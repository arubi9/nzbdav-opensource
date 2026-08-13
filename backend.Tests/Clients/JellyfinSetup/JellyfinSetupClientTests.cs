using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NzbWebDAV.Clients.JellyfinSetup;

namespace NzbWebDAV.Tests.Clients.JellyfinSetup;

public sealed class JellyfinSetupClientTests
{
    [Fact]
    public async Task SetupUsesThe10_11RoutesAndDoesNotDuplicateManagedResources()
    {
        var handler = new JellyfinFixtureHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://jellyfin:8096") };
        var options = new JellyfinSetupOptions("http://jellyfin:8096", "setup-user", "setup-password")
        {
            LibraryPath = "/media/nzbdav",
            PluginConfiguration = JsonNode.Parse("{\"ApiKey\":\"plugin-secret\"}")!.AsObject()
        };
        var client = new JellyfinSetupClient(http, options);

        await client.SetupAsync();
        await client.SetupAsync();

        Assert.Equal(1, handler.Count(HttpMethod.Post, "/Auth/Keys"));
        Assert.Equal(2, handler.Count(HttpMethod.Post, "/Library/VirtualFolders"));
        var libraryWrites = handler.Requests.Where(r => r.Method == HttpMethod.Post && r.Path == "/Library/VirtualFolders").ToArray();
        Assert.Contains("%2Fmedia%2Fnzbdav%2Fmovies", libraryWrites[0].Query);
        Assert.Contains("%2Fmedia%2Fnzbdav%2Ftv", libraryWrites[1].Query);
        Assert.NotEqual(libraryWrites[0].Query, libraryWrites[1].Query);
        Assert.Equal(1, handler.Count(HttpMethod.Post, "/Plugins/a1b2c3d4-e5f6-7890-abcd-ef1234567890/Configuration"));
        var pluginWrite = handler.Requests.Single(r => r.Method == HttpMethod.Post && r.Path.EndsWith("/Configuration"));
        Assert.Equal("http://nzbdav:8080", pluginWrite.Json!["NzbdavUrl"]!.GetValue<string>());
        Assert.Equal("/media/nzbdav", pluginWrite.Json["LibraryPath"]!.GetValue<string>());
        Assert.Contains(handler.Requests, r => r.Method == HttpMethod.Post && r.Path == "/Users/AuthenticateByName");
        Assert.Equal(2, handler.Count(HttpMethod.Post, "/Users/AuthenticateByName"));
        var auth = handler.Requests.First(r => r.Path == "/Users/AuthenticateByName");
        Assert.Equal("setup-user", auth.Json!["username"]!.GetValue<string>());
        Assert.Equal("setup-password", auth.Json["pw"]!.GetValue<string>());
        Assert.Equal("MediaBrowser", auth.Headers["X-Emby-Authorization"].Split(' ', 2)[0]);
        Assert.DoesNotContain("setup-password", string.Join("\n", handler.Requests.SelectMany(r => r.Headers.Values)));
    }

    [Fact]
    public async Task Compatibility_accepts_the_pinned_plugin_guid_contract()
    {
        var handler = new JellyfinFixtureHandler { PluginIdentityProperty = "Guid", CompactPluginIdentity = true };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://jellyfin:8096") };
        var client = new JellyfinSetupClient(http, new JellyfinSetupOptions("http://jellyfin:8096", "u", "p"));
        client.UseAccessToken("session-token");

        var compatibility = await client.CheckCompatibilityAsync();

        Assert.True(compatibility.PluginInstalled);
    }

    [Fact]
    public async Task Plugin_configuration_verification_accepts_the_persisted_10_11_base_url_alias()
    {
        var handler = new JellyfinFixtureHandler { DropsLegacyNzbdavUrlOnPluginWrite = true };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://jellyfin:8096") };
        var client = new JellyfinSetupClient(http, new JellyfinSetupOptions("http://jellyfin:8096", "u", "p"));
        client.UseAccessToken("session-token");

        await client.ConfigurePluginAsync(new JsonObject
        {
            ["NzbdavUrl"] = "http://nzbdav:8080",
            ["NzbdavBaseUrl"] = "http://nzbdav:8080",
            ["ApiKey"] = "plugin-secret",
            ["LibraryPath"] = "/media/nzbdav",
        });

        Assert.Equal(1, handler.Count(HttpMethod.Post, "/Plugins/a1b2c3d4-e5f6-7890-abcd-ef1234567890/Configuration"));
    }

    [Fact]
    public async Task Mutation_lease_is_fenced_before_and_after_each_jellyfin_write()
    {
        var events = new List<string>();
        var handler = new JellyfinFixtureHandler { OrderedEvents = events };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://jellyfin:8096") };
        var options = new JellyfinSetupOptions("http://jellyfin:8096", "u", "p");
        Task FenceAsync(CancellationToken _)
        {
            events.Add("fence");
            return Task.CompletedTask;
        }
        var client = new JellyfinSetupClient(http, options);

        await client.EnsureLibrariesAsync(default, FenceAsync);

        var writes = handler.Requests.Where(r => r.Method == HttpMethod.Post || r.Method == HttpMethod.Put || r.Method == HttpMethod.Delete).ToArray();
        Assert.Equal(2, writes.Length);
        Assert.Equal(6, events.Count);
        var expected = writes.SelectMany(write => new[] { "fence", "write:" + write.Path, "fence" });
        Assert.Equal(expected, events);
    }

    [Fact]
    public async Task Lease_takeover_before_a_jellyfin_write_stops_later_writes()
    {
        var calls = 0;
        var handler = new JellyfinFixtureHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://jellyfin:8096") };
        var options = new JellyfinSetupOptions("http://jellyfin:8096", "u", "p");
        Task FenceAsync(CancellationToken _)
        {
            if (Interlocked.Increment(ref calls) == 3)
                throw new InvalidOperationException("lease taken over");
            return Task.CompletedTask;
        }
        var client = new JellyfinSetupClient(http, options);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.EnsureLibrariesAsync(default, FenceAsync));
        Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post || r.Method == HttpMethod.Put || r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task ExistingLibraryWithDifferentPathFailsBeforeWriting()
    {
        var handler = new JellyfinFixtureHandler { ExistingMoviePath = "/other" };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://jellyfin:8096") };
        var client = new JellyfinSetupClient(http, new JellyfinSetupOptions("http://jellyfin:8096", "u", "p"));

        var error = await Assert.ThrowsAsync<JellyfinSetupException>(() => client.EnsureLibrariesAsync());

        Assert.Equal(JellyfinSetupFailure.LibraryCollision, error.Failure);
        Assert.Equal(0, handler.Count(HttpMethod.Post, "/Library/VirtualFolders"));
        Assert.DoesNotContain("other", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Administrator_policy_failure_fences_compensating_logout_before_and_after_post()
    {
        var events = new List<string>();
        var handler = new JellyfinFixtureHandler { OrderedEvents = events };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://jellyfin:8096") };
        var client = new JellyfinSetupClient(http, new JellyfinSetupOptions("http://jellyfin:8096", "u", "p"));
        Task FenceAsync(CancellationToken _)
        {
            events.Add("fence");
            return Task.CompletedTask;
        }

        var error = await Assert.ThrowsAsync<JellyfinSetupException>(() =>
            client.AuthenticateWithAdministratorPolicyAsync(default, FenceAsync));

        Assert.Equal(JellyfinSetupFailure.InvalidResponse, error.Failure);
        Assert.Equal(
            new[]
            {
                "fence", "write:/Users/AuthenticateByName", "fence",
                "fence", "write:/Sessions/Logout", "fence",
            },
            events);
    }

    [Fact]
    public async Task Administrator_policy_takeover_after_authentication_prevents_unfenced_logout()
    {
        var handler = new JellyfinFixtureHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://jellyfin:8096") };
        var client = new JellyfinSetupClient(http, new JellyfinSetupOptions("http://jellyfin:8096", "u", "p"));
        var calls = 0;
        Task FenceAsync(CancellationToken _)
        {
            if (Interlocked.Increment(ref calls) == 3)
                throw new InvalidOperationException("lease taken over");
            return Task.CompletedTask;
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.AuthenticateWithAdministratorPolicyAsync(default, FenceAsync));
        Assert.Equal(0, handler.CountSessionLogouts);
    }

    [Fact]
    public async Task Compensating_logout_error_still_runs_the_final_lease_check()
    {
        var events = new List<string>();
        var handler = new JellyfinFixtureHandler
        {
            OrderedEvents = events,
            SessionLogoutStatusCode = HttpStatusCode.BadGateway,
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://jellyfin:8096") };
        var client = new JellyfinSetupClient(http, new JellyfinSetupOptions("http://jellyfin:8096", "u", "p"));
        Task FenceAsync(CancellationToken _)
        {
            events.Add("fence");
            return Task.CompletedTask;
        }

        var error = await Assert.ThrowsAsync<JellyfinSetupException>(() =>
            client.AuthenticateWithAdministratorPolicyAsync(default, FenceAsync));
        Assert.Equal(JellyfinSetupFailure.UpstreamFailure, error.Failure);
        Assert.Equal(new[]
        {
            "fence", "write:/Users/AuthenticateByName", "fence",
            "fence", "write:/Sessions/Logout", "fence",
        }, events);
    }

    [Fact]
    public async Task RevokeSessionAsync_RetriesLogoutWithoutUsingStoredAuthState()
    {
        var handler = new JellyfinFixtureHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://jellyfin:8096") };
        var client = new JellyfinSetupClient(http, new JellyfinSetupOptions("http://jellyfin:8096", "u", "p"));

        await client.RevokeSessionAsync("session-token");

        Assert.Equal(1, handler.CountSessionLogouts);
        Assert.Equal("session-token", handler.SessionLogoutToken);
    }

    [Fact]
    public async Task RevokeSessionAsync_TreatsNotFoundAsAlreadyLoggedOut()
    {
        var handler = new JellyfinFixtureHandler { SessionLogoutStatusCode = HttpStatusCode.NotFound };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://jellyfin:8096") };
        var client = new JellyfinSetupClient(http, new JellyfinSetupOptions("http://jellyfin:8096", "u", "p"));

        await client.RevokeSessionAsync("session-token");

        Assert.Equal("session-token", handler.SessionLogoutToken);
    }

    [Fact]
    public async Task RevokeSessionAsync_RejectsRedirectResponse()
    {
        var handler = new JellyfinFixtureHandler { SessionLogoutStatusCode = HttpStatusCode.Redirect };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://jellyfin:8096") };
        var client = new JellyfinSetupClient(http, new JellyfinSetupOptions("http://jellyfin:8096", "u", "p"));

        var error = await Assert.ThrowsAsync<JellyfinSetupException>(() => client.RevokeSessionAsync("session-token"));

        Assert.Equal(JellyfinSetupFailure.UpstreamFailure, error.Failure);
    }

    [Fact]
    public async Task Pinned_10_11_same_device_auth_replaces_the_previous_token_without_session_discovery()
    {
        var handler = new SameDeviceAuthenticationHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://jellyfin:8096") };
        var options = new JellyfinSetupOptions("http://jellyfin:8096", "admin", "password")
        {
            DeviceId = "deterministic-device",
            DeviceName = "NzbDav Setup",
            ClientName = "NzbDav Setup",
        };
        var first = new JellyfinSetupClient(http, options);
        var firstAuth = await first.AuthenticateByNameAsync();
        var second = new JellyfinSetupClient(http, options);
        var replacement = await second.AuthenticateByNameAsync();

        Assert.Equal("token-a", firstAuth.AccessToken);
        Assert.Equal("token-b", replacement.AccessToken);
        Assert.Equal(["token-b"], handler.ActiveTokens);
        Assert.Equal(2, handler.AuthenticationDeviceIds.Count);
        Assert.All(handler.AuthenticationDeviceIds, id => Assert.Equal("deterministic-device", id));
        Assert.DoesNotContain(handler.Paths, path => path == "/Sessions");
    }

    [Fact]
    public async Task AdministratorPolicyIsRequiredAndARejectedSessionIsNotReturned()
    {
        var handler = new JellyfinFixtureHandler { AuthenticationJson = "{\"AccessToken\":\"policy-token\",\"User\":{\"Policy\":{\"IsAdministrator\":false}}}" };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://jellyfin:8096") };
        var client = new JellyfinSetupClient(http, new JellyfinSetupOptions("http://jellyfin:8096", "u", "password-to-protect"));

        var error = await Assert.ThrowsAsync<JellyfinSetupException>(() => client.AuthenticateWithAdministratorPolicyAsync());

        Assert.Equal(JellyfinSetupFailure.InvalidResponse, error.Failure);
        Assert.True(error.SessionCreated);
        Assert.Equal(1, handler.CountSessionLogouts);
        Assert.DoesNotContain("policy-token", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("password-to-protect", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationIsPropagatedAndCredentialsAreNotInErrors()
    {
        var handler = new JellyfinFixtureHandler { BlockRequests = true };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://jellyfin:8096") };
        var client = new JellyfinSetupClient(http, new JellyfinSetupOptions("http://jellyfin:8096", "u", "password-to-protect"));
        using var cancellation = new CancellationTokenSource();
        var pending = client.IsReadyAsync(cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.DoesNotContain("password-to-protect", handler.LastError ?? string.Empty, StringComparison.Ordinal);
    }

    private sealed class SameDeviceAuthenticationHandler : HttpMessageHandler
    {
        private readonly HashSet<string> _activeTokens = new(StringComparer.Ordinal);
        public List<string> AuthenticationDeviceIds { get; } = [];
        public List<string> Paths { get; } = [];
        public IReadOnlyList<string> ActiveTokens => _activeTokens.ToArray();
        private int _authenticationCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath != "/Users/AuthenticateByName")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            var authorization = request.Headers.GetValues("X-Emby-Authorization").Single();
            var deviceId = authorization.Split("DeviceId=\"", StringSplitOptions.None)[1].Split('"')[0];
            AuthenticationDeviceIds.Add(deviceId);
            _activeTokens.Clear(); // faithful 10.11 same-device replacement behavior
            var token = Interlocked.Increment(ref _authenticationCount) == 1 ? "token-a" : "token-b";
            _activeTokens.Add(token);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { AccessToken = token, User = new { Policy = new { IsAdministrator = true } } }), Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class JellyfinFixtureHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];
        public string? ExistingMoviePath { get; init; }
        public bool BlockRequests { get; init; }
        public List<string>? OrderedEvents { get; init; }
        public string? AuthenticationJson { get; set; } = "{\"AccessToken\":\"session-token\"}";
        public string? LastError { get; private set; }
        public string PluginIdentityProperty { get; init; } = "Id";
        public bool CompactPluginIdentity { get; init; }
        public bool DropsLegacyNzbdavUrlOnPluginWrite { get; init; }
        private readonly List<string> _keys = [];
        private readonly List<JsonObject> _libraries = [];
        private string _pluginConfiguration = "{\"NzbdavUrl\":\"http://nzbdav:8080\",\"ApiKey\":\"old\",\"LibraryPath\":\"/media/nzbdav\",\"Preserved\":true}";

        public int Count(HttpMethod method, string path) => Requests.Count(r => r.Method == method && r.Path == path);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (BlockRequests)
            {
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                catch (Exception ex) { LastError = ex.Message; throw; }
            }

            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(request.Method, path, request.RequestUri.Query,
                request.Headers.ToDictionary(x => x.Key, x => string.Join(" ", x.Value)),
                body is null ? null : JsonNode.Parse(body)));
            if (request.Method == HttpMethod.Post || request.Method == HttpMethod.Put || request.Method == HttpMethod.Delete)
                OrderedEvents?.Add("write:" + path);

            if (path == "/health") return Json(HttpStatusCode.OK, "{}");
            if (path == "/Users/AuthenticateByName") return Json(HttpStatusCode.OK, AuthenticationJson!);
            if (path == "/System/Info") return Json(HttpStatusCode.OK, "{\"Version\":\"10.11.8\"}");
            if (path == "/Plugins")
            {
                var identity = CompactPluginIdentity ? "a1b2c3d4e5f67890abcdef1234567890" : "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
                return Json(HttpStatusCode.OK, $"[{{\"{PluginIdentityProperty}\":\"{identity}\",\"Name\":\"NZBDAV\",\"Version\":\"1.0.0\"}}]");
            }
            if (path == "/Auth/Keys" && request.Method == HttpMethod.Get)
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { Items = _keys.Select(k => new { AppName = k, AccessToken = "created-token" }) }));
            if (path == "/Auth/Keys" && request.Method == HttpMethod.Post) { _keys.Add("NZBDAV"); return new HttpResponseMessage(HttpStatusCode.NoContent); }
            if (path == "/Library/VirtualFolders" && request.Method == HttpMethod.Get)
            {
                if (_libraries.Count == 0 && ExistingMoviePath is null) return Json(HttpStatusCode.OK, "[]");
                if (ExistingMoviePath is not null)
                {
                    return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new object[] {
                        new { Name = "NZBDAV Movies", Locations = new[] { ExistingMoviePath }, CollectionType = "movies" },
                    }));
                }
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(_libraries));
            }
            if (path == "/Library/VirtualFolders" && request.Method == HttpMethod.Post)
            {
                var values = request.RequestUri!.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                    .Select(pair => pair.Split('=', 2))
                    .ToDictionary(pair => pair[0], pair => pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : string.Empty);
                _libraries.Add(new JsonObject
                {
                    ["Name"] = values["name"],
                    ["Locations"] = new JsonArray(JsonValue.Create(values["paths"])),
                    ["CollectionType"] = values["collectionType"],
                });
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (path.Contains("/Plugins/") && path.EndsWith("/Configuration") && request.Method == HttpMethod.Get)
                return Json(HttpStatusCode.OK, _pluginConfiguration);
            if (path.Contains("/Plugins/") && path.EndsWith("/Configuration") && request.Method == HttpMethod.Post)
            {
                _pluginConfiguration = body!;
                if (DropsLegacyNzbdavUrlOnPluginWrite)
                {
                    var configuration = JsonNode.Parse(_pluginConfiguration)!.AsObject();
                    configuration.Remove("NzbdavUrl");
                    _pluginConfiguration = configuration.ToJsonString();
                }
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (path == "/ScheduledTasks") return Json(HttpStatusCode.OK, "[{\"Id\":\"task-id\",\"Key\":\"NzbdavLibrarySync\"}]");
            if (path == "/ScheduledTasks/Running/task-id") return new HttpResponseMessage(HttpStatusCode.NoContent);
            if (path == "/Auth/Keys/created-token") return new HttpResponseMessage(HttpStatusCode.NoContent);
            if (path == "/Sessions/Logout" && request.Method == HttpMethod.Post)
            {
                SessionLogoutToken = request.Headers.TryGetValues("X-Emby-Token", out var tokens)
                    ? tokens.FirstOrDefault()
                    : null;
                if (SessionLogoutStatusCode is not null)
                    return new HttpResponseMessage(SessionLogoutStatusCode.Value);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        public HttpStatusCode? SessionLogoutStatusCode { get; init; }
        public string? SessionLogoutToken { get; private set; }

        public int CountSessionLogouts => Requests.Count(r => r.Path == "/Sessions/Logout" && r.Method == HttpMethod.Post);

        private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body) };
    }

    private sealed record CapturedRequest(HttpMethod Method, string Path, string Query, Dictionary<string, string> Headers, JsonNode? Json);
}
