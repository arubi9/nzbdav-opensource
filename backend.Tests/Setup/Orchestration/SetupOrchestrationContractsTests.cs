using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using NzbWebDAV.Setup.Orchestration;

namespace NzbWebDAV.Tests.Setup.Orchestration;

public sealed class SetupOrchestrationContractsTests
{
    [Fact]
    public async Task ParseAsync_ValidPayload_PersistsBoundedValues()
    {
        var payload = new
        {
            providers = new[]
            {
                new { type = "Pooled", host = "provider.local", port = 563, useSsl = true, user = "user", pass = "secret", maxConnections = 5 }
            },
            indexers = new[]
            {
                new { name = "nzb", baseUrl = "https://indexer.example/api", apiKey = "abc", appProfileId = (int?)null },
            }
        };

        var context = CreateRequestContext(payload);
        var result = await SetupConfigurationData.ParseAsync(context, CancellationToken.None);

        Assert.Single(result.Providers.Providers);
        Assert.Single(result.Indexers);
        Assert.Contains("\"nzb\"", result.IndexerJson);
    }

    [Fact]
    public async Task ParseAsync_AcceptsNumericPooledProviderTypeFromTheBrowserContract()
    {
        var payload = new
        {
            providers = new[]
            {
                new { type = "1", host = "provider.local", port = 563, useSsl = true, user = "user", pass = "secret", maxConnections = 1 }
            },
            indexers = new[]
            {
                new { name = "nzb", baseUrl = "https://indexer.example/api", apiKey = "abc", appProfileId = (int?)null },
            }
        };

        var result = await SetupConfigurationData.ParseAsync(CreateRequestContext(payload), CancellationToken.None);

        Assert.Single(result.Providers.Providers);
    }

    [Fact]
    public async Task ParseAsync_RejectsIndexerUrlWithCredentialsWithoutLeakingSecrets()
    {
        var payload = new
        {
            providers = new[]
            {
                new { type = "Pooled", host = "provider.local", port = 563, useSsl = true, user = "user", pass = "pass", maxConnections = 1 }
            },
            indexers = new[]
            {
                new { name = "nzb", baseUrl = "https://token:supersecret@indexer.example/api", apiKey = "abc", appProfileId = (int?)null }
            }
        };

        var context = CreateRequestContext(payload);

        var exception = await Assert.ThrowsAsync<BadHttpRequestException>(() => SetupConfigurationData.ParseAsync(context, CancellationToken.None));
        Assert.DoesNotContain("supersecret", exception.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task ParseAsync_RejectsProviderCountBounds(int count)
    {
        var providers = Enumerable.Range(0, count).Select(i =>
            new { type = "Pooled", host = $"provider{i}.local", port = 563, useSsl = true, user = "user", pass = "pass", maxConnections = 1 });
        var payload = new
        {
            providers,
            indexers = new[]
            {
                new { name = "nzb", baseUrl = "https://indexer.example/api", apiKey = "abc", appProfileId = (int?)null },
            }
        };

        var context = CreateRequestContext(payload);
        await Assert.ThrowsAsync<BadHttpRequestException>(() => SetupConfigurationData.ParseAsync(context, CancellationToken.None));
    }

    private static DefaultHttpContext CreateRequestContext(object payload)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        var requestContext = new DefaultHttpContext();
        requestContext.Request.Body = new MemoryStream(bytes);
        requestContext.Request.ContentLength = bytes.Length;
        requestContext.Request.ContentType = "application/json";
        return requestContext;
    }
}
