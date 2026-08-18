using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace NzbWebDAV.Tests.Setup.Core;

internal sealed class JellyfinProductionFixture : IAsyncDisposable
{
    public const string Image = "jellyfin/jellyfin:10.11.8@sha256:1694ff069f0c9dafb283c36765175606866769f5d72f2ed56b6a0f1be922fc37";
    public const string Username = "admin";
    public const string Password = "secret";

    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private readonly string _configVolumeName;
    private readonly string _cacheVolumeName;
    private IContainer? _container;

    private JellyfinProductionFixture()
    {
        _configVolumeName = $"nzbdav-jellyfin-test-{_instanceId}-config";
        _cacheVolumeName = $"nzbdav-jellyfin-test-{_instanceId}-cache";
    }

    public Uri BaseUri { get; private set; } = null!;
    public string ConfigVolumeName => _configVolumeName;
    public string CacheVolumeName => _cacheVolumeName;

    public static bool DockerAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = "version --format {{.Server.Version}}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
                return false;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
            return process.HasExited && process.ExitCode == 0
                && !string.IsNullOrWhiteSpace(process.StandardOutput.ReadToEnd());
        }
        catch
        {
            return false;
        }
    }

    public static async Task<JellyfinProductionFixture> StartAsync()
    {
        var fixture = new JellyfinProductionFixture();
        try
        {
            fixture._container = new ContainerBuilder(Image)
                .WithName($"nzbdav-jellyfin-test-{fixture._instanceId}")
                .WithVolumeMount(fixture._configVolumeName, "/config")
                .WithVolumeMount(fixture._cacheVolumeName, "/cache")
                .WithPortBinding(8096, true)
                .WithCleanUp(true)
                .Build();
            await fixture._container.StartAsync().WaitAsync(TimeSpan.FromMinutes(2));
            fixture.BaseUri = new Uri($"http://127.0.0.1:{fixture._container.GetMappedPublicPort(8096)}");
            await fixture.BootstrapAsync().WaitAsync(TimeSpan.FromMinutes(2));
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    public HttpClient CreateClient(HttpMessageHandler? handler = null)
    {
        if (handler is not null)
            return new HttpClient(handler, disposeHandler: false) { BaseAddress = BaseUri };
        return new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { BaseAddress = BaseUri };
    }

    public async Task<ProbeResult> ProbeTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/Users/Me");
        request.Headers.TryAddWithoutValidation("X-Emby-Token", token);
        using var response = await client.SendAsync(request, cancellationToken);
        return new ProbeResult(response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
    }

    public async Task<JsonDocument> ReadSessionsAsync(string deviceId, string token, CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/Sessions?deviceId={Uri.EscapeDataString(deviceId)}");
        request.Headers.TryAddWithoutValidation("X-Emby-Token", token);
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private async Task BootstrapAsync()
    {
        using var client = CreateClient();
        await WaitForHealthAsync(client);
        await PostAsync(client, "/Startup/Configuration", new
        {
            ServerName = "NzbDav Jellyfin test",
            UICulture = "en-US",
            MetadataCountryCode = "US",
            PreferredMetadataLanguage = "en",
        });
        await GetAsync(client, "/Startup/User");
        await PostAsync(client, "/Startup/User", new { Name = Username, Password });
        await PostAsync(client, "/Startup/RemoteAccess", new { EnableRemoteAccess = true, EnableAutomaticPortMapping = false });
        await PostAsync(client, "/Startup/Complete", null);
        await WaitForHealthAsync(client);
    }

    private static async Task WaitForHealthAsync(HttpClient client)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        while (true)
        {
            try
            {
                using var response = await client.GetAsync("/health", timeout.Token);
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException) when (!timeout.IsCancellationRequested) { }
            await Task.Delay(TimeSpan.FromMilliseconds(250), timeout.Token);
        }
    }

    private static async Task GetAsync(HttpClient client, string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        while (true)
        {
            try
            {
                using var response = await client.GetAsync(path, timeout.Token);
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException) when (!timeout.IsCancellationRequested) { }
            await Task.Delay(TimeSpan.FromMilliseconds(250), timeout.Token);
        }
    }

    private static async Task PostAsync(HttpClient client, string path, object? body)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        while (true)
        {
            try
            {
                using var content = body is null
                    ? new StringContent(string.Empty, Encoding.UTF8, "application/json")
                    : new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                using var response = await client.PostAsync(path, content, timeout.Token);
                if (response.StatusCode != HttpStatusCode.ServiceUnavailable)
                {
                    response.EnsureSuccessStatusCode();
                    return;
                }
            }
            catch (HttpRequestException) when (!timeout.IsCancellationRequested) { }

            await Task.Delay(TimeSpan.FromMilliseconds(250), timeout.Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            try { await _container.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30)); }
            catch { /* cleanup below is independently bounded */ }
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = $"volume rm -f {ConfigVolumeName} {CacheVolumeName}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is not null)
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch { }
    }

    internal sealed class RealForwardingHandler : DelegatingHandler
    {
        private readonly Action<RealAuthentication> _onAuthentication;
        private readonly Func<string, Task>? _beforeLogout;
        private readonly CancellationTokenSource? _cancelAfterFirstAuthentication;
        private readonly bool _loseFirstAuthentication;
        private int _authenticationCount;
        private int _logoutCount;

        public RealForwardingHandler(
            Uri baseUri,
            Action<RealAuthentication> onAuthentication,
            bool loseFirstAuthentication = false,
            CancellationTokenSource? cancelAfterFirstAuthentication = null,
            Func<string, Task>? beforeLogout = null)
        {
            _onAuthentication = onAuthentication;
            _loseFirstAuthentication = loseFirstAuthentication;
            _cancelAfterFirstAuthentication = cancelAfterFirstAuthentication;
            _beforeLogout = beforeLogout;
            InnerHandler = new SocketsHttpHandler { AllowAutoRedirect = false };
            BaseUri = baseUri;
        }

        public Uri BaseUri { get; }
        public List<RealAuthentication> Authentications { get; } = [];
        public List<string> LogoutTokens { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, CancellationToken.None);
            var bytes = await response.Content.ReadAsByteArrayAsync(CancellationToken.None);
            var headers = response.Headers.ToDictionary(item => item.Key, item => item.Value.ToArray());
            var contentHeaders = response.Content.Headers.ToDictionary(item => item.Key, item => item.Value.ToArray());
            response.Dispose();
            var cloned = new HttpResponseMessage(response.StatusCode)
            {
                Content = new ByteArrayContent(bytes),
                RequestMessage = request,
                ReasonPhrase = response.ReasonPhrase,
            };
            foreach (var header in headers)
                cloned.Headers.TryAddWithoutValidation(header.Key, header.Value);
            foreach (var header in contentHeaders)
                cloned.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);

            var path = request.RequestUri?.AbsolutePath;
            if (path == "/Users/AuthenticateByName" && cloned.IsSuccessStatusCode)
            {
                var auth = RealAuthentication.Parse(bytes, request);
                Authentications.Add(auth);
                _onAuthentication(auth);
                var authNumber = Interlocked.Increment(ref _authenticationCount);
                if (authNumber == 1 && _loseFirstAuthentication)
                    throw new HttpRequestException("Injected transport loss after Jellyfin committed authentication.");
                if (authNumber == 1 && _cancelAfterFirstAuthentication is not null)
                {
                    _cancelAfterFirstAuthentication.Cancel();
                    throw new OperationCanceledException(cancellationToken);
                }
            }
            else if (path == "/Sessions/Logout" && request.Method == HttpMethod.Post
                     && request.Headers.TryGetValues("X-Emby-Token", out var values))
            {
                var token = values.Single();
                LogoutTokens.Add(token);
                if (Interlocked.Increment(ref _logoutCount) == 1 && _beforeLogout is not null)
                    await _beforeLogout(token);
            }

            return cloned;
        }
    }

    internal sealed record RealAuthentication(string AccessToken, string DeviceId, JsonElement SessionInfo, JsonDocument Response)
    {
        public static RealAuthentication Parse(byte[] bytes, HttpRequestMessage request)
        {
            var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            var authorization = request.Headers.GetValues("X-Emby-Authorization").Single();
            var marker = "DeviceId=\"";
            var start = authorization.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
            var end = authorization.IndexOf('"', start);
            var sessionInfo = root.TryGetProperty("SessionInfo", out var info) ? info.Clone() : default;
            return new RealAuthentication(root.GetProperty("AccessToken").GetString()!, authorization[start..end], sessionInfo, document);
        }
    }

    internal readonly record struct ProbeResult(HttpStatusCode StatusCode, string Body);
}
