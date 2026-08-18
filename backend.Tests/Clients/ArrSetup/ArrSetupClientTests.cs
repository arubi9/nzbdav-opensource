using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NzbWebDAV.Clients.ArrSetup;

namespace NzbWebDAV.Tests.Clients.ArrSetup;

public sealed class ArrSetupClientTests
{
    [Fact]
    public async Task Setup_is_schema_driven_idempotent_and_preserves_unrelated_resources()
    {
        var handler = new ArrHandler();
        using var httpClient = new HttpClient(handler);
        var client = new ArrSetupClient(httpClient, new Uri("http://setup.invalid"), "unused");
        var options = Options();

        var first = await client.SetupAsync(options);
        var second = await client.SetupAsync(options);

        Assert.Equal(4, first.Created);
        Assert.Equal(0, first.Updated);
        Assert.Equal(4, second.Unchanged);
        Assert.Equal(2, handler.Count("POST", "/api/v3/downloadclient"));
        Assert.Equal(2, handler.Count("POST", "/api/v3/rootfolder"));
        Assert.Equal(0, handler.Count("PUT", "/api/v3/rootfolder"));
        Assert.Equal("Keep me", handler.SentinelName);
        Assert.All(handler.Requests, request => Assert.Equal(
            request.Host == "sonarr" ? "sonarr-secret" : "radarr-secret", request.ApiKey));

        var sonarr = handler.CreatedClients.Single(x => x["name"]!.GetValue<string>() == "NZBDAV Sonarr");
        var radarr = handler.CreatedClients.Single(x => x["name"]!.GetValue<string>() == "NZBDAV Radarr");
        Assert.Equal("Sabnzbd", sonarr["implementation"]!.GetValue<string>());
        Assert.Equal("SabnzbdSettings", sonarr["configContract"]!.GetValue<string>());
        Assert.Equal("tv", Field(sonarr, "tvCategory")!.GetValue<string>());
        Assert.Equal("movies", Field(radarr, "movieCategory")!.GetValue<string>());
        Assert.Equal("nzbdav", Field(sonarr, "host")!.GetValue<string>());
        Assert.Equal(8080, Field(sonarr, "port")!.GetValue<int>());
        Assert.False(Field(sonarr, "useSsl")!.GetValue<bool>());
        Assert.Equal(2, handler.CreatedRoots.Count);
        Assert.Equal("/data/completed-downloads/tv", handler.CreatedRoots.Single(root => root["path"]!.GetValue<string>().EndsWith("/tv", StringComparison.Ordinal))["path"]!.GetValue<string>());
        Assert.Equal("/data/completed-downloads/movies", handler.CreatedRoots.Single(root => root["path"]!.GetValue<string>().EndsWith("/movies", StringComparison.Ordinal))["path"]!.GetValue<string>());
        Assert.Equal(0, handler.Count("GET", "/api/v3/queue"));
    }

    [Fact]
    public async Task Existing_client_is_updated_but_root_path_is_normalized_without_put()
    {
        var handler = new ArrHandler
        {
            SonarrClients = [Client(7, "NZBDAV Sonarr", "Sabnzbd", "old-key", "old")],
            SonarrRoots = [Root(8, "/data/completed-downloads/tv/")]
        };
        using var httpClient = new HttpClient(handler);
        var client = new ArrSetupClient(httpClient, "http://setup.invalid", "arr-secret");

        var result = await client.SetupAsync(Options());

        Assert.Equal(1, result.Updated);
        Assert.Equal(1, result.Unchanged);
        Assert.Equal(1, handler.Count("PUT", "/api/v3/downloadclient/7"));
        Assert.Equal(1, handler.Count("POST", "/api/v3/rootfolder"));
        Assert.Equal("/data/completed-downloads/movies", handler.CreatedRoots.Single()["path"]!.GetValue<string>());
        Assert.Equal("new-key", Field(handler.UpdatedClients.Single(), "apiKey")!.GetValue<string>());
        Assert.Equal("sentinel", Field(handler.UpdatedClients.Single(), "removeCompletedDownloads")!.GetValue<string>());
    }

    [Fact]
    public async Task Accepted_updates_for_masked_secrets_are_successful_for_both_arrs()
    {
        var handler = new ArrHandler();
        using var httpClient = new HttpClient(handler);
        var client = new ArrSetupClient(httpClient, "http://setup.invalid", "arr-secret");

        await client.SetupAsync(Options());
        var rootPostsBeforeUpdate = handler.Count("POST", "/api/v3/rootfolder");
        foreach (var managed in handler.CreatedClients)
        {
            var apiKey = managed["fields"]!.AsArray().OfType<JsonObject>()
                .Single(field => field["name"]!.GetValue<string>() == "apiKey");
            apiKey["value"] = "********";
        }
        handler.UpdateStatus = HttpStatusCode.Accepted;

        var result = await client.SetupAsync(Options());

        Assert.Equal(2, result.Updated);
        Assert.Equal(1, handler.Count("PUT", "/api/v3/downloadclient/100"));
        Assert.Equal(1, handler.Count("PUT", "/api/v3/downloadclient/101"));
        Assert.Equal(2, handler.Requests.Count(request => request.Method == "PUT"));
        Assert.Equal(rootPostsBeforeUpdate, handler.Count("POST", "/api/v3/rootfolder"));
    }

    [Fact]
    public async Task Verification_accepts_real_arr_rootfolder_payload_without_name()
    {
        var handler = new ArrHandler();
        using var httpClient = new HttpClient(handler);
        var client = new ArrSetupClient(httpClient, "http://setup.invalid", "arr-secret");

        await client.SetupAsync(Options());
        var result = await client.VerifyManagedResourcesDetailedAsync(Options());

        Assert.True(result.SonarrReady);
        Assert.True(result.RadarrReady);
    }

    [Fact]
    public async Task Name_collision_fails_before_any_write_and_sentinel_survives()
    {
        var handler = new ArrHandler
        {
            SonarrClients = [Client(7, "NZBDAV Sonarr", "Other", "key", "other")]
        };
        using var httpClient = new HttpClient(handler);
        var client = new ArrSetupClient(httpClient, "http://setup.invalid", "arr-secret");

        await Assert.ThrowsAsync<ArrSetupConflictException>(() => client.SetupAsync(Options()));

        Assert.DoesNotContain(handler.Requests, x => x.Method is "POST" or "PUT");
        Assert.Equal("Keep me", handler.SentinelName);
    }

    [Fact]
    public async Task Duplicate_normalized_root_identities_fail_before_writes()
    {
        var handler = new ArrHandler
        {
            SonarrRoots = [Root(1, "/data/completed-downloads/tv"), Root(2, "/data/completed-downloads/tv/")]
        };
        using var httpClient = new HttpClient(handler);
        var client = new ArrSetupClient(httpClient, "http://setup.invalid", "arr-secret");

        await Assert.ThrowsAsync<ArrSetupConflictException>(() => client.SetupAsync(Options()));

        Assert.DoesNotContain(handler.Requests, x => x.Method is "POST" or "PUT");
    }

    [Fact]
    public async Task Missing_schema_field_is_rejected_without_writes()
    {
        var handler = new ArrHandler { OmitApiKey = true };
        using var httpClient = new HttpClient(handler);
        var client = new ArrSetupClient(httpClient, "http://setup.invalid", "arr-secret");

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SetupAsync(Options()));
        Assert.DoesNotContain(handler.Requests, x => x.Method is "POST" or "PUT");
    }

    [Fact]
    public async Task Mutation_lease_is_fenced_before_and_after_each_arr_write()
    {
        var events = new List<string>();
        var handler = new ArrHandler { OrderedEvents = events };
        using var httpClient = new HttpClient(handler);
        var client = new ArrSetupClient(httpClient, "http://setup.invalid", "arr-secret");
        var options = Options();
        Task FenceAsync(CancellationToken _)
        {
            events.Add("fence");
            return Task.CompletedTask;
        }

        await client.SetupAsync(options, default, FenceAsync);

        var writeEvents = events.Where(x => x == "fence" || x.StartsWith("write:", StringComparison.Ordinal)).ToArray();
        var writes = handler.Requests.Where(x => x.Method is "POST" or "PUT").Select(x => "write:" + x.Path).ToArray();
        Assert.Equal(4, writes.Length);
        var expected = writes.SelectMany(write => new[] { "fence", write, "fence" });
        Assert.Equal(expected, writeEvents);
    }

    [Fact]
    public async Task Lease_takeover_before_a_write_stops_all_later_arr_writes()
    {
        var calls = 0;
        var handler = new ArrHandler();
        using var httpClient = new HttpClient(handler);
        var client = new ArrSetupClient(httpClient, "http://setup.invalid", "arr-secret");
        var options = Options();
        Task FenceAsync(CancellationToken _)
        {
            if (Interlocked.Increment(ref calls) == 3)
                throw new InvalidOperationException("lease taken over");
            return Task.CompletedTask;
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SetupAsync(options, default, FenceAsync));
        Assert.Single(handler.Requests, x => x.Method is "POST" or "PUT");
    }

    [Fact]
    public async Task Cancellation_and_bounded_timeout_are_honored()
    {
        var slow = new ArrHandler { Delay = TimeSpan.FromSeconds(2) };
        using var httpClient = new HttpClient(slow);
        var client = new ArrSetupClient(httpClient, "http://setup.invalid", "arr-secret", TimeSpan.FromMilliseconds(20));
        await Assert.ThrowsAsync<TimeoutException>(() => client.SetupAsync(Options()));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SetupAsync(Options(), cancelled.Token));
    }

    private static ArrSetupOptions Options() => new(
        "http://sonarr:8989", "sonarr-secret", "http://radarr:7878", "radarr-secret",
        "http://nzbdav:8080", "new-key");

    private static JsonNode? Field(JsonObject resource, string name) => resource["fields"]!.AsArray()
        .OfType<JsonObject>().Single(x => x["name"]!.GetValue<string>() == name)["value"];

    private static JsonObject Client(int id, string name, string implementation, string apiKey, string baseValue) => new()
    {
        ["id"] = id, ["name"] = name, ["implementation"] = implementation,
        ["implementationName"] = implementation, ["configContract"] = "OtherSettings",
        ["fields"] = new JsonArray { new JsonObject { ["name"] = "apiKey", ["value"] = apiKey },
            new JsonObject { ["name"] = "base", ["value"] = baseValue },
            new JsonObject { ["name"] = "removeCompletedDownloads", ["value"] = "sentinel" } }
    };

    private static JsonObject Root(int id, string path) => new() { ["id"] = id, ["path"] = path };

    private sealed class ArrHandler : HttpMessageHandler
    {
        public List<JsonObject> SonarrClients { get; set; } = [];
        public List<JsonObject> RadarrClients { get; set; } = [];
        public List<JsonObject> SonarrRoots { get; set; } = [];
        public List<JsonObject> RadarrRoots { get; set; } = [];
        public List<JsonObject> CreatedClients { get; } = [];
        public List<JsonObject> UpdatedClients { get; } = [];
        public List<JsonObject> CreatedRoots { get; } = [];
        public List<(string Method, string Path, string ApiKey, string Host)> Requests { get; } = [];
        public TimeSpan Delay { get; set; }
        public bool OmitApiKey { get; set; }
        public HttpStatusCode UpdateStatus { get; set; } = HttpStatusCode.OK;
        public List<string>? OrderedEvents { get; init; }
        public string SentinelName => "Keep me";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
            Requests.Add((request.Method.Method, request.RequestUri!.AbsolutePath,
                request.Headers.GetValues("X-Api-Key").Single(), request.RequestUri.Host));
            var path = request.RequestUri.AbsolutePath;
            if (request.Method != HttpMethod.Get)
                OrderedEvents?.Add("write:" + path);
            if (request.Method == HttpMethod.Get && path.EndsWith("/system/status")) return Reply("{}");
            if (request.Method == HttpMethod.Get && path.EndsWith("/downloadclient/schema")) return Reply(Schema());
            if (request.Method == HttpMethod.Get && path.EndsWith("/rootfolder")) return Reply(JsonSerializer.Serialize(
                request.RequestUri.Host == "sonarr" ? SonarrRoots : RadarrRoots));
            if (request.Method == HttpMethod.Get && path.EndsWith("/downloadclient")) return Reply(JsonSerializer.Serialize(
                request.RequestUri.Host == "sonarr" ? SonarrClients : RadarrClients));
            if (request.Method == HttpMethod.Post && path.EndsWith("/downloadclient/testall"))
            {
                var clients = request.RequestUri.Host == "sonarr" ? SonarrClients : RadarrClients;
                return Reply(JsonSerializer.Serialize(clients.Select(client => new
                {
                    id = client["id"]!.GetValue<int>(),
                    isValid = true,
                })));
            }
            if (request.Method == HttpMethod.Post && path.EndsWith("/downloadclient"))
            {
                var item = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
                item["id"] = 100 + CreatedClients.Count;
                (request.RequestUri.Host == "sonarr" ? SonarrClients : RadarrClients).Add(item);
                CreatedClients.Add(item);
                return Reply("{}", HttpStatusCode.Created);
            }
            if (request.Method == HttpMethod.Put && path.Contains("/downloadclient/"))
            {
                var item = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
                UpdatedClients.Add(item);
                return Reply("{}", UpdateStatus);
            }
            if (request.Method == HttpMethod.Post && path.EndsWith("/rootfolder"))
            {
                var item = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
                (request.RequestUri.Host == "sonarr" ? SonarrRoots : RadarrRoots).Add(item);
                CreatedRoots.Add(item);
                return Reply("{}", HttpStatusCode.Created);
            }
            throw new InvalidOperationException($"Unexpected request {request.Method} {path}");
        }

        private string Schema() => "[{\"implementation\":\"Sabnzbd\",\"implementationName\":\"SABnzbd\",\"configContract\":\"SabnzbdSettings\",\"fields\":[{\"name\":\"host\",\"value\":\"\"},{\"name\":\"port\",\"value\":8080},{\"name\":\"useSsl\",\"value\":false},{\"name\":\"urlBase\",\"value\":\"\"}," + (OmitApiKey ? "" : "{\"name\":\"apiKey\",\"value\":\"\"},") + "{\"name\":\"tvCategory\",\"value\":\"\"},{\"name\":\"movieCategory\",\"value\":\"\"},{\"name\":\"removeCompletedDownloads\",\"value\":true}]}]";

        public int Count(string method, string path) => Requests.Count(x => x.Method == method && x.Path == path);
        private static HttpResponseMessage Reply(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
