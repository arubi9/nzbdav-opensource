using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NzbWebDAV.Clients.ProwlarrSetup;

namespace NzbWebDAV.Tests.Clients.ProwlarrSetup;
[Collection(nameof(ProwlarrSetupCollection))]

public sealed class TransportStateRegressionTests
{
    private static readonly ProwlarrSetupOptions OneIndexer = new(
        "http://sonarr", "sonarr-key", "http://radarr", "radarr-key",
        [new ProwlarrNewznabIndexer("NZBDAV", "https://indexer.example/api", "indexer-key")]);
    private static readonly ProwlarrSetupOptions NoIndexers = new(
        "http://sonarr", "sonarr-key", "http://radarr", "radarr-key", []);

    [Theory]
    [InlineData(301)] [InlineData(302)] [InlineData(307)] [InlineData(308)]
    public async Task Public_constructor_get_redirect_is_a_sanitized_transport_failure(int status)
    {
        await AssertRedirectAsync(HttpMethod.Get, status, OneIndexer, "/api/v1/indexer/schema");
    }

    [Theory]
    [InlineData(301)] [InlineData(302)] [InlineData(307)] [InlineData(308)]
    public async Task Public_constructor_post_redirect_is_a_sanitized_transport_failure(int status)
    {
        await AssertRedirectAsync(HttpMethod.Post, status, OneIndexer, "/api/v1/indexer");
    }

    [Theory]
    [InlineData(301)] [InlineData(302)] [InlineData(307)] [InlineData(308)]
    public async Task Public_constructor_put_redirect_is_a_sanitized_transport_failure(int status)
    {
        await AssertRedirectAsync(HttpMethod.Put, status, OneIndexer, "/api/v1/indexer/10");
    }

    private static async Task AssertRedirectAsync(HttpMethod method, int status, ProwlarrSetupOptions options, string path)
    {
        await using var second = new LoopbackServer((_, response, _) => { response.StatusCode = 200; return Task.CompletedTask; });
        await using var first = new LoopbackServer(async (request, response, body) =>
        {
            var requestPath = request.Url!.AbsolutePath;
            if (request.HttpMethod == method.Method && requestPath.EndsWith(path, StringComparison.Ordinal))
            {
                response.StatusCode = status;
                response.RedirectLocation = new Uri(second.Url, "redirect-target?redirect-query=not-forwarded").ToString();
                return;
            }
            await ApiReplyAsync(request, response, body, options, existingForPut: method == HttpMethod.Put);
        });

        // This is a real second listener. AllowAutoRedirect=false must prevent the
        // request (and its API key, query, or headers) from reaching that origin.
        using var client = new ProwlarrSetupClient(first.Url, "transport-secret", TimeSpan.FromSeconds(2));
        var exception = await Assert.ThrowsAsync<ProwlarrSetupTransportException>(() => client.SetupAsync(options));
        Assert.DoesNotContain("transport-secret", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("sonarr-key", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("indexer-key", exception.ToString(), StringComparison.Ordinal);
        Assert.Empty(second.Requests);
        var hits = first.Requests.Where(request => request.Method == method.Method && request.Path.EndsWith(path, StringComparison.Ordinal)).ToArray();
        Assert.Single(hits);
        Assert.Equal("transport-secret", hits[0].ApiKey);
        if (method == HttpMethod.Get) Assert.Empty(hits[0].Body);
        else Assert.NotEmpty(hits[0].Body);
    }

    [Fact]
    public async Task Delayed_response_headers_are_a_transport_timeout_without_secrets()
    {
        await using var server = new LoopbackServer(async (request, response, _) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan);
        });
        using var client = new ProwlarrSetupClient(server.Url, "header-secret", TimeSpan.FromMilliseconds(80));
        var exception = await Assert.ThrowsAsync<ProwlarrSetupTransportException>(() => client.SetupAsync(NoIndexers));
        Assert.DoesNotContain("header-secret", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Headers_then_stalled_body_is_a_transport_timeout_without_secrets()
    {
        await using var server = new LoopbackServer(async (_, response, _) =>
        {
            response.StatusCode = 200;
            response.ContentType = "application/json";
            HttpListenerResponseExtensions.SendChunk(response, "[{\"implementation\":\"Newznab\"}");
            await Task.Delay(Timeout.InfiniteTimeSpan);
        });
        using var client = new ProwlarrSetupClient(server.Url, "body-secret", TimeSpan.FromMilliseconds(100));
        var exception = await Assert.ThrowsAsync<ProwlarrSetupTransportException>(() => client.SetupAsync(NoIndexers));
        Assert.DoesNotContain("body-secret", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stalled_owned_upload_is_a_transport_timeout_without_secrets()
    {
        using var handler = new StalledUploadHandler();
        // The caller-side scalar estimate remains below the 1 MiB capture limit.
        // BuildPayload then overwrites the small field values with these large identity
        // values, so the network payload is substantially larger than the input fields.
        var fields = new Dictionary<string, object?>
        {
            ["baseUrl"] = "b",
            ["apiPath"] = new string('x', 27_000),
            ["apiKey"] = "k",
            ["additionalParameters"] = new string('x', 27_000),
            ["vipExpiration"] = new string('x', 27_000),
            ["baseSettings.queryLimit"] = new string('x', 27_000),
            ["baseSettings.grabLimit"] = new string('x', 27_000),
            ["baseSettings.limitsUnit"] = new string('x', 27_000)
        };
        var largeBaseUrl = "https://indexer.example/" + new string('b', 60_000);
        var largeApiKey = new string('i', 60_000);
        var options = new ProwlarrSetupOptions("http://sonarr", "sonarr-secret", "http://radarr", "radarr-secret",
            [new ProwlarrNewznabIndexer("NZBDAV", largeBaseUrl, largeApiKey, fields)]);
        using var client = new ProwlarrSetupClient(handler, "http://prowlarr", "upload-secret", TimeSpan.FromMilliseconds(300));
        var setup = client.SetupAsync(options);
        await handler.UploadStarted.WaitAsync(TimeSpan.FromSeconds(2));
        await handler.EnteredWriteBlock.WaitAsync(TimeSpan.FromSeconds(2));
        var exception = await Assert.ThrowsAsync<ProwlarrSetupTransportException>(() => setup);
        await handler.CancellationObserved.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(handler.WasBlockedWhenCancelled);
        Assert.False(handler.ResponseHeadersReturned);
        Assert.InRange(handler.ContentLength, 100_000, 1_048_576);
        Assert.True(handler.AttemptedBytes >= handler.ContentLength);
        Assert.True(handler.AcceptedBytes < handler.ContentLength,
            $"upload completed before timeout ({handler.AcceptedBytes}/{handler.ContentLength} bytes accepted)");
        Assert.Equal("Prowlarr TransportError.", exception.Message);
        Assert.DoesNotContain("upload-secret", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("owned-upload-secret", exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task Caller_cancellation_in_each_transport_phase_is_not_sanitized(int phase)
    {
        await using var server = new LoopbackServer(async (request, response, _) =>
        {
            if ((phase == 0 && request.HttpMethod == "GET" && request.Url!.AbsolutePath.EndsWith("indexer/schema", StringComparison.Ordinal)) ||
                (phase == 1 && request.HttpMethod == "GET" && request.Url!.AbsolutePath.EndsWith("indexer/schema", StringComparison.Ordinal)) ||
                (phase == 2 && request.HttpMethod == "POST"))
            {
                if (phase == 1)
                {
                    response.StatusCode = 200; response.ContentType = "application/json";
                    HttpListenerResponseExtensions.SendChunk(response, "[");
                }
                await Task.Delay(Timeout.InfiniteTimeSpan);
                return;
            }
            await ApiReplyAsync(request, response, "", OneIndexer);
        });
        using var client = new ProwlarrSetupClient(server.Url, "caller-secret", TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource(100);
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SetupAsync(phase == 2 ? OneIndexer : NoIndexers, cancellation.Token));
        Assert.IsNotType<ProwlarrSetupTransportException>(exception);
        Assert.DoesNotContain("caller-secret", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Canonical_alias_clients_share_the_global_mutation_gate()
    {
        var probe = new GateProbe();
        using var firstHandler = new GateHandler(probe);
        using var secondHandler = new GateHandler(probe);
        using var first = new ProwlarrSetupClient(firstHandler, "http://PROWLARR:80/", "key", TimeSpan.FromSeconds(3));
        using var second = new ProwlarrSetupClient(secondHandler, "http://prowlarr/", "key", TimeSpan.FromSeconds(3));
        await Task.WhenAll(first.SetupAsync(NoIndexers), second.SetupAsync(NoIndexers));
        Assert.Equal(1, probe.MaximumConcurrentRequests);
        Assert.True(probe.CompletedRuns > 0);
    }

    [Fact]
    public async Task Waiting_gate_caller_cancellation_returns_promptly()
    {
        var probe = new GateProbe { HoldFirstRequest = true };
        using var firstHandler = new GateHandler(probe);
        using var secondHandler = new GateHandler(probe);
        using var first = new ProwlarrSetupClient(firstHandler, "http://prowlarr:80/", "key", TimeSpan.FromSeconds(5));
        using var second = new ProwlarrSetupClient(secondHandler, "http://PROWLARR/", "key", TimeSpan.FromSeconds(5));
        var running = first.SetupAsync(NoIndexers);
        await probe.FirstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cancellation = new CancellationTokenSource(100);
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.SetupAsync(NoIndexers, cancellation.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
        probe.ReleaseFirstRequest.TrySetResult();
        await running;
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task Gate_is_released_after_transport_parse_failure_or_cancellation(int failure)
    {
        var handler = new LifecycleHandler(failure);
        using var client = new ProwlarrSetupClient(handler, "http://prowlarr:9696", "key", TimeSpan.FromMilliseconds(250));
        if (failure == 2)
        {
            using var cancel = new CancellationTokenSource(80);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SetupAsync(NoIndexers, cancel.Token));
        }
        else await Assert.ThrowsAnyAsync<Exception>(() => client.SetupAsync(NoIndexers));
        handler.Failure = -1;
        await client.SetupAsync(NoIndexers);
        Assert.True(handler.GetCount > 1);
    }

    [Fact]
    public async Task External_managed_change_between_reads_requires_a_put_and_preserves_rich_metadata()
    {
        var handler = new StateHandler(StateChange.EnableAndMetadata);
        using var client = new ProwlarrSetupClient(handler, "http://prowlarr:9696", "key");
        var result = await client.SetupAsync(OneIndexer);
        Assert.Equal(1, result.Updated);
        Assert.Equal(1, handler.PutCount);
        var body = JsonNode.Parse(handler.LastPutBody)!.AsObject();
        Assert.True(body["enable"]!.GetValue<bool>());
        Assert.Equal("kept", body["newRichMetadata"]!["nested"]!["value"]!.GetValue<string>());
        Assert.Equal("new-field", body["fields"]!.AsArray().Single(f => f!["name"]!.GetValue<string>() == "externalField")!["value"]!.GetValue<string>());
    }

    [Fact]
    public async Task External_managed_identity_change_between_reads_is_a_conflict_without_a_write()
    {
        var handler = new StateHandler(StateChange.BaseUrl);
        using var client = new ProwlarrSetupClient(handler, "http://prowlarr:9696", "key");
        await Assert.ThrowsAsync<ProwlarrSetupConflictException>(() => client.SetupAsync(OneIndexer));
        Assert.Equal(0, handler.PutCount);
    }

    [Fact]
    public async Task A_new_client_restart_with_masked_resources_has_zero_writes()
    {
        var handler = new ProwlarrSetupClientTests.ProwlarrHandler { ExpectedApiKey = "key", Indexers = [RichIndexer(masked: true)] };
        using (var first = new ProwlarrSetupClient(handler, "http://prowlarr:9696", "key"))
            await first.SetupAsync(OneIndexer);
        var writes = handler.WriteCount;
        using (var restarted = new ProwlarrSetupClient(handler, "http://prowlarr:9696", "key"))
            await restarted.SetupAsync(OneIndexer);
        Assert.Equal(writes, handler.WriteCount);
    }

    [Theory]
    [InlineData("https://xn--bcher-kva.example:443/root/", "https://bücher.example/root", true)]
    [InlineData("https://bücher.example/root", "https://xn--bcher-kva.example/root?x=1", false)]
    [InlineData("https://bücher.example/root/a", "https://bücher.example/root/b", false)]
    public async Task Existing_resource_uri_identity_distinguishes_query_and_path(string current, string wanted, bool same)
    {
        var handler = new ProwlarrSetupClientTests.ProwlarrHandler
        {
            ExpectedApiKey = "key",
            Indexers = [Json($"{{\"id\":10,\"name\":\"NZBDAV\",\"implementation\":\"Newznab\",\"implementationName\":\"Newznab\",\"configContract\":\"NewznabSettings\",\"appProfileId\":1,\"enable\":true,\"fields\":[{{\"name\":\"baseUrl\",\"value\":\"{current}\"}},{{\"name\":\"apiPath\",\"value\":\"/api\"}},{{\"name\":\"apiKey\",\"value\":\"indexer-key\"}},{{\"name\":\"additionalParameters\",\"value\":\"\"}},{{\"name\":\"vipExpiration\",\"value\":\"\"}},{{\"name\":\"baseSettings.queryLimit\",\"value\":0}},{{\"name\":\"baseSettings.grabLimit\",\"value\":0}},{{\"name\":\"baseSettings.limitsUnit\",\"value\":\"day\"}}]}}")]
        };
        using var client = new ProwlarrSetupClient(handler, "http://prowlarr:9696", "key");
        var options = new ProwlarrSetupOptions("http://sonarr", "s", "http://radarr", "r",
            [new ProwlarrNewznabIndexer("NZBDAV", wanted, "indexer-key")]);
        if (same) await client.SetupAsync(options);
        else await Assert.ThrowsAsync<ProwlarrSetupConflictException>(() => client.SetupAsync(options));
        Assert.Equal(same ? 1 : 0, handler.Count("PUT", "/api/v1/indexer/10"));
    }

    [Fact]
    public async Task Conflict_after_external_create_regets_once_then_puts_without_a_second_post()
    {
        var handler = new ProwlarrSetupClientTests.ProwlarrHandler { ExpectedApiKey = "key", CreateBeforeNextIndexerPost = true };
        handler.Indexers = [];
        handler.ExternalCreateChangesApiKey = true;
        using var client = new ProwlarrSetupClient(handler, "http://prowlarr:9696", "key");
        var result = await client.SetupAsync(OneIndexer);
        Assert.Equal(1, result.Updated);
        Assert.Equal(1, handler.Count("POST", "/api/v1/indexer"));
        Assert.Equal(1, handler.Count("PUT", "/api/v1/indexer/10"));
        Assert.Equal(2, handler.Count("GET", "/api/v1/indexer"));
    }

    [Fact]
    public async Task Five_point_seven_megabyte_schema_stays_within_allocation_ceiling()
    {
        var fixture = ReadGzipFixture("indexer-schema.json.gz");
        var handler = new ProwlarrSetupClientTests.ProwlarrHandler
        {
            ExpectedApiKey = "key",
            IndexerSchema = Encoding.UTF8.GetString(fixture)
        };
        using var client = new ProwlarrSetupClient(handler, "http://prowlarr:9696", "key");
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var before = GC.GetTotalAllocatedBytes(true);
        await client.SetupAsync(OneIndexer);
        var allocated = GC.GetTotalAllocatedBytes(true) - before;
        Assert.InRange(allocated, 0, 150_000_000);
    }

    [Fact]
    public async Task Many_tiny_tokens_near_element_limit_do_not_trigger_double_materialization()
    {
        var schema = new StringBuilder("[{\"implementation\":\"Newznab\",\"configContract\":\"NewznabSettings\",\"name\":\"Generic Newznab\",\"fields\":[{\"name\":\"baseUrl\",\"value\":\"\"},{\"name\":\"apiPath\",\"value\":\"/api\"},{\"name\":\"apiKey\",\"value\":\"\"},{\"name\":\"additionalParameters\",\"value\":\"\"},{\"name\":\"vipExpiration\",\"value\":\"\"},{\"name\":\"baseSettings.queryLimit\",\"value\":0},{\"name\":\"baseSettings.grabLimit\",\"value\":0},{\"name\":\"baseSettings.limitsUnit\",\"value\":\"day\"}]}");
                for (var i = 0; i < 250_000; i++) schema.Append(",{}");
        schema.Append(']');
        var handler = new ProwlarrSetupClientTests.ProwlarrHandler { ExpectedApiKey = "key", IndexerSchema = schema.ToString() };
        using var client = new ProwlarrSetupClient(handler, "http://prowlarr:9696", "key", TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<ProwlarrSetupProtocolException>(() => client.SetupAsync(OneIndexer));
        Assert.DoesNotContain(handler.RequestBodies, b => b.Contains("indexer-key", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Nested_response_at_safety_boundary_is_deterministic()
    {
        var nested = new string('[', 28) + new string(']', 28);
        var schema = "[{\"implementation\":\"Newznab\",\"configContract\":\"NewznabSettings\",\"name\":\"Generic Newznab\",\"fields\":[{\"name\":\"baseUrl\",\"value\":\"\"},{\"name\":\"apiPath\",\"value\":\"/api\"},{\"name\":\"apiKey\",\"value\":\"\"},{\"name\":\"additionalParameters\",\"value\":\"\"},{\"name\":\"vipExpiration\",\"value\":\"\"},{\"name\":\"baseSettings.queryLimit\",\"value\":0},{\"name\":\"baseSettings.grabLimit\",\"value\":0},{\"name\":\"baseSettings.limitsUnit\",\"value\":\"day\"}],\"nested\":" + nested + "}]";
        var handler = new ProwlarrSetupClientTests.ProwlarrHandler { ExpectedApiKey = "key", IndexerSchema = schema };
        using var client = new ProwlarrSetupClient(handler, "http://prowlarr:9696", "key");
        await client.SetupAsync(OneIndexer);
        Assert.Equal(1, handler.Count("POST", "/api/v1/indexer"));
    }

    private static JsonElement RichIndexer(bool masked) => Json($"{{\"id\":10,\"name\":\"NZBDAV\",\"implementation\":\"Newznab\",\"implementationName\":\"Newznab\",\"configContract\":\"NewznabSettings\",\"appProfileId\":1,\"enable\":true,\"custom\":{{\"keep\":true}},\"fields\":[{{\"name\":\"baseUrl\",\"value\":\"https://indexer.example/api\"}},{{\"name\":\"apiPath\",\"value\":\"/api\"}},{{\"name\":\"apiKey\",\"value\":\"{(masked ? "********" : "indexer-key")}\"}},{{\"name\":\"additionalParameters\",\"value\":\"\"}},{{\"name\":\"vipExpiration\",\"value\":\"\"}},{{\"name\":\"baseSettings.queryLimit\",\"value\":0}},{{\"name\":\"baseSettings.grabLimit\",\"value\":0}},{{\"name\":\"baseSettings.limitsUnit\",\"value\":\"day\"}}]}}") ;

    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();

    private static async Task ApiReplyAsync(HttpListenerRequest request, HttpListenerResponse response, string body, ProwlarrSetupOptions options, bool existingForPut = false)
    {
        var path = request.Url!.AbsolutePath;
        if (request.HttpMethod == "GET" && path.EndsWith("/indexer/schema", StringComparison.Ordinal)) { await HttpListenerResponseExtensions.SendJsonAsync(response, GenericIndexerSchema); return; }
        if (request.HttpMethod == "GET" && path.EndsWith("/applications/schema", StringComparison.Ordinal)) { await HttpListenerResponseExtensions.SendJsonAsync(response, ApplicationSchema); return; }
        if (request.HttpMethod == "GET" && path.EndsWith("/appprofile", StringComparison.OrdinalIgnoreCase)) { await HttpListenerResponseExtensions.SendJsonAsync(response, "[{\"id\":1,\"name\":\"Standard\"}]"); return; }
        if (request.HttpMethod == "GET" && path.EndsWith("/indexer", StringComparison.OrdinalIgnoreCase))
        {
            if (!existingForPut) { await HttpListenerResponseExtensions.SendJsonAsync(response, "[]"); return; }
            // The PUT redirect cases must reach the PUT exchange. Keep the
            // identity fields equal but make a managed top-level field stale.
            var existing = JsonNode.Parse(RichIndexer(false).GetRawText())!.AsObject();
            existing["enable"] = false;
            await HttpListenerResponseExtensions.SendJsonAsync(response, "[" + existing.ToJsonString() + "]");
            return;
        }
        if (request.HttpMethod == "GET" && path.EndsWith("/applications", StringComparison.OrdinalIgnoreCase)) { await HttpListenerResponseExtensions.SendJsonAsync(response, "[]"); return; }
        response.StatusCode = request.HttpMethod is "POST" ? 201 : 200;
        await HttpListenerResponseExtensions.SendJsonAsync(response, "{}" );
    }

    private const string GenericIndexerSchema = "[{\"implementation\":\"Newznab\",\"implementationName\":\"Newznab\",\"name\":\"Generic Newznab\",\"configContract\":\"NewznabSettings\",\"fields\":[{\"name\":\"baseUrl\",\"value\":\"\"},{\"name\":\"apiPath\",\"value\":\"/api\"},{\"name\":\"apiKey\",\"value\":\"\"},{\"name\":\"additionalParameters\",\"value\":\"\"},{\"name\":\"vipExpiration\",\"value\":\"\"},{\"name\":\"baseSettings.queryLimit\",\"value\":0},{\"name\":\"baseSettings.grabLimit\",\"value\":0},{\"name\":\"baseSettings.limitsUnit\",\"value\":\"day\"}]}]";
    private const string ApplicationSchema = "[{\"implementation\":\"Sonarr\",\"configContract\":\"SonarrSettings\",\"fields\":[{\"name\":\"prowlarrUrl\",\"value\":\"\"},{\"name\":\"baseUrl\",\"value\":\"\"},{\"name\":\"apiKey\",\"value\":\"\"}]},{\"implementation\":\"Radarr\",\"configContract\":\"RadarrSettings\",\"fields\":[{\"name\":\"prowlarrUrl\",\"value\":\"\"},{\"name\":\"baseUrl\",\"value\":\"\"},{\"name\":\"apiKey\",\"value\":\"\"}]}]";

    private static byte[] ReadGzipFixture(string name)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "backend.Tests", "Clients", "ProwlarrSetup", "Fixtures", name);
            if (File.Exists(path)) using (var input = File.OpenRead(path)) using (var gzip = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress)) using (var output = new MemoryStream()) { gzip.CopyTo(output); return output.ToArray(); }
        }
        throw new FileNotFoundException(name);
    }

    private enum StateChange { EnableAndMetadata, BaseUrl }

    private sealed class StateHandler(StateChange change) : HttpMessageHandler
    {
        private readonly ProwlarrSetupClientTests.ProwlarrHandler _inner = new() { ExpectedApiKey = "key", Indexers = [RichIndexer(false)] };
        private int _reads;
        public int PutCount => _inner.Count("PUT", "/api/v1/indexer/10");
        public string LastPutBody => _inner.RequestBodies.First(body => body.Contains("externalField", StringComparison.Ordinal));
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/indexer", StringComparison.OrdinalIgnoreCase) && ++_reads == 2)
            {
                var item = JsonNode.Parse(_inner.Indexers[0].GetRawText())!.AsObject();
                if (change == StateChange.BaseUrl) item["fields"]!.AsArray().Single(f => f!["name"]!.GetValue<string>() == "baseUrl")!["value"] = "https://external.example";
                else { item["enable"] = false; item["newRichMetadata"] = new JsonObject { ["nested"] = new JsonObject { ["value"] = "kept" } }; item["fields"]!.AsArray().Add(new JsonObject { ["name"] = "externalField", ["value"] = "new-field" }); }
                _inner.Indexers[0] = Json(item.ToJsonString());
            }
            return await _inner.SendForTestAsync(request, token);
        }
    }

    private sealed class GateProbe
    {
        private int _active;
        public int MaximumConcurrentRequests;
        public int CompletedRuns;
        public bool HoldFirstRequest;
        public TaskCompletionSource FirstRequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Enter() { var active = Interlocked.Increment(ref _active); InterlockedExtensions.Max(ref MaximumConcurrentRequests, active); FirstRequestStarted.TrySetResult(); }
        public void Exit() { Interlocked.Decrement(ref _active); }
    }

    private sealed class GateHandler(GateProbe probe) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            probe.Enter();
            try
            {
                if (probe.HoldFirstRequest) { probe.HoldFirstRequest = false; await probe.ReleaseFirstRequest.Task.WaitAsync(cancellationToken); }
                await Task.Delay(10, cancellationToken);
                Interlocked.Increment(ref probe.CompletedRuns);
                var path = request.RequestUri!.AbsolutePath;
                var response = path.EndsWith("applications/schema", StringComparison.Ordinal) ? ApplicationSchema : path.EndsWith("indexer/schema", StringComparison.Ordinal) ? GenericIndexerSchema : path.EndsWith("appprofile", StringComparison.OrdinalIgnoreCase) ? "[{\"id\":1,\"name\":\"Standard\"}]" : "[]";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
            }
            finally { probe.Exit(); }
        }
    }

    private sealed class LifecycleHandler(int failure) : HttpMessageHandler
    {
        public int Failure { get; set; } = failure;
        public int GetCount;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("indexer/schema", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref GetCount);
                if (Failure == 0) throw new HttpRequestException("simulated transport");
                if (Failure == 1) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not-json", Encoding.UTF8, "application/json") };
                if (Failure == 2) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(GenericIndexerSchema, Encoding.UTF8, "application/json") };
            }
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/applications/schema", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ApplicationSchema, Encoding.UTF8, "application/json") };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]", Encoding.UTF8, "application/json") };
        }
    }

    private sealed record RequestLog(string Method, string Path, string ApiKey, string Body);

    private sealed class StalledUploadHandler : HttpMessageHandler
    {
        private readonly BoundedPrefixStream _upload = new();
        public Task UploadStarted { get; }
        public Task EnteredWriteBlock => _upload.EnteredWriteBlock;
        public Task CancellationObserved => _upload.CancellationObserved;
        public bool WasBlockedWhenCancelled => _upload.WasBlockedWhenCancelled;
        public bool ResponseHeadersReturned { get; private set; }
        public long ContentLength { get; private set; }
        public long AttemptedBytes => _upload.AttemptedBytes;
        public long AcceptedBytes => _upload.AcceptedBytes;
        private readonly TaskCompletionSource<bool> _uploadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public StalledUploadHandler() => UploadStarted = _uploadStarted.Task;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                ContentLength = request.Content?.Headers.ContentLength ?? -1;
                _uploadStarted.TrySetResult(true);
                await request.Content!.CopyToAsync(_upload, cancellationToken).ConfigureAwait(false);
                ResponseHeadersReturned = true;
                throw new InvalidOperationException("The stalled upload unexpectedly completed.");
            }

            var path = request.RequestUri!.AbsolutePath;
            var response = path.EndsWith("indexer/schema", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(GenericIndexerSchema, Encoding.UTF8, "application/json") }
                : path.EndsWith("appprofile", StringComparison.OrdinalIgnoreCase)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[{\"id\":1,\"name\":\"Standard\"}]", Encoding.UTF8, "application/json") }
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]", Encoding.UTF8, "application/json") };
            return response;
        }
    }

    private sealed class BoundedPrefixStream : Stream
    {
        private const int PrefixBytes = 256;
        private readonly TaskCompletionSource<bool> _never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _enteredWriteBlock = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _cancellationObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _attemptedBytes;
        private long _acceptedBytes;
        private int _blocked;
        public Task EnteredWriteBlock => _enteredWriteBlock.Task;
        public Task CancellationObserved => _cancellationObserved.Task;
        public long AttemptedBytes => Interlocked.Read(ref _attemptedBytes);
        public long AcceptedBytes => Interlocked.Read(ref _acceptedBytes);
        public bool WasBlockedWhenCancelled { get; private set; }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteChunkAsync(count, cancellationToken);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => new(WriteChunkAsync(buffer.Length, cancellationToken));

        private async Task WriteChunkAsync(int count, CancellationToken cancellationToken)
        {
            Interlocked.Add(ref _attemptedBytes, count);
            var remaining = PrefixBytes - Interlocked.Read(ref _acceptedBytes);
            var accepted = Math.Min(Math.Max(remaining, 0), count);
            Interlocked.Add(ref _acceptedBytes, accepted);
            if (accepted == count) return;

            Interlocked.Exchange(ref _blocked, 1);
            _enteredWriteBlock.TrySetResult(true);
            try { await _never.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                WasBlockedWhenCancelled = Volatile.Read(ref _blocked) == 1;
                _cancellationObserved.TrySetResult(true);
                throw;
            }
            finally { Volatile.Write(ref _blocked, 0); }
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Func<HttpListenerRequest, HttpListenerResponse, string, Task> _handler;
        private readonly Func<HttpListenerRequest, bool> _readBody;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        public List<RequestLog> Requests { get; } = [];
        public Uri Url { get; }

        public LoopbackServer(Func<HttpListenerRequest, HttpListenerResponse, string, Task> handler, Func<HttpListenerRequest, bool>? readBody = null)
        {
            _handler = handler;
            _readBody = readBody ?? (request => request.HttpMethod is not "GET");
            var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            Url = new Uri($"http://127.0.0.1:{port}/");
            _listener.Prefixes.Add(Url.ToString()); _listener.Start();
            _loop = RunAsync();
        }

        private async Task RunAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var context = await _listener.GetContextAsync().ConfigureAwait(false);
                    var request = context.Request; var body = "";
                    if (_readBody(request) && request.InputStream.CanRead)
                    {
                        using var reader = new StreamReader(request.InputStream, Encoding.UTF8, leaveOpen: false);
                        body = await reader.ReadToEndAsync().ConfigureAwait(false);
                    }
                    Requests.Add(new RequestLog(request.HttpMethod, request.Url!.AbsolutePath, request.Headers["X-Api-Key"] ?? "", body));
                    try { await _handler(request, context.Response, body).WaitAsync(_stop.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
                    catch (Exception) when (_stop.IsCancellationRequested) { }
                    finally { try { context.Response.Close(); } catch { } }
                }
            }
            catch (HttpListenerException) when (_stop.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel(); _listener.Close();
            try { await _loop.ConfigureAwait(false); } catch { }
            _stop.Dispose();
        }
    }

    private static class HttpListenerResponseExtensions
    {
        public static async Task SendJsonAsync(HttpListenerResponse response, string json)
        {
            response.ContentType = "application/json"; response.ContentEncoding = Encoding.UTF8;
            var bytes = Encoding.UTF8.GetBytes(json); response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false); response.Close();
        }
        public static void SendChunk(HttpListenerResponse response, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value); response.SendChunked = true; response.OutputStream.Write(bytes, 0, bytes.Length); response.OutputStream.Flush();
        }
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref int location, int value)
        {
            while (true) { var current = Volatile.Read(ref location); if (current >= value || Interlocked.CompareExchange(ref location, value, current) == current) return; }
        }
    }
}
