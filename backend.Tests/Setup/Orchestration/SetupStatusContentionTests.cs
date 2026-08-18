using System.Net;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Services;
using NzbWebDAV.Setup.Core;
using NzbWebDAV.Setup.Orchestration;

namespace NzbWebDAV.Tests.Setup.Orchestration;

[Collection(nameof(backend.Tests.Config.EnvironmentVariableCollection))]
public sealed class SetupStatusContentionTests
{
    [Fact]
    public async Task CompletedStatus_WhenAnotherRunLeaseIsHeld_ReturnsExactBusyServicesWithoutRepairLatchOrTestall()
    {
        using var environment = CreateEnvironment();
        await using var context = new DavDatabaseContext();
        await context.Database.MigrateAsync();
        var manager = new ConfigManager(new ConfigEncryptionService());
        await manager.LoadConfig();
        var persistence = new SetupConfigPersistence(manager, context);
        await persistence.SaveAsync(new SetupSecretValues
        {
            Indexers = "[]",
            UsenetProviders = "{\"providers\":[{\"type\":\"Pooled\",\"host\":\"provider.local\",\"port\":563,\"useSsl\":true,\"user\":\"u\",\"pass\":\"p\",\"maxConnections\":1}]}",
            PluginApiKey = "plugin-key",
        }, completed: true);

        await using var ownerContext = new DavDatabaseContext();
        var owner = new SetupRunLeaseService(ownerContext, ownerId: "other-worker");
        var held = await owner.TryAcquireAsync(string.Empty, "setup");
        Assert.NotNull(held);

        var handler = new ThrowIfCalledHandler();
        var grant = new SetupGrantService(context, manager, TimeProvider.System,
            () => new HttpClient(handler, disposeHandler: false), persistence);
        var service = new SetupOrchestrationService(
            manager, persistence, grant, new DavDatabaseClient(context),
            () => new HttpClient(handler, disposeHandler: false));

        var status = await service.GetStatusAsync();

        Assert.True(status.Completed);
        Assert.False(status.RepairRequired);
        Assert.Equal(["nzbdav", "jellyfin", "sonarr", "radarr", "prowlarr"], status.Services.Select(item => item.Name));
        Assert.All(status.Services, item =>
        {
            Assert.False(item.Ready);
            Assert.Equal(SetupReasonCodes.SetupRunBusy, item.Reason);
            Assert.Equal(SetupReasonCodes.SetupRunBusy, item.Code);
        });
        Assert.All(status.Steps.Where(step => step.Name is not "arr-key-discovery" and not "health-check"), step => Assert.Equal(SetupReasonCodes.SetupRunBusy, step.Code));
        Assert.Equal(0, handler.Calls);
        Assert.False(await context.ConfigItems.AnyAsync(row => row.ConfigName == SetupConfigKeys.RepairRequired));
        Assert.True(await held.AssertCurrentAsync());
        await held.ReleaseAsync();
    }

    private static backend.Tests.Config.TemporaryEnvironment CreateEnvironment()
    {
        var path = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-status-contention-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return new backend.Tests.Config.TemporaryEnvironment(
            ("CONFIG_PATH", path),
            ("DATABASE_URL", null),
            ("NZBDAV_FULL_STACK", "true"),
            ("SETUP_JELLYFIN_URL", "http://jellyfin.test"),
            ("NZBDAV_MASTER_KEY", Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))));
    }

    private sealed class ThrowIfCalledHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("status contention must not issue upstream testall requests");
        }
    }
}
