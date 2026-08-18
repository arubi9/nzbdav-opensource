using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.ProwlarrSetup;
using NzbWebDAV.Clients.Newznab;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Services;
using NzbWebDAV.Setup.Core;
using NzbWebDAV.Setup.Discovery;
using NzbWebDAV.Setup.Orchestration;

namespace NzbWebDAV.Tests.Setup.Orchestration;

[Collection(nameof(backend.Tests.Config.EnvironmentVariableCollection))]
public sealed class SetupStatusVerificationProductionTests
{
    [Fact]
    public async Task ArrPersistence_PublishesPlaintextCacheAfterBothExactArrFixturesSucceed()
    {
        await using var prowlarrServer = ProwlarrLoopbackServer.Start();
        using var environment = CreateEnvironment("arr-persist-cache", prowlarrServer.Url);
        await using var context = new DavDatabaseContext();
        await context.Database.MigrateAsync();
        var manager = new ConfigManager(new ConfigEncryptionService());
        await manager.LoadConfig();
        var persistence = new SetupConfigPersistence(manager, context);
        await persistence.SaveAsync(
            new SetupSecretValues
            {
                Indexers = "[{\"name\":\"indexer\",\"baseUrl\":\"https://indexer.example/api\",\"apiKey\":\"indexer-key\"}]",
                UsenetProviders = "{\"providers\":[{\"type\":\"Pooled\",\"host\":\"provider.local\",\"port\":563,\"useSsl\":true,\"user\":\"u\",\"pass\":\"p\",\"maxConnections\":1}]}",
                PluginApiKey = "plugin-key",
                RunProgress = "{\"provider-indexers\":\"complete\",\"arr-key-discovery\":\"complete\",\"prowlarr\":\"complete\",\"arr-clients\":\"pending\",\"jellyfin\":\"complete\",\"health-check\":\"pending\"}",
                RunProgressReasons = "{}",
            },
            completed: false);

        var handler = new CompleteVerificationHandler { BackendApiKey = manager.GetApiKey() };
        var service = CreateService(
            context,
            manager,
            persistence,
            handler,
            static (credential, _) => Task.FromResult(
                new NewznabCapabilityResult(credential.DisplayName, NewznabCapabilityStatus.Valid)));
        InjectTestDiscovery(service);

        var status = await service.RunAsync(CancellationToken.None);

        Assert.True(status.Completed, string.Join(", ", status.Steps.Select(step => $"{step.Name}:{step.State}:{step.Code}")));
        Assert.Equal(["radarr", "sonarr"], handler.ArrSchemaHosts.OrderBy(host => host, StringComparer.Ordinal));
        var persisted = await context.ConfigItems.AsNoTracking().SingleAsync(item => item.ConfigName == "arr.instances");
        Assert.True(persisted.IsEncrypted);
        var arr = manager.GetArrConfig();
        Assert.Single(arr.SonarrInstances);
        Assert.Single(arr.RadarrInstances);
    }

    [Fact]
    public async Task IncompleteCompletedPhases_RetryUsesPersistedPluginKeyWithoutResourceChurn()
    {
        await using var prowlarrServer = ProwlarrLoopbackServer.Start();
        using var environment = CreateEnvironment("retry-plugin-key", prowlarrServer.Url);
        await using var context = new DavDatabaseContext();
        await context.Database.MigrateAsync();
        var manager = new ConfigManager(new ConfigEncryptionService());
        await manager.LoadConfig();
        var persistence = new SetupConfigPersistence(manager, context);
        await persistence.SaveAsync(
            new SetupSecretValues
            {
                Indexers = "[{\"name\":\"indexer\",\"baseUrl\":\"https://indexer.example/api\",\"apiKey\":\"indexer-key\"}]",
                UsenetProviders = "{\"providers\":[{\"type\":\"Pooled\",\"host\":\"provider.local\",\"port\":563,\"useSsl\":true,\"user\":\"u\",\"pass\":\"p\",\"maxConnections\":1}]}",
                PluginApiKey = "plugin-key",
                RunProgress = "{\"provider-indexers\":\"complete\",\"arr-key-discovery\":\"complete\",\"prowlarr\":\"complete\",\"arr-clients\":\"complete\",\"jellyfin\":\"complete\",\"health-check\":\"complete\"}",
                RunProgressReasons = "{}",
            },
            completed: false);

        var handler = new CompleteVerificationHandler { BackendApiKey = manager.GetApiKey() };
        var service = CreateService(
            context,
            manager,
            persistence,
            handler,
            static (credential, _) => Task.FromResult(
                new NewznabCapabilityResult(credential.DisplayName, NewznabCapabilityStatus.Valid)));
        InjectTestDiscovery(service);

        var status = await service.RunAsync(CancellationToken.None);

        Assert.True(status.Completed, string.Join(", ", status.Steps.Select(step => $"{step.Name}:{step.State}:{step.Code}")));
        Assert.DoesNotContain(handler.Requests, request => request.Method != HttpMethod.Get && (
            request.Path.StartsWith("/api/v1/indexer/", StringComparison.Ordinal) && !request.Path.EndsWith("/testall", StringComparison.Ordinal)
            || request.Path.StartsWith("/api/v1/applications/", StringComparison.Ordinal) && !request.Path.EndsWith("/testall", StringComparison.Ordinal)
            || request.Path is "/api/v3/downloadclient/1" or "/api/v3/rootfolder" or "/Auth/Keys" or "/ScheduledTasks/Running/sync-task"));
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post && request.Path.Contains("/Plugins/", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post && request.Path.StartsWith("/Library/VirtualFolders", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, true, SetupReasonCodes.SonarrFailed)]
    [InlineData(true, false, SetupReasonCodes.RadarrFailed)]
    public async Task MixedArrHealth_PersistsDeterministicPhaseReasonAcrossRestartAndClearsWhenRepaired(
        bool sonarrReady,
        bool radarrReady,
        string expectedReason)
    {
        await using var prowlarrServer = ProwlarrLoopbackServer.Start();
        using var environment = CreateEnvironment($"mixed-arr-{expectedReason}", prowlarrServer.Url);
        await using var context = new DavDatabaseContext();
        await context.Database.MigrateAsync();
        var manager = new ConfigManager(new ConfigEncryptionService());
        await manager.LoadConfig();
        var persistence = new SetupConfigPersistence(manager, context);
        await persistence.SaveAsync(
            new SetupSecretValues
            {
                Indexers = "[{\"name\":\"indexer\",\"baseUrl\":\"https://indexer.example/api\",\"apiKey\":\"indexer-key\"}]",
                UsenetProviders = "{\"providers\":[{\"type\":\"Pooled\",\"host\":\"provider.local\",\"port\":563,\"useSsl\":true,\"user\":\"u\",\"pass\":\"p\",\"maxConnections\":1}]}",
                PluginApiKey = "plugin-key",
            },
            completed: true);

        var handler = new CompleteVerificationHandler
        {
            BackendApiKey = manager.GetApiKey(),
            SonarrReady = sonarrReady,
            RadarrReady = radarrReady,
        };
        var service = CreateService(context, manager, persistence, handler);
        InjectTestDiscovery(service);

        var drifted = await service.VerifyStatusAsync();

        Assert.True(drifted.RepairRequired);
        var arrStep = drifted.Steps.Single(step => step.Name == "arr-clients");
        Assert.Equal(SetupStepState.Warning, arrStep.State);
        Assert.Equal(expectedReason, arrStep.Code);
        Assert.Equal(sonarrReady, drifted.Services.Single(item => item.Name == "sonarr").Ready);
        Assert.Equal(radarrReady, drifted.Services.Single(item => item.Name == "radarr").Ready);

        var restartedManager = new ConfigManager(new ConfigEncryptionService());
        await restartedManager.LoadConfig();
        var restartedPersistence = new SetupConfigPersistence(restartedManager, context);
        var restartedGrant = new SetupGrantService(context, restartedManager, TimeProvider.System, () => new HttpClient(handler, disposeHandler: false), restartedPersistence);
        var restarted = new SetupOrchestrationService(
            restartedManager,
            restartedPersistence,
            restartedGrant,
            new DavDatabaseClient(context),
            () => new HttpClient(handler, disposeHandler: false),
            usenetCredentialValidator: new CompleteVerificationHandler.HealthyProviderValidator());
        InjectTestDiscovery(restarted);

        var persisted = await restarted.GetStatusAsync();
        var persistedArr = persisted.Steps.Single(step => step.Name == "arr-clients");
        Assert.Equal(SetupStepState.Warning, persistedArr.State);
        Assert.Equal(expectedReason, persistedArr.Code);
        Assert.True(persisted.RepairRequired);

        handler.SonarrReady = true;
        handler.RadarrReady = true;
        var repaired = await service.VerifyStatusAsync();

        Assert.False(repaired.RepairRequired, string.Join(", ", repaired.Services.Select(item => $"{item.Name}:{item.Ready}:{item.Reason}")));
        Assert.Equal(SetupStepState.Complete, repaired.Steps.Single(step => step.Name == "arr-clients").State);
        Assert.Null(repaired.Steps.Single(step => step.Name == "arr-clients").Code);
        Assert.True(repaired.Services.Single(item => item.Name == "sonarr").Ready);
        Assert.True(repaired.Services.Single(item => item.Name == "radarr").Ready);
    }

    [Fact]
    public async Task FreshCompletedScope_VerifiesEveryManagedServiceAndReleasesShortLease()
    {
        await using var prowlarrServer = ProwlarrLoopbackServer.Start();
        using var environment = CreateEnvironment("completed", prowlarrServer.Url);
        await using var context = new DavDatabaseContext();
        await context.Database.MigrateAsync();
        var manager = new ConfigManager(new ConfigEncryptionService());
        await manager.LoadConfig();
        var persistence = new SetupConfigPersistence(manager, context);
        await persistence.SaveAsync(
            new SetupSecretValues
            {
                Indexers = "[{\"name\":\"indexer\",\"baseUrl\":\"https://indexer.example/api\",\"apiKey\":\"indexer-key\"}]",
                UsenetProviders = "{\"providers\":[{\"type\":\"Pooled\",\"host\":\"provider.local\",\"port\":563,\"useSsl\":true,\"user\":\"u\",\"pass\":\"p\",\"maxConnections\":1}]}",
                PluginApiKey = "plugin-key",
            },
            completed: true);

        var handler = new CompleteVerificationHandler { BackendApiKey = manager.GetApiKey() };
        Assert.NotEmpty(handler.BackendApiKey);
        var service = CreateService(context, manager, persistence, handler);
        InjectTestDiscovery(service);
        Assert.Null(typeof(SetupOrchestrationService).GetField("_activeRunLease", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service));

        var first = await service.VerifyStatusAsync();

        Assert.True(first.Completed);
        Assert.False(first.RepairRequired);
        Assert.True(first.Services.All(serviceStatus => serviceStatus.Ready));
        Assert.All(first.Steps, step => Assert.Equal(SetupStepState.Complete, step.State));
        Assert.All(first.Steps.Where(step => step.Name != "arr-key-discovery"), step => Assert.True(step.ServiceReady));
        Assert.Equal(1, prowlarrServer.Count(HttpMethod.Post, "/api/v1/indexer/testall"));
        Assert.Equal(2, prowlarrServer.Count(HttpMethod.Post, "/api/v1/applications/testall"));

        var lease = await context.SetupRunLeases.AsNoTracking().SingleAsync();
        Assert.True(lease.LeaseUntilUtc <= DateTime.UtcNow.AddSeconds(1));

        var second = await service.VerifyStatusAsync();
        Assert.True(second.Completed);
        Assert.False(second.RepairRequired);
        Assert.All(second.Services, serviceStatus => Assert.True(serviceStatus.Ready));
        Assert.Equal(2, prowlarrServer.Count(HttpMethod.Post, "/api/v1/indexer/testall"));
        Assert.Equal(4, prowlarrServer.Count(HttpMethod.Post, "/api/v1/applications/testall"));
        lease = await context.SetupRunLeases.AsNoTracking().SingleAsync();
        Assert.True(lease.LeaseUntilUtc <= DateTime.UtcNow.AddSeconds(1));

        prowlarrServer.IndexerSchemaStatus = HttpStatusCode.BadGateway;
        var prowlarrFailure = await service.VerifyStatusAsync();
        Assert.True(prowlarrFailure.Completed);
        Assert.True(prowlarrFailure.RepairRequired);
        Assert.All(
            prowlarrFailure.Services.Where(serviceStatus => serviceStatus.Name != "prowlarr"),
            serviceStatus => Assert.True(serviceStatus.Ready));
        var failedProwlarr = Assert.Single(prowlarrFailure.Services.Where(serviceStatus => serviceStatus.Name == "prowlarr"));
        Assert.False(failedProwlarr.Ready);
        Assert.Equal(SetupReasonCodes.ProwlarrFailed, failedProwlarr.Reason);
        Assert.Equal(SetupReasonCodes.ProwlarrFailed,
            prowlarrFailure.Steps.Single(step => step.Name == "health-check").Code);
        prowlarrServer.IndexerSchemaStatus = HttpStatusCode.OK;

        handler.BlockArrRequests = true;
        using var cancellation = new CancellationTokenSource();
        var cancelledStatus = service.VerifyStatusAsync(cancellation.Token);
        await handler.ArrRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledStatus);
        lease = await context.SetupRunLeases.AsNoTracking().SingleAsync();
        Assert.True(lease.LeaseUntilUtc <= DateTime.UtcNow.AddSeconds(1));
    }

    private static SetupOrchestrationService CreateService(
        DavDatabaseContext context,
        ConfigManager manager,
        SetupConfigPersistence persistence,
        CompleteVerificationHandler handler,
        Func<NewznabIndexerCredential, CancellationToken, Task<NewznabCapabilityResult>>? indexerCapabilityValidator = null)
    {
        var grant = new SetupGrantService(context, manager, TimeProvider.System, () => new HttpClient(handler, disposeHandler: false), persistence);
        return new SetupOrchestrationService(
            manager,
            persistence,
            grant,
            new DavDatabaseClient(context),
            () => new HttpClient(handler, disposeHandler: false),
            usenetCredentialValidator: new CompleteVerificationHandler.HealthyProviderValidator(),
            indexerCapabilityValidator: indexerCapabilityValidator);
    }

    private static void InjectTestDiscovery(SetupOrchestrationService service)
    {
        var root = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"arr-bootstrap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var paths = new Dictionary<ArrService, string>
        {
            [ArrService.Sonarr] = WriteConfig(root, "sonarr", "sonarr-key"),
            [ArrService.Radarr] = WriteConfig(root, "radarr", "radarr-key"),
            [ArrService.Prowlarr] = WriteConfig(root, "prowlarr", "prowlarr-key"),
        };
        var discoveryOptions = new ArrConfigDiscoveryOptions(
            new ArrConfigDiscoveryPaths(paths[ArrService.Sonarr], paths[ArrService.Radarr], paths[ArrService.Prowlarr]))
        {
            TestOpenedFileFactory = serviceName => new FileStream(paths[serviceName], FileMode.Open, FileAccess.Read, FileShare.Read)
        };
        var discovery = new ArrApiKeyDiscovery(discoveryOptions);
        typeof(SetupOrchestrationService)
            .GetField("_discovery", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, discovery);
    }

    private static string WriteConfig(string root, string name, string key)
    {
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "config.xml");
        File.WriteAllText(path, $"<Config><ApiKey>{key}</ApiKey></Config>");
        return path;
    }

    private static backend.Tests.Config.TemporaryEnvironment CreateEnvironment(string name, string prowlarrUrl)
    {
        var configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-status-verification-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(configPath);
        return new backend.Tests.Config.TemporaryEnvironment(
            ("CONFIG_PATH", configPath),
            ("DATABASE_URL", null),
            ("NZBDAV_FULL_STACK", "true"),
            ("FRONTEND_BACKEND_API_KEY", "backend-api-key"),
            ("SETUP_NZBDAV_URL", "http://nzbdav:8080"),
            ("SETUP_SONARR_URL", "http://sonarr:8989"),
            ("SETUP_RADARR_URL", "http://radarr:7878"),
            ("SETUP_PROWLARR_URL", prowlarrUrl),
            ("SETUP_JELLYFIN_URL", "http://jellyfin:8096"),
            ("NZBDAV_MASTER_KEY", Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))));
    }

    private sealed class CompleteVerificationHandler : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path)> Requests { get; } = [];
        public List<string> ArrSchemaHosts { get; } = [];
        public string BackendApiKey { get; init; } = string.Empty;
        public bool SonarrReady { get; set; } = true;
        public bool RadarrReady { get; set; } = true;
        public bool BlockArrRequests { get; set; }
        public TaskCompletionSource ArrRequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Count(HttpMethod method, string path) => Requests.Count(request => request.Method == method && request.Path == path);
        public List<string> ArrBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add((request.Method, path));
            var host = request.RequestUri.Host;

            if (host is "sonarr" or "radarr")
            {
                if (path == "/api/v3/downloadclient/schema") ArrSchemaHosts.Add(host);
                if ((host == "sonarr" && !SonarrReady) || (host == "radarr" && !RadarrReady))
                    return new HttpResponseMessage(HttpStatusCode.BadGateway);
                if (BlockArrRequests)
                {
                    ArrRequestStarted.TrySetResult();
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                return ArrResponse(host, path);
            }
            if (host == "prowlarr")
                return ProwlarrResponse(path);
            if (host == "jellyfin")
                return JellyfinResponse(path);
            if (host == "nzbdav")
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private HttpResponseMessage ArrResponse(string host, string path)
        {
            var name = host == "sonarr" ? "NZBDAV Sonarr" : "NZBDAV Radarr";
            var root = host == "sonarr" ? "NZBDAV TV" : "NZBDAV Movies";
            var category = host == "sonarr" ? "tvCategory" : "movieCategory";
            var fields = new object[]
            {
                new { name = "host", value = "nzbdav" },
                new { name = "port", value = 8080 },
                new { name = "useSsl", value = false },
                new { name = "urlBase", value = "" },
                new { name = "apiKey", value = BackendApiKey },
                new { name = category, value = host == "sonarr" ? "tv" : "movies" },
            };
            return path switch
            {
                "/api/v3/" or "/api/v3/system/status" => Json(HttpStatusCode.OK, new { version = "4.0.0" }),
                "/api/v3/downloadclient/schema" => Json(HttpStatusCode.OK, new[] { new { implementation = "Sabnzbd", implementationName = "SABnzbd", configContract = "SabnzbdSettings", fields } }),
                "/api/v3/downloadclient" => Json(HttpStatusCode.OK, new[] { new { id = 1, name, implementation = "Sabnzbd", configContract = "SabnzbdSettings", protocol = "usenet", enable = true, fields } }),
                "/api/v3/downloadclient/testall" => Json(HttpStatusCode.OK, new[] { new { id = 1, isValid = true } }),
                // Pinned Servarr GET root-folder payloads have no display name;
                // the exact accessible path is the durable managed identity.
                "/api/v3/rootfolder" => Json(HttpStatusCode.OK, new[] { new { id = 1, path = host == "sonarr" ? "/data/completed-downloads/tv" : "/data/completed-downloads/movies", accessible = true } }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }

        private static HttpResponseMessage ProwlarrResponse(string path)
        {
            if (path == "/api/v1/indexer/schema")
                return Json(HttpStatusCode.OK, new[] { new
                {
                    implementation = "Newznab", implementationName = "Newznab", configContract = "NewznabSettings", name = "Generic Newznab",
                    fields = new object[]
                    {
                        new { name = "baseUrl", value = "" }, new { name = "apiPath", value = "api" }, new { name = "apiKey", value = "" },
                        new { name = "additionalParameters", value = "" }, new { name = "vipExpiration", value = "" },
                        new { name = "baseSettings.queryLimit", value = 0 }, new { name = "baseSettings.grabLimit", value = 0 }, new { name = "baseSettings.limitsUnit", value = "day" },
                    }
                }});
            if (path == "/api/v1/appprofile")
                return Json(HttpStatusCode.OK, new[] { new { id = 1, name = "Standard" } });
            if (path == "/api/v1/applications/schema")
                return Json(HttpStatusCode.OK, new[]
                {
                    new { implementation = "Sonarr", implementationName = "Sonarr", configContract = "SonarrSettings", fields = new object[] { new { name = "prowlarrUrl", value = "" }, new { name = "baseUrl", value = "" }, new { name = "apiKey", value = "" } } },
                    new { implementation = "Radarr", implementationName = "Radarr", configContract = "RadarrSettings", fields = new object[] { new { name = "prowlarrUrl", value = "" }, new { name = "baseUrl", value = "" }, new { name = "apiKey", value = "" } } },
                });
            if (path == "/api/v1/indexer")
                return Json(HttpStatusCode.OK, new[] { new
                {
                    id = 1, name = "indexer", implementation = "Newznab", implementationName = "Newznab", configContract = "NewznabSettings", appProfileId = 1, enable = true,
                    fields = new object[]
                    {
                        new { name = "baseUrl", value = "https://indexer.example/api" }, new { name = "apiPath", value = "api" }, new { name = "apiKey", value = "********" },
                        new { name = "additionalParameters", value = "" }, new { name = "vipExpiration", value = "" }, new { name = "baseSettings.queryLimit", value = 0 },
                        new { name = "baseSettings.grabLimit", value = 0 }, new { name = "baseSettings.limitsUnit", value = "day" },
                    }
                }});
            if (path == "/api/v1/applications")
                return Json(HttpStatusCode.OK, new[]
                {
                    Application(1, "NZBDAV Sonarr", "Sonarr", "http://sonarr:8989", "sonarr-key"),
                    Application(2, "NZBDAV Radarr", "Radarr", "http://radarr:7878", "radarr-key"),
                });
            if (path.EndsWith("/testall", StringComparison.Ordinal) && path.StartsWith("/api/v1/indexer/", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, new[] { new { id = 1, isValid = true } });
            if (path.EndsWith("/testall", StringComparison.Ordinal) && path.StartsWith("/api/v1/applications/", StringComparison.Ordinal))
            {
                var id = path.Contains("/2/", StringComparison.Ordinal) ? 2 : 1;
                return Json(HttpStatusCode.OK, new[] { new { id, isValid = true } });
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static object Application(int id, string name, string implementation, string baseUrl, string apiKey)
            => new
            {
                id, name, implementation, implementationName = implementation, configContract = implementation + "Settings", syncLevel = "fullSync",
                fields = new object[]
                {
                    new { name = "prowlarrUrl", value = "http://prowlarr:9696/" },
                    new { name = "baseUrl", value = baseUrl },
                    new { name = "apiKey", value = apiKey },
                }
            };

        private static HttpResponseMessage JellyfinResponse(string path)
        {
            return path switch
            {
                "/health" => Json(HttpStatusCode.OK, new { status = "Healthy" }),
                "/Auth/Keys" => Json(HttpStatusCode.OK, new { Items = new[] { new { AppName = "NZBDAV", AccessToken = "plugin-key" } } }),
                "/System/Info" => Json(HttpStatusCode.OK, new { Version = "10.11.8" }),
                "/Plugins" => Json(HttpStatusCode.OK, new[] { new { Id = "a1b2c3d4-e5f6-7890-abcd-ef1234567890", Name = "NZBDAV", Version = "1.0.0" } }),
                "/Plugins/a1b2c3d4-e5f6-7890-abcd-ef1234567890/Configuration" => Json(HttpStatusCode.OK, new { NzbdavUrl = "http://nzbdav:8080", NzbdavBaseUrl = "http://nzbdav:8080", ApiKey = "plugin-key", LibraryPath = "/media/nzbdav" }),
                "/Library/VirtualFolders" => Json(HttpStatusCode.OK, new object[]
                {
                    new { Name = "NZBDAV Movies", Locations = new[] { "/media/nzbdav/movies" }, CollectionType = "movies" },
                    new { Name = "NZBDAV TV", Locations = new[] { "/media/nzbdav/tv" }, CollectionType = "tvshows" },
                }),
                "/ScheduledTasks" => Json(HttpStatusCode.OK, new[] { new { Id = "sync-task", Key = "NzbdavLibrarySync" } }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }

        public sealed class HealthyProviderValidator : IUsenetCredentialValidator
    {
        public Task<UsenetCredentialValidationResult> ValidateAsync(
            UsenetProviderConfig.ConnectionDetails provider,
            CancellationToken cancellationToken = default)
            => Task.FromResult(UsenetCredentialValidationResult.Success);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object value)
            => new(status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }

    private sealed class ProwlarrLoopbackServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly Task _serveTask;
        private readonly List<(HttpMethod Method, string Path)> _requests = [];
        private int _applicationTests;

        private ProwlarrLoopbackServer(int port)
        {
            Url = $"http://127.0.0.1:{port}";
            _listener = new HttpListener();
            _listener.Prefixes.Add(Url + "/");
            _listener.Start();
            _serveTask = ServeAsync();
        }

        public string Url { get; }
        public IReadOnlyList<(HttpMethod Method, string Path)> Requests => _requests;
        public HttpStatusCode IndexerSchemaStatus { get; set; } = HttpStatusCode.OK;
        public int Count(HttpMethod method, string path) => _requests.Count(request => request.Method == method && request.Path == path);
        public static ProwlarrLoopbackServer Start()
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                try { return new ProwlarrLoopbackServer(Random.Shared.Next(20000, 45000)); }
                catch (HttpListenerException) { }
            }
            throw new InvalidOperationException("Could not bind a loopback Prowlarr test port.");
        }

        private async Task ServeAsync()
        {
            try
            {
                while (_listener.IsListening)
                {
                    var context = await _listener.GetContextAsync();
                    var request = context.Request;
                    var path = request.Url!.AbsolutePath;
                    var method = new HttpMethod(request.HttpMethod);
                    _requests.Add((method, path));
                    var response = Response(path, method);
                    context.Response.StatusCode = (int)response.StatusCode;
                    if (response.Content is not null)
                    {
                        var body = await response.Content.ReadAsByteArrayAsync();
                        context.Response.ContentType = "application/json";
                        context.Response.ContentLength64 = body.Length;
                        await context.Response.OutputStream.WriteAsync(body);
                    }
                    context.Response.Close();
                    response.Dispose();
                }
            }
            catch (HttpListenerException) when (!_listener.IsListening) { }
            catch (ObjectDisposedException) { }
        }

        private HttpResponseMessage Response(string path, HttpMethod method)
        {
            if (path == "/api/v1/indexer/schema")
            {
                if (IndexerSchemaStatus != HttpStatusCode.OK)
                    return new HttpResponseMessage(IndexerSchemaStatus);
                return Json(new[] { new
                {
                    implementation = "Newznab", implementationName = "Newznab", configContract = "NewznabSettings", name = "Generic Newznab",
                    fields = new object[]
                    {
                        new { name = "baseUrl", value = "" }, new { name = "apiPath", value = "api" }, new { name = "apiKey", value = "" },
                        new { name = "additionalParameters", value = "" }, new { name = "vipExpiration", value = "" }, new { name = "baseSettings.queryLimit", value = 0 },
                        new { name = "baseSettings.grabLimit", value = 0 }, new { name = "baseSettings.limitsUnit", value = "day" },
                    }
                }});
            }
            if (path == "/api/v1/appprofile")
                return Json(new[] { new { id = 1, name = "Standard" } });
            if (path == "/api/v1/applications/schema")
                return Json(new[]
                {
                    new { implementation = "Sonarr", implementationName = "Sonarr", configContract = "SonarrSettings", fields = new object[] { new { name = "prowlarrUrl", value = "" }, new { name = "baseUrl", value = "" }, new { name = "apiKey", value = "" } } },
                    new { implementation = "Radarr", implementationName = "Radarr", configContract = "RadarrSettings", fields = new object[] { new { name = "prowlarrUrl", value = "" }, new { name = "baseUrl", value = "" }, new { name = "apiKey", value = "" } } },
                });
            if (path == "/api/v1/indexer")
                return Json(new[] { new
                {
                    id = 1, name = "indexer", implementation = "Newznab", implementationName = "Newznab", configContract = "NewznabSettings", appProfileId = 1, enable = true,
                    fields = new object[]
                    {
                        new { name = "baseUrl", value = "https://indexer.example/api" }, new { name = "apiPath", value = "api" }, new { name = "apiKey", value = "********" },
                        new { name = "additionalParameters", value = "" }, new { name = "vipExpiration", value = "" }, new { name = "baseSettings.queryLimit", value = 0 },
                        new { name = "baseSettings.grabLimit", value = 0 }, new { name = "baseSettings.limitsUnit", value = "day" },
                    }
                }});
            if (path == "/api/v1/applications")
                return Json(new[]
                {
                    Application(1, "NZBDAV Sonarr", "Sonarr", "http://sonarr:8989", "sonarr-key"),
                    Application(2, "NZBDAV Radarr", "Radarr", "http://radarr:7878", "radarr-key"),
                });
            if (method == HttpMethod.Post && path.EndsWith("/testall", StringComparison.Ordinal) && path.StartsWith("/api/v1/indexer/", StringComparison.Ordinal))
                return Json(new[] { new { id = 1, isValid = true } });
            if (method == HttpMethod.Post && path.EndsWith("/testall", StringComparison.Ordinal) && path.StartsWith("/api/v1/applications/", StringComparison.Ordinal))
            {
                var id = (Interlocked.Increment(ref _applicationTests) - 1) % 2 + 1;
                return Json(new[] { new { id, isValid = true } });
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private object Application(int id, string name, string implementation, string baseUrl, string apiKey)
            => new
            {
                id, name, implementation, implementationName = implementation, configContract = implementation + "Settings", syncLevel = "fullSync",
                fields = new object[]
                {
                    new { name = "prowlarrUrl", value = Url + "/" }, new { name = "baseUrl", value = baseUrl }, new { name = "apiKey", value = apiKey },
                }
            };

        private static HttpResponseMessage Json(object value)
            => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            _listener.Close();
            await _serveTask;
        }
    }
}
