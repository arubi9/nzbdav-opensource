using System.Collections.Concurrent;
using System.Data;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Setup.Core;
using NzbWebDAV.Tests.Clients.Usenet.Caching;

namespace NzbWebDAV.Tests.Setup.Core;

[Collection(nameof(backend.Tests.Config.EnvironmentVariableCollection))]
public sealed class SetupCompletionFencingPostgresTests : IClassFixture<PostgresHeaderCacheFixture>
{
    private readonly PostgresHeaderCacheFixture _fixture;

    public SetupCompletionFencingPostgresTests(PostgresHeaderCacheFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task PhaseA_PostgresFenceBeatsOlderCandidateClearAfterTransferBegins()
    {
        Assert.SkipUnless(_fixture.IsAvailable, "Docker is required for PostgreSQL completion fencing tests.");
        await _fixture.ResetAsync();
        using var environment = CreateEnvironment("candidate-fence", CreateKey());

        await using var seed = await CreateProcessAsync();
        await SeedSetupAsync(seed, activeSession: "active-fence", candidateSession: "candidate-fence", candidateOperation: "issue:old");

        var cleanupStarted = NewSignal();
        var allowCleanup = NewSignal();
        await using var owner = await CreateProcessAsync();
        await using var stale = await CreateProcessAsync(
            persistenceBeginTransaction: async (context, isolation, cancellationToken) =>
            {
                var transaction = await context.Database.BeginTransactionAsync(isolation, cancellationToken);
                cleanupStarted.TrySetResult();
                await allowCleanup.Task.WaitAsync(cancellationToken);
                return transaction;
            });

        var clearTask = stale.Persistence.ClearCandidateUnderMutationGateAsync(
            "candidate-fence", "issue:old", CancellationToken.None);
        await cleanupStarted.Task;

        // The stale cleanup transaction exists, but is held before it can take
        // the database fence. Phase A is therefore guaranteed to transfer first
        // while both independent contexts are live.
        var plan = await owner.Service.PrepareCompletionLogoutUnderMutationGateAsync(CancellationToken.None);
        Assert.Equal("candidate-fence", plan.CandidateSession);
        Assert.Equal("issue:old", plan.CandidateOperation);

        allowCleanup.TrySetResult();
        Assert.False(await clearTask);

        await using var verify = new DavDatabaseContext();
        Assert.True(await verify.SetupCompletionOperations.AnyAsync());
        Assert.Empty(await verify.ConfigItems.Where(row =>
            row.ConfigName == SetupConfigKeys.CandidateSessionToken
            || row.ConfigName == SetupConfigKeys.CandidateSessionOperation).ToListAsync());
        var operation = await verify.SetupCompletionOperations.SingleAsync();
        Assert.NotNull(operation.CandidateSessionCiphertext);
        Assert.NotNull(operation.CandidateOperationCiphertext);
    }

    [Fact]
    public async Task PhaseA_PostgresFreshRestartLogsOutAndCompletesExactlyOnce()
    {
        Assert.SkipUnless(_fixture.IsAvailable, "Docker is required for PostgreSQL completion restart tests.");
        await _fixture.ResetAsync();
        using var environment = CreateEnvironment("fresh-restart", CreateKey());
        var handler = new CountingLogoutHandler();

        await using (var seed = await CreateProcessAsync())
        {
            await SeedSetupAsync(
                seed,
                activeSession: "active-restart",
                revocationSession: "previous-restart",
                candidateSession: "candidate-restart",
                candidateOperation: "issue:restart");
            await seed.Persistence.SaveEmergencyCandidateUnderMutationGateAsync(
                "emergency-restart", "issue:emergency", CancellationToken.None);
        }

        SetupCompletionPlan plan;
        await using (var phaseA = await CreateProcessAsync())
        {
            plan = await phaseA.Service.PrepareCompletionLogoutUnderMutationGateAsync(CancellationToken.None);
        }

        Assert.Equal(
            ["active-restart", "previous-restart", "candidate-restart", "emergency-restart"],
            plan.Sessions.Where(token => token is not null).Cast<string>().ToArray());

        await using var restarted = await CreateProcessAsync(handler);
        var recovered = await restarted.Service.RecoverPendingUnderMutationGateAsync(
            null,
            SetupEnvironmentOptions.FromEnvironment().JellyfinUrl,
            CancellationToken.None);
        var replay = await restarted.Service.RecoverPendingUnderMutationGateAsync(
            null,
            SetupEnvironmentOptions.FromEnvironment().JellyfinUrl,
            CancellationToken.None);

        Assert.True(recovered);
        Assert.False(replay);
        Assert.Equal(
            ["active-restart", "previous-restart", "candidate-restart", "emergency-restart"],
            handler.LogoutTokens.ToArray());

        await using var verify = new DavDatabaseContext();
        var setupRows = await verify.ConfigItems
            .Where(row => row.ConfigName == SetupConfigKeys.Completed
                       || row.ConfigName == SetupConfigKeys.JellyfinApiKey
                       || row.ConfigName == SetupConfigKeys.RevocationPending
                       || row.ConfigName == SetupConfigKeys.RevocationPendingToken
                       || row.ConfigName == SetupConfigKeys.CandidateSessionToken
                       || row.ConfigName == SetupConfigKeys.CandidateSessionOperation)
            .ToListAsync();
        var marker = Assert.Single(setupRows, row => row.ConfigName == SetupConfigKeys.Completed);
        Assert.Equal("true", marker.ConfigValue);
        Assert.DoesNotContain(setupRows, row => row.ConfigName != SetupConfigKeys.Completed);
        Assert.False(await verify.SetupCompletionOperations.AnyAsync());
        Assert.Null(await restarted.Persistence.ReadEmergencyCandidateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PhaseC_PostgresCrossProcessRaceHasOneWinnerAndRecoveryConverges()
    {
        Assert.SkipUnless(_fixture.IsAvailable, "Docker is required for PostgreSQL completion race tests.");
        await _fixture.ResetAsync();
        using var environment = CreateEnvironment("phase-c-race", CreateKey());
        var handler = new CountingLogoutHandler();

        await using (var seed = await CreateProcessAsync())
            await SeedSetupAsync(seed, activeSession: "active-race");

        var bothTransactionsStarted = NewSignal();
        var releaseTransactions = NewSignal();
        var raceArmed = NewSignal();
        var started = 0;
        async Task<IDbContextTransaction> BeginRacingTransactionAsync(
            DavDatabaseContext context,
            IsolationLevel isolation,
            CancellationToken cancellationToken)
        {
            var transaction = await context.Database.BeginTransactionAsync(isolation, cancellationToken);
            if (!raceArmed.Task.IsCompleted)
                return transaction;

            if (Interlocked.Increment(ref started) == 2)
                bothTransactionsStarted.TrySetResult();
            await releaseTransactions.Task.WaitAsync(cancellationToken);
            return transaction;
        }

        DavDatabaseContext? phaseAContext = null;
        await using var phaseA = await CreateProcessAsync(
            handler,
            serviceBeginTransaction: (isolation, cancellationToken) =>
                BeginRacingTransactionAsync(phaseAContext!, isolation, cancellationToken));
        phaseAContext = phaseA.Context;
        var plan = await phaseA.Service.PrepareCompletionLogoutUnderMutationGateAsync(CancellationToken.None);

        DavDatabaseContext? racerContext = null;
        await using var racer = await CreateProcessAsync(
            handler,
            serviceBeginTransaction: (isolation, cancellationToken) =>
                BeginRacingTransactionAsync(racerContext!, isolation, cancellationToken));
        racerContext = racer.Context;

        await phaseA.Service.LogoutCompletionSessionsAsync(
            plan,
            SetupEnvironmentOptions.FromEnvironment().JellyfinUrl,
            CancellationToken.None);

        // Both Phase C transactions are created before either is allowed to
        // acquire the production database fence. PostgreSQL may report a
        // serialization abort to the loser; restart recovery must then
        // converge against the winner's marker without a second logout.
        raceArmed.TrySetResult();
        var c1 = CaptureAsync(() => phaseA.Service.CompleteAfterExternalLogoutUnderMutationGateAsync(
            plan, "[]", "plugin-race", null, null, CancellationToken.None));
        var c2 = CaptureAsync(() => racer.Service.CompleteAfterExternalLogoutUnderMutationGateAsync(
            plan, "[]", "plugin-race", null, null, CancellationToken.None));
        await bothTransactionsStarted.Task;
        releaseTransactions.TrySetResult();

        var raceFailures = await Task.WhenAll(c1, c2);
        Assert.InRange(raceFailures.Count(exception => exception is null), 1, 2);
        Assert.All(
            raceFailures.Where(exception => exception is not null),
            exception => Assert.Equal("40001", Assert.IsType<PostgresException>(exception).SqlState));
        Assert.False(await racer.Service.RecoverPendingUnderMutationGateAsync(
            null,
            SetupEnvironmentOptions.FromEnvironment().JellyfinUrl,
            CancellationToken.None));
        Assert.Equal(["active-race"], handler.LogoutTokens.ToArray());

        await using var verify = new DavDatabaseContext();
        Assert.False(await verify.SetupCompletionOperations.AnyAsync());
        Assert.Equal(
            1,
            await verify.ConfigItems.CountAsync(row =>
                row.ConfigName == SetupConfigKeys.Completed && row.ConfigValue == "true"));

    }

    [Fact]
    public async Task CompletionPostgresCommitOutcomesKeepOwnershipAndUseProductionContract()
    {
        Assert.SkipUnless(_fixture.IsAvailable, "Docker is required for PostgreSQL completion outcome tests.");
        await _fixture.ResetAsync();
        using var environment = CreateEnvironment("commit-outcomes", CreateKey());
        await using (var seed = await CreateProcessAsync())
            await SeedSetupAsync(seed, activeSession: "active-ack", candidateSession: "candidate-ack", candidateOperation: "issue:ack");

        SetupCompletionPlan plan;
        await using (var phaseA = await CreateProcessAsync())
            plan = await phaseA.Service.PrepareCompletionLogoutUnderMutationGateAsync(CancellationToken.None);

        await using (var acknowledgementLoss = await CreateProcessAsync(
                         serviceCommitFault: CommitFault.AcknowledgementLostAfterCommit))
        {
            var exception = await Assert.ThrowsAsync<SetupTransactionRecoveryRequiredException>(() =>
                acknowledgementLoss.Service.CompleteAfterExternalLogoutUnderMutationGateAsync(
                    plan, "[]", "plugin-ack", null, null, CancellationToken.None));
            Assert.IsType<IOException>(exception.InnerException);
        }

        await using (var verifyAcknowledgedCommit = new DavDatabaseContext())
        {
            Assert.False(await verifyAcknowledgedCommit.SetupCompletionOperations.AnyAsync());
            Assert.Equal(
                1,
                await verifyAcknowledgedCommit.ConfigItems.CountAsync(row =>
                    row.ConfigName == SetupConfigKeys.Completed && row.ConfigValue == "true"));
            Assert.Empty(await verifyAcknowledgedCommit.ConfigItems.Where(row =>
                row.ConfigName == SetupConfigKeys.CandidateSessionToken
                || row.ConfigName == SetupConfigKeys.CandidateSessionOperation).ToListAsync());
        }

        foreach (var sqlState in new[] { "40001", "40P01" })
        {
            await _fixture.ResetAsync();
            await using (var sqlSeed = await CreateProcessAsync())
                await SeedSetupAsync(sqlSeed, activeSession: $"active-{sqlState}", candidateSession: $"candidate-{sqlState}", candidateOperation: $"issue:{sqlState}");

            SetupCompletionPlan sqlPlan;
            await using (var sqlPhaseA = await CreateProcessAsync())
                sqlPlan = await sqlPhaseA.Service.PrepareCompletionLogoutUnderMutationGateAsync(CancellationToken.None);

            var beginCalls = 0;
            await using var retryingPersistenceProcess = await CreateProcessAsync(
                persistenceBeginTransaction: async (context, isolation, cancellationToken) =>
                {
                    var transaction = await context.Database.BeginTransactionAsync(isolation, cancellationToken);
                    return Interlocked.Increment(ref beginCalls) == 1
                        ? new FaultingTransaction(transaction, CommitFault.SqlStateBeforeCommit(sqlState))
                        : transaction;
                });

            Assert.False(await retryingPersistenceProcess.Persistence.ClearCandidateUnderMutationGateAsync(
                sqlPlan.CandidateSession ?? "old-candidate",
                sqlPlan.CandidateOperation ?? "issue:old",
                CancellationToken.None));
            Assert.Equal(2, beginCalls);

            await using var verifyRetry = new DavDatabaseContext();
            Assert.True(await verifyRetry.SetupCompletionOperations.AnyAsync());
            Assert.Empty(await verifyRetry.ConfigItems.Where(row =>
                row.ConfigName == SetupConfigKeys.CandidateSessionToken
                || row.ConfigName == SetupConfigKeys.CandidateSessionOperation).ToListAsync());
        }
    }

    [Fact]
    public async Task StartupRotation_PostgresMigratesEncryptedCompletionMarkerToPlaintext()
    {
        Assert.SkipUnless(_fixture.IsAvailable, "Docker is required for PostgreSQL completion marker rotation tests.");
        await _fixture.ResetAsync();
        var oldKey = CreateKey();
        var newKey = CreateKey();
        using var environment = CreateEnvironment("completion-marker-rotation", oldKey);

        string ciphertext;
        using (var oldEncryption = new ConfigEncryptionService())
            ciphertext = oldEncryption.Encrypt(SetupConfigKeys.Completed, "true");

        await using (var seed = new DavDatabaseContext())
        {
            seed.ConfigItems.Add(new ConfigItem
            {
                ConfigName = SetupConfigKeys.Completed,
                ConfigValue = ciphertext,
                IsEncrypted = true,
            });
            await seed.SaveChangesAsync();
        }

        Environment.SetEnvironmentVariable("NZBDAV_MASTER_KEY", newKey);
        Environment.SetEnvironmentVariable("NZBDAV_MASTER_KEY_OLD", oldKey);
        await using (var startupContext = new DavDatabaseContext())
        using (var startupEncryption = new ConfigEncryptionService())
            await StartupEncryptionCheck.RunAsync(startupContext, startupEncryption);

        Environment.SetEnvironmentVariable("NZBDAV_MASTER_KEY_OLD", null);
        await using var verify = new DavDatabaseContext();
        var marker = await verify.ConfigItems.SingleAsync(row => row.ConfigName == SetupConfigKeys.Completed);
        Assert.False(marker.IsEncrypted);
        Assert.Equal("true", marker.ConfigValue);
    }

    [Fact]
    public async Task StartupRotation_PostgresRotatesCompletionSnapshotForFreshRecovery()
    {
        Assert.SkipUnless(_fixture.IsAvailable, "Docker is required for PostgreSQL completion key-rotation tests.");
        await _fixture.ResetAsync();
        var oldKey = CreateKey();
        var newKey = CreateKey();
        using var environment = CreateEnvironment("completion-key-rotation", oldKey);

        SetupCompletionPlan plan;
        await using (var seed = await CreateProcessAsync())
        {
            await SeedSetupAsync(
                seed,
                activeSession: "active-old-key",
                candidateSession: "candidate-old-key",
                candidateOperation: "issue:old-key");
            plan = await seed.Service.PrepareCompletionLogoutUnderMutationGateAsync(CancellationToken.None);
        }

        await using var beforeRotation = new DavDatabaseContext();
        var oldSnapshot = await beforeRotation.SetupCompletionOperations.AsNoTracking().SingleAsync();
        var oldActiveCiphertext = oldSnapshot.ActiveSessionCiphertext;
        var oldCandidateCiphertext = oldSnapshot.CandidateSessionCiphertext;
        Assert.NotNull(oldActiveCiphertext);
        Assert.NotNull(oldCandidateCiphertext);

        Environment.SetEnvironmentVariable("NZBDAV_MASTER_KEY_OLD", oldKey);
        Environment.SetEnvironmentVariable("NZBDAV_MASTER_KEY", newKey);
        await using (var startupContext = new DavDatabaseContext())
        using (var startupEncryption = new ConfigEncryptionService())
        {
            await StartupEncryptionCheck.RunAsync(startupContext, startupEncryption, CancellationToken.None);
        }

        await using var afterRotation = new DavDatabaseContext();
        var rotated = await afterRotation.SetupCompletionOperations.AsNoTracking().SingleAsync();
        Assert.NotEqual(oldActiveCiphertext, rotated.ActiveSessionCiphertext);
        Assert.NotEqual(oldCandidateCiphertext, rotated.CandidateSessionCiphertext);

        using (var newOnlyEncryption = new ConfigEncryptionService())
        {
            Assert.Equal(
                "active-old-key",
                newOnlyEncryption.Decrypt("setup.completion.active", rotated.ActiveSessionCiphertext!).plaintext);
            Assert.Equal(
                "candidate-old-key",
                newOnlyEncryption.Decrypt("setup.completion.candidate", rotated.CandidateSessionCiphertext!).plaintext);
        }

        Environment.SetEnvironmentVariable("NZBDAV_MASTER_KEY_OLD", null);
        var handler = new CountingLogoutHandler();
        await using var restarted = await CreateProcessAsync(handler);
        Assert.True(await restarted.Service.RecoverPendingUnderMutationGateAsync(
            null,
            SetupEnvironmentOptions.FromEnvironment().JellyfinUrl,
            CancellationToken.None));
        Assert.Equal(["active-old-key", "candidate-old-key"], handler.LogoutTokens.ToArray());
        Assert.False(await restarted.Context.SetupCompletionOperations.AnyAsync());
        Assert.Equal(
            1,
            await restarted.Context.ConfigItems.CountAsync(row =>
                row.ConfigName == SetupConfigKeys.Completed && row.ConfigValue == "true"));
        _ = plan;
    }

    private async Task<ProcessInstance> CreateProcessAsync(
        CountingLogoutHandler? handler = null,
        CommitFault? serviceCommitFault = null,
        Func<DavDatabaseContext, IsolationLevel, CancellationToken, Task<IDbContextTransaction>>? persistenceBeginTransaction = null,
        Func<IsolationLevel, CancellationToken, Task<IDbContextTransaction>>? serviceBeginTransaction = null)
    {
        var context = new DavDatabaseContext();
        var encryption = new ConfigEncryptionService();
        var manager = new ConfigManager(encryption);
        await manager.LoadConfig();

        Func<IsolationLevel, CancellationToken, Task<IDbContextTransaction>>? persistenceBegin = null;
        if (persistenceBeginTransaction is not null)
            persistenceBegin = (isolation, cancellationToken) =>
                persistenceBeginTransaction(context, isolation, cancellationToken);

        if (serviceBeginTransaction is null && serviceCommitFault is not null)
        {
            serviceBeginTransaction = async (isolation, cancellationToken) =>
            {
                var transaction = await context.Database.BeginTransactionAsync(isolation, cancellationToken);
                return new FaultingTransaction(transaction, serviceCommitFault);
            };
        }

        var persistence = new SetupConfigPersistence(manager, context, persistenceBegin);
        var service = new SetupGrantService(
            context,
            manager,
            TimeProvider.System,
            () => new HttpClient(handler ?? new CountingLogoutHandler(), disposeHandler: handler is null),
            persistence,
            null,
            serviceBeginTransaction);
        return new ProcessInstance(context, encryption, manager, persistence, service);
    }

    private static async Task SeedSetupAsync(
        ProcessInstance process,
        string activeSession,
        string? revocationSession = null,
        string? candidateSession = null,
        string? candidateOperation = null)
    {
        await process.Persistence.SaveAsync(new SetupSecretValues
        {
            Indexers = "[]",
            PluginApiKey = "plugin-key",
            JellyfinApiKey = activeSession,
            RevocationPending = revocationSession is not null,
            RevocationPendingToken = revocationSession,
        }, completed: false, CancellationToken.None);

        if (candidateSession is not null)
        {
            await process.Persistence.SaveCandidateUnderMutationGateAsync(
                candidateSession,
                candidateOperation ?? $"issue:{Guid.NewGuid():N}",
                CancellationToken.None);
        }
    }

    private static backend.Tests.Config.TemporaryEnvironment CreateEnvironment(string name, string masterKey)
    {
        var configPath = Path.Combine(
            Path.GetTempPath(),
            "nzbdav-tests",
            $"completion-fencing-postgres-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(configPath);
        return new backend.Tests.Config.TemporaryEnvironment(
            ("CONFIG_PATH", configPath),
            ("NZBDAV_FULL_STACK", "true"),
            ("SETUP_JELLYFIN_URL", "http://jellyfin.test"),
            ("NZBDAV_MASTER_KEY", masterKey),
            ("NZBDAV_MASTER_KEY_OLD", null));
    }

    private static string CreateKey()
        => Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<Exception?> CaptureAsync(Func<Task> operation)
    {
        try
        {
            await operation();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private sealed class ProcessInstance : IAsyncDisposable
    {
        private readonly ConfigEncryptionService _encryption;

        public ProcessInstance(
            DavDatabaseContext context,
            ConfigEncryptionService encryption,
            ConfigManager manager,
            SetupConfigPersistence persistence,
            SetupGrantService service)
        {
            Context = context;
            _encryption = encryption;
            Manager = manager;
            Persistence = persistence;
            Service = service;
        }

        public DavDatabaseContext Context { get; }
        public ConfigManager Manager { get; }
        public SetupConfigPersistence Persistence { get; }
        public SetupGrantService Service { get; }

        public async ValueTask DisposeAsync()
        {
            _encryption.Dispose();
            await Context.DisposeAsync();
        }
    }

    private sealed class CountingLogoutHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<string> _logoutTokens = new();

        public IReadOnlyCollection<string> LogoutTokens => _logoutTokens.ToArray();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/Sessions/Logout")
            {
                _logoutTokens.Enqueue(request.Headers.GetValues("X-Emby-Token").Single());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private enum CommitFaultKind
    {
        AcknowledgementLostAfterCommit,
        SqlStateBeforeCommit,
    }

    private sealed record CommitFault(CommitFaultKind Kind, string? SqlState = null)
    {
        public static CommitFault AcknowledgementLostAfterCommit { get; } =
            new(CommitFaultKind.AcknowledgementLostAfterCommit);

        public static CommitFault SqlStateBeforeCommit(string sqlState)
            => new(CommitFaultKind.SqlStateBeforeCommit, sqlState);
    }

    private sealed class FaultingTransaction(IDbContextTransaction inner, CommitFault fault) : IDbContextTransaction
    {
        public Guid TransactionId => inner.TransactionId;
        public bool SupportsSavepoints => inner.SupportsSavepoints;

        public void Commit() => CommitAsync(CancellationToken.None).GetAwaiter().GetResult();

        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            if (fault.Kind == CommitFaultKind.SqlStateBeforeCommit)
                throw new PostgresException(
                    $"injected PostgreSQL {fault.SqlState}", "ERROR", "ERROR", fault.SqlState!);

            await inner.CommitAsync(cancellationToken);
            if (fault.Kind == CommitFaultKind.AcknowledgementLostAfterCommit)
                throw new IOException("commit acknowledgement was lost after the database commit");
        }

        public void Rollback() => RollbackAsync(CancellationToken.None).GetAwaiter().GetResult();
        public Task RollbackAsync(CancellationToken cancellationToken = default)
            => inner.RollbackAsync(cancellationToken);
        public void CreateSavepoint(string name) => inner.CreateSavepoint(name);
        public Task CreateSavepointAsync(string name, CancellationToken cancellationToken = default)
            => inner.CreateSavepointAsync(name, cancellationToken);
        public void RollbackToSavepoint(string name) => inner.RollbackToSavepoint(name);
        public Task RollbackToSavepointAsync(string name, CancellationToken cancellationToken = default)
            => inner.RollbackToSavepointAsync(name, cancellationToken);
        public void ReleaseSavepoint(string name) => inner.ReleaseSavepoint(name);
        public Task ReleaseSavepointAsync(string name, CancellationToken cancellationToken = default)
            => inner.ReleaseSavepointAsync(name, cancellationToken);
        public void Dispose() => inner.Dispose();
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
