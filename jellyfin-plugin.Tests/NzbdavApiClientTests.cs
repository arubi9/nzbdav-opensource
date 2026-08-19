using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Nzbdav.Api;
using Jellyfin.Plugin.Nzbdav.Configuration;
using Xunit;

namespace Jellyfin.Plugin.Nzbdav.Tests;

public sealed class NzbdavApiClientTests
{
    [Fact]
    public async Task GetMetaAsync_UsesHeaderApiKey_AndReadsExpiringToken()
    {
        var id = Guid.NewGuid();
        var token = "4102444800." + new string('A', 43);
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal($"/api/meta/{id}", request.RequestUri?.AbsolutePath);
            Assert.Equal("header-only-secret", request.Headers.GetValues("X-Api-Key").Single());
            Assert.DoesNotContain("apikey=", request.RequestUri?.Query ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            return TestHttpMessageHandler.Json("{\"id\":\"" + id + "\",\"streamToken\":\"" + token + "\"}");
        });
        var client = new NzbdavApiClient(new PluginConfiguration
        {
            NzbdavBaseUrl = "https://nzbdav.example/",
            ApiKey = "header-only-secret"
        }, handler);

        var meta = await client.GetMetaAsync(id, CancellationToken.None);

        Assert.Equal(token, meta?.StreamToken);
    }

    [Fact]
    public async Task GetProbeDataAsync_MissingProbeIsRetryableFailure()
    {
        var handler = new TestHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = new NzbdavApiClient(new PluginConfiguration
        {
            NzbdavBaseUrl = "https://nzbdav.example",
            ApiKey = "secret"
        }, handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetProbeDataAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task GetProbeDataAsync_MalformedProbeIsFailure()
    {
        var handler = new TestHttpMessageHandler((_, _) => TestHttpMessageHandler.Json("{ malformed"));
        var client = new NzbdavApiClient(new PluginConfiguration
        {
            NzbdavBaseUrl = "https://nzbdav.example",
            ApiKey = "secret"
        }, handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetProbeDataAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"format\":null}")]
    [InlineData("{\"format\":{\"format_name\":\"matroska\"}}")]
    [InlineData("{\"format\":{\"format_name\":\"matroska\"},\"streams\":null}")]
    [InlineData("{\"format\":{\"format_name\":\"matroska\"},\"streams\":{}}")]
    public async Task GetProbeDataAsync_RejectsNonMediaInfoShape(string json)
    {
        var client = new NzbdavApiClient(new PluginConfiguration
        {
            NzbdavBaseUrl = "https://nzbdav.example",
            ApiKey = "secret"
        }, new TestHttpMessageHandler((_, _) => TestHttpMessageHandler.Json(json)));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetProbeDataAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Theory]
    [InlineData("{\"format\":{\"format_name\":\"matroska\"},\"streams\":[],\"STREAMS\":[]}")]
    public async Task GetProbeDataAsync_RejectsDuplicatePropertiesOutsideTags(string json)
    {

        var client = new NzbdavApiClient(new PluginConfiguration
        {
            NzbdavBaseUrl = "https://nzbdav.example",
            ApiKey = "secret"
        }, new TestHttpMessageHandler((_, _) => TestHttpMessageHandler.Json(json)));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetProbeDataAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task GetProbeDataAsync_AcceptsCorrectedMediaInfoShapeAfterInvalidRetry()
    {
        var responses = new Queue<string>(["{}", "{\"format\":{\"format_name\":\"matroska\"},\"streams\":[]}"]);
        var client = new NzbdavApiClient(new PluginConfiguration
        {
            NzbdavBaseUrl = "https://nzbdav.example",
            ApiKey = "secret"
        }, new TestHttpMessageHandler((_, _) => TestHttpMessageHandler.Json(responses.Dequeue())));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetProbeDataAsync(Guid.NewGuid(), CancellationToken.None));
        var corrected = await client.GetProbeDataAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.Contains("matroska", corrected, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetProbeDataAsync_OversizedChunkedBodyIsBoundedFailure()
    {
        var handler = new TestHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = ChunkedContent(new string('x', 8 * 1024 * 1024 + 1))
        });
        var client = new NzbdavApiClient(new PluginConfiguration
        {
            NzbdavBaseUrl = "https://nzbdav.example",
            ApiKey = "secret"
        }, handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetProbeDataAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public void GetSignedStreamUrl_UsesTokenQuery_AndNeverApiKey()
    {
        var id = Guid.NewGuid();
        var client = new NzbdavApiClient(new PluginConfiguration
        {
            NzbdavBaseUrl = "https://nzbdav.example",
            ApiKey = "must-not-be-persisted"
        });

        var url = client.GetSignedStreamUrl(id, "123.signature");

        Assert.Equal($"https://nzbdav.example/api/stream/{id}?token=123.signature", url);
        Assert.DoesNotContain("apikey=", url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetManifestAsync_ReturnsNotModifiedAndEtag()
    {
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            Assert.Equal("\"manifest-v1\"", request.Headers.IfNoneMatch.Single().Tag);
            return new HttpResponseMessage(HttpStatusCode.NotModified)
            {
                Headers = { ETag = new EntityTagHeaderValue("\"manifest-v1\"") }
            };
        });
        var client = new NzbdavApiClient(new PluginConfiguration
        {
            NzbdavBaseUrl = "https://nzbdav.example",
            TimeoutSeconds = 2
        }, handler);

        var result = await client.GetManifestAsync("\"manifest-v1\"", CancellationToken.None);

        Assert.Null(result.Manifest);
        Assert.Equal("\"manifest-v1\"", result.ETag);
    }

    [Fact]
    public async Task GetManifestAsync_AcceptsChunkedBodyWithoutContentLength()
    {
        var handler = new TestHttpMessageHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = TestHttpMessageHandler.CreateManifestContent(CreateManifestPayload(new ManifestResponse
                {
                    ItemCount = 0,
                    Items = []
                }), contentLength: null)
            });

        var client = new NzbdavApiClient(new PluginConfiguration
        {
            NzbdavBaseUrl = "https://nzbdav.example",
            TimeoutSeconds = 2
        }, handler);

        var result = await client.GetManifestAsync(null, CancellationToken.None);
        Assert.Equal(0, result.Manifest?.ItemCount);
    }

    [Fact]
    public async Task GetManifestAsync_RejectsWhenManifestItemCountAndLengthMismatched()
    {
        var payload = CreateManifestPayload(new ManifestResponse
        {
            ItemCount = 1,
            Items =
            [
                new ManifestItem
                {
                    Id = Guid.NewGuid(),
                    Path = "/content/a.mkv",
                    Name = "a.mkv",
                    Type = "file",
                    FileSize = 1,
                    CreatedAt = DateTime.UtcNow,
                    HasProbeData = false
                }
            ]
        });

        var mismatchedPayload = payload.Replace("\"itemCount\":1", "\"itemCount\":2");

        var handler = new TestHttpMessageHandler((_, _) => TestHttpMessageHandler.OkJson(mismatchedPayload));
        var client = new NzbdavApiClient(new PluginConfiguration
        {
            NzbdavBaseUrl = "https://nzbdav.example",
            TimeoutSeconds = 2
        }, handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetManifestAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task GetManifestAsync_RejectsItemPathTraversal()
    {
        var payload = CreateManifestPayload(new ManifestResponse
        {
            ItemCount = 1,
            Items =
            [
                new ManifestItem
                {
                    Id = Guid.NewGuid(),
                    Path = "/content/../secret.mkv",
                    Name = "secret.mkv",
                    Type = "file",
                    FileSize = 1,
                    CreatedAt = DateTime.UtcNow,
                    HasProbeData = false
                }
            ]
        });

        var handler = new TestHttpMessageHandler((_, _) => TestHttpMessageHandler.OkJson(payload));
        var client = new NzbdavApiClient(new PluginConfiguration
        {
            NzbdavBaseUrl = "https://nzbdav.example",
            TimeoutSeconds = 2
        }, handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetManifestAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task GetManifestAsync_RejectsDuplicateIdsAndPaths()
    {
        var duplicateId = Guid.NewGuid();

        var payload = CreateManifestPayload(new ManifestResponse
        {
            ItemCount = 2,
            Items =
            [
                new ManifestItem
                {
                    Id = duplicateId,
                    Path = "/content/a.mkv",
                    Name = "a.mkv",
                    Type = "file",
                    FileSize = 1,
                    CreatedAt = DateTime.UtcNow,
                    HasProbeData = false
                },
                new ManifestItem
                {
                    Id = duplicateId,
                    Path = "/content/b.mkv",
                    Name = "b.mkv",
                    Type = "file",
                    FileSize = 1,
                    CreatedAt = DateTime.UtcNow,
                    HasProbeData = false
                }
            ]
        });

        var handler = new TestHttpMessageHandler((_, _) => TestHttpMessageHandler.OkJson(payload));
        var client = new NzbdavApiClient(new PluginConfiguration
        {
            NzbdavBaseUrl = "https://nzbdav.example",
            TimeoutSeconds = 2
        }, handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetManifestAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task GetManifestAsync_RejectsItemCountExceedingConfiguredLimit()
    {
        var payload = CreateManifestPayload(new ManifestResponse
        {
            ItemCount = 2,
            Items =
            [
                new ManifestItem
                {
                    Id = Guid.NewGuid(),
                    Path = "/content/a.mkv",
                    Name = "a.mkv",
                    Type = "file",
                    FileSize = 1,
                    CreatedAt = DateTime.UtcNow,
                    HasProbeData = false
                },
                new ManifestItem
                {
                    Id = Guid.NewGuid(),
                    Path = "/content/b.mkv",
                    Name = "b.mkv",
                    Type = "file",
                    FileSize = 1,
                    CreatedAt = DateTime.UtcNow,
                    HasProbeData = false
                }
            ]
        });

        var handler = new TestHttpMessageHandler((_, _) => TestHttpMessageHandler.OkJson(payload));
        var client = new NzbdavApiClient(new PluginConfiguration
        {
            NzbdavBaseUrl = "https://nzbdav.example",
            TimeoutSeconds = 2
        }, handler, maxManifestItems: 1);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetManifestAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task GetManifestAsync_RejectsOversizeManifestBodyByConfiguredLimit()
    {
        var clientOversizePayload = new string('x', 128);
        var handler = new TestHttpMessageHandler((_, _) =>
        {
            var response = TestHttpMessageHandler.Json(clientOversizePayload);
            response.Content.Headers.ContentLength = 128;
            return response;
        });

        var client = new NzbdavApiClient(new PluginConfiguration
        {
            NzbdavBaseUrl = "https://nzbdav.example",
            TimeoutSeconds = 2
        }, handler, maxManifestItems: 50_000, maxManifestContentLength: 64);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetManifestAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task GetManifestAsync_RejectsInvalidJson()
    {
        var handler = new TestHttpMessageHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = TestHttpMessageHandler.CreateManifestContent("{ invalid", contentLength: 9)
            });

        var client = new NzbdavApiClient(new PluginConfiguration
        {
            NzbdavBaseUrl = "https://nzbdav.example",
            TimeoutSeconds = 2
        }, handler);

        await Assert.ThrowsAsync<JsonException>(() => client.GetManifestAsync(null, CancellationToken.None));
    }

    [Fact]
    public void CreateHttpClient_DisablesAutoRedirectForSocketsHttpHandler()
    {
        var handler = new SocketsHttpHandler();
        _ = new NzbdavApiClient(new PluginConfiguration
        {
            NzbdavBaseUrl = "https://nzbdav.example",
            TimeoutSeconds = 2
        }, handler);

        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public async Task GetManifestAsync_DoesNotFollowRedirects()
    {
        var calls = 0;
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            calls++;
            if (calls == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.Redirect)
                {
                    Headers = { Location = new Uri("https://evil.example/manifest") }
                };
            }

            throw new InvalidOperationException("Client followed redirect unexpectedly");
        });

        var client = new NzbdavApiClient(new PluginConfiguration
        {
            NzbdavBaseUrl = "https://nzbdav.example",
            TimeoutSeconds = 2
        }, handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetManifestAsync(null, CancellationToken.None));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SharedHttp_AllowsConstructionAfterARequestWasSent()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start(); // Keep the socket bound: the selected port cannot race another process.
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

        using var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = Task.Run(async () =>
        {
            var body = Encoding.UTF8.GetBytes("{}");
            var requests = 0;
            while (requests < 2)
            {
                using var socket = await listener.AcceptTcpClientAsync(serverCts.Token);
                using var stream = socket.GetStream();
                var headers = new byte[16 * 1024];
                var length = 0;
                while (length < headers.Length)
                {
                    var read = await stream.ReadAsync(headers.AsMemory(length, 1), serverCts.Token);
                    if (read == 0) break;
                    length += read;
                    if (length >= 4 && headers.AsSpan(0, length).EndsWith("\r\n\r\n"u8)) break;
                }
                if (length == 0) continue;
                if (length == headers.Length)
                    throw new InvalidOperationException("HTTP headers exceeded the test limit.");

                var response = "HTTP/1.1 200 OK\r\nConnection: close\r\nContent-Type: application/json\r\nContent-Length: 2\r\n\r\n"u8.ToArray();
                await stream.WriteAsync(response, serverCts.Token);
                await stream.WriteAsync(body, serverCts.Token);
                requests++;
            }
        }, serverCts.Token);

        try
        {
            var config = new PluginConfiguration
            {
                NzbdavBaseUrl = $"http://127.0.0.1:{port}",
                TimeoutSeconds = 5
            };
            var clientA = new NzbdavApiClient(config);
            await clientA.GetMetaAsync(Guid.NewGuid(), serverCts.Token);

            // This used to throw InvalidOperationException because the
            // constructor reset SharedHttp.Timeout after its first send.
            var clientB = new NzbdavApiClient(config);
            await clientB.GetMetaAsync(Guid.NewGuid(), serverCts.Token);
            await server;
        }
        finally
        {
            serverCts.Cancel();
            listener.Stop();
            try { await server; } catch (OperationCanceledException) { } catch (ObjectDisposedException) { }
        }
    }

    [Fact]
    public async Task GetMetaAsync_HonorsConfiguredTimeout()
    {
        using var entered = new ManualResetEventSlim();
        var handler = new TestHttpMessageHandler(async (_, ct) =>
        {
            entered.Set();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return TestHttpMessageHandler.Json("{}");
        });
        var client = new NzbdavApiClient(new PluginConfiguration
        {
            NzbdavBaseUrl = "https://nzbdav.example",
            TimeoutSeconds = 1
        }, handler);

        var operation = client.GetMetaAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }

    [Fact]
    public async Task GetMetaAsync_PropagatesCallerCancellation()
    {
        using var cts = new CancellationTokenSource();
        var handler = new TestHttpMessageHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return TestHttpMessageHandler.Json("{}");
        });
        var client = new NzbdavApiClient(new PluginConfiguration
        {
            NzbdavBaseUrl = "https://nzbdav.example",
            TimeoutSeconds = 30
        }, handler);

        var operation = client.GetMetaAsync(Guid.NewGuid(), cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }

    private static string CreateManifestPayload(ManifestResponse manifest)
    {
        return JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }

    private static StringContent ChunkedContent(string body)
    {
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        content.Headers.ContentLength = null;
        return content;
    }

}

internal sealed class TestHttpMessageHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
{
    public TestHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler)
        : this((request, ct) => Task.FromResult(handler(request, ct)))
    {
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => handler(request, cancellationToken);

    public static HttpResponseMessage Json(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = CreateManifestContent(json)
        };
    }

    public static HttpResponseMessage OkJson(string json) => Json(json);

    public static StringContent CreateManifestContent(string json, long? contentLength = null)
    {
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        content.Headers.ContentLength = contentLength;
        return content;
    }
}
