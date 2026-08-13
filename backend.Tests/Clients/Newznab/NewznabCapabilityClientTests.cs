using System.Net;
using System.Text;
using NzbWebDAV.Clients.Newznab;

namespace NzbWebDAV.Tests.Clients.Newznab;

public sealed class NewznabCapabilityClientTests
{
    [Fact]
    public async Task Injected_handler_is_not_a_transport_bypass_for_public_endpoints()
    {
        var handler = new RecordingHandler("<caps><server version=\"1\" /></caps>");
        using var httpClient = new HttpClient(handler);
        var client = new NewznabCapabilityClient(httpClient, TimeSpan.FromSeconds(1),
            hostResolver: static (_, _) => Task.FromResult(new[] { IPAddress.Parse("192.168.1.10") }));
        var credential = new NewznabIndexerCredential(
            "Example Indexer", "https://indexer.example/newznab/api?client=desktop", "key with&symbols");

        var result = await client.CheckAsync(credential);

        // The production client pins its own socket connection; an injected
        // HttpClient handler is not a transport bypass. This public fixture is
        // intentionally resolved to a private answer and rejected before any
        // request can be sent.
        Assert.Equal(NewznabCapabilityStatus.UnsafeNetworkAddress, result.Status);
        Assert.Null(handler.RequestUri);
    }

    [Theory]
    [InlineData("http://127.0.0.1/api")]
    [InlineData("http://10.0.0.1/api")]
    [InlineData("http://[::1]/api")]
    [InlineData("http://[fe80::1]/api")]
    [InlineData("http://169.254.169.254/api")]
    [InlineData("http://2130706433/api")]
    [InlineData("http://0x7f000001/api")]
    [InlineData("http://0177.0.0.1/api")]
    public async Task Unsafe_literal_endpoints_are_rejected_before_transport(string url)
    {
        var handler = new RecordingHandler("<caps />");
        using var httpClient = new HttpClient(handler);
        var exception = Assert.Throws<ArgumentException>(() =>
            new NewznabIndexerCredential("Indexer", url, "secret"));
        Assert.DoesNotContain("secret", exception.Message);
        Assert.Null(handler.RequestUri);
    }

    [Fact]
    public async Task Dns_private_result_and_rebinding_are_rejected_without_global_dns_state()
    {
        var handler = new RecordingHandler("<caps />");
        using var httpClient = new HttpClient(handler);
        var calls = 0;
        var client = new NewznabCapabilityClient(
            httpClient,
            TimeSpan.FromSeconds(1),
            hostResolver: (_, _) =>
            {
                calls++;
                return Task.FromResult(new[] { IPAddress.Parse("1.1.1.1"), IPAddress.Parse("192.168.1.10") });
            });

        var result = await client.CheckAsync(new NewznabIndexerCredential("Indexer", "https://indexer.example/api", "secret"));

        // The entire validated answer set is considered, rather than trusting
        // a first public answer that can be rebound before the request.
        Assert.Equal(NewznabCapabilityStatus.UnsafeNetworkAddress, result.Status);
        Assert.Equal(1, calls);

        var privateResult = await new NewznabCapabilityClient(
                httpClient,
                TimeSpan.FromSeconds(1),
                hostResolver: (_, _) => Task.FromResult(new[] { IPAddress.Parse("192.168.1.10") }))
            .CheckAsync(new NewznabIndexerCredential("Indexer", "https://indexer.example/api", "secret"));
        Assert.Equal(NewznabCapabilityStatus.UnsafeNetworkAddress, privateResult.Status);

        var optedIn = await new NewznabCapabilityClient(
                httpClient,
                TimeSpan.FromSeconds(1),
                hostResolver: (_, _) => Task.FromResult(new[] { IPAddress.Parse("192.168.1.10") }))
            .CheckAsync(new NewznabIndexerCredential("Indexer", "https://indexer.example/api", "secret", true));
        Assert.NotEqual(NewznabCapabilityStatus.Valid, optedIn.Status);
        Assert.Contains(optedIn.Status, new[] { NewznabCapabilityStatus.Unreachable, NewznabCapabilityStatus.TimedOut });
    }

    [Fact]
    public async Task Public_endpoint_policy_rejects_before_credentials_or_response_processing()
    {
        const string url = "https://indexer.example/api?client=desktop";
        const string apiKey = "api-key-that-must-not-leak";
        var client = CreateClient("<caps><error code=\"100\" description=\"Incorrect user credentials\" /></caps>");

        var result = await client.CheckAsync(new NewznabIndexerCredential("Private Indexer", url, apiKey));

        Assert.Equal(NewznabCapabilityStatus.UnsafeNetworkAddress, result.Status);
        AssertSafeOutput(result, apiKey, url);
    }

    [Fact]
    public async Task Top_level_response_fixture_is_not_reached_before_endpoint_policy()
    {
        var result = await CreateClient("<error code=\"100\" description=\"Incorrect user credentials\" />").CheckAsync(
            new NewznabIndexerCredential("Indexer", "https://indexer.example/api", "secret"));

        Assert.Equal(NewznabCapabilityStatus.UnsafeNetworkAddress, result.Status);
    }

    [Fact]
    public async Task Injected_unauthorized_handler_is_not_a_transport_bypass()
    {
        var client = new NewznabCapabilityClient(new HttpClient(new RecordingHandler("", HttpStatusCode.Unauthorized)),
            TimeSpan.FromSeconds(1), hostResolver: static (_, _) => Task.FromResult(new[] { IPAddress.Parse("192.168.1.10") }));

        var result = await client.CheckAsync(
            new NewznabIndexerCredential("Indexer", "https://indexer.example/api", "secret"));

        Assert.Equal(NewznabCapabilityStatus.UnsafeNetworkAddress, result.Status);
    }

    [Theory]
    [InlineData("not xml")]
    [InlineData("<caps><server></caps>")]
    public async Task Malformed_capability_fixtures_are_not_reached_before_endpoint_policy(string body)
    {
        const string apiKey = "secret-key";
        var result = await CreateClient(body).CheckAsync(
            new NewznabIndexerCredential("Indexer", "https://indexer.example/api", apiKey));

        Assert.Equal(NewznabCapabilityStatus.UnsafeNetworkAddress, result.Status);
        AssertSafeOutput(result, apiKey, "https://indexer.example/api");
    }

    [Fact]
    public async Task Dtd_and_oversized_xml_fixtures_are_not_reached_before_endpoint_policy()
    {
        var dtd = "<!DOCTYPE caps [<!ENTITY secret SYSTEM 'file:///definitely-not-read'>]><caps>&secret;</caps>";
        var dtdResult = await CreateClient(dtd, maxResponseBytes: 4096).CheckAsync(
            new NewznabIndexerCredential("Indexer", "https://indexer.example/api", "secret"));
        var oversizedResult = await CreateClient("<caps>" + new string('x', 5000) + "</caps>", maxResponseBytes: 1024)
            .CheckAsync(new NewznabIndexerCredential("Indexer", "https://indexer.example/api", "secret"));

        Assert.Equal(NewznabCapabilityStatus.UnsafeNetworkAddress, dtdResult.Status);
        Assert.Equal(NewznabCapabilityStatus.UnsafeNetworkAddress, oversizedResult.Status);
    }

    [Fact]
    public async Task Unreachable_indexer_is_a_safe_typed_failure()
    {
        var client = new NewznabCapabilityClient(new HttpClient(new ThrowingHandler()), TimeSpan.FromSeconds(1),
            hostResolver: static (_, _) => Task.FromResult(new[] { IPAddress.Parse("192.168.1.10") }));
        var result = await client.CheckAsync(
            new NewznabIndexerCredential("Offline Indexer", "https://offline.example/api", "offline-secret"));

        Assert.Equal(NewznabCapabilityStatus.UnsafeNetworkAddress, result.Status);
        AssertSafeOutput(result, "offline-secret", "https://offline.example/api");
    }

    [Fact]
    public async Task Request_timeout_fixture_is_not_reached_before_endpoint_policy()
    {
        using var httpClient = new HttpClient(new DelayingHandler());
        var client = new NewznabCapabilityClient(httpClient, TimeSpan.FromMilliseconds(20),
            hostResolver: static (_, _) => Task.FromResult(new[] { IPAddress.Parse("192.168.1.10") }));

        var result = await client.CheckAsync(
            new NewznabIndexerCredential("Slow Indexer", "https://slow.example/api", "slow-secret"));

        Assert.Equal(NewznabCapabilityStatus.UnsafeNetworkAddress, result.Status);
    }

    [Fact]
    public async Task Caller_cancellation_is_honored()
    {
        using var httpClient = new HttpClient(new DelayingHandler());
        var client = new NewznabCapabilityClient(httpClient, TimeSpan.FromSeconds(1));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CheckAsync(
            new NewznabIndexerCredential("Cancelled Indexer", "https://cancelled.example/api", "secret"),
            cancellation.Token));
    }

    [Fact]
    public async Task Batch_check_returns_only_operator_display_names_and_safe_statuses()
    {
        var handler = new QueueHandler("<caps />", "<caps><error code=\"100\" /></caps>");
        using var httpClient = new HttpClient(handler);
        var client = new NewznabCapabilityClient(httpClient, TimeSpan.FromSeconds(1));
        var credentials = new[]
        {
            new NewznabIndexerCredential("Working", "https://working.example/api", "working-secret"),
            new NewznabIndexerCredential("Rejected", "https://rejected.example/api", "rejected-secret")
        };

        var results = await client.CheckAsync(credentials);

        Assert.Collection(results,
            result => Assert.Equal(("Working", NewznabCapabilityStatus.UnsafeNetworkAddress), (result.DisplayName, result.Status)),
            result => Assert.Equal(("Rejected", NewznabCapabilityStatus.UnsafeNetworkAddress), (result.DisplayName, result.Status)));
        var output = string.Join('|', results.Select(result => result.ToString()));
        Assert.DoesNotContain("working-secret", output);
        Assert.DoesNotContain("rejected-secret", output);
        Assert.DoesNotContain("https://", output);
    }

    private static NewznabCapabilityClient CreateClient(string body, int maxResponseBytes = 256 * 1024) =>
        new(new HttpClient(new RecordingHandler(body)), TimeSpan.FromSeconds(1), maxResponseBytes,
            hostResolver: static (_, _) => Task.FromResult(new[] { IPAddress.Parse("192.168.1.10") }));

    private static void AssertSafeOutput(NewznabCapabilityResult result, string apiKey, string url)
    {
        var output = result.ToString();
        Assert.DoesNotContain(apiKey, output);
        Assert.DoesNotContain(url, output);
        Assert.DoesNotContain("password", output, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class RecordingHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public Dictionary<string, string> RequestHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            foreach (var header in request.Headers)
                RequestHeaders[header.Key] = string.Join(" ", header.Value);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/xml")
            });
        }
    }

    private sealed class QueueHandler(params string[] bodies) : HttpMessageHandler
    {
        private int _position;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(bodies[_position++], Encoding.UTF8, "application/xml")
            });
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("This message must never reach the result.");
    }

    private sealed class DelayingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
