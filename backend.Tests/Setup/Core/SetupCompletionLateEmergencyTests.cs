using System.Collections.Concurrent;
using System.Data;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Services;
using NzbWebDAV.Setup.Core;

namespace NzbWebDAV.Tests.Setup.Core;

[Collection(nameof(backend.Tests.Config.EnvironmentVariableCollection))]
public sealed class SetupCompletionLateEmergencyTests
{
    [Fact]
    public async Task PhaseAThenLateEmergency_PhaseCDoesNotOwnIt_AndFreshRestartLogsItOnce()
    {
        var configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"late-emergency-{Guid.NewGuid():N}");
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("CONFIG_PATH", configPath),
            ("DATABASE_URL", null),
            ("NZBDAV_FULL_STACK", "true"),
            ("SETUP_JELLYFIN_URL", "http://jellyfin.test"),
            ("NZBDAV_MASTER_KEY", Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))));
        Directory.CreateDirectory(configPath);

        await using var ownerContext = new DavDatabaseContext();
        await ownerContext.Database.MigrateAsync();
        var ownerManager = new ConfigManager(new ConfigEncryptionService());
        await ownerManager.LoadConfig();
        var ownerPersistence = new SetupConfigPersistence(ownerManager, ownerContext);
        await ownerPersistence.SaveAsync(new SetupSecretValues
        {
            Indexers = "[]",
            PluginApiKey = "plugin-key",
            JellyfinApiKey = "phase-a-session",
        }, completed: false);

        var logoutHandler = new CountingLogoutHandler();
        var phaseCStarted = NewSignal();
        var allowPhaseC = NewSignal();
        var raceArmed = false;
        async Task<IDbContextTransaction> BeginPhaseCTransactionAsync(
            IsolationLevel isolation,
            CancellationToken cancellationToken)
        {
            var transaction = await ownerContext.Database.BeginTransactionAsync(isolation, cancellationToken);
            if (raceArmed)
            {
                phaseCStarted.TrySetResult();
                await allowPhaseC.Task.WaitAsync(cancellationToken);
            }

            return transaction;
        }

        var owner = new SetupGrantService(
            ownerContext,
            ownerManager,
            TimeProvider.System,
            () => new HttpClient(logoutHandler, disposeHandler: false),
            ownerPersistence,
            null,
            BeginPhaseCTransactionAsync);
        var plan = await owner.PrepareCompletionLogoutUnderMutationGateAsync(CancellationToken.None);
        Assert.Null(plan.EmergencySession);
        raceArmed = true;

        await using var issuerContext = new DavDatabaseContext();
        var issuerManager = new ConfigManager(new ConfigEncryptionService());
        await issuerManager.LoadConfig();
        var issuerPersistence = new SetupConfigPersistence(issuerManager, issuerContext);
        await owner.LogoutCompletionSessionsAsync(
            plan,
            SetupEnvironmentOptions.FromEnvironment().JellyfinUrl,
            CancellationToken.None);

        var completeTask = owner.CompleteAfterExternalLogoutUnderMutationGateAsync(
            plan,
            "[]",
            "plugin-key",
            null,
            null,
            CancellationToken.None);
        await phaseCStarted.Task;
        await issuerPersistence.SaveEmergencyCandidateUnderMutationGateAsync(
            "late-session",
            "issue:late",
            CancellationToken.None);
        allowPhaseC.TrySetResult();
        await completeTask;

        await using var restartContext = new DavDatabaseContext();
        var restartManager = new ConfigManager(new ConfigEncryptionService());
        await restartManager.LoadConfig();
        var restartPersistence = new SetupConfigPersistence(restartManager, restartContext);
        var restarted = new SetupGrantService(
            restartContext,
            restartManager,
            TimeProvider.System,
            () => new HttpClient(logoutHandler, disposeHandler: false),
            restartPersistence);

        Assert.True(await restarted.RecoverPendingAsync());
        Assert.False(await restarted.RecoverPendingAsync());
        Assert.Equal(["phase-a-session", "late-session"], logoutHandler.LogoutTokens.ToArray());
        Assert.Null(await restartPersistence.ReadEmergencyCandidateAsync(CancellationToken.None));

        await using var verify = new DavDatabaseContext();
        Assert.Equal(
            1,
            await verify.ConfigItems.CountAsync(row =>
                row.ConfigName == SetupConfigKeys.Completed && row.ConfigValue == "true"));
        Assert.False(await verify.SetupCompletionOperations.AnyAsync());
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class CountingLogoutHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<string> _tokens = new();
        public IReadOnlyCollection<string> LogoutTokens => _tokens.ToArray();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/Sessions/Logout")
            {
                _tokens.Enqueue(request.Headers.GetValues("X-Emby-Token").Single());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
