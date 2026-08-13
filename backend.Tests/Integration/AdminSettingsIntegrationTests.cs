using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using NzbWebDAV;

namespace backend.Tests.Integration;

[Collection(nameof(RestApiIntegrationCollection))]
public sealed class AdminSettingsIntegrationTests(RestApiFactoryFixture fixture)
{
    private HttpClient Client => fixture.Factory.CreateClient();

    [Fact]
    public async Task RoutedAdminSettingsRequiresAuthAndUsesJsonFormatterWithoutMarker()
    {
        using var client = Client;
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/admin-settings")).StatusCode);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin-settings")
        {
            Content = JsonContent.Create(new
            {
                config = new Dictionary<string, string?> { ["webdav.pass"] = "admin-marker-5f3c" },
                clearSecrets = Array.Empty<string>()
            })
        };
        request.Headers.Add("x-api-key", "integration-test-api-key");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Content.Headers.ContentType);
        Assert.Equal("application/json", response.Content.Headers.ContentType.MediaType);
        Assert.DoesNotContain("admin-marker-5f3c", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RoutedAdminSettingsRejectsInvalidApiKey()
    {
        using var client = Client;
        using var missing = new HttpRequestMessage(HttpMethod.Get, "/api/admin-settings");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(missing)).StatusCode);

        using var invalid = new HttpRequestMessage(HttpMethod.Get, "/api/admin-settings");
        invalid.Headers.Add("x-api-key", "invalid");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(invalid)).StatusCode);
    }

    [Theory]
    [InlineData("{\"config\":{},\"clearSecrets\":[],\"extra\":true}")]
    [InlineData("{\"config\":{\"general.base-url\":\"x\",\"general.base-url\":\"y\"},\"clearSecrets\":[]}")]
    public async Task RoutedAdminSettingsRejectsUnmappedAndDuplicateJson(string body)
    {
        using var client = Client;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin-settings")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-api-key", "integration-test-api-key");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(request)).StatusCode);
    }

    [Theory]
    [InlineData("{\"config\":{\"api.strm-key\":\"blocked\"},\"clearSecrets\":[]}")]
    [InlineData("{\"config\":{},\"clearSecrets\":[\"api.strm-key\"]}")]
    public async Task RoutedAdminSettingsRejectsStreamKeyMutations(string body)
    {
        using var client = Client;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin-settings")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-api-key", "integration-test-api-key");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task RoutedAdminSettingsRejectsDuplicateArrPropertiesAndBounds()
    {
        using var client = Client;
        var duplicateNested = new HttpRequestMessage(HttpMethod.Post, "/api/admin-settings")
        {
            Content = new StringContent(
                "{\"config\":{\"arr.instances\":\"{\\\"RadarrInstances\\\":[{\\\"Host\\\":\\\"http://example.com/Api/V1\\\",\\\"ApiKey\\\":\\\"x\\\",\\\"ApiKey\\\":\\\"y\\\"}],\\\"SonarrInstances\\\":[],\\\"QueueRules\\\":[]}\"},\"clearSecrets\":[]}",
                Encoding.UTF8, "application/json"),
        };
        duplicateNested.Headers.Add("x-api-key", "integration-test-api-key");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(duplicateNested)).StatusCode);

        var tooManyItems = new Dictionary<string, string?>();
        for (var i = 0; i < 129; i++)
            tooManyItems[$"general.base-url-{i}"] = "value";
        var body = JsonContent.Create(new { config = tooManyItems, clearSecrets = Array.Empty<string>() });
        using var oversizedBody = new HttpRequestMessage(HttpMethod.Post, "/api/admin-settings") { Content = body };
        oversizedBody.Headers.Add("x-api-key", "integration-test-api-key");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(oversizedBody)).StatusCode);
    }

    [Fact]
    public async Task RoutedAdminSettingsRejectsWrongContentTypeAndOversizedPayload()
    {
        using var client = Client;
        using var wrongType = new HttpRequestMessage(HttpMethod.Post, "/api/admin-settings")
        {
            Content = new StringContent("{}", Encoding.UTF8, "text/plain")
        };
        wrongType.Headers.Add("x-api-key", "integration-test-api-key");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(wrongType)).StatusCode);

        using var jsonp = new HttpRequestMessage(HttpMethod.Post, "/api/admin-settings")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/jsonp")
        };
        jsonp.Headers.Add("x-api-key", "integration-test-api-key");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(jsonp)).StatusCode);

        using var jsonPatch = new HttpRequestMessage(HttpMethod.Post, "/api/admin-settings")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json-patch+json")
        };
        jsonPatch.Headers.Add("x-api-key", "integration-test-api-key");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(jsonPatch)).StatusCode);

        using var oversized = new HttpRequestMessage(HttpMethod.Post, "/api/admin-settings")
        {
            Content = new StringContent(new string('x', 8 * 1024 * 1024 + 1), Encoding.UTF8, "application/json")
        };
        oversized.Headers.Add("x-api-key", "integration-test-api-key");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(oversized)).StatusCode);
    }
}
