using System.Net;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Services;
using NzbWebDAV.Setup.Core;

namespace NzbWebDAV.Tests.Setup.Core;

[Collection(nameof(backend.Tests.Config.EnvironmentVariableCollection))]
public sealed class SetupCompletionOwnershipTests
{
    [Fact]
    public async Task PhaseA_TransfersCandidateAndFreshRecoveryUsesOnlyImmutablePlan()
    {
        var configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"completion-ownership-{Guid.NewGuid():N}");
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("CONFIG_PATH", configPath),
            ("DATABASE_URL", null),
            ("NZBDAV_FULL_STACK", "true"),
            ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))),
            ("SETUP_JELLYFIN_URL", "http://jellyfin.test"));
        Directory.CreateDirectory(configPath);

        await using var firstContext = new DavDatabaseContext();
        await firstContext.Database.MigrateAsync();
        var firstManager = new ConfigManager(new ConfigEncryptionService());
        await firstManager.LoadConfig();
        var firstPersistence = new SetupConfigPersistence(firstManager, firstContext);
        await firstPersistence.SaveAsync(new SetupSecretValues
        {
            Indexers = "[]",
            PluginApiKey = "plugin-key",
            JellyfinApiKey = "active-session",
        }, completed: false);
        await firstPersistence.SaveCandidateUnderMutationGateAsync("candidate-session", "issue:old");

        var first = new SetupGrantService(firstContext, firstManager, TimeProvider.System, () => new HttpClient(new LogoutHandler()));
        _ = await first.PrepareCompletionLogoutUnderMutationGateAsync(CancellationToken.None);

        Assert.False(await firstPersistence.ClearCandidateUnderMutationGateAsync(
            "candidate-session", "issue:old"));
        Assert.Empty(await firstContext.ConfigItems.Where(row =>
            row.ConfigName == SetupConfigKeys.CandidateSessionToken
            || row.ConfigName == SetupConfigKeys.CandidateSessionOperation).ToListAsync());

        await using var secondContext = new DavDatabaseContext();
        var secondManager = new ConfigManager(new ConfigEncryptionService());
        await secondManager.LoadConfig();
        var second = new SetupGrantService(
            secondContext,
            secondManager,
            TimeProvider.System,
            () => new HttpClient(new LogoutHandler()));

        Assert.True(await second.RecoverPendingAsync());
        Assert.True(await secondContext.ConfigItems.AnyAsync(row =>
            row.ConfigName == SetupConfigKeys.Completed && row.ConfigValue == "true"));
        Assert.False(await secondContext.SetupCompletionOperations.AnyAsync());
        Assert.Empty(await secondContext.ConfigItems.Where(row =>
            row.ConfigName == SetupConfigKeys.CandidateSessionToken
            || row.ConfigName == SetupConfigKeys.CandidateSessionOperation
            || row.ConfigName == SetupConfigKeys.JellyfinApiKey).ToListAsync());
    }

    private sealed class LogoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
    }
}
