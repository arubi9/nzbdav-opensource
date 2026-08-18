using System.Data;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NzbWebDAV.Clients.JellyfinSetup;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Setup.Core;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Tests.Setup.Core;

[Collection(nameof(backend.Tests.Config.EnvironmentVariableCollection))]
public sealed class SetupGrantRecoveryStateMachineTests
{
    [Fact]
    public async Task RecoverPending_CandidateOnly_LogsOutCandidateThenClearsJournal()
    {
        using var environment = CreateEnvironment("candidate-only");
        await using var context = await CreateMigratedContextAsync();
        var manager = new ConfigManager(new ConfigEncryptionService());
        await manager.LoadConfig();
        var persistence = new SetupConfigPersistence(manager, context);
        await persistence.SaveAsync(new SetupSecretValues
        {
            CandidateSessionToken = "candidate-session",
            CandidateSessionOperation = "operation-1",
        }, completed: false);

        using var handler = new LogoutHandler();
        var service = new SetupGrantService(
            context,
            manager,
            TimeProvider.System,
            () => new HttpClient(handler, disposeHandler: false));

        Assert.True(await service.RecoverPendingAsync());
        Assert.Equal(["candidate-session"], handler.LogoutTokens);
        Assert.Empty(await CandidateRowsAsync(context));
    }

    [Fact]
    public async Task RecoverPending_EqualActiveLiveGrant_PreservesActiveSessionAndGrant()
    {
        using var environment = CreateEnvironment("equal-active");
        await using var context = await CreateMigratedContextAsync();
        var manager = new ConfigManager(new ConfigEncryptionService());
        await manager.LoadConfig();
        context.SetupGrants.Add(LiveGrant());
        await context.SaveChangesAsync();
        var persistence = new SetupConfigPersistence(manager, context);
        await persistence.SaveAsync(new SetupSecretValues
        {
            JellyfinApiKey = "active-session",
            CandidateSessionToken = "active-session",
            CandidateSessionOperation = "operation-1",
        }, completed: false);

        using var handler = new LogoutHandler();
        var service = new SetupGrantService(
            context,
            manager,
            TimeProvider.System,
            () => new HttpClient(handler, disposeHandler: false));

        Assert.True(await service.RecoverPendingAsync());
        Assert.Empty(handler.LogoutTokens);
        Assert.Empty(await CandidateRowsAsync(context));
        Assert.Equal("active-session", manager.GetSetupJellyfinApiKey());
        Assert.True(await service.ValidateAsync("grant"));
    }

    [Fact]
    public async Task RecoverPending_DistinctActiveLiveGrant_LogsOutOnlyCandidate()
    {
        using var environment = CreateEnvironment("distinct-active");
        await using var context = await CreateMigratedContextAsync();
        var manager = new ConfigManager(new ConfigEncryptionService());
        await manager.LoadConfig();
        context.SetupGrants.Add(LiveGrant());
        await context.SaveChangesAsync();
        var persistence = new SetupConfigPersistence(manager, context);
        await persistence.SaveAsync(new SetupSecretValues
        {
            JellyfinApiKey = "active-session",
            CandidateSessionToken = "candidate-session",
            CandidateSessionOperation = "operation-1",
        }, completed: false);

        using var handler = new LogoutHandler();
        var service = new SetupGrantService(
            context,
            manager,
            TimeProvider.System,
            () => new HttpClient(handler, disposeHandler: false));

        Assert.True(await service.RecoverPendingAsync());
        Assert.Equal(["candidate-session"], handler.LogoutTokens);
        Assert.Empty(await CandidateRowsAsync(context));
        Assert.Equal("active-session", manager.GetSetupJellyfinApiKey());
        Assert.True(await service.ValidateAsync("grant"));
    }

    [Fact]
    public async Task RecoverRevocation_FreshDistinctCandidateLogoutFailure_RetainsCandidateAndActiveGrant()
    {
        using var environment = CreateEnvironment("authenticated-fresh-failure");
        await using var context = await CreateMigratedContextAsync();
        var manager = new ConfigManager(new ConfigEncryptionService());
        await manager.LoadConfig();
        await SeedAdminAndGrantAsync(context);
        var persistence = new SetupConfigPersistence(manager, context);
        await persistence.SaveAsync(new SetupSecretValues
        {
            JellyfinApiKey = "active-session",
            RevocationPending = true,
            RevocationPendingToken = "previous-session",
        }, completed: false);

        using var handler = new AuthenticatedRecoveryHandler(HttpStatusCode.InternalServerError);
        var service = new SetupGrantService(
            context,
            manager,
            TimeProvider.System,
            () => new HttpClient(handler, disposeHandler: false));

        await Assert.ThrowsAsync<JellyfinSetupException>(() =>
            service.RecoverRevocationAsync("admin", "secret"));

        Assert.Equal(["fresh-session", "previous-session"], handler.LogoutTokens);
        Assert.Equal("fresh-session", await persistence.ReadCandidateSessionTokenAsync(CancellationToken.None));
        Assert.True(await service.ValidateAsync("grant"));
        Assert.Equal("active-session", manager.GetSetupJellyfinApiKey());
    }

    [Fact]
    public async Task RecoverRevocation_CleanupCommitUnknown_RemainsSafeAndActiveGrantSurvives()
    {
        using var environment = CreateEnvironment("authenticated-clear-unknown");
        await using var context = await CreateMigratedContextAsync();
        var manager = new ConfigManager(new ConfigEncryptionService());
        await manager.LoadConfig();
        await SeedAdminAndGrantAsync(context);
        var seedPersistence = new SetupConfigPersistence(manager, context);
        await seedPersistence.SaveAsync(new SetupSecretValues
        {
            JellyfinApiKey = "active-session",
            RevocationPending = true,
            RevocationPendingToken = "previous-session",
        }, completed: false);

        var transactions = 0;
        var persistence = new SetupConfigPersistence(
            manager,
            context,
            async (isolation, cancellationToken) =>
            {
                var inner = await context.Database.BeginTransactionAsync(isolation, cancellationToken);
                return new CommitAfterUnderlyingTransaction(
                    inner,
                    () => Interlocked.Increment(ref transactions) == 2);
            });
        using var handler = new AuthenticatedRecoveryHandler(HttpStatusCode.NotFound);
        var service = new SetupGrantService(
            context,
            manager,
            TimeProvider.System,
            () => new HttpClient(handler, disposeHandler: false),
            persistence);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            service.RecoverRevocationAsync("admin", "secret"));

        Assert.Equal(["fresh-session", "previous-session"], handler.LogoutTokens);
        Assert.Empty(await CandidateRowsAsync(context));
        Assert.True(await service.ValidateAsync("grant"));
        Assert.Equal("active-session", manager.GetSetupJellyfinApiKey());
    }

    [Fact]
    public async Task RecoverPending_RevocationMarker_RetiresPreviousSessionAndPreservesActiveGrant()
    {
        using var environment = CreateEnvironment("revocation-marker");
        await using var context = await CreateMigratedContextAsync();
        var manager = new ConfigManager(new ConfigEncryptionService());
        await manager.LoadConfig();
        context.SetupGrants.Add(LiveGrant());
        await context.SaveChangesAsync();
        var persistence = new SetupConfigPersistence(manager, context);
        await persistence.SaveAsync(new SetupSecretValues
        {
            JellyfinApiKey = "active-session",
            RevocationPending = true,
            RevocationPendingToken = "previous-session",
        }, completed: false);

        using var handler = new LogoutHandler();
        var service = new SetupGrantService(
            context,
            manager,
            TimeProvider.System,
            () => new HttpClient(handler, disposeHandler: false));

        Assert.True(await service.RecoverPendingAsync());
        Assert.Equal(["previous-session"], handler.LogoutTokens);
        Assert.False(await service.IsRevocationPendingAsync());
        Assert.Equal("active-session", manager.GetSetupJellyfinApiKey());
        Assert.True(await service.ValidateAsync("grant"));
    }

    private static async Task SeedAdminAndGrantAsync(DavDatabaseContext context)
    {
        var salt = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        context.Accounts.Add(new Account
        {
            Type = Account.AccountType.Admin,
            Username = "admin",
            RandomSalt = salt,
            PasswordHash = PasswordUtil.Hash("secret", salt),
        });
        context.SetupGrants.Add(LiveGrant());
        await context.SaveChangesAsync();
    }

    private static SetupGrant LiveGrant()
        => new()
        {
            Id = SetupGrant.SingletonId,
            GrantedTokenHash = SetupGrantCrypto.ComputeIssuedTokenHash("grant"),
            IssuedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(10),
            IsRevoked = false,
            IssuedByUsername = "admin",
        };

    private static async Task<List<ConfigItem>> CandidateRowsAsync(DavDatabaseContext context)
        => await context.ConfigItems
            .Where(row => row.ConfigName == SetupConfigKeys.CandidateSessionToken
                       || row.ConfigName == SetupConfigKeys.CandidateSessionOperation
                       || row.ConfigName == SetupConfigKeys.CandidateSessionIntent)
            .AsNoTracking()
            .ToListAsync();

    private static backend.Tests.Config.TemporaryEnvironment CreateEnvironment(string name)
    {
        var configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-recovery-state-{name}-{Guid.NewGuid():N}");
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

    private sealed class LogoutHandler : HttpMessageHandler
    {
        public List<string> LogoutTokens { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/Sessions/Logout")
            {
                LogoutTokens.Add(request.Headers.GetValues("X-Emby-Token").Single());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class AuthenticatedRecoveryHandler(HttpStatusCode logoutStatus) : HttpMessageHandler
    {
        public List<string> LogoutTokens { get; } = [];
        private readonly HttpStatusCode _logoutStatus = logoutStatus;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/Users/AuthenticateByName")
            {
                var body = JsonSerializer.Serialize(new
                {
                    AccessToken = "fresh-session",
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
                return Task.FromResult(new HttpResponseMessage(_logoutStatus));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class CommitAfterUnderlyingTransaction(
        IDbContextTransaction inner,
        Func<bool> throwAfterCommit) : IDbContextTransaction
    {
        private readonly IDbContextTransaction _inner = inner;
        private readonly Func<bool> _throwAfterCommit = throwAfterCommit;

        public Guid TransactionId => _inner.TransactionId;
        public bool SupportsSavepoints => _inner.SupportsSavepoints;
        public void Commit() => CommitAsync(CancellationToken.None).GetAwaiter().GetResult();
        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            await _inner.CommitAsync(cancellationToken);
            if (_throwAfterCommit())
                throw new IOException("cleanup acknowledgement was lost after commit");
        }

        public void Rollback() => RollbackAsync(CancellationToken.None).GetAwaiter().GetResult();
        public Task RollbackAsync(CancellationToken cancellationToken = default)
            => _inner.RollbackAsync(cancellationToken);
        public void CreateSavepoint(string name) => _inner.CreateSavepoint(name);
        public Task CreateSavepointAsync(string name, CancellationToken cancellationToken = default)
            => _inner.CreateSavepointAsync(name, cancellationToken);
        public void RollbackToSavepoint(string name) => _inner.RollbackToSavepoint(name);
        public Task RollbackToSavepointAsync(string name, CancellationToken cancellationToken = default)
            => _inner.RollbackToSavepointAsync(name, cancellationToken);
        public void ReleaseSavepoint(string name) => _inner.ReleaseSavepoint(name);
        public Task ReleaseSavepointAsync(string name, CancellationToken cancellationToken = default)
            => _inner.ReleaseSavepointAsync(name, cancellationToken);
        public void Dispose() => _inner.Dispose();
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
