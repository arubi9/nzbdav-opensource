using System.Net;
using System.Security.Cryptography;
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
using NzbWebDAV.Setup.Orchestration;
using NzbWebDAV.Tests.Clients.Usenet.Caching;

namespace NzbWebDAV.Tests.Api.Controllers.Setup;

[Collection(nameof(backend.Tests.Config.EnvironmentVariableCollection))]
public sealed class SetupConfigurationLeaseContractTests
{
    [Fact]
    public async Task Configure_ReturnsSafeConflictWithoutWrites_ThenRunsOnceAfterOwnerReleases()
    {
        var configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-configure-lease-{Guid.NewGuid():N}");
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("CONFIG_PATH", configPath),
            ("DATABASE_URL", null),
            ("FRONTEND_BACKEND_API_KEY", "unit-api-key"),
            ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))),
            ("NZBDAV_FULL_STACK", "true"));
        Directory.CreateDirectory(configPath);

        await using var context = new DavDatabaseContext();
        await context.Database.MigrateAsync();
        var configManager = new ConfigManager(new ConfigEncryptionService());
        await configManager.LoadConfig();
        var persistence = new SetupConfigPersistence(configManager, context);
        var grantService = new SetupGrantService(
            context,
            configManager,
            TimeProvider.System,
            () => new HttpClient(new HandoffAndNotFoundHandler(), disposeHandler: false),
            persistence);
        var orchestration = new SetupOrchestrationService(
            configManager,
            persistence,
            grantService,
            new DavDatabaseClient(context),
            () => new HttpClient(new NotFoundHandler(), disposeHandler: false),
            usenetCredentialValidator: new AlwaysValidUsenetCredentialValidator(),
            indexerCapabilityValidator: static (credential, _) => Task.FromResult(
                new NzbWebDAV.Clients.Newznab.NewznabCapabilityResult(
                    credential.DisplayName,
                    NzbWebDAV.Clients.Newznab.NewznabCapabilityStatus.Valid)));

        // A successful public handoff must release its grant-issue lease
        // before the browser can submit the very next configure request.
        var issued = await grantService.IssueAsync("admin", "password", CancellationToken.None);
        var otherOwner = new SetupRunLeaseService(context, TimeProvider.System, ownerId: "other-owner");
        var held = await otherOwner.TryAcquireAsync("other-grant", SetupGrantConstants.NormalPurpose);
        Assert.NotNull(held);

        var requestGrant = issued.Grant;
        var requestContext = CreateRequestContext(requestGrant);
        var controller = new SetupConfigurationController(orchestration)
        {
            ControllerContext = new ControllerContext { HttpContext = requestContext },
        };

        var result = await controller.HandleApiRequest();

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        var response = Assert.IsType<BaseApiResponse>(conflict.Value);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        Assert.False(response.Status);
        Assert.Equal(SetupRunBusyException.SafeCode, response.Code);
        Assert.Equal(SetupRunBusyException.SafeMessage, response.Error);
        Assert.Equal("1", requestContext.Response.Headers["Retry-After"].ToString());
        Assert.DoesNotContain(requestGrant, JsonSerializer.Serialize(response));
        Assert.False(await context.ConfigItems.AnyAsync(item => item.ConfigName == "usenet.providers"
            || item.ConfigName == "setup.indexers"
            || item.ConfigName == "setup.run-progress"));

        await held.ReleaseAsync();

        var status = await orchestration.ConfigureAndRunAsync(
            await ParseConfigurationAsync(),
            requestGrant,
            SetupGrantConstants.NormalPurpose,
            CancellationToken.None);
        Assert.Equal(SetupStepState.Complete, status.Steps.Single(step => step.Name == "provider-indexers").State);
        Assert.Single(await context.ConfigItems.Where(item => item.ConfigName == "usenet.providers").ToListAsync());
        Assert.Single(await context.ConfigItems.Where(item => item.ConfigName == "setup.indexers").ToListAsync());

        await orchestration.ConfigureAndRunAsync(
            await ParseConfigurationAsync(),
            requestGrant,
            SetupGrantConstants.NormalPurpose,
            CancellationToken.None);
        Assert.Single(await context.ConfigItems.Where(item => item.ConfigName == "usenet.providers").ToListAsync());
        Assert.Single(await context.ConfigItems.Where(item => item.ConfigName == "setup.indexers").ToListAsync());
    }

    private static DefaultHttpContext CreateRequestContext(string grant)
    {
        var json = JsonSerializer.Serialize(CreateConfigurationPayload());
        var bytes = Encoding.UTF8.GetBytes(json);
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = "application/json";
        context.Request.Headers["x-api-key"] = "unit-api-key";
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Items["nzbdav.setup.grant"] = grant;
        context.Items["nzbdav.setup.scope"] = SetupGrantScope.Normal;
        return context;
    }

    private static async Task<SetupConfigurationData> ParseConfigurationAsync()
    {
        var json = JsonSerializer.Serialize(CreateConfigurationPayload());
        var bytes = Encoding.UTF8.GetBytes(json);
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        return await SetupConfigurationData.ParseAsync(context, CancellationToken.None);
    }

    private static object CreateConfigurationPayload() => new
    {
        providers = new[]
        {
            new { type = "Pooled", host = "provider.local", port = 563, useSsl = true, user = "user", pass = "pass", maxConnections = 1 },
        },
        indexers = new[]
        {
            new { name = "indexer", baseUrl = "https://indexer.example/api", apiKey = "indexer-key", appProfileId = (int?)null },
        },
    };

    private sealed class AlwaysValidUsenetCredentialValidator : IUsenetCredentialValidator
    {
        public Task<UsenetCredentialValidationResult> ValidateAsync(
            UsenetProviderConfig.ConnectionDetails provider,
            CancellationToken cancellationToken = default)
            => Task.FromResult(UsenetCredentialValidationResult.Success);
    }

    private sealed class NotFoundHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private sealed class HandoffAndNotFoundHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/Users/AuthenticateByName")
            {
                var body = JsonSerializer.Serialize(new
                {
                    AccessToken = "handoff-session",
                    User = new { Policy = new { IsAdministrator = true } },
                });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}

[Collection(nameof(backend.Tests.Config.EnvironmentVariableCollection))]
public sealed class SetupConfigurationLeasePostgresContractTests : IClassFixture<PostgresHeaderCacheFixture>
{
    private readonly PostgresHeaderCacheFixture _fixture;

    public SetupConfigurationLeasePostgresContractTests(PostgresHeaderCacheFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task PostgreSql_HandoffReleasesLeaseForImmediateConfigureSlot()
    {
        Assert.SkipUnless(_fixture.IsAvailable, "Docker is required for PostgreSQL setup lease tests.");
        await _fixture.ResetAsync();
        var configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-configure-lease-pg-{Guid.NewGuid():N}");
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("DATABASE_URL", _fixture.ConnectionString),
            ("CONFIG_PATH", configPath),
            ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))),
            ("NZBDAV_FULL_STACK", "true"),
            ("SETUP_JELLYFIN_URL", "http://jellyfin.test"));
        Directory.CreateDirectory(configPath);

        await using var issueContext = new DavDatabaseContext();
        var configManager = new ConfigManager(new ConfigEncryptionService());
        await configManager.LoadConfig();
        var grantService = new SetupGrantService(
            issueContext,
            configManager,
            TimeProvider.System,
            () => new HttpClient(new PostgresHandoffHandler(), disposeHandler: false));
        var issued = await grantService.IssueAsync("admin", "password", CancellationToken.None);

        await using var configureContext = new DavDatabaseContext();
        var configureLease = new SetupRunLeaseService(configureContext, TimeProvider.System, ownerId: "configure-owner");
        var slot = await configureLease.TryAcquireAsync(issued.Grant, SetupGrantConstants.NormalPurpose);
        Assert.NotNull(slot);
        await slot.ReleaseAsync();
    }

    private sealed class PostgresHandoffHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JsonSerializer.Serialize(new
            {
                AccessToken = "postgres-handoff-session",
                User = new { Policy = new { IsAdministrator = true } },
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
