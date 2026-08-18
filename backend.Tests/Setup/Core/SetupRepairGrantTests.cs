using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.JellyfinSetup;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Setup.Core;

namespace NzbWebDAV.Tests.Setup.Core;

[Collection(nameof(backend.Tests.Config.EnvironmentVariableCollection))]
public sealed class SetupRepairGrantTests
{
    [Fact]
    public async Task RepairGrantIsNarrowlyScoped_AndCleanupFailureRetainsRepairStateForRestart()
    {
        var configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"repair-grant-{Guid.NewGuid():N}");
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("CONFIG_PATH", configPath),
            ("DATABASE_URL", null),
            ("NZBDAV_FULL_STACK", "true"),
            ("SETUP_JELLYFIN_URL", "http://jellyfin.test"),
            ("NZBDAV_MASTER_KEY", Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))));
        Directory.CreateDirectory(configPath);

        await using var context = new DavDatabaseContext();
        await context.Database.MigrateAsync();
        var manager = new ConfigManager(new ConfigEncryptionService());
        await manager.LoadConfig();
        var persistence = new SetupConfigPersistence(manager, context);
        await persistence.SaveAsync(new SetupSecretValues
        {
            Indexers = "[{\"name\":\"stored\"}]",
            PluginApiKey = "plugin-key",
        }, completed: true);
        await persistence.SaveLiveHealthStateAsync(
            ready: false,
            checkedAtUtc: DateTime.UtcNow,
            sonarrReason: SetupReasonCodes.SonarrFailed,
            radarrReason: null,
            reasonsJson: "{}",
            CancellationToken.None);

        var handler = new RepairHandler();
        var service = new SetupGrantService(
            context,
            manager,
            TimeProvider.System,
            () => new HttpClient(handler, disposeHandler: false),
            persistence);

        var issued = await service.IssueRepairAsync("admin", "password");
        Assert.Equal(SetupGrantScope.Repair, issued.Scope);
        Assert.Equal(SetupGrantScope.Repair, await service.ValidateScopeAsync(issued.Grant));
        Assert.False(await service.ValidateAsync(issued.Grant));
        Assert.True(await service.ValidateAnyAsync(issued.Grant));
        Assert.True(manager.IsSetupCompleted());
        Assert.True(manager.IsSetupRepairRequired());
        Assert.Equal("[{\"name\":\"stored\"}]", manager.GetSetupIndexers());

        handler.FailLogout = true;
        await Assert.ThrowsAsync<JellyfinSetupException>(() => service.CompleteRepairAsync(issued.Grant));
        Assert.True(manager.IsSetupCompleted());
        Assert.True(manager.IsSetupRepairRequired());
        var pending = await context.SetupGrants.AsNoTracking().SingleAsync();
        Assert.False(pending.IsRevoked);
        Assert.NotNull(pending.RepairSessionCiphertext);

        // A fresh service can retry the exact encrypted repair session; no
        // marker deletion or normal setup grant is required.
        handler.FailLogout = false;
        await using var restartedContext = new DavDatabaseContext();
        var restartedManager = new ConfigManager(new ConfigEncryptionService());
        await restartedManager.LoadConfig();
        var restartedPersistence = new SetupConfigPersistence(restartedManager, restartedContext);
        var restarted = new SetupGrantService(
            restartedContext,
            restartedManager,
            TimeProvider.System,
            () => new HttpClient(handler, disposeHandler: false),
            restartedPersistence);
        Assert.True(await restarted.RecoverRepairAsync());
        var repairedGrant = await restartedContext.SetupGrants.AsNoTracking().SingleAsync();
        Assert.True(repairedGrant.IsRevoked);
        Assert.Null(repairedGrant.RepairSessionCiphertext);
        Assert.True(restartedManager.IsSetupCompleted());
        Assert.True(restartedManager.IsSetupRepairRequired());
        Assert.Contains("repair-session", handler.LogoutTokens);
    }

    private sealed class RepairHandler : HttpMessageHandler
    {
        public bool FailLogout { get; set; }
        public List<string> LogoutTokens { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/Users/AuthenticateByName")
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

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/Sessions/Logout")
            {
                var token = request.Headers.GetValues("X-Emby-Token").Single();
                LogoutTokens.Add(token);
                return Task.FromResult(new HttpResponseMessage(
                    FailLogout ? HttpStatusCode.InternalServerError : HttpStatusCode.NoContent));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
