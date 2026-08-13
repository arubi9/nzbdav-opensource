using System.Data;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Services;
using NzbWebDAV.Setup.Core;

namespace NzbWebDAV.Tests.Setup.Core;

[Collection(nameof(backend.Tests.Config.EnvironmentVariableCollection))]
public sealed class SetupGrantCommitOutcomeTests
{
    [Fact]
    public async Task ConfirmedPostgresAbort_CompensatesAfterDisposeAndRetriesWithoutOrphanSession()
    {
        using var environment = CreateEnvironment("confirmed-abort");
        await using var context = await CreateMigratedContextAsync();
        var manager = new ConfigManager(new ConfigEncryptionService());
        await manager.LoadConfig();

        var events = new List<string>();
        var handler = new CommitOutcomeHandler(events);
        var transactions = 0;
        var commitCalls = 0;
        var service = new SetupGrantService(
            context,
            manager,
            TimeProvider.System,
            () => new HttpClient(handler, disposeHandler: false),
            null,
            null,
            async (isolation, cancellationToken) =>
            {
                transactions++;
                var inner = await context.Database.BeginTransactionAsync(isolation, cancellationToken);
                return new RecordingTransaction(
                    inner,
                    events,
                    transactions == 1
                        ? new PostgresException("serialization failure", "ERROR", "ERROR", "40001")
                        : null,
                    () => commitCalls++);
            });

        var result = await service.IssueAsync("admin", "secret");

        Assert.NotEmpty(result.Grant);
        Assert.Equal(2, handler.AuthenticationCalls);
        Assert.Equal(2, commitCalls);
        Assert.Equal(["dispose", "logout:token-1", "dispose"], events);
        Assert.True(await service.ValidateAsync(result.Grant));
        Assert.Single(await context.SetupGrants.ToListAsync());
        Assert.False(await service.IsRevocationPendingAsync());
    }

    [Fact]
    public async Task UncertainCommit_DoesNotRetryOrCompensateAuthenticatedSession()
    {
        using var environment = CreateEnvironment("uncertain-commit");
        await using var context = await CreateMigratedContextAsync();
        var manager = new ConfigManager(new ConfigEncryptionService());
        await manager.LoadConfig();

        var events = new List<string>();
        var handler = new CommitOutcomeHandler(events);
        var commitCalls = 0;
        var service = new SetupGrantService(
            context,
            manager,
            TimeProvider.System,
            () => new HttpClient(handler, disposeHandler: false),
            null,
            null,
            async (isolation, cancellationToken) => new RecordingTransaction(
                await context.Database.BeginTransactionAsync(isolation, cancellationToken),
                events,
                new IOException("commit response was lost"),
                () => commitCalls++));

        var exception = await Assert.ThrowsAsync<SetupTransactionRecoveryRequiredException>(
            () => service.IssueAsync("admin", "secret"));

        var innerException = Assert.IsType<IOException>(exception.InnerException);
        Assert.Equal("commit response was lost", innerException.Message);
        Assert.Equal(1, handler.AuthenticationCalls);
        Assert.Equal(1, commitCalls);
        Assert.Equal(["dispose"], events);
        Assert.False(await context.Accounts.AnyAsync());
        Assert.False(await context.SetupGrants.AnyAsync());
        Assert.False(await context.ConfigItems.AnyAsync(x => x.ConfigName == SetupConfigKeys.JellyfinApiKey));
    }

    [Fact]
    public async Task CommitAcknowledgementLossAfterUnderlyingCommit_LeavesCandidateAndPromotedRowsRecoverable()
    {
        using var environment = CreateEnvironment("commit-acknowledgement-loss");
        await using var context = await CreateMigratedContextAsync();
        var manager = new ConfigManager(new ConfigEncryptionService());
        await manager.LoadConfig();

        var events = new List<string>();
        var handler = new CommitOutcomeHandler(events);
        var service = new SetupGrantService(
            context,
            manager,
            TimeProvider.System,
            () => new HttpClient(handler, disposeHandler: false),
            null,
            null,
            async (isolation, cancellationToken) => new RecordingTransaction(
                await context.Database.BeginTransactionAsync(isolation, cancellationToken),
                events,
                null,
                () => { },
                throwAfterSuccessfulCommit: true));

        var exception = await Assert.ThrowsAsync<SetupTransactionRecoveryRequiredException>(
            () => service.IssueAsync("admin", "secret"));

        var innerException = Assert.IsType<IOException>(exception.InnerException);
        Assert.Equal("commit acknowledgement was lost after commit", innerException.Message);
        Assert.Equal(["dispose"], events);
        Assert.Equal(0, handler.LogoutCalls);
        Assert.True(await context.Accounts.AnyAsync(account => account.Username == "admin"));
        Assert.True(await context.SetupGrants.AnyAsync(grant => !grant.IsRevoked));
        var candidateRows = await context.ConfigItems
            .Where(row => row.ConfigName == SetupConfigKeys.CandidateSessionToken
                       || row.ConfigName == SetupConfigKeys.CandidateSessionOperation)
            .ToListAsync();
        Assert.Equal(2, candidateRows.Count);
        Assert.All(candidateRows, row => Assert.True(row.IsEncrypted));
    }

    [Fact]
    public async Task CandidateCommitAcknowledgementLoss_UsesRecoveryOutcomeAndNeverCompensates()
    {
        using var environment = CreateEnvironment("candidate-commit-acknowledgement-loss");
        await using var context = await CreateMigratedContextAsync();
        var manager = new ConfigManager(new ConfigEncryptionService());
        await manager.LoadConfig();

        var events = new List<string>();
        var handler = new CommitOutcomeHandler(events);
        var candidateTransactions = 0;
        var persistence = new SetupConfigPersistence(
            manager,
            context,
            async (isolation, cancellationToken) =>
            {
                candidateTransactions++;
                return new RecordingTransaction(
                    await context.Database.BeginTransactionAsync(isolation, cancellationToken),
                    events,
                    null,
                    () => { },
                    throwAfterSuccessfulCommit: true);
            });
        var service = new SetupGrantService(
            context,
            manager,
            TimeProvider.System,
            () => new HttpClient(handler, disposeHandler: false),
            persistence,
            null,
            null);

        await Assert.ThrowsAsync<SetupTransactionRecoveryRequiredException>(
            () => service.IssueAsync("admin", "secret"));

        Assert.Equal(1, candidateTransactions);
        Assert.Equal(1, handler.AuthenticationCalls);
        Assert.Equal(0, handler.LogoutCalls);
        Assert.Contains("dispose", events);
        Assert.Equal(2, await context.ConfigItems.CountAsync(row =>
            row.ConfigName == SetupConfigKeys.CandidateSessionToken
            || row.ConfigName == SetupConfigKeys.CandidateSessionOperation));
        Assert.NotNull(await persistence.ReadEmergencyCandidateAsync());
    }

    [Fact]
    public async Task RollbackFailure_DisposesBeforeSurfacingAndNeverCallsExternalLogout()
    {
        using var environment = CreateEnvironment("rollback-failure");
        await using var context = await CreateMigratedContextAsync();
        var manager = new ConfigManager(new ConfigEncryptionService());
        await manager.LoadConfig();

        var events = new List<string>();
        var handler = new CommitOutcomeHandler(events);
        var service = new SetupGrantService(
            context,
            manager,
            TimeProvider.System,
            () => new HttpClient(handler, disposeHandler: false),
            null,
            (_, _) => throw new InvalidOperationException("forced pre-commit failure"),
            async (isolation, cancellationToken) => new RecordingTransaction(
                await context.Database.BeginTransactionAsync(isolation, cancellationToken),
                events,
                null,
                () => { },
                failRollback: true));

        await Assert.ThrowsAsync<SetupTransactionRecoveryRequiredException>(
            () => service.IssueAsync("admin", "secret"));

        Assert.Equal(["dispose"], events);
        Assert.Equal(0, handler.LogoutCalls);
    }

    private static backend.Tests.Config.TemporaryEnvironment CreateEnvironment(string name)
    {
        var configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-grant-outcome-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(configPath);
        return new backend.Tests.Config.TemporaryEnvironment(
            ("CONFIG_PATH", configPath),
            ("DATABASE_URL", null),
            ("NZBDAV_FULL_STACK", "true"),
            ("SETUP_JELLYFIN_URL", "http://jellyfin.test"),
            ("NZBDAV_MASTER_KEY", Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))));
    }

    private static async Task<DavDatabaseContext> CreateMigratedContextAsync()
    {
        var context = new DavDatabaseContext();
        await context.Database.MigrateAsync();
        return context;
    }

    private sealed class CommitOutcomeHandler(List<string> events) : HttpMessageHandler
    {
        private int _authenticationCalls;
        public int AuthenticationCalls => _authenticationCalls;
        public int LogoutCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/Users/AuthenticateByName")
            {
                var token = $"token-{Interlocked.Increment(ref _authenticationCalls)}";
                var body = JsonSerializer.Serialize(new
                {
                    AccessToken = token,
                    User = new { Policy = new { IsAdministrator = true } },
                });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
            }

            if (request.RequestUri?.AbsolutePath == "/Sessions/Logout")
            {
                LogoutCalls++;
                var token = request.Headers.GetValues("X-Emby-Token").Single();
                events.Add($"logout:{token}");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class RecordingTransaction(
        IDbContextTransaction inner,
        List<string> events,
        Exception? commitFailure,
        Action incrementCommit,
        bool failRollback = false,
        bool throwAfterSuccessfulCommit = false) : IDbContextTransaction
    {
        private readonly IDbContextTransaction _inner = inner;
        private readonly List<string> _events = events;
        private readonly Exception? _commitFailure = commitFailure;
        private readonly bool _failRollback = failRollback;
        private readonly bool _throwAfterSuccessfulCommit = throwAfterSuccessfulCommit;
        private readonly Action _incrementCommit = incrementCommit;
        private int _commitCalls;

        public Guid TransactionId => _inner.TransactionId;
        public bool SupportsSavepoints => _inner.SupportsSavepoints;

        public void Commit()
        {
            CommitAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            _incrementCommit();
            var call = Interlocked.Increment(ref _commitCalls);
            if (_commitFailure is not null && call == 1)
                throw _commitFailure;

            await _inner.CommitAsync(cancellationToken);
            if (_throwAfterSuccessfulCommit && call == 1)
                throw new IOException("commit acknowledgement was lost after commit");
        }

        public void Rollback()
        {
            RollbackAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        public async Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            if (_failRollback)
                throw new InvalidOperationException("rollback response was lost");

            await _inner.RollbackAsync(cancellationToken);
        }

        public void CreateSavepoint(string name) => _inner.CreateSavepoint(name);
        public Task CreateSavepointAsync(string name, CancellationToken cancellationToken = default)
            => _inner.CreateSavepointAsync(name, cancellationToken);
        public void RollbackToSavepoint(string name) => _inner.RollbackToSavepoint(name);
        public Task RollbackToSavepointAsync(string name, CancellationToken cancellationToken = default)
            => _inner.RollbackToSavepointAsync(name, cancellationToken);
        public void ReleaseSavepoint(string name) => _inner.ReleaseSavepoint(name);
        public Task ReleaseSavepointAsync(string name, CancellationToken cancellationToken = default)
            => _inner.ReleaseSavepointAsync(name, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            _events.Add("dispose");
            await _inner.DisposeAsync();
        }

        public void Dispose()
        {
            _events.Add("dispose");
            _inner.Dispose();
        }
    }
}
