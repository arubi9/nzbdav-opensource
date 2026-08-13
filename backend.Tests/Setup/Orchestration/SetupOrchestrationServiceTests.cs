using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Setup.Core;
using NzbWebDAV.Setup.Orchestration;

namespace NzbWebDAV.Tests.Setup.Orchestration;

[Collection(nameof(backend.Tests.Config.EnvironmentVariableCollection))]
public sealed class SetupOrchestrationServiceTests
{
    [Fact]
    public async Task ConfigureAsync_IsIdempotentForStoredValues()
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"nzbdav-tests/setup-orch-config-{Guid.NewGuid():N}");
        using var _ = new backend.Tests.Config.TemporaryEnvironment(
            ("CONFIG_PATH", configPath),
            ("DATABASE_URL", null),
            ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))),
            ("NZBDAV_FULL_STACK", "true"));
        Directory.CreateDirectory(configPath);

        await using var context = await CreateMigratedContextAsync();
        var operatorQueueRules = new ArrConfig
        {
            QueueRules =
            [
                new ArrConfig.QueueRule { Message = "first operator rule", Action = ArrConfig.QueueAction.Remove },
                new ArrConfig.QueueRule { Message = "second operator rule", Action = ArrConfig.QueueAction.RemoveAndBlocklist },
            ],
        };
        context.ConfigItems.Add(new ConfigItem
        {
            ConfigName = "arr.instances",
            ConfigValue = JsonSerializer.Serialize(operatorQueueRules),
        });
        await context.SaveChangesAsync();

        var configManager = new ConfigManager(new ConfigEncryptionService());
        await configManager.LoadConfig();
        var persistence = new SetupConfigPersistence(configManager, context);
        var grantService = new SetupGrantService(context, configManager, TimeProvider.System, () => new HttpClient());
        var dbClient = new DavDatabaseClient(context);
        var capabilityHandler = new StrictNewznabHandler();
        var service = new SetupOrchestrationService(
            configManager,
            persistence,
            grantService,
            dbClient,
            () => new HttpClient(capabilityHandler),
            usenetCredentialValidator: new AlwaysValidUsenetCredentialValidator(),
            capabilityHostResolver: static (_, _) => Task.FromResult(new[] { IPAddress.Parse("1.1.1.1") }),
            // The production capability client uses a DNS-pinned transport,
            // so its injected HttpClient is not a fake upstream. Keep this
            // orchestration test deterministic by supplying the validated
            // capability outcome explicitly.
            indexerCapabilityValidator: static (credential, _) => Task.FromResult(
                new NzbWebDAV.Clients.Newznab.NewznabCapabilityResult(
                    credential.DisplayName,
                    NzbWebDAV.Clients.Newznab.NewznabCapabilityStatus.Valid)));

        var request = await ParseConfigurationRequestAsync(new
        {
            providers = new[]
            {
                new { type = "Pooled", host = "provider.local", port = 563, useSsl = true, user = "user", pass = "pass", maxConnections = 1 }
            },
            indexers = new[]
            {
                new { name = "indexer", baseUrl = "https://indexer.example/api", apiKey = "indexer-key", appProfileId = (int?)null }
            }
        });

        await service.ConfigureAsync(request, CancellationToken.None);
        await service.ConfigureAsync(request, CancellationToken.None);

        Assert.Equal(1, await context.ConfigItems.CountAsync(item => item.ConfigName == "usenet.providers"));
        Assert.Equal(1, await context.ConfigItems.CountAsync(item => item.ConfigName == "setup.indexers"));
        Assert.True((await context.ConfigItems.SingleAsync(item => item.ConfigName == "usenet.providers")).IsEncrypted);
        Assert.True((await context.ConfigItems.SingleAsync(item => item.ConfigName == "setup.indexers")).IsEncrypted);
        Assert.Equal(0, capabilityHandler.RequestCount);
        var arrRow = await context.ConfigItems.AsNoTracking().SingleAsync(item => item.ConfigName == "arr.instances");
        var persistedArr = JsonSerializer.Deserialize<ArrConfig>(arrRow.ConfigValue)!;
        Assert.Equal(operatorQueueRules.QueueRules.Select(rule => rule.Message), persistedArr.QueueRules.Select(rule => rule.Message));
        Assert.Equal(operatorQueueRules.QueueRules.Select(rule => rule.Action), persistedArr.QueueRules.Select(rule => rule.Action));
    }

    [Fact]
    public async Task ConfigureAsync_InvalidIndexerDoesNotPublishPartialSetupSecrets()
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"nzbdav-tests/setup-orch-invalid-indexer-{Guid.NewGuid():N}");
        using var _ = new backend.Tests.Config.TemporaryEnvironment(
            ("CONFIG_PATH", configPath),
            ("DATABASE_URL", null),
            ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))),
            ("NZBDAV_FULL_STACK", "true"));
        Directory.CreateDirectory(configPath);

        await using var context = await CreateMigratedContextAsync();
        var configManager = new ConfigManager(new ConfigEncryptionService());
        await configManager.LoadConfig();
        var persistence = new SetupConfigPersistence(configManager, context);
        var grantService = new SetupGrantService(context, configManager, TimeProvider.System, () => new HttpClient());
        var service = new SetupOrchestrationService(
            configManager,
            persistence,
            grantService,
            new DavDatabaseClient(context),
            usenetCredentialValidator: new AlwaysValidUsenetCredentialValidator(),
            indexerCapabilityValidator: static (credential, _) => Task.FromResult(
                new NzbWebDAV.Clients.Newznab.NewznabCapabilityResult(
                    credential.DisplayName,
                    NzbWebDAV.Clients.Newznab.NewznabCapabilityStatus.InvalidCredentials)));

        var request = await ParseConfigurationRequestAsync(new
        {
            providers = new[]
            {
                new { type = "Pooled", host = "provider.local", port = 563, useSsl = true, user = "user", pass = "pass", maxConnections = 1 }
            },
            indexers = new[]
            {
                new { name = "indexer", baseUrl = "https://indexer.example/api", apiKey = "indexer-key", appProfileId = (int?)null }
            }
        });

        var status = await service.ConfigureAsync(request, CancellationToken.None);

        Assert.Equal(SetupStepState.Failed, status.Steps.Single(step => step.Name == "provider-indexers").State);
        Assert.Equal(SetupReasonCodes.IndexerCapabilityFailed, status.Steps.Single(step => step.Name == "provider-indexers").Code);
        Assert.False(await context.ConfigItems.AnyAsync(item => item.ConfigName == "usenet.providers"));
        Assert.False(await context.ConfigItems.AnyAsync(item => item.ConfigName == "setup.indexers"));
    }

    [Fact]
    public async Task RepairRunFailure_CleansRepairSessionButRetainsCompletionAndRepairLatch()
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"nzbdav-tests/setup-orch-repair-{Guid.NewGuid():N}");
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("CONFIG_PATH", configPath),
            ("DATABASE_URL", null),
            ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))),
            ("NZBDAV_FULL_STACK", "true"),
            ("SETUP_JELLYFIN_URL", "http://jellyfin.test"));
        Directory.CreateDirectory(configPath);

        await using var context = await CreateMigratedContextAsync();
        var configManager = new ConfigManager(new ConfigEncryptionService());
        await configManager.LoadConfig();
        var persistence = new SetupConfigPersistence(configManager, context);
        await persistence.SaveAsync(new SetupSecretValues
        {
            Indexers = "[]",
            PluginApiKey = "plugin-key",
        }, completed: true);
        await persistence.SaveLiveHealthStateAsync(
            ready: false,
            checkedAtUtc: DateTime.UtcNow,
            sonarrReason: SetupReasonCodes.SonarrFailed,
            radarrReason: null,
            reasonsJson: "{}",
            CancellationToken.None);

        var handler = new RepairRunHandler();
        var grantService = new SetupGrantService(
            context,
            configManager,
            TimeProvider.System,
            () => new HttpClient(handler, disposeHandler: false),
            persistence);
        var repairGrant = await grantService.IssueRepairAsync("admin", "password");
        var service = new SetupOrchestrationService(
            configManager,
            persistence,
            grantService,
            new DavDatabaseClient(context),
            () => new HttpClient(handler, disposeHandler: false));

        var status = await service.RunRepairAsync(repairGrant.Grant, CancellationToken.None);

        Assert.True(status.RepairRequired);
        Assert.Contains("repair-session", handler.LogoutTokens);
        await configManager.LoadConfig();
        Assert.True(configManager.IsSetupCompleted());
        Assert.True(configManager.IsSetupRepairRequired());
        var row = await context.SetupGrants.AsNoTracking().SingleAsync();
        Assert.True(row.IsRevoked);
        Assert.Null(row.RepairSessionCiphertext);
    }

    [Fact]
    public async Task GetStatusAsync_IsPureAndReportsStaleCompletionWithoutLiveVerification()
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"nzbdav-tests/setup-orch-status-{Guid.NewGuid():N}");
        using var _ = new backend.Tests.Config.TemporaryEnvironment(
            ("CONFIG_PATH", configPath),
            ("DATABASE_URL", null),
            ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))),
            ("NZBDAV_FULL_STACK", "true"));
        Directory.CreateDirectory(configPath);

        await using var context = await CreateMigratedContextAsync();
        var configManager = new ConfigManager(new ConfigEncryptionService());
        await configManager.LoadConfig();

        var persistence = new SetupConfigPersistence(configManager, context);
        await persistence.SaveAsync(new SetupSecretValues
        {
            Indexers = "[]",
            PluginApiKey = "plugin",
            JellyfinApiKey = "jellyfin",
        }, completed: true);

        var liveCheckHandler = new RejectingHttpHandler();
        var grantService = new SetupGrantService(context, configManager, TimeProvider.System, () => new HttpClient(liveCheckHandler, disposeHandler: false));
        var dbClient = new DavDatabaseClient(context);
        var service = new SetupOrchestrationService(
            configManager,
            persistence,
            grantService,
            dbClient,
            () => new HttpClient(liveCheckHandler, disposeHandler: false));

        var staleStatus = await service.GetStatusAsync();

        Assert.True(staleStatus.Enabled);
        Assert.True(staleStatus.Completed);
        Assert.False(staleStatus.RepairRequired);
        Assert.Equal(["nzbdav", "jellyfin", "sonarr", "radarr", "prowlarr"], staleStatus.Services.Select(item => item.Name));
        Assert.False(staleStatus.Services.Single(item => item.Name == "nzbdav").Ready);
        Assert.Equal(SetupReasonCodes.NzbdavFailed, staleStatus.Services.Single(item => item.Name == "nzbdav").Reason);
        Assert.All(staleStatus.Services.Where(item => item.Name != "nzbdav"), item => Assert.False(item.Ready));
        Assert.Equal(SetupStepState.Warning, staleStatus.Steps.Single(step => step.Name == "jellyfin").State);
        Assert.Equal(SetupReasonCodes.JellyfinFailed, staleStatus.Steps.Single(step => step.Name == "jellyfin").Code);
        Assert.Equal(0, liveCheckHandler.RequestCount);

        Assert.False(await context.ConfigItems.AsNoTracking()
            .AnyAsync(row => row.ConfigName == SetupConfigKeys.RepairRequired));
        Assert.False(await context.ConfigItems.AsNoTracking()
            .AnyAsync(row => row.ConfigName == SetupConfigKeys.RepairCheckedAtUtc));

        // Rebuild the orchestration dependencies to model a process restart;
        // the warning and reason must come from durable state/marker semantics,
        // not the previous service instance's in-memory progress.
        var restartedConfigManager = new ConfigManager(new ConfigEncryptionService());
        await restartedConfigManager.LoadConfig();
        var restartedPersistence = new SetupConfigPersistence(restartedConfigManager, context);
        var restartedGrantService = new SetupGrantService(
            context,
            restartedConfigManager,
            TimeProvider.System,
            () => new HttpClient(liveCheckHandler, disposeHandler: false),
            restartedPersistence);
        var restartedService = new SetupOrchestrationService(
            restartedConfigManager,
            restartedPersistence,
            restartedGrantService,
            new DavDatabaseClient(context),
            () => new HttpClient(liveCheckHandler, disposeHandler: false));

        var reloadedStatus = restartedService.GetStatus();
        Assert.True(reloadedStatus.Completed);
        Assert.False(reloadedStatus.RepairRequired);
        Assert.Equal(SetupStepState.Warning, reloadedStatus.Steps.Single(step => step.Name == "jellyfin").State);
        Assert.Equal(SetupReasonCodes.JellyfinFailed, reloadedStatus.Steps.Single(step => step.Name == "jellyfin").Code);
        Assert.Equal(SetupStepState.Warning, reloadedStatus.Steps.Single(step => step.Name == "health-check").State);

        var retriedStatus = await restartedService.GetStatusAsync();
        Assert.True(retriedStatus.Completed);
        Assert.False(retriedStatus.RepairRequired);
        Assert.Equal(SetupStepState.Warning, retriedStatus.Steps.Single(step => step.Name == "health-check").State);
        Assert.Equal(0, liveCheckHandler.RequestCount);
    }

    private sealed class AlwaysValidUsenetCredentialValidator : IUsenetCredentialValidator
    {
        public Task<UsenetCredentialValidationResult> ValidateAsync(
            UsenetProviderConfig.ConnectionDetails provider,
            CancellationToken cancellationToken = default)
            => Task.FromResult(UsenetCredentialValidationResult.Success);
    }

    private sealed class RepairRunHandler : HttpMessageHandler
    {
        public List<string> LogoutTokens { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/Users/AuthenticateByName")
            {
                var body = JsonSerializer.Serialize(new
                {
                    AccessToken = "repair-session",
                    User = new { Policy = new { IsAdministrator = true } },
                });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
            }

            if (request.RequestUri?.AbsolutePath == "/Sessions/Logout")
            {
                LogoutTokens.Add(request.Headers.GetValues("X-Emby-Token").Single());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class RejectingHttpHandler : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => _requestCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class StrictNewznabHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://indexer.example/api?t=caps", request.RequestUri?.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<caps><server version=\"1\" /></caps>", Encoding.UTF8, "application/xml"),
            });
        }
    }

    private static async Task<DavDatabaseContext> CreateMigratedContextAsync()
    {
        var context = new DavDatabaseContext();
        await context.Database.MigrateAsync();
        return context;
    }

    private static async Task<SetupConfigurationData> ParseConfigurationRequestAsync(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Request.ContentType = "application/json";

        return await SetupConfigurationData.ParseAsync(context, CancellationToken.None);
    }
}
