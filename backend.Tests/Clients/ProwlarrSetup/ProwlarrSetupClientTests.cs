using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NzbWebDAV.Clients.ProwlarrSetup;

namespace NzbWebDAV.Tests.Clients.ProwlarrSetup;

public sealed class ProwlarrSetupClientTests
{
    [Fact]
    public async Task Pre_cancelled_setup_does_not_enter_transport_or_gate()
    {
        var handler = new ProwlarrHandler(); using var client = new ProwlarrSetupClient(handler, "http://prowlarr:9696", "key");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SetupAsync(new ProwlarrSetupOptions("http://sonarr", "s", "http://radarr", "r", []), cancelled.Token));
        Assert.Empty(handler.RequestUris);
    }

    [Fact]
    public async Task Desired_indexer_count_is_bounded_before_network_io()
    {
        var handler = new ProwlarrHandler(); using var client = new ProwlarrSetupClient(handler, "http://prowlarr:9696", "key");
        var indexers = Enumerable.Range(0, 101).Select(i => new ProwlarrNewznabIndexer($"Indexer{i}", "http://indexer", "key")).ToArray();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.SetupAsync(new ProwlarrSetupOptions("http://sonarr", "s", "http://radarr", "r", indexers)));
        Assert.Empty(handler.RequestUris);
    }

    [Fact]
    public async Task Deceptive_indexer_count_is_ignored_and_actual_101st_is_rejected()
    {
        var handler = new ProwlarrHandler(); using var client = new ProwlarrSetupClient(handler, "http://prowlarr:9696", "key");
        var indexers = new DeceptiveIndexerCollection(101);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.SetupAsync(new ProwlarrSetupOptions("http://sonarr", "s", "http://radarr", "r", indexers)));
        Assert.Empty(handler.RequestUris);
    }

    [Fact]
    public async Task Actual_indexer_field_count_is_bounded_before_serializing_the_65th_value()
    {
        var fields = new DeceptiveFields(65);
        var handler = new ProwlarrHandler(); using var client = new ProwlarrSetupClient(handler, "http://prowlarr:9696", "key");
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.SetupAsync(new ProwlarrSetupOptions("http://sonarr", "s", "http://radarr", "r", [new ProwlarrNewznabIndexer("one", "http://indexer", "i", fields)])));
        Assert.Equal(65, fields.Enumerated);
        Assert.Empty(handler.RequestUris);
    }

    [Fact]
    public async Task Aggregate_serialized_input_budget_is_bounded_across_indexers()
    {
        var value = new string('x', 60_000);
        var fields = Enumerable.Range(0, 20).ToDictionary(i => $"field{i}", _ => (object?)value);
        var handler = new ProwlarrHandler(); using var client = new ProwlarrSetupClient(handler, "http://prowlarr:9696", "key");
        await Assert.ThrowsAsync<ProwlarrSetupProtocolException>(() => client.SetupAsync(new ProwlarrSetupOptions("http://sonarr", "s", "http://radarr", "r", [new ProwlarrNewznabIndexer("one", "http://indexer", "i", fields)])));
        Assert.Empty(handler.RequestUris);
    }

    [Fact]
    public async Task Force_secret_update_puts_existing_managed_resources_once()
    {
        var handler = new ProwlarrHandler { ExpectedApiKey = "key" }; using var client = new ProwlarrSetupClient(handler, "http://prowlarr:9696", "key");
        var options = new ProwlarrSetupOptions("http://sonarr", "s", "http://radarr", "r", [new ProwlarrNewznabIndexer("one", "http://indexer", "i")]);
        await client.SetupAsync(options);
        await client.SetupAsync(new ProwlarrSetupOptions("http://sonarr", "s", "http://radarr", "r", [new ProwlarrNewznabIndexer("one", "http://indexer", "i")], forceSecretUpdate: true));
        Assert.Equal(3, handler.Count("PUT", "/api/v1/indexer/10") + handler.Count("PUT", "/api/v1/applications/11") + handler.Count("PUT", "/api/v1/applications/12"));
    }

    [Fact]
    public async Task Second_run_is_idempotent_and_does_not_touch_unrelated_indexers()
    {
        var handler = new ProwlarrHandler();
        using var httpClient = new HttpClient(handler);
        var client = new ProwlarrSetupClient(handler, new Uri("http://prowlarr:9696"), "prowlarr-secret");
        var options = new ProwlarrSetupOptions(
            "http://192.168.1.20:8989", "sonarr-secret", "http://10.0.0.7:7878", "radarr-secret",
            [new ProwlarrNewznabIndexer("NZBDAV", "http://indexer.example/api", "indexer-secret")]);

        await client.SetupAsync(options);
        await client.SetupAsync(options);

        Assert.Equal(2, handler.Count("GET", "/api/v1/indexer/schema"));
        Assert.Equal(2, handler.Count("GET", "/api/v1/applications/schema"));
        Assert.Equal(3, handler.Count("GET", "/api/v1/indexer"));
        Assert.Equal(6, handler.Count("GET", "/api/v1/applications"));
        Assert.Equal(1, handler.Count("POST", "/api/v1/indexer"));
        Assert.Equal(2, handler.Count("POST", "/api/v1/applications"));
        Assert.Equal(0, handler.Count("PUT", "/api/v1/indexer/10"));
        Assert.Equal("Unrelated", handler.Indexers[0].GetProperty("name").GetString());
        var indexerBody = JsonNode.Parse(handler.RequestBodies.First())!.AsObject();
        Assert.Equal(1, indexerBody["appProfileId"]!.GetValue<int>());
        Assert.DoesNotContain(indexerBody["fields"]!.AsArray(), field =>
            string.Equals(field!["name"]!.GetValue<string>(), "appProfileId", StringComparison.OrdinalIgnoreCase));
        Assert.All(handler.RequestUris, uri => Assert.Empty(uri.Query));
    }

    [Fact]
    public async Task Credential_bearing_urls_are_rejected_without_echoing_secrets()
    {
        const string secret = "do-not-log-this";
        var handler = new ProwlarrHandler();
        using var httpClient = new HttpClient(handler);
        var client = new ProwlarrSetupClient(handler, "http://prowlarr:9696", "key");
        var options = new ProwlarrSetupOptions($"http://user:{secret}@sonarr", "s", "http://radarr", "r", []);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => client.SetupAsync(options));
        Assert.DoesNotContain(secret, exception.Message);
        Assert.Throws<ArgumentException>(() => new ProwlarrSetupClient(handler,
            $"http://user:{secret}@prowlarr:9696", "key"));
    }

    [Fact]
    public async Task Requested_case_variant_indexer_names_fail_before_writing()
    {
        var handler = new ProwlarrHandler();
        using var httpClient = new HttpClient(handler);
        var client = new ProwlarrSetupClient(handler, new Uri("http://prowlarr:9696"), "key");
        var options = new ProwlarrSetupOptions("http://sonarr", "s", "http://radarr", "r",
            [new ProwlarrNewznabIndexer("NZBDAV", "http://one", "i"),
             new ProwlarrNewznabIndexer("nZbDaV", "http://two", "j")]);

        await Assert.ThrowsAsync<ProwlarrSetupConflictException>(() => client.SetupAsync(options));
        Assert.DoesNotContain(handler.RequestUris, uri => uri.AbsolutePath.Contains("/indexer") &&
            !uri.AbsolutePath.EndsWith("/schema", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.RequestUris, uri => uri.AbsolutePath.Contains("/applications") &&
            !uri.AbsolutePath.EndsWith("/schema", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Same_name_with_different_url_fails_before_writing()
    {
        var handler = new ProwlarrHandler
        {
            Indexers = [Json("{\"id\":10,\"name\":\"NZBDAV\",\"implementation\":\"Newznab\",\"fields\":[{\"name\":\"baseUrl\",\"value\":\"http://other\"}]}" )]
        };
        using var httpClient = new HttpClient(handler);
        handler.ExpectedApiKey = "key";
        var client = new ProwlarrSetupClient(handler, new Uri("http://prowlarr:9696"), "key");
        var options = new ProwlarrSetupOptions("http://sonarr", "s", "http://radarr", "r",
            [new ProwlarrNewznabIndexer("NZBDAV", "http://wanted", "i")]);

        await Assert.ThrowsAsync<ProwlarrSetupConflictException>(() => client.SetupAsync(options));
        Assert.Equal(0, handler.Count("PUT", "/api/v1/indexer/10"));
        Assert.Equal(0, handler.Count("POST", "/api/v1/indexer"));
    }

    [Fact]
    public async Task Selects_generic_newznab_from_multiple_same_contract_presets()
    {
        var handler = new ProwlarrHandler
        {
            ExpectedApiKey = "key",
            IndexerSchema = "[{\"implementation\":\"Newznab\",\"implementationName\":\"Newznab\",\"name\":\"Preset A\",\"configContract\":\"NewznabSettings\",\"fields\":[{\"name\":\"baseUrl\",\"value\":\"\"},{\"name\":\"apiKey\",\"value\":\"\"},{\"name\":\"presetOnly\",\"value\":\"wrong\"}]},{\"implementation\":\"Newznab\",\"implementationName\":\"Newznab\",\"name\":\"Generic Newznab\",\"configContract\":\"NewznabSettings\",\"appProfileId\":1,\"fields\":[{\"name\":\"baseUrl\",\"value\":\"\"},{\"name\":\"apiPath\",\"value\":\"/api\"},{\"name\":\"apiKey\",\"value\":\"\"},{\"name\":\"additionalParameters\",\"value\":\"\"},{\"name\":\"vipExpiration\",\"value\":\"\"},{\"name\":\"baseSettings.queryLimit\",\"value\":0},{\"name\":\"baseSettings.grabLimit\",\"value\":0},{\"name\":\"baseSettings.limitsUnit\",\"value\":\"day\"}]}]"
        };
        using var httpClient = new HttpClient(handler);
        var client = new ProwlarrSetupClient(handler, "http://prowlarr:9696", "key");

        await client.SetupAsync(new ProwlarrSetupOptions("http://sonarr", "s", "http://radarr", "r",
            [new ProwlarrNewznabIndexer("NZBDAV", "http://indexer", "i")]));

        var body = JsonNode.Parse(handler.RequestBodies.First())!.AsObject();
        Assert.Equal(8, body["fields"]!.AsArray().Count);
        Assert.DoesNotContain(body["fields"]!.AsArray(), field => field!["name"]!.GetValue<string>() is "genericOnly" or "presetOnly");
    }

    [Fact]
    public async Task Pinned_schema_catalog_is_accepted_with_bounded_limits()
    {
        var handler = new ProwlarrHandler { ExpectedApiKey = "key", IndexerSchema = RepresentativePinnedIndexerSchema() };
        using var httpClient = new HttpClient(handler);
        var client = new ProwlarrSetupClient(handler, "http://prowlarr:9696", "key");

        await client.SetupAsync(new ProwlarrSetupOptions("http://sonarr", "s", "http://radarr", "r",
            [new ProwlarrNewznabIndexer("NZBDAV", "http://indexer", "i")]));

        Assert.Equal(1, handler.Count("POST", "/api/v1/indexer"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Schema_response_cap_is_bounded(bool overLimit)
    {
        var handler = new ProwlarrHandler
        {
            ExpectedApiKey = "key",
            IndexerSchema = SchemaNearResponseLimit(overLimit)
        };
        using var httpClient = new HttpClient(handler);
        var client = new ProwlarrSetupClient(handler, "http://prowlarr:9696", "key");
        var options = new ProwlarrSetupOptions("http://sonarr", "s", "http://radarr", "r",
            [new ProwlarrNewznabIndexer("NZBDAV", "http://indexer", "i")]);

        if (overLimit)
            await Assert.ThrowsAsync<ProwlarrSetupProtocolException>(() => client.SetupAsync(options));
        else
            await client.SetupAsync(options);

        static string SchemaNearResponseLimit(bool overLimit)
        {
            var fields = new StringBuilder("[{\"name\":\"baseUrl\",\"value\":\"\"},{\"name\":\"apiPath\",\"value\":\"/api\"},{\"name\":\"apiKey\",\"value\":\"\"},{\"name\":\"additionalParameters\",\"value\":\"\"},{\"name\":\"vipExpiration\",\"value\":\"\"},{\"name\":\"baseSettings.queryLimit\",\"value\":0},{\"name\":\"baseSettings.grabLimit\",\"value\":0},{\"name\":\"baseSettings.limitsUnit\",\"value\":\"day\"}");
            var count = overLimit ? 8192 : 7000;
            for (var i = 0; i < count; i++)
                fields.Append(",{\"name\":\"metadata").Append(i).Append("\",\"value\":\"\",\"label\":\"").Append('x', 1000).Append("\"}");
            fields.Append("]");
            return "[{\"implementation\":\"Newznab\",\"implementationName\":\"Newznab\",\"name\":\"Generic Newznab\",\"configContract\":\"NewznabSettings\",\"appProfileId\":1,\"fields\":" + fields + "}]";
        }
    }

    [Fact]
    public async Task Representative_pinned_catalog_with_multiple_newznab_presets_is_accepted()
    {
        var handler = new ProwlarrHandler
        {
            ExpectedApiKey = "key",
            IndexerSchema = RepresentativePinnedIndexerSchema()
        };
        using var httpClient = new HttpClient(handler);
        var client = new ProwlarrSetupClient(handler, "http://prowlarr:9696", "key");

        await client.SetupAsync(new ProwlarrSetupOptions("http://sonarr", "s", "http://radarr", "r",
            [new ProwlarrNewznabIndexer("NZBDAV", "http://indexer", "i")]));

        var body = JsonNode.Parse(handler.RequestBodies.First())!.AsObject();
        Assert.DoesNotContain(body["fields"]!.AsArray(), field => field!["name"]!.GetValue<string>() == "queryAuth");
        Assert.Equal(8, body["fields"]!.AsArray().Count);
        Assert.Equal(1, body["appProfileId"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JSON_element_safety_bound_is_enforced(bool overLimit)
    {
        var handler = new ProwlarrHandler
        {
            ExpectedApiKey = "key",
            IndexerSchema = ElementBoundCatalog(overLimit ? 77 : 75)
        };
        using var httpClient = new HttpClient(handler);
        var client = new ProwlarrSetupClient(handler, "http://prowlarr:9696", "key");
        var options = new ProwlarrSetupOptions("http://sonarr", "s", "http://radarr", "r",
            [new ProwlarrNewznabIndexer("NZBDAV", "http://indexer", "i")]);

        if (overLimit)
            await Assert.ThrowsAsync<ProwlarrSetupProtocolException>(() => client.SetupAsync(options));
        else
            await client.SetupAsync(options);
    }

    [Fact]
    public async Task Application_profile_selection_prefers_standard_and_rejects_ambiguous_profiles()
    {
        var handler = new ProwlarrHandler
        {
            ExpectedApiKey = "key",
            ApplicationProfiles = "[{\"id\":9,\"name\":\"Other\"},{\"id\":4,\"name\":\"Standard\"}]"
        };
        using var httpClient = new HttpClient(handler);
        var client = new ProwlarrSetupClient(handler, "http://prowlarr:9696", "key");
        await client.SetupAsync(new ProwlarrSetupOptions("http://sonarr", "s", "http://radarr", "r",
            [new ProwlarrNewznabIndexer("NZBDAV", "http://indexer", "i")]));
        Assert.Equal(4, JsonNode.Parse(handler.RequestBodies.First())!["appProfileId"]!.GetValue<int>());

        handler.ApplicationProfiles = "[{\"id\":1,\"name\":\"Standard\"},{\"id\":2,\"name\":\"standard\"}]";
        await Assert.ThrowsAsync<ProwlarrSetupProtocolException>(() => client.SetupAsync(
            new ProwlarrSetupOptions("http://sonarr", "s", "http://radarr", "r",
                [new ProwlarrNewznabIndexer("Second", "http://other", "j")] )));
    }

    [Fact]
    public async Task No_positive_existing_application_profile_is_rejected_without_writing()
    {
        var handler = new ProwlarrHandler { ExpectedApiKey = "key", ApplicationProfiles = "[{\"id\":0,\"name\":\"Standard\"}]" };
        using var httpClient = new HttpClient(handler);
        var client = new ProwlarrSetupClient(handler, "http://prowlarr:9696", "key");

        await Assert.ThrowsAsync<ProwlarrSetupProtocolException>(() => client.SetupAsync(new ProwlarrSetupOptions(
            "http://sonarr", "s", "http://radarr", "r",
            [new ProwlarrNewznabIndexer("NZBDAV", "http://indexer", "i")] )));
        Assert.Equal(0, handler.Count("POST", "/api/v1/indexer"));
    }

    [Fact]
    public async Task Unrelated_implementation_display_name_mismatch_is_preserved()
    {
        var handler = new ProwlarrHandler
        {
            ExpectedApiKey = "key",
            Indexers = [Json("{\"id\":8,\"name\":\"FileList\",\"implementation\":\"FileList\",\"implementationName\":\"FileList.io\",\"fields\":[]}")],
            Applications = [Json("{\"id\":7,\"name\":\"FileList\",\"implementation\":\"FileList\",\"implementationName\":\"FileList.io\",\"fields\":[]}")]
        };
        using var httpClient = new HttpClient(handler);
        var client = new ProwlarrSetupClient(handler, "http://prowlarr:9696", "key");

        await client.SetupAsync(new ProwlarrSetupOptions("http://sonarr", "s", "http://radarr", "r",
            [new ProwlarrNewznabIndexer("NZBDAV", "http://indexer", "i")]));

        Assert.Equal(8, handler.Indexers.Single(item => item.GetProperty("name").GetString() == "FileList").GetProperty("id").GetInt32());
        Assert.Equal(7, handler.Applications.Single(item => item.GetProperty("name").GetString() == "FileList").GetProperty("id").GetInt32());
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Accepted)]
    public async Task Existing_managed_names_match_case_and_whitespace_without_duplicate_posts(HttpStatusCode putStatus)
    {
        var handler = new ProwlarrHandler
        {
            ExpectedApiKey = "key",
            PutStatus = putStatus,
            Applications =
            [
                Json("{\"id\":1,\"name\":\" NZBDAV Sonarr \",\"implementation\":\"Sonarr\",\"fields\":[{\"name\":\"prowlarrUrl\",\"value\":\"http://prowlarr:9696\"},{\"name\":\"baseUrl\",\"value\":\"http://sonarr\"},{\"name\":\"apiKey\",\"value\":\"s\"}] }"),
                Json("{\"id\":2,\"name\":\" nzbdav radarr\",\"implementation\":\"Radarr\",\"fields\":[{\"name\":\"prowlarrUrl\",\"value\":\"http://prowlarr:9696\"},{\"name\":\"baseUrl\",\"value\":\"http://radarr\"},{\"name\":\"apiKey\",\"value\":\"r\"}] }")
            ]
        };
        using var httpClient = new HttpClient(handler);
        var client = new ProwlarrSetupClient(handler, "http://prowlarr:9696", "key");

        await client.SetupAsync(new ProwlarrSetupOptions("http://sonarr", "s", "http://radarr", "r", []));

        Assert.Equal(0, handler.Count("POST", "/api/v1/applications"));
        Assert.Equal(2, handler.Count("PUT", "/api/v1/applications/1") + handler.Count("PUT", "/api/v1/applications/2"));
    }

    [Fact]
    public async Task Multiple_requested_indexers_are_written_from_one_schema()
    {
        var handler = new ProwlarrHandler { ExpectedApiKey = "key" };
        using var httpClient = new HttpClient(handler);
        var client = new ProwlarrSetupClient(handler, "http://prowlarr:9696", "key");

        await client.SetupAsync(new ProwlarrSetupOptions("http://sonarr", "s", "http://radarr", "r",
            [new ProwlarrNewznabIndexer("One", "http://one", "one-key"),
             new ProwlarrNewznabIndexer("Two", "http://two", "two-key")]));

        Assert.Equal(2, handler.Count("POST", "/api/v1/indexer"));
        Assert.Equal(new[] { "One", "Two" }, handler.RequestBodies.Take(2)
            .Select(body => JsonNode.Parse(body)!["name"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Retry_after_partial_post_failure_does_not_duplicate_indexer()
    {
        var handler = new ProwlarrHandler { ExpectedApiKey = "key", FailFirstIndexerPostAfterPersist = true };
        using var httpClient = new HttpClient(handler);
        var client = new ProwlarrSetupClient(handler, "http://prowlarr:9696", "key");
        var options = new ProwlarrSetupOptions("http://sonarr", "s", "http://radarr", "r",
            [new ProwlarrNewznabIndexer("NZBDAV", "http://indexer", "i")]);

        await Assert.ThrowsAsync<ProwlarrSetupHttpException>(() => client.SetupAsync(options));
        await client.SetupAsync(options);

        Assert.Equal(1, handler.Count("POST", "/api/v1/indexer"));
        Assert.Contains(handler.Indexers, indexer => indexer.GetProperty("name").GetString() == "NZBDAV");
    }

    [Fact]
    public async Task Retry_after_failure_of_second_indexer_does_not_duplicate_prior_posts()
    {
        var handler = new ProwlarrHandler { ExpectedApiKey = "key", FailIndexerPostNumberAfterPersist = 2 };
        using var httpClient = new HttpClient(handler);
        var client = new ProwlarrSetupClient(handler, "http://prowlarr:9696", "key");
        var options = new ProwlarrSetupOptions("http://sonarr", "s", "http://radarr", "r",
            [new ProwlarrNewznabIndexer("One", "http://one", "one-key"),
             new ProwlarrNewznabIndexer("Two", "http://two", "two-key"),
             new ProwlarrNewznabIndexer("Three", "http://three", "three-key")]);

        await Assert.ThrowsAsync<ProwlarrSetupHttpException>(() => client.SetupAsync(options));
        await client.SetupAsync(options);

        Assert.Equal(3, handler.Count("POST", "/api/v1/indexer"));
        Assert.Equal(1, handler.Indexers.Count(indexer => indexer.GetProperty("name").GetString() == "One"));
        Assert.Equal(1, handler.Indexers.Count(indexer => indexer.GetProperty("name").GetString() == "Two"));
        Assert.Equal(1, handler.Indexers.Count(indexer => indexer.GetProperty("name").GetString() == "Three"));
    }

    [Fact]
    public async Task Pinned_2525491_fixtures_have_expected_hashes_counts_and_payload_contract()
    {
        Assert.Equal(277_272, new FileInfo(FindFixture("indexer-schema.json.gz")).Length);
        var indexerBytes = ReadGzipFixture("indexer-schema.json.gz");
        Assert.Equal(5_731_535, indexerBytes.Length);
        Assert.Equal("2143cf7d84318779003a8003d5477033d11374be47e3a1f25c4ea81994aaa10d", Convert.ToHexString(SHA256.HashData(indexerBytes)).ToLowerInvariant());
        using (var document = JsonDocument.Parse(indexerBytes))
        {
            Assert.Equal(621, document.RootElement.GetArrayLength());
            Assert.Equal(176_110, CountElements(document.RootElement));
        }
        var applications = ReadFixture("applications-schema.json");
        Assert.Equal(124_015, applications.Length);
        Assert.Equal("5e145b6abf77571640fe6345d15d930d17f167f652a8ec43d9bc1055370c33af", Convert.ToHexString(SHA256.HashData(applications)).ToLowerInvariant());
        Assert.Equal(7, JsonDocument.Parse(applications).RootElement.GetArrayLength());
        var profiles = ReadFixture("appprofile.json");
        Assert.Equal(167, profiles.Length);
        Assert.Equal("455dca451291bd50bc100a1ac3a4db15f49b56d9b961b5a31d415f6afc113752", Convert.ToHexString(SHA256.HashData(profiles)).ToLowerInvariant());
        Assert.Equal(1, JsonDocument.Parse(profiles).RootElement.GetArrayLength());

        var handler = new ProwlarrHandler
        {
            ExpectedApiKey = "key",
            IndexerSchema = Encoding.UTF8.GetString(indexerBytes),
            ApplicationSchema = Encoding.UTF8.GetString(applications),
            ApplicationProfiles = Encoding.UTF8.GetString(profiles)
        };
        using var client = new ProwlarrSetupClient(handler, "http://prowlarr:9696", "key");
        await client.SetupAsync(new ProwlarrSetupOptions("http://sonarr", "s", "http://radarr", "r",
            [new ProwlarrNewznabIndexer("NZBDAV", "http://indexer/api", "i")]));

        var payload = JsonNode.Parse(handler.RequestBodies.First())!.AsObject();
        var fields = payload["fields"]!.AsArray().Select(x => x!["name"]!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "baseUrl", "apiPath", "apiKey", "additionalParameters", "vipExpiration", "baseSettings.queryLimit", "baseSettings.grabLimit", "baseSettings.limitsUnit" }, fields);
        Assert.Equal("i", payload["fields"]!.AsArray().Single(x => x!["name"]!.GetValue<string>() == "apiKey")!["value"]!.GetValue<string>());
        Assert.DoesNotContain("queryAuth", handler.RequestBodies.First(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rich_masked_resources_are_idempotent_and_preserve_unrelated_metadata()
    {
        var handler = new ProwlarrHandler
        {
            ExpectedApiKey = "key",
            Indexers = [Json("{\"id\":10,\"name\":\"NZBDAV\",\"implementation\":\"Newznab\",\"implementationName\":\"Newznab\",\"configContract\":\"NewznabSettings\",\"appProfileId\":1,\"enable\":true,\"custom\":{\"keep\":true},\"fields\":[{\"name\":\"baseUrl\",\"value\":\"http://indexer/api\"},{\"name\":\"apiPath\",\"value\":\"/api\"},{\"name\":\"apiKey\",\"value\":\"real-indexer-secret\"},{\"name\":\"additionalParameters\",\"value\":\"\"},{\"name\":\"vipExpiration\",\"value\":\"\"},{\"name\":\"baseSettings.queryLimit\",\"value\":0},{\"name\":\"baseSettings.grabLimit\",\"value\":0},{\"name\":\"baseSettings.limitsUnit\",\"value\":\"day\"},{\"name\":\"vendorField\",\"value\":{\"keep\":true}}]}" )],
            Applications = [
                Json("{\"id\":11,\"name\":\"NZBDAV Sonarr\",\"implementation\":\"Sonarr\",\"implementationName\":\"Sonarr\",\"configContract\":\"SonarrSettings\",\"syncLevel\":\"fullSync\",\"custom\":\"sonarr\",\"fields\":[{\"name\":\"prowlarrUrl\",\"value\":\"http://prowlarr:9696\"},{\"name\":\"baseUrl\",\"value\":\"http://sonarr\"},{\"name\":\"apiKey\",\"value\":\"real-sonarr-secret\"}]}"),
                Json("{\"id\":12,\"name\":\"NZBDAV Radarr\",\"implementation\":\"Radarr\",\"implementationName\":\"Radarr\",\"configContract\":\"RadarrSettings\",\"syncLevel\":\"fullSync\",\"custom\":\"radarr\",\"fields\":[{\"name\":\"prowlarrUrl\",\"value\":\"http://prowlarr:9696\"},{\"name\":\"baseUrl\",\"value\":\"http://radarr\"},{\"name\":\"apiKey\",\"value\":\"real-radarr-secret\"}]}" )]
        };
        using var client = new ProwlarrSetupClient(handler, "http://prowlarr:9696", "key");
        var options = new ProwlarrSetupOptions("http://sonarr", "s", "http://radarr", "r", [new ProwlarrNewznabIndexer("NZBDAV", "http://indexer/api", "i")]);
        var first = await client.SetupAsync(options);
        var before = handler.ResourcesJson();
        var writes = handler.WriteCount;
        var second = await client.SetupAsync(options);

        Assert.Equal(new ProwlarrSetupResult(0, 0, 3).ToString(), second.ToString());
        Assert.Equal(0, handler.WriteCount - writes);
        Assert.Equal(before, handler.ResourcesJson());
        Assert.Contains("keep", handler.ResourcesJson());
        Assert.DoesNotContain("real-", handler.RequestBodies.LastOrDefault() ?? string.Empty);
        Assert.Equal(3, first.Created + first.Updated + first.Unchanged);
    }

    [Fact]
    public async Task Final_get_wins_over_an_external_change_after_the_initial_snapshot()
    {
        var handler = new ProwlarrHandler
        {
            ExpectedApiKey = "key",
            Indexers = [Json("{\"id\":10,\"name\":\"NZBDAV\",\"implementation\":\"Newznab\",\"implementationName\":\"Newznab\",\"configContract\":\"NewznabSettings\",\"appProfileId\":1,\"enable\":true,\"fields\":[{\"name\":\"baseUrl\",\"value\":\"http://indexer\"},{\"name\":\"apiPath\",\"value\":\"/api\"},{\"name\":\"apiKey\",\"value\":\"i\"},{\"name\":\"additionalParameters\",\"value\":\"\"},{\"name\":\"vipExpiration\",\"value\":\"\"},{\"name\":\"baseSettings.queryLimit\",\"value\":0},{\"name\":\"baseSettings.grabLimit\",\"value\":0},{\"name\":\"baseSettings.limitsUnit\",\"value\":\"day\"}]}" )],
            ChangeIndexerOnFinalRead = true
        };
        using var client = new ProwlarrSetupClient(handler, "http://prowlarr:9696", "key");
        var result = await client.SetupAsync(new ProwlarrSetupOptions("http://sonarr", "s", "http://radarr", "r", [new ProwlarrNewznabIndexer("NZBDAV", "http://indexer", "i")]));
        Assert.Equal(1, result.Unchanged);
        Assert.Equal(0, handler.Count("PUT", "/api/v1/indexer/10"));
        Assert.Contains("external", handler.ResourcesJson());
    }

    [Fact]
    public async Task Base_alias_and_query_serialization_are_preserved_without_querying_the_api_endpoint()
    {
        var handler = new ProwlarrHandler { ExpectedApiKey = "key" };
        using var client = new ProwlarrSetupClient(handler, "http://Prowlarr:9696/Prowlarr/", "key");
        var indexerUrl = "http://Indexer:80/Api?QueryCase=MiXeD";
        var sonarrUrl = "http://Sonarr:80/Api?QueryCase=MiXeD";
        await client.SetupAsync(new ProwlarrSetupOptions(sonarrUrl, "s", "http://Radarr:80/Api?QueryCase=MiXeD", "r", [new ProwlarrNewznabIndexer("NZBDAV", indexerUrl, "i")], "HTTP://prowlarr:9696/Prowlarr/"));

        Assert.All(handler.RequestUris, uri => Assert.StartsWith("/Prowlarr/api/v1/", uri.AbsolutePath, StringComparison.Ordinal));
        var indexer = JsonNode.Parse(handler.RequestBodies.First())!.AsObject();
        Assert.Equal(indexerUrl, indexer["fields"]!.AsArray().Single(x => x!["name"]!.GetValue<string>() == "baseUrl")!["value"]!.GetValue<string>());
        var sonarr = JsonNode.Parse(handler.RequestBodies.Skip(1).First())!.AsObject();
        Assert.Equal(sonarrUrl, sonarr["fields"]!.AsArray().Single(x => x!["name"]!.GetValue<string>() == "baseUrl")!["value"]!.GetValue<string>());
        Assert.DoesNotContain(handler.RequestUris, uri => !string.IsNullOrEmpty(uri.Query));
    }

    [Fact]
    public async Task External_create_before_post_is_reconciled_once_after_conflict()
    {
        var handler = new ProwlarrHandler { ExpectedApiKey = "key", CreateBeforeNextIndexerPost = true };
        using var client = new ProwlarrSetupClient(handler, "http://prowlarr:9696", "key");
        var result = await client.SetupAsync(new ProwlarrSetupOptions("http://sonarr", "s", "http://radarr", "r", [new ProwlarrNewznabIndexer("NZBDAV", "http://indexer", "i")]));
        Assert.Equal(1, result.Unchanged);
        Assert.Equal(1, handler.Count("POST", "/api/v1/indexer"));
        Assert.Equal(0, handler.Count("PUT", "/api/v1/indexer/10"));
    }

    [Fact]
    public async Task Nested_user_field_input_is_rejected_at_the_safety_boundary()
    {
        object value = "leaf";
        for (var i = 0; i < 40; i++) value = new Dictionary<string, object?> { ["nested"] = value };
        var handler = new ProwlarrHandler { ExpectedApiKey = "key" };
        using var client = new ProwlarrSetupClient(handler, "http://prowlarr:9696", "key");
        await Assert.ThrowsAsync<ProwlarrSetupProtocolException>(() => client.SetupAsync(new ProwlarrSetupOptions("http://sonarr", "s", "http://radarr", "r", [new ProwlarrNewznabIndexer("one", "http://indexer", "i", new Dictionary<string, object?> { ["additionalParameters"] = value })])));
        Assert.Empty(handler.RequestUris);
    }

    private static byte[] ReadFixture(string name)
    {
        var path = FindFixture(name);
        return File.ReadAllBytes(path);
    }

    private static byte[] ReadGzipFixture(string name)
    {
        using var input = File.OpenRead(FindFixture(name));
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    private static string FindFixture(string name)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "backend.Tests", "Clients", "ProwlarrSetup", "Fixtures", name);
            if (File.Exists(candidate)) return candidate;
        }
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "backend.Tests", "Clients", "ProwlarrSetup", "Fixtures", name);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(name);
    }

    private static int CountElements(JsonElement element)
    {
        var count = 0;
        void Visit(JsonElement value)
        {
            count++;
            if (value.ValueKind == JsonValueKind.Array) foreach (var child in value.EnumerateArray()) Visit(child);
            else if (value.ValueKind == JsonValueKind.Object) foreach (var property in value.EnumerateObject()) Visit(property.Value);
        }
        Visit(element);
        return count;
    }

    private static string RepresentativePinnedIndexerSchema()
    {
        var schemas = new JsonArray();
        for (var i = 0; i < 621; i++)
        {
            var isNewznab = i < 3;
            var implementation = isNewznab ? "Newznab" : i == 3 ? "FileList" : $"Indexer{i}";
            var implementationName = implementation == "FileList" ? "FileList.io" : implementation;
            var name = i switch
            {
                0 => "Generic Newznab",
                1 => "Preset - Usenet",
                2 => "Preset - Torznab",
                _ => $"{implementationName} preset"
            };
            var fields = new JsonArray
            {
                Field("baseUrl", "Base URL", ""), Field("apiPath", "API path", "/api"),
                Field("apiKey", "API Key", ""), Field("additionalParameters", "Additional parameters", ""),
                Field("vipExpiration", "VIP expiration", ""), Field("baseSettings.queryLimit", "Query limit", 0),
                Field("baseSettings.grabLimit", "Grab limit", 0), Field("baseSettings.limitsUnit", "Limits unit", "day")
            };
            schemas.Add(new JsonObject
            {
                ["implementation"] = implementation,
                ["implementationName"] = implementationName,
                ["name"] = name,
                ["configContract"] = isNewznab ? "NewznabSettings" : $"Settings{i}",
                ["appProfileId"] = 0,
                ["fields"] = fields
            });
        }
        return schemas.ToJsonString();

        static JsonObject Field(string name, string label, object value) => new()
        {
            ["name"] = name,
            ["label"] = label,
            ["value"] = JsonSerializer.SerializeToNode(value),
            ["type"] = "textbox",
            ["advanced"] = false
        };
    }

    private static string ElementBoundCatalog(int fieldCount)
    {
        var schemas = new JsonArray();
        for (var schemaIndex = 0; schemaIndex < 800; schemaIndex++)
        {
            var fields = new JsonArray();
            for (var fieldIndex = 0; fieldIndex < fieldCount; fieldIndex++)
                fields.Add(new JsonObject
                {
                    ["name"] = schemaIndex == 0 && fieldIndex < 8 ? new[] { "baseUrl", "apiPath", "apiKey", "additionalParameters", "vipExpiration", "baseSettings.queryLimit", "baseSettings.grabLimit", "baseSettings.limitsUnit" }[fieldIndex] : $"field{fieldIndex}",
                    ["value"] = schemaIndex == 0 && fieldIndex >= 5 ? fieldIndex == 7 ? "day" : 0 : "",
                    ["label"] = "field"
                });
            schemas.Add(new JsonObject
            {
                ["implementation"] = schemaIndex == 0 ? "Newznab" : $"Implementation{schemaIndex}",
                ["implementationName"] = schemaIndex == 0 ? "Newznab" : $"Implementation{schemaIndex}",
                ["name"] = schemaIndex == 0 ? "Generic Newznab" : $"Schema {schemaIndex}",
                ["configContract"] = schemaIndex == 0 ? "NewznabSettings" : $"Contract{schemaIndex}",
                ["fields"] = fields
            });
        }
        return schemas.ToJsonString();
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class DeceptiveIndexerCollection(int count) : IReadOnlyCollection<ProwlarrNewznabIndexer>
    {
        public int Count => throw new InvalidOperationException("Count must not be trusted");
        public IEnumerator<ProwlarrNewznabIndexer> GetEnumerator()
        {
            for (var i = 0; i < count; i++) yield return new ProwlarrNewznabIndexer($"Indexer{i}", "http://indexer", "key");
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class DeceptiveFields : IReadOnlyDictionary<string, object?>
    {
        private readonly List<KeyValuePair<string, object?>> _items;
        public DeceptiveFields(int count) => _items = Enumerable.Range(0, count).Select(i => new KeyValuePair<string, object?>($"field{i}", "value")).ToList();
        public int Enumerated { get; private set; }
        public int Count => throw new InvalidOperationException("Count must not be trusted");
        public IEnumerable<string> Keys => _items.Select(x => x.Key);
        public IEnumerable<object?> Values => _items.Select(x => x.Value);
        public object? this[string key] => _items.Single(x => x.Key == key).Value;
        public bool ContainsKey(string key) => _items.Any(x => x.Key == key);
        public bool TryGetValue(string key, out object? value)
        {
            var found = _items.FirstOrDefault(x => x.Key == key); value = found.Value; return found.Key is not null;
        }
        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        {
            foreach (var item in _items) { Enumerated++; yield return item; }
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    internal sealed class ProwlarrHandler : HttpMessageHandler
    {
        public List<JsonElement> Indexers { get; set; } = [Json("{\"id\":99,\"name\":\"Unrelated\",\"implementation\":\"Other\",\"fields\":[]}")];
        public List<JsonElement> Applications { get; set; } = [];
        public string IndexerSchema { get; set; } = "[{\"implementation\":\"Newznab\",\"implementationName\":\"Newznab\",\"name\":\"Generic Newznab\",\"configContract\":\"NewznabSettings\",\"fields\":[{\"name\":\"baseUrl\",\"value\":\"\"},{\"name\":\"apiPath\",\"value\":\"/api\"},{\"name\":\"apiKey\",\"value\":\"\"},{\"name\":\"additionalParameters\",\"value\":\"\"},{\"name\":\"vipExpiration\",\"value\":\"\"},{\"name\":\"baseSettings.queryLimit\",\"value\":0},{\"name\":\"baseSettings.grabLimit\",\"value\":0},{\"name\":\"baseSettings.limitsUnit\",\"value\":\"day\"}]}]";
        public string ApplicationSchema { get; set; } = "[{\"implementation\":\"Sonarr\",\"implementationName\":\"Sonarr\",\"configContract\":\"SonarrSettings\",\"fields\":[{\"name\":\"prowlarrUrl\",\"value\":\"\"},{\"name\":\"baseUrl\",\"value\":\"\"},{\"name\":\"apiKey\",\"value\":\"\"}]},{\"implementation\":\"Radarr\",\"implementationName\":\"Radarr\",\"configContract\":\"RadarrSettings\",\"fields\":[{\"name\":\"prowlarrUrl\",\"value\":\"\"},{\"name\":\"baseUrl\",\"value\":\"\"},{\"name\":\"apiKey\",\"value\":\"\"}]}]";
        public string ApplicationProfiles { get; set; } = "[{\"id\":1,\"name\":\"Standard\"}]";
        public List<string> RequestBodies { get; } = [];
        public List<Uri> RequestUris { get; } = [];
        public string ExpectedApiKey { get; set; } = "prowlarr-secret";
        public HttpStatusCode PutStatus { get; set; } = HttpStatusCode.OK;
        public bool FailFirstIndexerPostAfterPersist { get; set; }
        public int? FailIndexerPostNumberAfterPersist { get; set; }
        public bool ChangeIndexerOnFinalRead { get; set; }
        public bool CreateBeforeNextIndexerPost { get; set; }
        public bool ExternalCreateChangesApiKey { get; set; }
        public int WriteCount { get; private set; }
        private int _indexerReadCount;
        private int _indexerPostCount;
        private readonly List<(string Method, string Path)> _requests = [];

        public Task<HttpResponseMessage> SendForTestAsync(HttpRequestMessage request, CancellationToken cancellationToken) => SendAsync(request, cancellationToken);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(ExpectedApiKey, request.Headers.GetValues("X-Api-Key").Single());
            RequestUris.Add(request.RequestUri!);
            var path = request.RequestUri!.AbsolutePath;
            _requests.Add((request.Method.Method, path));
            if (request.Content is not null) RequestBodies.Add(request.Content.ReadAsStringAsync(cancellationToken).Result);
            // Pinned contract fixture: Prowlarr v2.5.2.5491, API /api/v1.
            if (request.Method == HttpMethod.Get && path.EndsWith("/indexer/schema"))
                return Reply(IndexerSchema);
            if (request.Method == HttpMethod.Get && path.EndsWith("/applications/schema"))
                return Reply(ApplicationSchema);
            if (request.Method == HttpMethod.Get && path.EndsWith("/appprofile", StringComparison.OrdinalIgnoreCase)) return Reply(ApplicationProfiles);
            if (request.Method == HttpMethod.Get && path.EndsWith("/indexer", StringComparison.OrdinalIgnoreCase))
            {
                if (ChangeIndexerOnFinalRead && ++_indexerReadCount == 2)
                {
                    var target = Indexers.FindIndex(x => x.GetProperty("name").GetString() == "NZBDAV");
                    if (target >= 0)
                    {
                        var changed = JsonNode.Parse(Indexers[target].GetRawText())!.AsObject();
                        changed["external"] = "external";
                        Indexers[target] = Json(changed.ToJsonString());
                    }
                }
                return Reply(Mask(Indexers));
            }
            if (request.Method == HttpMethod.Get && path.EndsWith("/applications", StringComparison.OrdinalIgnoreCase)) return Reply(Mask(Applications));
            if (request.Method == HttpMethod.Post && path.EndsWith("/indexer", StringComparison.OrdinalIgnoreCase)) { WriteCount++; var indexer = JsonNode.Parse(RequestBodies[^1])!.AsObject(); if (CreateBeforeNextIndexerPost) { CreateBeforeNextIndexerPost = false; if (ExternalCreateChangesApiKey) indexer["enable"] = false; indexer["id"] = 10 + _indexerPostCount++; Indexers.Add(Json(indexer.ToJsonString())); return Reply("{}", HttpStatusCode.Conflict); } if (Indexers.Any(x => string.Equals(x.GetProperty("name").GetString(), indexer["name"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase))) return Reply("{}", HttpStatusCode.Conflict); indexer["id"] = 10 + _indexerPostCount++; Indexers.Add(Json(indexer.ToJsonString())); if (FailFirstIndexerPostAfterPersist || _indexerPostCount == FailIndexerPostNumberAfterPersist) { FailFirstIndexerPostAfterPersist = false; FailIndexerPostNumberAfterPersist = null; return Reply("{}", HttpStatusCode.InternalServerError); } return Reply("{\"id\":10}", HttpStatusCode.Created); }
            if (request.Method == HttpMethod.Post && path.EndsWith("/applications", StringComparison.OrdinalIgnoreCase)) { WriteCount++; var app = JsonNode.Parse(RequestBodies[^1])!.AsObject(); if (Applications.Any(x => string.Equals(x.GetProperty("name").GetString(), app["name"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase))) return Reply("{}", HttpStatusCode.Conflict); app["id"] = 11 + Applications.Count; Applications.Add(Json(app.ToJsonString())); return Reply("{\"id\":11}", HttpStatusCode.Created); }
            if (request.Method == HttpMethod.Put) { WriteCount++; var body = JsonNode.Parse(RequestBodies[^1])!.AsObject(); var target = path.Contains("/api/v1/indexer/", StringComparison.OrdinalIgnoreCase) ? Indexers : Applications; var id = int.Parse(path[(path.LastIndexOf('/') + 1)..]); var at = target.FindIndex(x => x.GetProperty("id").GetInt32() == id); if (at >= 0) target[at] = Json(body.ToJsonString()); return Reply("{}", PutStatus); }
            return Reply("{}", HttpStatusCode.OK);
        }

        public int Count(string method, string path) => _requests.Count(x => x.Method == method && x.Path == path);
        public string ResourcesJson() => JsonSerializer.Serialize(new { Indexers, Applications });
        private static string Mask(List<JsonElement> resources)
        {
            var array = new JsonArray();
            foreach (var resource in resources) { var copy = JsonNode.Parse(resource.GetRawText())!.AsObject(); if (copy["fields"] is JsonArray fields) foreach (var field in fields.OfType<JsonObject>()) { var name = field["name"]?.GetValue<string>(); if (name is not null && (name.Contains("apikey", StringComparison.OrdinalIgnoreCase) || name.Contains("password", StringComparison.OrdinalIgnoreCase) || name.Contains("secret", StringComparison.OrdinalIgnoreCase) || name.Contains("token", StringComparison.OrdinalIgnoreCase))) field["value"] = "********"; } array.Add(copy); }
            return array.ToJsonString();
        }
        private static Task<HttpResponseMessage> Reply(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
