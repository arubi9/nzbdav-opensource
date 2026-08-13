using System.Net;
using System.Net.Sockets;
using System.Text;
using NzbWebDAV.Clients;
using NzbWebDAV.Clients.ArrSetup;
using NzbWebDAV.Clients.JellyfinSetup;
using NzbWebDAV.Clients.ProwlarrSetup;

namespace NzbWebDAV.Tests.Clients;

public sealed class SetupHttpRedirectBoundaryTests
{
    [Fact]
    public void Repeated_setup_clients_share_one_long_lived_redirect_disabled_handler()
    {
        var before = SetupHttpClientFactory.HandlerCreationCountForTests;
        var clients = Enumerable.Range(0, 100)
            .Select(_ => SetupHttpClientFactory.Create())
            .ToArray();
        try
        {
            Assert.Equal(before, SetupHttpClientFactory.HandlerCreationCountForTests);
            Assert.False(SetupHttpClientFactory.SharedHandlerForTests.AllowAutoRedirect);
        }
        finally
        {
            foreach (var client in clients) client.Dispose();
        }
    }

    [Theory]
    [InlineData("GET", HttpStatusCode.MovedPermanently)]
    [InlineData("GET", HttpStatusCode.Found)]
    [InlineData("GET", HttpStatusCode.TemporaryRedirect)]
    [InlineData("GET", HttpStatusCode.PermanentRedirect)]
    [InlineData("POST", HttpStatusCode.MovedPermanently)]
    [InlineData("POST", HttpStatusCode.Found)]
    [InlineData("POST", HttpStatusCode.TemporaryRedirect)]
    [InlineData("POST", HttpStatusCode.PermanentRedirect)]
    [InlineData("PUT", HttpStatusCode.MovedPermanently)]
    [InlineData("PUT", HttpStatusCode.Found)]
    [InlineData("PUT", HttpStatusCode.TemporaryRedirect)]
    [InlineData("PUT", HttpStatusCode.PermanentRedirect)]
    public async Task Shared_setup_transport_does_not_replay_credentials_for_get_post_or_put_redirects(string method, HttpStatusCode status)
    {
        await AssertNoRedirectFollowAsync(status, async upstream =>
        {
            using var client = SetupHttpClientFactory.Create();
            using var request = new HttpRequestMessage(new HttpMethod(method), upstream.Url + "/credential-bearing");
            request.Headers.TryAddWithoutValidation("X-Api-Key", "setup-secret");
            if (method is "POST" or "PUT") request.Content = new StringContent("body-secret", Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request);
            Assert.Equal(status, response.StatusCode);
            Assert.Single(upstream.Requests);
            Assert.Contains("X-Api-Key", upstream.Requests.Single().Headers.Keys, StringComparer.OrdinalIgnoreCase);
        });
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task Arr_default_client_does_not_follow_credential_bearing_get_redirects(HttpStatusCode status)
    {
        await AssertNoRedirectFollowAsync(status, async upstream =>
        {
            var client = new ArrSetupClient(TimeSpan.FromSeconds(2));
            await Assert.ThrowsAsync<ArrSetupHttpException>(() => client.SetupAsync(new ArrSetupOptions(
                upstream.Url, "sonarr-secret", upstream.Url, "radarr-secret", "http://nzbdav:8080", "nzbdav-secret")));
            Assert.Contains(upstream.Requests, request => request.Method == "GET");
            Assert.Contains("X-Api-Key", upstream.Requests.Single().Headers.Keys, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("sonarr-secret", upstream.Requests.Single().Body, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task Jellyfin_default_client_does_not_follow_credential_bearing_post_redirects(HttpStatusCode status)
    {
        await AssertNoRedirectFollowAsync(status, async upstream =>
        {
            var client = new JellyfinSetupClient(new JellyfinSetupOptions(upstream.Url, "administrator", "jellyfin-password"));
            var error = await Assert.ThrowsAsync<JellyfinSetupException>(() => client.AuthenticateByNameAsync());
            Assert.Equal(JellyfinSetupFailure.UpstreamFailure, error.Failure);
            var request = upstream.Requests.Single();
            Assert.Equal("POST", request.Method);
            Assert.Contains("jellyfin-password", request.Body, StringComparison.Ordinal);
            Assert.Contains("X-Emby-Authorization", request.Headers.Keys, StringComparer.OrdinalIgnoreCase);
        });
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task Prowlarr_default_client_does_not_follow_credential_bearing_get_redirects(HttpStatusCode status)
    {
        await AssertNoRedirectFollowAsync(status, async upstream =>
        {
            using var client = new ProwlarrSetupClient(upstream.Url, "prowlarr-secret", TimeSpan.FromSeconds(2));
            await Assert.ThrowsAsync<ProwlarrSetupTransportException>(() => client.SetupAsync(new ProwlarrSetupOptions(
                "http://sonarr", "sonarr-secret", "http://radarr", "radarr-secret", [])));
            Assert.Contains(upstream.Requests, request => request.Method == "GET");
            Assert.Contains("X-Api-Key", upstream.Requests.Single().Headers.Keys, StringComparer.OrdinalIgnoreCase);
        });
    }

    private static async Task AssertNoRedirectFollowAsync(
        HttpStatusCode status,
        Func<LoopbackHttpServer, Task> exercise)
    {
        await using var target = await LoopbackHttpServer.StartAsync(_ => Response(HttpStatusCode.OK, "target-received"));
        await using var upstream = await LoopbackHttpServer.StartAsync(_ => Response(status,
            string.Empty,
            $"http://127.0.0.1:{target.Port}/credential-target"));

        await exercise(upstream);
        await Task.Delay(100);
        Assert.Empty(target.Requests);
    }

    private static string Response(HttpStatusCode status, string body, string? location = null)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var builder = new StringBuilder()
            .Append("HTTP/1.1 ").Append((int)status).Append(" ").Append(status).Append("\r\n")
            .Append("Connection: close\r\n")
            .Append("Content-Length: ").Append(bytes.Length).Append("\r\n");
        if (location is not null) builder.Append("Location: ").Append(location).Append("\r\n");
        return builder.Append("\r\n").Append(body).ToString();
    }

    private sealed class LoopbackHttpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Func<CapturedRequest, string> _response;
        private readonly CancellationTokenSource _stopping = new();
        private readonly Task _acceptLoop;
        private readonly List<CapturedRequest> _requests = [];
        private readonly object _sync = new();

        private LoopbackHttpServer(Func<CapturedRequest, string> response)
        {
            _response = response;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptLoop = AcceptLoopAsync();
        }

        public int Port { get; }
        public string Url => $"http://127.0.0.1:{Port}";
        public IReadOnlyList<CapturedRequest> Requests
        {
            get { lock (_sync) return _requests.ToArray(); }
        }

        public static Task<LoopbackHttpServer> StartAsync(Func<CapturedRequest, string> response)
            => Task.FromResult(new LoopbackHttpServer(response));

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stopping.Token).ConfigureAwait(false);
                    _ = HandleAsync(client);
                }
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (_stopping.IsCancellationRequested) { }
            catch (SocketException) when (_stopping.IsCancellationRequested) { }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                await using var stream = client.GetStream();
                await HandleRequestAsync(stream).ConfigureAwait(false);
            }
        }

        private async Task HandleRequestAsync(NetworkStream stream)
        {
            var bytes = new List<byte>();
            var buffer = new byte[4096];
            var headerEnd = -1;
            while (headerEnd < 0)
            {
                var read = await stream.ReadAsync(buffer, _stopping.Token).ConfigureAwait(false);
                if (read == 0) return;
                bytes.AddRange(buffer.AsSpan(0, read).ToArray());
                headerEnd = FindHeaderEnd(bytes);
                if (bytes.Count > 64 * 1024) return;
            }

            var headerBytes = bytes.Take(headerEnd).ToArray();
            var headers = Encoding.ASCII.GetString(headerBytes).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            var requestLine = headers[0].Split(' ', 3);
            var values = headers.Skip(1)
                .Select(line => line.Split(':', 2))
                .Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);
            var contentLength = values.TryGetValue("Content-Length", out var length) && int.TryParse(length, out var parsedLength) ? parsedLength : 0;
            var bodyOffset = headerEnd + 4;
            while (bytes.Count < bodyOffset + contentLength)
            {
                var read = await stream.ReadAsync(buffer, _stopping.Token).ConfigureAwait(false);
                if (read == 0) break;
                bytes.AddRange(buffer.AsSpan(0, read).ToArray());
            }
            var bodyLength = Math.Min(contentLength, Math.Max(0, bytes.Count - bodyOffset));
            var captured = new CapturedRequest(requestLine[0], requestLine.Length > 1 ? requestLine[1] : "", values,
                Encoding.UTF8.GetString(bytes.Skip(bodyOffset).Take(bodyLength).ToArray()));
            lock (_sync) _requests.Add(captured);

            var response = Encoding.UTF8.GetBytes(_response(captured));
            await stream.WriteAsync(response, _stopping.Token).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            _stopping.Cancel();
            _listener.Stop();
            try { await _acceptLoop.ConfigureAwait(false); } catch (OperationCanceledException) { }
            _stopping.Dispose();
        }

        private static int FindHeaderEnd(List<byte> bytes)
        {
            for (var i = 3; i < bytes.Count; i++)
                if (bytes[i - 3] == '\r' && bytes[i - 2] == '\n' && bytes[i - 1] == '\r' && bytes[i] == '\n')
                    return i - 3;
            return -1;
        }
    }

    private sealed record CapturedRequest(
        string Method,
        string Path,
        IReadOnlyDictionary<string, string> Headers,
        string Body);
}
