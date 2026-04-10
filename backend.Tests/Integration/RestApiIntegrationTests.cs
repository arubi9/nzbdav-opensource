using System.Net;
using System.Net.Http.Headers;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;

namespace backend.Tests.Integration;

[Collection(nameof(RestApiIntegrationCollection))]
public class RestApiIntegrationTests : IClassFixture<RestApiFactoryFixture>
{
    private readonly HttpClient _client;

    public RestApiIntegrationTests(RestApiFactoryFixture fixture)
    {
        _client = fixture.Factory.CreateClient();
    }

    [Fact]
    public async Task Health_ReturnsOk()
    {
        var response = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Metrics_ReturnsPrometheusFormat()
    {
        var response = await _client.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("nzbdav_cache_bytes", content);
        Assert.Contains("nzbdav_streams_active", content);
    }

    [Fact]
    public async Task Browse_WithoutApiKey_Returns401()
    {
        var response = await _client.GetAsync("/api/browse");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Meta_WithoutApiKey_Returns401()
    {
        var response = await _client.GetAsync($"/api/meta/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Stream_WithoutApiKey_Returns401()
    {
        var response = await _client.GetAsync($"/api/stream/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task EncryptionStatus_WithApiKey_ReturnsStatusPayload()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/encryption-status");
        request.Headers.Add("x-api-key", "integration-test-api-key");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"keySet\":true", content);
        Assert.Contains("\"bannerSeverity\":\"none\"", content);
    }
}

public sealed class RestApiFactoryFixture : IAsyncLifetime
{
    private readonly string _configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", "rest-api-integration");
    private readonly string? _previousConfigPath = Environment.GetEnvironmentVariable("CONFIG_PATH");
    private readonly string? _previousApiKey = Environment.GetEnvironmentVariable("FRONTEND_BACKEND_API_KEY");
    private readonly string? _previousMasterKey = Environment.GetEnvironmentVariable("NZBDAV_MASTER_KEY");

    public WebApplicationFactory<NzbWebDAV.Program> Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_configPath);
        ResetDatabaseFiles();
        Environment.SetEnvironmentVariable("CONFIG_PATH", _configPath);
        Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", "integration-test-api-key");
        Environment.SetEnvironmentVariable("NZBDAV_MASTER_KEY", Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));

        await using (var dbContext = new DavDatabaseContext())
        {
            await dbContext.Database.MigrateAsync();
            var apiKeyRow = await dbContext.ConfigItems.SingleAsync(x => x.ConfigName == "api.key");
            apiKeyRow.ConfigValue = "integration-test-api-key";
            apiKeyRow.IsEncrypted = false;
            await dbContext.SaveChangesAsync();
        }

        Factory = new WebApplicationFactory<NzbWebDAV.Program>();
    }

    public async Task DisposeAsync()
    {
        Factory.Dispose();
        Environment.SetEnvironmentVariable("CONFIG_PATH", _previousConfigPath);
        Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", _previousApiKey);
        Environment.SetEnvironmentVariable("NZBDAV_MASTER_KEY", _previousMasterKey);
        await Task.Yield();

        try
        {
            if (Directory.Exists(_configPath))
                Directory.Delete(_configPath, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private void ResetDatabaseFiles()
    {
        SqliteConnection.ClearAllPools();
        DeleteIfExists(Path.Combine(_configPath, "db.sqlite"));
        DeleteIfExists(Path.Combine(_configPath, "db.sqlite-wal"));
        DeleteIfExists(Path.Combine(_configPath, "db.sqlite-shm"));
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}

[CollectionDefinition(nameof(RestApiIntegrationCollection), DisableParallelization = true)]
public sealed class RestApiIntegrationCollection : ICollectionFixture<RestApiFactoryFixture>
{
}
