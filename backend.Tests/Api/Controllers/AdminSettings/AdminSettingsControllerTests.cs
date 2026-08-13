using System.Data;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NzbWebDAV.Api.Controllers;
using NzbWebDAV.Api.Controllers.AdminSettings;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Api.Controllers.AdminSettings;

[Collection(nameof(backend.Tests.Services.ConfigEncryptionDatabaseCollection))]
public sealed class AdminSettingsControllerTests
{
    private readonly backend.Tests.Services.ConfigEncryptionDatabaseFixture _fixture;

    public AdminSettingsControllerTests(backend.Tests.Services.ConfigEncryptionDatabaseFixture fixture)
        => _fixture = fixture;

    [Fact]
    public async Task Get_RedactsEveryScalarAndArrExtensionSecret()
    {
        const string marker = "admin-settings-marker-8f2e";
        using var test = await CreateTestAsync();
        await UpsertAsync(test.Db, "api.key", marker);
        await UpsertAsync(test.Db, "api.strm-key", marker);
        await UpsertAsync(test.Db, "webdav.pass", marker);
        await UpsertAsync(test.Db, "cache.l2.access-key", marker);
        await UpsertAsync(test.Db, "cache.l2.secret-key", marker);
        await UpsertAsync(test.Db, "usenet.providers", marker);
        await UpsertAsync(test.Db, "arr.instances", $$"""
        {
          "RadarrInstances": [{
            "Host": "http://radarr:7878",
            "ApiKey": "{{marker}}",
            "credential": "{{marker}}",
            "Nested": { "token": "{{marker}}", "password": "{{marker}}" }
          }],
          "SonarrInstances": [],
          "QueueRules": [],
          "credential": "{{marker}}",
          "unknownObject": { "accessKey": "{{marker}}" },
          "unknownArray": ["{{marker}}"],
          "nonObject": 42
        }
        """);
        await test.Db.SaveChangesAsync();
        await test.Manager.LoadConfig();

        var result = await InvokeAsync(test, HttpMethods.Get);
        var ok = Assert.IsType<OkObjectResult>(result);
        var wire = JsonSerializer.Serialize(ok.Value);

        Assert.DoesNotContain(marker, wire, StringComparison.Ordinal);
        var response = Assert.IsType<AdminSettingsResponse>(ok.Value);

        // Secret scalars are redacted in config responses.
        Assert.Equal(string.Empty, response.Config.ApiKey);
        Assert.Equal(string.Empty, response.Config.WebdavPass);
        Assert.Equal(string.Empty, response.Config.CacheL2AccessKey);
        Assert.Equal(string.Empty, response.Config.CacheL2SecretKey);
        Assert.True(response.HasSecrets.ApiKey);
        Assert.DoesNotContain("api.strm-key", JsonSerializer.Serialize(response.Config), StringComparison.OrdinalIgnoreCase);

        var configWire = JsonSerializer.Serialize(response.Config);
        Assert.DoesNotContain(marker, configWire, StringComparison.Ordinal);
        Assert.DoesNotContain("usenet.providers", configWire);

        using var arr = JsonDocument.Parse(response.Config.ArrInstances);
        Assert.DoesNotContain(marker, response.Config.ArrInstances, StringComparison.Ordinal);
        var instance = arr.RootElement.GetProperty("RadarrInstances")[0];
        var instanceProperties = instance.EnumerateObject().Select(x => x.Name).ToArray();
        Assert.Equal(2, instanceProperties.Length);
        Assert.Equal("Host", instanceProperties[0]);
        Assert.Equal("HasApiKey", instanceProperties[1]);

        // Assert exact response shape for ARR instances.
        Assert.True(instanceProperties.SequenceEqual(new[] { "Host", "HasApiKey" }));
        Assert.Equal("http://radarr:7878", instance.GetProperty("Host").GetString());
        Assert.True(instance.GetProperty("HasApiKey").GetBoolean());

        // Ensure no unsafe secret/unknown fields are surfaced.
        Assert.False(instance.TryGetProperty("ApiKey", out _));
        Assert.False(instance.TryGetProperty("apiKey", out _));
        Assert.False(instance.TryGetProperty("credential", out _));
        Assert.False(instance.TryGetProperty("token", out _));
        Assert.False(instance.TryGetProperty("password", out _));
        Assert.False(instance.TryGetProperty("unknown", out _));
        Assert.DoesNotContain("credential", response.Config.ArrInstances, StringComparison.Ordinal);
        Assert.DoesNotContain("token", response.Config.ArrInstances, StringComparison.Ordinal);
        Assert.DoesNotContain("password", response.Config.ArrInstances, StringComparison.Ordinal);
        Assert.DoesNotContain("unknown", response.Config.ArrInstances, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_RejectsMalformedLegacyArrWithoutReturningItsValue()
    {
        const string marker = "legacy-arr-marker-2c4a";
        using var test = await CreateTestAsync();
        await UpsertAsync(test.Db, "arr.instances", "{\"RadarrInstances\":[\"" + marker + "\"],\"SonarrInstances\":[],\"QueueRules\":[]}");
        await test.Db.SaveChangesAsync();
        await test.Manager.LoadConfig();

        var result = await InvokeAsync(test, HttpMethods.Get);
        var error = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, error.StatusCode);
        Assert.DoesNotContain(marker, JsonSerializer.Serialize(error.Value), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"config":{},"clearSecrets":[],"unknown":true}""")]
    [InlineData("""{"config":{"public.setting":true},"clearSecrets":[]}""")]
    [InlineData("""{"config":{"arr.instances":"{\"RadarrInstances\":[1],\"SonarrInstances\":[],\"QueueRules\":[]}"},"clearSecrets":[]}""")]
    [InlineData("""{"config":{"arr.instances":"{\"RadarrInstances\":[],\"RadarrInstances\":[],\"SonarrInstances\":[],\"QueueRules\":[]}"},"clearSecrets":[]}""")]
    [InlineData("""{"config":{"arr.instances":"{\"RadarrInstances\":[{\"Host\":\"http://same\",\"ApiKey\":\"one\"},{\"Host\":\"HTTP://SAME/\",\"ApiKey\":\"two\"}],\"SonarrInstances\":[],\"QueueRules\":[]}"},"clearSecrets":[]}""")]
    public async Task Post_RejectsMalformedStrictBodiesBeforeWrite(string body)
    {
        using var test = await CreateTestAsync();
        var before = await SnapshotAsync(test.Db);
        var events = 0;
        test.Manager.OnConfigChanged += (_, _) => events++;

        var result = await InvokeAsync(test, HttpMethods.Post, body);
        Assert.True(result is BadRequestObjectResult or ObjectResult);
        Assert.NotEqual(200, (result as ObjectResult)?.StatusCode);
        Assert.Equal(0, events);
        Assert.Equal(before, await SnapshotAsync(test.Db));
        Assert.DoesNotContain("admin-settings-marker", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Post_RejectsApiStrmKeyInConfigOrClearList()
    {
        using var test = await CreateTestAsync();
        var before = await SnapshotAsync(test.Db);
        var events = 0;
        test.Manager.OnConfigChanged += (_, _) => events++;

        var configResult = await InvokeAsync(test, HttpMethods.Post,
            Body(new Dictionary<string, string?> { ["api.strm-key"] = "blocked" }, []));
        Assert.IsType<BadRequestObjectResult>(configResult);

        var clearResult = await InvokeAsync(test, HttpMethods.Post,
            Body(new Dictionary<string, string?>(), ["api.strm-key"]));
        Assert.IsType<BadRequestObjectResult>(clearResult);

        Assert.Equal(0, events);
        Assert.Equal(before, await SnapshotAsync(test.Db));
    }

    [Fact]
    public async Task Post_RejectsCountDepthAndStringBounds()
    {
        using var test = await CreateTestAsync();
        var tooMany = Enumerable.Range(0, 129)
            .ToDictionary(i => $"public.{i}", _ => (string?)"value");
        Assert.Equal(400, Assert.IsType<BadRequestObjectResult>(
            await InvokeAsync(test, HttpMethods.Post, Body(tooMany))).StatusCode);

        var tooLong = new Dictionary<string, string?> { ["public.setting"] = new string('x', 64 * 1024 + 1) };
        Assert.Equal(400, Assert.IsType<BadRequestObjectResult>(
            await InvokeAsync(test, HttpMethods.Post, Body(tooLong))).StatusCode);

        var tooDeep = "{\"config\":{" + string.Concat(Enumerable.Repeat("\"nested\":[", 20))
            + "null" + new string(']', 20) + "},\"clearSecrets\":[]}";
        Assert.Equal(400, Assert.IsType<BadRequestObjectResult>(
            await InvokeAsync(test, HttpMethods.Post, tooDeep)).StatusCode);
    }

    [Fact]
    public async Task Post_RejectsResponseOnlyArrFieldsAndOversizedBodies()
    {
        using var test = await CreateTestAsync();
        var body = """{"config":{"arr.instances":"{\"RadarrInstances\":[{\"Host\":\"http://radarr\",\"ApiKey\":\"x\",\"HasApiKey\":true}],\"SonarrInstances\":[],\"QueueRules\":[]}"},"clearSecrets":[]}""";
        var malformed = await InvokeAsync(test, HttpMethods.Post, body);
        Assert.Equal(400, Assert.IsType<BadRequestObjectResult>(malformed).StatusCode);

        var oversized = await InvokeAsync(test, HttpMethods.Post, new string('x', 8 * 1024 * 1024 + 1));
        Assert.Equal(400, Assert.IsType<BadRequestObjectResult>(oversized).StatusCode);
    }

    [Fact]
    public async Task Post_PreservesEscapedPathCaseAndLeadingSlashWithoutIdentityDrift()
    {
        using var test = await CreateTestAsync();
        var requested = "{\"RadarrInstances\":[{\"Host\":\"http://example.com:80/Api/V1/%2f/\",\"ApiKey\":\"key\"}],\"SonarrInstances\":[],\"QueueRules\":[]}";
        var parseStrict = typeof(AdminSettingsController).GetMethod("ParseStrictArr", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(parseStrict);
        parseStrict.Invoke(null, [requested]);

        var mergeArrSecrets = typeof(AdminSettingsController).GetMethod("MergeArrSecrets", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(mergeArrSecrets);
        mergeArrSecrets.Invoke(null, [null, requested, false]);

        var updateRequest = new AdminSettingsRequest
        {
            Config = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["arr.instances"] = requested
            },
            ClearSecrets = []
        };

        var updateMethod = typeof(AdminSettingsController).GetMethod("UpdateAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(updateMethod);

        var updateController = new AdminSettingsController(new DavDatabaseClient(test.Db), test.Manager, test.Encryption);
        try
        {
            var updateTask = (Task<AdminSettingsResponse>)updateMethod.Invoke(updateController, [updateRequest, CancellationToken.None])!;
            var updateResponse = await updateTask;
            Assert.NotNull(updateResponse);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is BadHttpRequestException badRequest)
        {
            Assert.Fail($"UpdateAsync threw before endpoint path: {badRequest.Message}{Environment.NewLine}{badRequest.StackTrace}");
        }

        var result = await InvokeAsync(test, HttpMethods.Post, JsonSerializer.Serialize(new
        {
            config = new Dictionary<string, string?> { ["arr.instances"] = requested },
            clearSecrets = Array.Empty<string>()
        }));

        if (result is BadRequestObjectResult bad)
        {
            var details = bad.Value?.GetType().FullName;
            var body = bad.Value is null ? "<null>" : JsonSerializer.Serialize(bad.Value);
            Assert.Fail($"Unexpected response type {result.GetType()} status={bad.StatusCode} details={details} body={body}");
        }

        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<AdminSettingsResponse>(okResult.Value);
        using var arr = JsonDocument.Parse(response.Config.ArrInstances);
        Assert.Equal("http://example.com/Api/V1/%2F", arr.RootElement
            .GetProperty("RadarrInstances")[0].GetProperty("Host").GetString());
    }

    [Fact]
    public async Task Post_RejectsArrIdentityWithMalformedPercentEncoding()
    {
        using var test = await CreateTestAsync();
        var body = Body(new Dictionary<string, string?>
        {
            ["arr.instances"] = "{\"RadarrInstances\":[{\"Host\":\"http://example.com/%ZZ/\",\"ApiKey\":\"x\"}],\"SonarrInstances\":[],\"QueueRules\":[]}"
        });

        var result = await InvokeAsync(test, HttpMethods.Post, body);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Post_CanonicalizesUnicodeHostAndDotSegmentsInArrIdentity()
    {
        using var test = await CreateTestAsync();
        var body = Body(new Dictionary<string, string?>
        {
            ["arr.instances"] = "{\"RadarrInstances\":[{\"Host\":\"HTTP://bücher.example./a/./../B/%2f/%2F\",\"ApiKey\":\"x\"}],\"SonarrInstances\":[],\"QueueRules\":[]}"
        });

        var result = await InvokeAsync(test, HttpMethods.Post, body);
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<AdminSettingsResponse>(ok.Value);
        using var arr = JsonDocument.Parse(response.Config.ArrInstances);
        Assert.Equal("http://xn--bcher-kva.example/B/%2F/%2F", arr.RootElement
            .GetProperty("RadarrInstances")[0].GetProperty("Host").GetString());
    }

    [Fact]
    public async Task Post_RejectsCrossCollectionArrIdentityConflictsWhenCanonicalized()
    {
        using var test = await CreateTestAsync();
        var body = Body(new Dictionary<string, string?>
        {
            ["arr.instances"] = "{\"RadarrInstances\":[{\"Host\":\"http://Example.com./API/../\",\"ApiKey\":\"x\"}],\"SonarrInstances\":[{\"Host\":\"http://example.com/\",\"ApiKey\":\"y\"}],\"QueueRules\":[]}"
        });

        var result = await InvokeAsync(test, HttpMethods.Post, body);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Post_RejectsMixedEscapeCaseArrIdentityCollision()
    {
        using var test = await CreateTestAsync();
        var body = """{"config":{"arr.instances":"{\"RadarrInstances\":[{\"Host\":\"http://example.com/Api/V1/%2f\",\"ApiKey\":\"one\"},{\"Host\":\"http://example.com/Api/V1/%2F\",\"ApiKey\":\"two\"}],\"SonarrInstances\":[],\"QueueRules\":[]}"},"clearSecrets":[]}""";
        var result = await InvokeAsync(test, HttpMethods.Post, body);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(400, badRequest.StatusCode);
    }

    [Fact]
    public async Task Post_MergesByNormalizedIdentityAndDropsLegacyExtensions()
    {
        using var test = await CreateTestAsync();
        await UpsertAsync(test.Db, "arr.instances", "{\"RadarrInstances\":[{\"Host\":\"HTTP://Radarr:7878/\",\"ApiKey\":\"old-key\",\"credential\":\"legacy-marker\"},{\"Host\":\"http://delete:7878\",\"ApiKey\":\"delete-key\"}],\"SonarrInstances\":[],\"QueueRules\":[],\"rootSecret\":\"legacy-marker\"}");
        await test.Db.SaveChangesAsync();
        await test.Manager.LoadConfig();

        var parseStrict = typeof(AdminSettingsController).GetMethod("ParseStrictArr", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(parseStrict);
        var mergeArrSecrets = typeof(AdminSettingsController).GetMethod("MergeArrSecrets", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(mergeArrSecrets);
        var requested = "{\"RadarrInstances\":[{\"Host\":\"http://new:7878\",\"ApiKey\":\"new-key\"},{\"Host\":\"http://radarr:7878\",\"ApiKey\":\"\"}],\"SonarrInstances\":[],\"QueueRules\":[]}";
        parseStrict.Invoke(null, [requested]);
        var merged = mergeArrSecrets.Invoke(null, ["{\"RadarrInstances\":[{\"Host\":\"HTTP://Radarr:7878/\",\"ApiKey\":\"old-key\",\"credential\":\"legacy-marker\"},{\"Host\":\"http://delete:7878\",\"ApiKey\":\"delete-key\"}],\"SonarrInstances\":[],\"QueueRules\":[],\"rootSecret\":\"legacy-marker\"}", requested, false]);
        Assert.NotNull(merged);

        var updateRequest = new AdminSettingsRequest
        {
            Config = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["arr.instances"] = requested
            },
            ClearSecrets = []
        };

        var updateMethod = typeof(AdminSettingsController).GetMethod("UpdateAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(updateMethod);

        var updateController = new AdminSettingsController(new DavDatabaseClient(test.Db), test.Manager, test.Encryption);
        try
        {
            var updateTask = (Task<AdminSettingsResponse>)updateMethod.Invoke(updateController, [updateRequest, CancellationToken.None])!;
            var updateResponse = await updateTask;
            Assert.NotNull(updateResponse);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is BadHttpRequestException badRequest)
        {
            Assert.Fail($"UpdateAsync threw before endpoint path: {badRequest.Message}{Environment.NewLine}{badRequest.StackTrace}");
        }

        var result = await InvokeAsync(test, HttpMethods.Post, JsonSerializer.Serialize(new
        {
            config = new Dictionary<string, string?> { ["arr.instances"] = requested },
            clearSecrets = Array.Empty<string>()
        }));
        if (result is BadRequestObjectResult bad)
        {
            var details = bad.Value?.GetType().FullName;
            var body = bad.Value is null ? "<null>" : JsonSerializer.Serialize(bad.Value);
            Assert.Fail($"Unexpected response type {result.GetType()} status={bad.StatusCode} details={details} body={body}");
        }

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<AdminSettingsResponse>(ok.Value);
        using var returned = JsonDocument.Parse(response.Config.ArrInstances);
        var hosts = returned.RootElement.GetProperty("RadarrInstances").EnumerateArray().ToArray();
        Assert.Equal(2, hosts.Length);
        Assert.Equal("http://new:7878", hosts[0].GetProperty("Host").GetString());
        Assert.False(hosts[0].GetProperty("HasApiKey").GetBoolean() == false);
        Assert.Equal("http://radarr:7878", hosts[1].GetProperty("Host").GetString());
        Assert.True(hosts[1].GetProperty("HasApiKey").GetBoolean());
        Assert.DoesNotContain("legacy-marker", JsonSerializer.Serialize(response), StringComparison.Ordinal);

        var stored = await test.Db.ConfigItems.AsNoTracking().SingleAsync(x => x.ConfigName == "arr.instances");
        Assert.True(stored.IsEncrypted);
        Assert.DoesNotContain("old-key", stored.ConfigValue, StringComparison.Ordinal);
        Assert.DoesNotContain("legacy-marker", stored.ConfigValue, StringComparison.Ordinal);
        var plaintext = test.Encryption.Decrypt("arr.instances", stored.ConfigValue).plaintext;
        Assert.DoesNotContain("credential", plaintext, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rootSecret", plaintext, StringComparison.OrdinalIgnoreCase);

        var clearRequested = "{\"RadarrInstances\":[{\"Host\":\"http://new:7878\",\"ApiKey\":\"\"},{\"Host\":\"http://radarr:7878\",\"ApiKey\":\"\"}],\"SonarrInstances\":[],\"QueueRules\":[]}";
        var clearResult = await InvokeAsync(test, HttpMethods.Post, Body(
            new Dictionary<string, string?> { ["arr.instances"] = clearRequested }, ["arr.instances"]));
        var cleared = Assert.IsType<AdminSettingsResponse>(Assert.IsType<OkObjectResult>(clearResult).Value);
        using var clearedArr = JsonDocument.Parse(cleared.Config.ArrInstances);
        Assert.All(clearedArr.RootElement.GetProperty("RadarrInstances").EnumerateArray(), instance =>
            Assert.False(instance.GetProperty("HasApiKey").GetBoolean()));
    }

    [Fact]
    public async Task PostWithoutMasterKeyRejectsBeforeAnyDatabaseCacheOrEventWrite()
    {
        using var test = await CreateTestAsync(withMasterKey: false);
        var before = await SnapshotAsync(test.Db);
        var events = 0;
        test.Manager.OnConfigChanged += (_, _) => events++;

        var result = await InvokeAsync(test, HttpMethods.Post,
            Body(new Dictionary<string, string?> { ["general.base-url"] = "http://example.test" }));

        Assert.Equal(500, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Equal(before, await SnapshotAsync(test.Db));
        Assert.Equal(0, events);
    }

    [Fact]
    public async Task Post_PreservesBlankSecretReplacesAndExplicitlyClearsScalar()
    {
        using var test = await CreateTestAsync();
        await UpsertAsync(test.Db, "webdav.pass", "old-password");
        await test.Db.SaveChangesAsync();
        await test.Manager.LoadConfig();

        var unchanged = await InvokeAsync(test, HttpMethods.Post, Body(new Dictionary<string, string?> { ["webdav.pass"] = "" }));
        var unchangedResponse = Assert.IsType<AdminSettingsResponse>(Assert.IsType<OkObjectResult>(unchanged).Value);
        Assert.True(unchangedResponse.HasSecrets.WebdavPass, JsonSerializer.Serialize(unchangedResponse));

        await InvokeAsync(test, HttpMethods.Post, Body(new Dictionary<string, string?> { ["webdav.pass"] = "replacement" }));
        var row = await test.Db.ConfigItems.AsNoTracking().SingleAsync(x => x.ConfigName == "webdav.pass");
        Assert.True(row.IsEncrypted);
        Assert.DoesNotContain("replacement", row.ConfigValue, StringComparison.Ordinal);
        var storedPasswordHash = test.Encryption.Decrypt("webdav.pass", row.ConfigValue).plaintext;
        Assert.NotEqual("replacement", storedPasswordHash);
        Assert.True(NzbWebDAV.Utils.PasswordUtil.Verify(storedPasswordHash, "replacement"));

        var cleared = await InvokeAsync(test, HttpMethods.Post, Body(
            new Dictionary<string, string?> { ["webdav.pass"] = "" }, ["webdav.pass"]));
        var response = Assert.IsType<AdminSettingsResponse>(Assert.IsType<OkObjectResult>(cleared).Value);
        Assert.False(response.HasSecrets.WebdavPass);
        row = await test.Db.ConfigItems.AsNoTracking().SingleAsync(x => x.ConfigName == "webdav.pass");
        Assert.True(row.IsEncrypted);
        Assert.DoesNotContain("replacement", row.ConfigValue, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://example.com/Api/V1?source=query")]
    [InlineData("http://user:pass@example.com/Api/V1")]
    [InlineData("http://example.com/Api/V1#fragment")]
    public async Task Post_RejectsArrIdentityWithDisallowedUriComponents(string requestedHost)
    {
        using var test = await CreateTestAsync();
        var malformed = Body(new Dictionary<string, string?>
        {
            ["arr.instances"] = $"{{\"RadarrInstances\":[{{\"Host\":\"{requestedHost}\",\"ApiKey\":\"x\"}}],\"SonarrInstances\":[],\"QueueRules\":[]}}"
        });

        var result = await InvokeAsync(test, HttpMethods.Post, malformed);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task SQLite_commit_then_throw_is_reconciled_from_fresh_read_and_published_once()
    {
        using var test = await CreateTestAsync();
        var events = 0;
        test.Manager.OnConfigChanged += (_, _) => events++;
        var fault = new CommitFaultTransaction.FaultState(CommitFaultTransaction.Mode.CommitThenThrow);
        var controller = new AdminSettingsController(
            new DavDatabaseClient(test.Db), test.Manager, test.Encryption,
            (isolation, cancellationToken) => BeginFaultedTransactionAsync(test.Db, isolation, cancellationToken, fault));

        var result = await InvokeAsync(controller, HttpMethods.Post,
            Body(new Dictionary<string, string?> { ["webdav.pass"] = "commit-then-throw" }));

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(200, ok.StatusCode);
        var response = Assert.IsType<AdminSettingsResponse>(ok.Value);
        Assert.True(response.HasSecrets.WebdavPass);

        Assert.Equal(1, events);
        Assert.DoesNotContain("commit-then-throw", JsonSerializer.Serialize(ok.Value));

        var row = await test.Db.ConfigItems.AsNoTracking().SingleAsync(x => x.ConfigName == "webdav.pass");
        var stored = row.IsEncrypted
            ? test.Encryption.Decrypt("webdav.pass", row.ConfigValue).plaintext
            : row.ConfigValue;
        Assert.True(NzbWebDAV.Utils.PasswordUtil.Verify(stored, "commit-then-throw"));
        Assert.True(fault.Disposed);
    }

    [Fact]
    public async Task SQLite_commit_then_cancel_is_reconciled_as_success_not_rolled_back()
    {
        using var test = await CreateTestAsync();
        var events = 0;
        test.Manager.OnConfigChanged += (_, _) => events++;
        var fault = new CommitFaultTransaction.FaultState(CommitFaultTransaction.Mode.CommitThenCancel);
        var controller = new AdminSettingsController(
            new DavDatabaseClient(test.Db), test.Manager, test.Encryption,
            (isolation, cancellationToken) => BeginFaultedTransactionAsync(test.Db, isolation, cancellationToken, fault));

        var result = await InvokeAsync(controller, HttpMethods.Post,
            Body(new Dictionary<string, string?> { ["webdav.pass"] = "commit-then-cancel" }));

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(200, ok.StatusCode);
        Assert.Equal(1, events);

        var row = await test.Db.ConfigItems.AsNoTracking().SingleAsync(x => x.ConfigName == "webdav.pass");
        var stored = row.IsEncrypted
            ? test.Encryption.Decrypt("webdav.pass", row.ConfigValue).plaintext
            : row.ConfigValue;
        Assert.True(NzbWebDAV.Utils.PasswordUtil.Verify(stored, "commit-then-cancel"));
        Assert.True(fault.Disposed);
    }

    [Fact]
    public async Task SQLite_concurrent_blank_then_replace_writes_are_deterministic()
    {
        const string replacement = "replacement-secret";
        using var test = await CreateTestAsync();
        await UpsertAsync(test.Db, "webdav.pass", "legacy-secret");
        await test.Db.SaveChangesAsync();
        await test.Manager.LoadConfig();

        await using var secondaryDb = await _fixture.CreateMigratedContextAsync();
        var replaceController = new AdminSettingsController(
            new DavDatabaseClient(secondaryDb), test.Manager, test.Encryption);
        var preserveController = new AdminSettingsController(
            new DavDatabaseClient(test.Db), test.Manager, test.Encryption);

        var preserveResultTask = InvokeAsync(preserveController, HttpMethods.Post, Body(new Dictionary<string, string?> { ["webdav.pass"] = "" }));
        var replaceResultTask = InvokeAsync(replaceController, HttpMethods.Post, Body(new Dictionary<string, string?> { ["webdav.pass"] = replacement }));

        var first = await preserveResultTask;
        var second = await replaceResultTask;

        Assert.All(new[] { first, second }, result => Assert.IsType<OkObjectResult>(result));

        var row = await test.Db.ConfigItems.AsNoTracking().SingleAsync(x => x.ConfigName == "webdav.pass");
        var stored = row.IsEncrypted
            ? test.Encryption.Decrypt("webdav.pass", row.ConfigValue).plaintext
            : row.ConfigValue;
        Assert.True(NzbWebDAV.Utils.PasswordUtil.Verify(stored, replacement));
    }

    private static async Task<IDbContextTransaction> BeginFaultedTransactionAsync(
        DavDatabaseContext context,
        IsolationLevel isolation,
        CancellationToken cancellationToken,
        CommitFaultTransaction.FaultState fault)
        => new CommitFaultTransaction(
            await context.Database.BeginTransactionAsync(isolation, cancellationToken),
            fault);

    private async Task<TestContext> CreateTestAsync(bool withMasterKey = true)
    {
        await _fixture.ResetAsync();
        var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("DATABASE_URL", null),
            ("FRONTEND_BACKEND_API_KEY", "unit-api-key"),
            ("NZBDAV_MASTER_KEY", withMasterKey ? _fixture.CreateKey() : null),
            ("NZBDAV_MASTER_KEY_OLD", null));
        var encryption = new ConfigEncryptionService();
        var setup = await _fixture.CreateMigratedContextAsync();
        await setup.DisposeAsync();
        var manager = new ConfigManager(encryption);
        await manager.LoadConfig();
        var db = await _fixture.CreateMigratedContextAsync();
        return new TestContext(environment, encryption, manager, db);
    }

    private static async Task UpsertAsync(DavDatabaseContext db, string name, string value)
    {
        var row = await db.ConfigItems.SingleOrDefaultAsync(x => x.ConfigName == name);
        if (row is null)
            db.ConfigItems.Add(new ConfigItem { ConfigName = name, ConfigValue = value, IsEncrypted = false });
        else
        {
            row.ConfigValue = value;
            row.IsEncrypted = false;
        }
    }

    private static async Task<IActionResult> InvokeAsync(AdminSettingsController controller, string method, string? body = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Headers["x-api-key"] = "unit-api-key";
        if (body is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Request.Body = new MemoryStream(bytes);
            context.Request.ContentLength = bytes.Length;
            context.Request.ContentType = "application/json";
        }
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        return await controller.HandleApiRequest();
    }

    private static async Task<IActionResult> InvokeAsync(TestContext test, string method, string? body = null)
    {
        // The production request scope starts with no tracked setup rows;
        // clear test seeding entities before exercising the controller path.
        test.Db.ChangeTracker.Clear();
        return await InvokeAsync(new AdminSettingsController(
            new DavDatabaseClient(test.Db), test.Manager, test.Encryption), method, body);
    }

    private static string Body(Dictionary<string, string?> config, params string[] clearSecrets)
        => JsonSerializer.Serialize(new { config, clearSecrets });

    private static async Task<Dictionary<string, (string Value, bool Encrypted)>> SnapshotAsync(DavDatabaseContext db)
        => await db.ConfigItems.AsNoTracking().ToDictionaryAsync(x => x.ConfigName, x => (x.ConfigValue, x.IsEncrypted));

    private sealed class CommitFaultTransaction(IDbContextTransaction inner, CommitFaultTransaction.FaultState fault) : IDbContextTransaction
    {
        internal enum Mode { CommitThenThrow, CommitThenCancel }

        internal sealed class FaultState(Mode mode)
        {
            internal Mode Mode { get; } = mode;
            internal bool Disposed { get; private set; }
            internal bool RolledBack { get; private set; }
            internal void MarkDisposed() => Disposed = true;
            internal void MarkRolledBack() => RolledBack = true;
        }

        public Guid TransactionId => inner.TransactionId;
        public bool SupportsSavepoints => inner.SupportsSavepoints;

        public void Commit()
            => CommitAsync(CancellationToken.None).GetAwaiter().GetResult();

        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            await inner.CommitAsync(cancellationToken).ConfigureAwait(false);
            if (fault.Mode == Mode.CommitThenThrow)
                throw new IOException("commit acknowledgement was lost");
            if (fault.Mode == Mode.CommitThenCancel)
                throw new OperationCanceledException("commit acknowledgement was cancelled");
        }

        public void Rollback()
            => RollbackAsync(CancellationToken.None).GetAwaiter().GetResult();

        public async Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            fault.MarkRolledBack();
            await inner.RollbackAsync(cancellationToken).ConfigureAwait(false);
        }

        public void CreateSavepoint(string name)
            => inner.CreateSavepoint(name);

        public Task CreateSavepointAsync(string name, CancellationToken cancellationToken = default)
            => inner.CreateSavepointAsync(name, cancellationToken);

        public void RollbackToSavepoint(string name)
            => inner.RollbackToSavepoint(name);

        public Task RollbackToSavepointAsync(string name, CancellationToken cancellationToken = default)
            => inner.RollbackToSavepointAsync(name, cancellationToken);

        public void ReleaseSavepoint(string name)
            => inner.ReleaseSavepoint(name);

        public Task ReleaseSavepointAsync(string name, CancellationToken cancellationToken = default)
            => inner.ReleaseSavepointAsync(name, cancellationToken);

        public ValueTask DisposeAsync()
        {
            fault.MarkDisposed();
            return inner.DisposeAsync();
        }

        public void Dispose()
        {
            fault.MarkDisposed();
            inner.Dispose();
        }
    }

    private sealed class TestContext(
        backend.Tests.Config.TemporaryEnvironment environment,
        ConfigEncryptionService encryption,
        ConfigManager manager,
        DavDatabaseContext db) : IDisposable
    {
        public backend.Tests.Config.TemporaryEnvironment Environment { get; } = environment;
        public ConfigEncryptionService Encryption { get; } = encryption;
        public ConfigManager Manager { get; } = manager;
        public DavDatabaseContext Db { get; } = db;

        public void Dispose()
        {
            Db.Dispose();
            Encryption.Dispose();
            Environment.Dispose();
        }
    }
}
