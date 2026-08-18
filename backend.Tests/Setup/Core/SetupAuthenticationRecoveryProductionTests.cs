using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Setup.Core;

namespace NzbWebDAV.Tests.Setup.Core;

[Collection(nameof(backend.Tests.Config.EnvironmentVariableCollection))]
public sealed class SetupAuthenticationRecoveryProductionTests
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ServiceTimeout = TimeSpan.FromMinutes(3);

    [Fact]
    public async Task IssueAsync_ServerCommittedTransportLoss_RetainsIntentAndPromotesTheRealSameDeviceSession()
    {
        await using var jellyfin = await JellyfinProductionFixture.StartAsync();
        using var environment = CreateEnvironment("issue-transport", jellyfin.BaseUri);
        var observed = new List<JellyfinProductionFixture.RealAuthentication>();
        var handler = new JellyfinProductionFixture.RealForwardingHandler(
            jellyfin.BaseUri, observed.Add, loseFirstAuthentication: true);

        string operationId;
        await using (var context = await CreateMigratedContextAsync())
        {
            var manager = await CreateManagerAsync();
            var service = CreateService(context, manager, handler);
            using var serviceTimeout = new CancellationTokenSource(ServiceTimeout);
            var failure = await Assert.ThrowsAsync<SetupAuthenticationRecoveryRequiredException>(
                () => service.IssueAsync(
                    JellyfinProductionFixture.Username,
                    JellyfinProductionFixture.Password,
                    serviceTimeout.Token));
            operationId = failure.OperationId;
            Assert.Equal(operationId, await ReadIntentAsync(context));
            Assert.Empty(await CandidateRowsAsync(context));
        }

        var old = Assert.Single(observed);
        Assert.NotEmpty(old.AccessToken);
        AssertNoBearerToken(old.SessionInfo);
        await AssertTokenProbeAsync(jellyfin, old.AccessToken, HttpStatusCode.OK);

        await using var restartedContext = await CreateMigratedContextAsync();
        var restartedManager = await CreateManagerAsync();
        var restarted = CreateService(restartedContext, restartedManager, handler);
        using var reissueTimeout = new CancellationTokenSource(ServiceTimeout);
        _ = await restarted.IssueAsync(
            JellyfinProductionFixture.Username,
            JellyfinProductionFixture.Password,
            reissueTimeout.Token);
        var replacement = Assert.Single(observed.Skip(1));

        Assert.Equal(old.DeviceId, replacement.DeviceId);
        Assert.Equal(replacement.AccessToken, restartedManager.GetSetupJellyfinApiKey());
        await AssertTokenProbeAsync(jellyfin, old.AccessToken, HttpStatusCode.Unauthorized);
        await AssertTokenProbeAsync(jellyfin, replacement.AccessToken, HttpStatusCode.OK);
        Assert.Null(await ReadIntentAsync(restartedContext));
        Assert.Empty(await CandidateRowsAsync(restartedContext));
        AssertNoBearerToken(replacement.SessionInfo);


        await restarted.RevokeAsync();
        await AssertTokenProbeAsync(jellyfin, old.AccessToken, HttpStatusCode.Unauthorized);
        await AssertTokenProbeAsync(jellyfin, replacement.AccessToken, HttpStatusCode.Unauthorized);
        Assert.Null(restartedManager.GetSetupJellyfinApiKey());
        Assert.Empty(await CandidateRowsAsync(restartedContext));
    }

    [Fact]
    public async Task IssueRepairAsync_ServerCommittedCancellation_RetainsIntentThenFreshRepairReauthCleansTheRealSessions()
    {
        await using var jellyfin = await JellyfinProductionFixture.StartAsync();
        using var environment = CreateEnvironment("repair-cancel", jellyfin.BaseUri);
        var observed = new List<JellyfinProductionFixture.RealAuthentication>();
        using var cancellation = new CancellationTokenSource();
        var handler = new JellyfinProductionFixture.RealForwardingHandler(
            jellyfin.BaseUri, observed.Add, cancelAfterFirstAuthentication: cancellation);

        await using (var context = await CreateMigratedContextAsync())
        {
            var manager = await CreateManagerAsync();
            var persistence = new SetupConfigPersistence(manager, context);
            await persistence.SaveAsync(new SetupSecretValues { Indexers = "[]", PluginApiKey = "plugin-key" }, completed: true);
            await persistence.SaveLiveHealthStateAsync(
                ready: false, checkedAtUtc: DateTime.UtcNow,
                sonarrReason: SetupReasonCodes.SonarrFailed, radarrReason: null,
                reasonsJson: "{}", CancellationToken.None);
            var service = CreateService(context, manager, handler, persistence);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.IssueRepairAsync(
                    JellyfinProductionFixture.Username,
                    JellyfinProductionFixture.Password,
                    cancellation.Token));
            Assert.NotNull(await ReadIntentAsync(context));
            Assert.Empty(await CandidateRowsAsync(context));
        }

        var old = Assert.Single(observed);
        AssertNoBearerToken(old.SessionInfo);
        await AssertTokenProbeAsync(jellyfin, old.AccessToken, HttpStatusCode.OK);

        await using var restartedContext = await CreateMigratedContextAsync();
        var restartedManager = await CreateManagerAsync();
        var restartedPersistence = new SetupConfigPersistence(restartedManager, restartedContext);
        var restarted = CreateService(restartedContext, restartedManager, handler, restartedPersistence);
        using var repairTimeout = new CancellationTokenSource(ServiceTimeout);
        var result = await restarted.IssueRepairAsync(
            JellyfinProductionFixture.Username,
            JellyfinProductionFixture.Password,
            repairTimeout.Token);
        var replacement = Assert.Single(observed.Skip(1));

        Assert.Equal(SetupGrantScope.Repair, result.Scope);
        Assert.Equal(replacement.AccessToken, await restarted.ReadRepairSessionAsync(result.Grant));
        Assert.Equal(old.DeviceId, replacement.DeviceId);
        Assert.Null(await ReadIntentAsync(restartedContext));
        Assert.Null(await restartedPersistence.ReadEmergencyCandidateAsync());
        await AssertTokenProbeAsync(jellyfin, old.AccessToken, HttpStatusCode.Unauthorized);
        await AssertTokenProbeAsync(jellyfin, replacement.AccessToken, HttpStatusCode.OK);

        Assert.True(await restarted.RecoverRepairAsync());
        Assert.False(await restarted.RecoverRepairAsync());
        Assert.Null(await restarted.ReadRepairSessionAsync(result.Grant));
        Assert.Null(await restartedPersistence.ReadEmergencyCandidateAsync());
        await AssertAllTokenProbesAsync(jellyfin, observed, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RecoverRevocation_WrongRetryPreservesOriginalIntentBeforeUsingANewerReservation()
    {
        await using var jellyfin = await JellyfinProductionFixture.StartAsync();
        using var environment = CreateEnvironment("authenticated-recovery", jellyfin.BaseUri);
        var observed = new List<JellyfinProductionFixture.RealAuthentication>();
        var newerIntent = $"issue:newer-{Guid.NewGuid():N}";
        var handler = new JellyfinProductionFixture.RealForwardingHandler(
            jellyfin.BaseUri,
            observed.Add,
            loseFirstAuthentication: true);

        string operationId;
        await using (var context = await CreateMigratedContextAsync())
        {
            var manager = await CreateManagerAsync();
            var service = CreateService(context, manager, handler);
            using var serviceTimeout = new CancellationTokenSource(ServiceTimeout);
            var failure = await Assert.ThrowsAsync<SetupAuthenticationRecoveryRequiredException>(
                () => service.IssueAsync(
                    JellyfinProductionFixture.Username,
                    JellyfinProductionFixture.Password,
                    serviceTimeout.Token));
            operationId = failure.OperationId;
            Assert.Equal(operationId, await ReadIntentAsync(context));
        }

        await using (var intentContext = await CreateMigratedContextAsync())
        {
            var fence = await intentContext.SetupMutationFences
                .SingleAsync(row => row.Id == SetupMutationFence.SingletonId);
            fence.ReservedCandidateOperationId = newerIntent;
            await intentContext.SaveChangesAsync();
            Assert.Equal(newerIntent, await ReadIntentAsync(intentContext));
            Assert.Equal(operationId, await ReadAuthenticationIntentAsync(intentContext));
        }

        var old = Assert.Single(observed);
        await AssertTokenProbeAsync(jellyfin, old.AccessToken, HttpStatusCode.OK);

        await using (var wrongContext = await CreateMigratedContextAsync())
        {
            var wrongManager = await CreateManagerAsync();
            var wrong = CreateService(wrongContext, wrongManager, handler);
            using var wrongRecoveryTimeout = new CancellationTokenSource(ServiceTimeout);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                wrong.RecoverRevocationAsync(
                    JellyfinProductionFixture.Username,
                    "wrong-password",
                    wrongRecoveryTimeout.Token));
            Assert.Equal(newerIntent, await ReadIntentAsync(wrongContext));
            Assert.Equal(operationId, await ReadAuthenticationIntentAsync(wrongContext));
            Assert.Empty(await CandidateRowsAsync(wrongContext));
            await AssertTokenProbeAsync(jellyfin, old.AccessToken, HttpStatusCode.OK);
        }

        await using var recoveryContext = await CreateMigratedContextAsync();
        var recoveryManager = await CreateManagerAsync();
        var recoveryPersistence = new SetupConfigPersistence(recoveryManager, recoveryContext);
        var recovery = CreateService(recoveryContext, recoveryManager, handler, recoveryPersistence);
        using var recoverTimeout = new CancellationTokenSource(ServiceTimeout);
        Assert.True(await recovery.RecoverRevocationAsync(
            JellyfinProductionFixture.Username,
            JellyfinProductionFixture.Password,
            recoverTimeout.Token));
        var replacement = Assert.Single(observed.Skip(1));

        Assert.Equal(old.DeviceId, replacement.DeviceId);
        Assert.Empty(await CandidateRowsAsync(recoveryContext));
        Assert.Equal(newerIntent, await ReadIntentAsync(recoveryContext));
        Assert.Null(await ReadAuthenticationIntentAsync(recoveryContext));
        await AssertTokenProbeAsync(jellyfin, replacement.AccessToken, HttpStatusCode.Unauthorized);
        await AssertTokenProbeAsync(jellyfin, old.AccessToken, HttpStatusCode.Unauthorized);
    }

    private static SetupGrantService CreateService(
        DavDatabaseContext context,
        ConfigManager manager,
        HttpMessageHandler handler,
        SetupConfigPersistence? persistence = null)
        => new(context, manager, TimeProvider.System,
            () => new HttpClient(handler, disposeHandler: false), persistence);

    private static async Task<ConfigManager> CreateManagerAsync()
    {
        var manager = new ConfigManager(new ConfigEncryptionService());
        await manager.LoadConfig();
        return manager;
    }

    private static async Task<DavDatabaseContext> CreateMigratedContextAsync()
    {
        var context = new DavDatabaseContext();
        await context.Database.MigrateAsync();
        return context;
    }

    private static backend.Tests.Config.TemporaryEnvironment CreateEnvironment(string name, Uri jellyfin)
    {
        var configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-auth-recovery-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(configPath);
        return new backend.Tests.Config.TemporaryEnvironment(
            ("CONFIG_PATH", configPath),
            ("DATABASE_URL", null),
            ("NZBDAV_FULL_STACK", "true"),
            ("SETUP_JELLYFIN_URL", jellyfin.ToString().TrimEnd('/')),
            ("NZBDAV_MASTER_KEY", Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))),
            ("NZBDAV_MASTER_KEY_OLD", null));
    }

    private static Task<string?> ReadIntentAsync(DavDatabaseContext context)
        => context.SetupMutationFences.AsNoTracking()
            .Where(row => row.Id == SetupMutationFence.SingletonId)
            .Select(row => row.ReservedCandidateOperationId)
            .SingleAsync();

    private static async Task<string?> ReadAuthenticationIntentAsync(DavDatabaseContext context)
    {
        var manager = new ConfigManager(new ConfigEncryptionService());
        var persistence = new SetupConfigPersistence(manager, context);
        return await persistence.ReadAuthenticationIntentAsync(CancellationToken.None);
    }

    private static Task<List<ConfigItem>> CandidateRowsAsync(DavDatabaseContext context)
        => context.ConfigItems.AsNoTracking()
            .Where(row => row.ConfigName == SetupConfigKeys.CandidateSessionToken
                       || row.ConfigName == SetupConfigKeys.CandidateSessionOperation)
            .ToListAsync();


    private static async Task AssertTokenProbeAsync(JellyfinProductionFixture fixture, string accessToken, HttpStatusCode expected)
    {
        using var timeout = new CancellationTokenSource(ProbeTimeout);
        Assert.Equal(expected, (await fixture.ProbeTokenAsync(accessToken, timeout.Token)).StatusCode);
    }

    private static async Task AssertAllTokenProbesAsync(
        JellyfinProductionFixture fixture,
        IEnumerable<JellyfinProductionFixture.RealAuthentication> tokens,
        HttpStatusCode expected)
    {
        foreach (var token in tokens)
            await AssertTokenProbeAsync(fixture, token.AccessToken, expected);
    }

    private static void AssertNoBearerToken(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                Assert.False(string.Equals(property.Name, "AccessToken", StringComparison.OrdinalIgnoreCase),
                    "Jellyfin session metadata must not be treated as a bearer-token source.");
                AssertNoBearerToken(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                AssertNoBearerToken(item);
        }
    }
}
