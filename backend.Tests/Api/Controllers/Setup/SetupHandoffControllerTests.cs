using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Api.Controllers;
using NzbWebDAV.Api.Controllers.Setup;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Setup.Core;

namespace NzbWebDAV.Tests.Api.Controllers.Setup;

[Collection(nameof(backend.Tests.Config.EnvironmentVariableCollection))]
public sealed class SetupHandoffControllerTests
{
    [Fact]
    public async Task HandleRequest_AuthenticatesOnceAndPersistsGrantAtomically()
    {
        var configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-handoff-request-success-{Guid.NewGuid():N}");
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("CONFIG_PATH", configPath),
            ("DATABASE_URL", null),
            ("FRONTEND_BACKEND_API_KEY", "unit-api-key"),
            ("NZBDAV_FULL_STACK", "true"),
            ("SETUP_JELLYFIN_URL", "http://jellyfin.test"),
            ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
        Directory.CreateDirectory(configPath);

        await using var context = await CreateMigratedContextAsync();
        var configManager = new ConfigManager(new ConfigEncryptionService());
        await configManager.LoadConfig();

        var authHandler = new CountingJellyfinHandler(_ => (true, "token-success"));
        var service = new SetupGrantService(context, configManager, TimeProvider.System, () => new HttpClient(authHandler));
        var controller = new SetupHandoffController(service)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = CreateRequestContext("unit-api-key", "admin", "secret")
            }
        };

        var result = await controller.HandleApiRequest();

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<SetupGrantResponse>(ok.Value);
        Assert.NotEmpty(response.Grant);

        Assert.Equal(1, authHandler.CallCount);

        var setupGrant = await context.SetupGrants.SingleAsync();
        Assert.False(setupGrant.IsRevoked);
        Assert.Equal("admin", setupGrant.IssuedByUsername);

        var setupToken = await context.ConfigItems
            .SingleOrDefaultAsync(x => x.ConfigName == SetupConfigKeys.JellyfinApiKey);
        Assert.NotNull(setupToken);
        Assert.True(setupToken.IsEncrypted);
    }

    [Fact]
    public async Task HandleRequest_RetriesRotateTokenAndPersistAfterPreviousCommitFailure()
    {
        var configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-handoff-request-retry-{Guid.NewGuid():N}");
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("CONFIG_PATH", configPath),
            ("DATABASE_URL", null),
            ("FRONTEND_BACKEND_API_KEY", "unit-api-key"),
            ("NZBDAV_FULL_STACK", "true"),
            ("SETUP_JELLYFIN_URL", "http://jellyfin.test"),
            ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
        Directory.CreateDirectory(configPath);

        await using var failingContext = await CreateMigratedContextAsync();
        var configManager = new ConfigManager(new ConfigEncryptionService());
        await configManager.LoadConfig();

        var firstAuthHandler = new CountingJellyfinHandler(_ => (true, "token-lost"));
        var failingService = new SetupGrantService(
            failingContext,
            configManager,
            TimeProvider.System,
            () => new HttpClient(firstAuthHandler),
            null,
            (_, _) => Task.FromException(new InvalidOperationException("simulate commit failure")));
        var failingController = new SetupHandoffController(failingService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = CreateRequestContext("unit-api-key", "admin", "secret")
            }
        };

        var failedResult = await failingController.HandleApiRequest();
        var failureObject = Assert.IsType<ObjectResult>(failedResult);
        var failureResponse = Assert.IsType<BaseApiResponse>(failureObject.Value);
        Assert.Equal(500, failureObject.StatusCode);
        Assert.False(failureResponse.Status);
        Assert.Equal(1, firstAuthHandler.AuthenticationCallCount);

        await using (var verifyAfterFailure = await CreateMigratedContextAsync())
        {
            Assert.False(await verifyAfterFailure.Accounts.AnyAsync(x => x.Type == Account.AccountType.Admin && x.Username == "admin"));
            Assert.False(await verifyAfterFailure.SetupGrants.AnyAsync());
            Assert.False(await verifyAfterFailure.ConfigItems.AnyAsync(x => x.ConfigName == SetupConfigKeys.JellyfinApiKey));
        }

        await using var retryContext = await CreateMigratedContextAsync();
        var retryConfigManager = new ConfigManager(new ConfigEncryptionService());
        await retryConfigManager.LoadConfig();
        var secondAuthHandler = new CountingJellyfinHandler(_ => (true, "token-retry"));
        var retryService = new SetupGrantService(
            retryContext,
            retryConfigManager,
            TimeProvider.System,
            () => new HttpClient(secondAuthHandler));
        var retryController = new SetupHandoffController(retryService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = CreateRequestContext("unit-api-key", "admin", "secret")
            }
        };

        var retryResult = await retryController.HandleApiRequest();
        Assert.IsType<OkObjectResult>(retryResult);

        await using var finalContext = await CreateMigratedContextAsync();
        var persisted = await finalContext.ConfigItems.SingleAsync(x => x.ConfigName == SetupConfigKeys.JellyfinApiKey);
        Assert.Equal(1, secondAuthHandler.CallCount);
        Assert.True(persisted.IsEncrypted);
        Assert.Single(await finalContext.SetupGrants.Where(x => x.Id == SetupGrant.SingletonId).ToListAsync());

        var persistedConfig = new ConfigManager(new ConfigEncryptionService());
        await persistedConfig.LoadConfig();
        Assert.Equal("token-retry", persistedConfig.GetSetupJellyfinApiKey());
    }

    private static DefaultHttpContext CreateRequestContext(string apiKey, string username, string password)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["x-api-key"] = apiKey;
        context.Request.Method = "POST";
        context.Request.ContentType = "application/x-www-form-urlencoded";

        var body = $"username={Uri.EscapeDataString(username.ToLowerInvariant())}&password={Uri.EscapeDataString(password)}";
        var bytes = System.Text.Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;

        return context;
    }

    private static async Task<DavDatabaseContext> CreateMigratedContextAsync()
    {
        var context = new DavDatabaseContext();
        await context.Database.MigrateAsync();
        return context;
    }

    private sealed class CountingJellyfinHandler(Func<int, (bool Administrator, string Token)> responseFactory) : HttpMessageHandler
    {
        private readonly Func<int, (bool Administrator, string Token)> _responseFactory = responseFactory;
        private int _call;
        private int _authenticationCalls;
        public int CallCount => _call;
        public int AuthenticationCallCount => _authenticationCalls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/Users/AuthenticateByName")
                Interlocked.Increment(ref _authenticationCalls);

            var response = _responseFactory(Interlocked.Increment(ref _call) - 1);
            var body = System.Text.Json.JsonSerializer.Serialize(new
            {
                AccessToken = response.Token,
                User = new { Policy = new { IsAdministrator = response.Administrator } },
            });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
