using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Api.Controllers;
using NzbWebDAV.Api.Controllers.Setup;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Services;
using NzbWebDAV.Setup.Core;

namespace NzbWebDAV.Tests.Api.Controllers.Setup;

[Collection(nameof(backend.Tests.Config.EnvironmentVariableCollection))]
public sealed class SetupGrantRecoveryControllerTests
{
    [Fact]
    public async Task HandleApiRequest_RequiresBackendAuthenticationBeforeReadingRecoveryBody()
    {
        using var environment = CreateEnvironment("auth");
        await using var context = await CreateMigratedContextAsync();
        var manager = new ConfigManager(new ConfigEncryptionService());
        await manager.LoadConfig();
        var service = new SetupGrantService(context, manager, TimeProvider.System, () => CreateClient());
        var controller = new SetupGrantRecoveryController(service)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = CreateRequestContext(null, "admin", "secret"),
            },
        };

        var result = await controller.HandleApiRequest();

        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.False(Assert.IsType<BaseApiResponse>(unauthorized.Value).Status);
    }

    [Fact]
    public async Task HandleApiRequest_RejectsMalformedRecoveryBody()
    {
        using var environment = CreateEnvironment("body");
        await using var context = await CreateMigratedContextAsync();
        var manager = new ConfigManager(new ConfigEncryptionService());
        await manager.LoadConfig();
        var service = new SetupGrantService(context, manager, TimeProvider.System, () => CreateClient());
        var request = CreateRequestContext("unit-api-key", "admin", "secret");
        request.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("username=admin"));
        request.Request.ContentLength = request.Request.Body.Length;
        var controller = new SetupGrantRecoveryController(service)
        {
            ControllerContext = new ControllerContext { HttpContext = request },
        };

        var result = await controller.HandleApiRequest();

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task HandleApiRequest_AuthenticatesCredentialsAndCompletesPendingCleanup()
    {
        using var environment = CreateEnvironment("success");
        await using var context = await CreateMigratedContextAsync();
        var manager = new ConfigManager(new ConfigEncryptionService());
        await manager.LoadConfig();
        var handler = new RecoveryHandler();
        var clientFactory = () => new HttpClient(handler, disposeHandler: false);
        var service = new SetupGrantService(context, manager, TimeProvider.System, clientFactory);
        await service.IssueAsync("admin", "secret");
        handler.FailPreviousLogout = true;
        var rotation = await service.RenewAsync("admin", "secret");
        Assert.True(rotation.RevocationPending);
        handler.FailPreviousLogout = false;

        var controller = new SetupGrantRecoveryController(service)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = CreateRequestContext("unit-api-key", "admin", "secret"),
            },
        };

        var result = await controller.HandleApiRequest();

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<SetupGrantRecoveryResponse>(ok.Value);
        Assert.True(response.Status);
        Assert.False(response.RevocationPending);
        Assert.False(await service.IsRevocationPendingAsync());
        Assert.Contains("token-a", handler.LogoutTokens);
    }

    [Fact]
    public async Task HandleApiRequest_ReturnsUnauthorizedForInvalidLocalCredentials()
    {
        using var environment = CreateEnvironment("failure");
        await using var context = await CreateMigratedContextAsync();
        var manager = new ConfigManager(new ConfigEncryptionService());
        await manager.LoadConfig();
        var handler = new RecoveryHandler();
        var service = new SetupGrantService(
            context,
            manager,
            TimeProvider.System,
            () => new HttpClient(handler, disposeHandler: false));
        await service.IssueAsync("admin", "secret");
        handler.FailPreviousLogout = true;
        Assert.True((await service.RenewAsync("admin", "secret")).RevocationPending);
        handler.FailPreviousLogout = false;

        var controller = new SetupGrantRecoveryController(service)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = CreateRequestContext("unit-api-key", "admin", "wrong"),
            },
        };

        var result = await controller.HandleApiRequest();

        Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.True(await service.IsRevocationPendingAsync());
    }

    private static backend.Tests.Config.TemporaryEnvironment CreateEnvironment(string name)
    {
        var configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-recovery-controller-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(configPath);
        return new backend.Tests.Config.TemporaryEnvironment(
            ("CONFIG_PATH", configPath),
            ("DATABASE_URL", null),
            ("FRONTEND_BACKEND_API_KEY", "unit-api-key"),
            ("NZBDAV_FULL_STACK", "true"),
            ("SETUP_JELLYFIN_URL", "http://jellyfin.test"),
            ("NZBDAV_MASTER_KEY", Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))));
    }

    private static DefaultHttpContext CreateRequestContext(string? apiKey, string username, string password)
    {
        var context = new DefaultHttpContext();
        if (apiKey is not null)
            context.Request.Headers["x-api-key"] = apiKey;
        context.Request.Method = "POST";
        context.Request.ContentType = "application/x-www-form-urlencoded";
        var bytes = Encoding.UTF8.GetBytes($"username={username}&password={password}");
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        return context;
    }

    private static HttpClient CreateClient()
        => new(new RecoveryHandler(), disposeHandler: true);

    private static async Task<DavDatabaseContext> CreateMigratedContextAsync()
    {
        var context = new DavDatabaseContext();
        await context.Database.MigrateAsync();
        return context;
    }

    private sealed class RecoveryHandler : HttpMessageHandler
    {
        private int _authenticationCalls;
        public bool FailPreviousLogout { get; set; }
        public List<string> LogoutTokens { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/Users/AuthenticateByName")
            {
                var token = Interlocked.Increment(ref _authenticationCalls) == 1 ? "token-a" : "token-b";
                var body = JsonSerializer.Serialize(new
                {
                    AccessToken = token,
                    User = new { Policy = new { IsAdministrator = true } },
                });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
            }

            if (request.RequestUri?.AbsolutePath == "/Sessions/Logout")
            {
                var token = request.Headers.GetValues("X-Emby-Token").Single();
                LogoutTokens.Add(token);
                if (FailPreviousLogout && token == "token-a")
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
