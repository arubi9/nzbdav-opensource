using System.Data;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NzbWebDAV.Api.Controllers;
using NzbWebDAV.Api.Controllers.UsenetSettings;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Services;
using NzbWebDAV.Setup.Core;
using NzbWebDAV.Tests.Clients.Usenet.Caching;

namespace backend.Tests.Api.Controllers.UsenetSettings;

[Collection(nameof(backend.Tests.Config.EnvironmentVariableCollection))]
public sealed class UsenetSettingsControllerPostgresTests : IClassFixture<PostgresHeaderCacheFixture>
{
    private readonly PostgresHeaderCacheFixture _fixture;

    public UsenetSettingsControllerPostgresTests(PostgresHeaderCacheFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Postgres_commit_before_throw_reconciles_durable_row_revision_cache_and_event_once()
    {
        using var environment = await CreateEnvironmentAsync();
        await using var context = new DavDatabaseContext();
        using var encryption = new ConfigEncryptionService();
        var manager = new ConfigManager(encryption);
        var events = new List<string>();
        manager.OnConfigChanged += (_, args) => events.Add(JsonSerializer.Serialize(args.ChangedConfig));
        var persistence = new SetupConfigPersistence(manager, context);
        var fault = new CommitFaultTransaction.FaultState(CommitFaultTransaction.Mode.CommitThenThrow);
        var controller = CreateController(context, manager, encryption, persistence,
            (isolation, cancellationToken) => BeginFaultedAsync(context, isolation, cancellationToken, fault));

        var snapshot = await ReadAsync(controller);
        var result = await PostAsync(controller, snapshot.Revision, "commit-postgres-secret");

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(200, ok.StatusCode);
        Assert.Single(events);
        Assert.DoesNotContain("commit-postgres-secret", JsonSerializer.Serialize(ok.Value));
        Assert.DoesNotContain("commit-postgres-secret", string.Join("\n", events));
        await AssertDurableAsync(encryption, manager, snapshot.Revision, "commit-postgres-secret", ok.Value);
        Assert.True(fault.Disposed);
        Assert.False(fault.RolledBack);
    }

    [Fact]
    public async Task Postgres_cancellation_during_commit_returns_typed_unknown_conflict_without_duplicate_event_or_plaintext()
    {
        using var environment = await CreateEnvironmentAsync();
        await using var context = new DavDatabaseContext();
        using var encryption = new ConfigEncryptionService();
        var manager = new ConfigManager(encryption);
        var events = new List<string>();
        manager.OnConfigChanged += (_, args) => events.Add(JsonSerializer.Serialize(args.ChangedConfig));
        var persistence = new SetupConfigPersistence(manager, context);
        var fault = new CommitFaultTransaction.FaultState(CommitFaultTransaction.Mode.CommitThenCancel);
        var controller = CreateController(context, manager, encryption, persistence,
            (isolation, cancellationToken) => BeginFaultedAsync(context, isolation, cancellationToken, fault));

        var snapshot = await ReadAsync(controller);
        var result = await PostAsync(controller, snapshot.Revision, "cancel-postgres-secret");

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(409, conflict.StatusCode);
        Assert.False(Assert.IsType<BaseApiResponse>(conflict.Value).Status);
        Assert.Single(events);
        Assert.DoesNotContain("cancel-postgres-secret", JsonSerializer.Serialize(conflict.Value));
        Assert.DoesNotContain("cancel-postgres-secret", string.Join("\n", events));
        await AssertDurableAsync(encryption, manager, snapshot.Revision, "cancel-postgres-secret", null);
        Assert.True(fault.Disposed);
        Assert.False(fault.RolledBack);
    }

    [Fact]
    public async Task Postgres_confirmed_precommit_conflict_rolls_back_and_disposes_without_publishing()
    {
        using var environment = await CreateEnvironmentAsync();
        await using var context = new DavDatabaseContext();
        using var encryption = new ConfigEncryptionService();
        var manager = new ConfigManager(encryption);
        var events = new List<string>();
        manager.OnConfigChanged += (_, args) => events.Add(JsonSerializer.Serialize(args.ChangedConfig));
        var persistence = new SetupConfigPersistence(manager, context);
        var initialController = CreateController(context, manager, encryption, persistence);
        var initial = await ReadAsync(initialController);
        Assert.IsType<OkObjectResult>(await PostAsync(initialController, initial.Revision, "old-rollback-secret"));
        var before = await ReadDurableAsync(encryption);
        var eventCount = events.Count;
        var fault = new CommitFaultTransaction.FaultState(CommitFaultTransaction.Mode.CommitThenThrow);
        var controller = CreateController(context, manager, encryption, persistence,
            (isolation, cancellationToken) => BeginFaultedAsync(context, isolation, cancellationToken, fault));

        var result = await PostAsync(controller, "stale-revision", "new-rollback-secret");

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(409, conflict.StatusCode);
        Assert.False(Assert.IsType<BaseApiResponse>(conflict.Value).Status);
        Assert.Equal(eventCount, events.Count);
        Assert.DoesNotContain("new-rollback-secret", JsonSerializer.Serialize(conflict.Value));
        Assert.DoesNotContain("new-rollback-secret", string.Join("\n", events));
        var after = await ReadDurableAsync(encryption);
        Assert.Equal(before.StoredValue, after.StoredValue);
        Assert.Equal(before.Plaintext, after.Plaintext);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal("old-rollback-secret", manager.GetUsenetProviderConfig().Providers.Single().Pass);
        Assert.Equal(after.Plaintext, JsonSerializer.Serialize(manager.GetUsenetProviderConfig()));
        Assert.True(fault.Disposed);
        Assert.True(fault.RolledBack);
    }

    [Theory]
    [InlineData("40001")]
    [InlineData("40P01")]
    public async Task Postgres_confirmed_provider_abort_uses_production_classifier_and_only_disposes(string sqlState)
    {
        using var environment = await CreateEnvironmentAsync();
        await using var context = new DavDatabaseContext();
        using var encryption = new ConfigEncryptionService();
        var manager = new ConfigManager(encryption);
        var events = new List<string>();
        manager.OnConfigChanged += (_, args) => events.Add(JsonSerializer.Serialize(args.ChangedConfig));
        var persistence = new SetupConfigPersistence(manager, context);
        var initialController = CreateController(context, manager, encryption, persistence);
        var initial = await ReadAsync(initialController);
        Assert.IsType<OkObjectResult>(await PostAsync(initialController, initial.Revision, "old-postgres-secret"));
        var eventCount = events.Count;
        var durableBefore = await ReadDurableAsync(encryption);
        var revisionBefore = encryption.CreateOpaqueRevision(durableBefore.StoredValue);

        var fault = new CommitFaultTransaction.FaultState(CommitFaultTransaction.Mode.SqlStateBeforeCommit, sqlState);
        var controller = CreateController(context, manager, encryption, persistence,
            (isolation, cancellationToken) => BeginFaultedAsync(context, isolation, cancellationToken, fault));
        var current = await ReadAsync(controller);
        var result = await PostAsync(controller, current.Revision, "new-postgres-secret");

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(409, conflict.StatusCode);
        Assert.False(Assert.IsType<BaseApiResponse>(conflict.Value).Status);
        Assert.Equal(eventCount, events.Count);
        Assert.DoesNotContain("new-postgres-secret", JsonSerializer.Serialize(conflict.Value));
        Assert.DoesNotContain("new-postgres-secret", string.Join("\n", events));
        var durableAfter = await ReadDurableAsync(encryption);
        Assert.Equal(durableBefore.StoredValue, durableAfter.StoredValue);
        Assert.Equal(durableBefore.Plaintext, durableAfter.Plaintext);
        Assert.Equal(revisionBefore, durableAfter.Revision);
        Assert.Equal("old-postgres-secret", durableAfter.Password);
        Assert.Equal("old-postgres-secret", manager.GetUsenetProviderConfig().Providers.Single().Pass);
        Assert.Equal(durableAfter.Plaintext, JsonSerializer.Serialize(manager.GetUsenetProviderConfig()));
        Assert.True(fault.Disposed);
        Assert.False(fault.RolledBack);
    }

    private async Task<IDisposable> CreateEnvironmentAsync()
    {
        Assert.True(_fixture.IsAvailable, "PostgreSQL 17 fixture is required; this production-controller suite must not skip.");
        await _fixture.ResetAsync();
        var path = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"usenet-postgres-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return new backend.Tests.Config.TemporaryEnvironment(
            ("CONFIG_PATH", path),
            ("DATABASE_URL", _fixture.ConnectionString),
            ("FRONTEND_BACKEND_API_KEY", "unit-api-key"),
            ("NZBDAV_MASTER_KEY", Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))));
    }

    private static UsenetSettingsController CreateController(
        DavDatabaseContext context,
        ConfigManager manager,
        ConfigEncryptionService encryption,
        SetupConfigPersistence persistence,
        Func<IsolationLevel, CancellationToken, Task<IDbContextTransaction>>? begin = null)
        => new(new DavDatabaseClient(context), manager, encryption, persistence, begin,
            () => new DavDatabaseContext());

    private static async Task<UsenetSettingsResponse> ReadAsync(UsenetSettingsController controller)
    {
        var result = await InvokeAsync(controller, null);
        return Assert.IsType<UsenetSettingsResponse>(Assert.IsType<OkObjectResult>(result).Value);
    }

    private static Task<IActionResult> PostAsync(UsenetSettingsController controller, string revision, string password)
        => InvokeAsync(controller, JsonSerializer.Serialize(new
        {
            revision,
            providers = new[] { new { host = "news.postgres.test", port = 563, ssl = true, user = "alice", max = 4, type = 1, password } },
        }));

    private static async Task AssertDurableAsync(
        ConfigEncryptionService encryption,
        ConfigManager manager,
        string oldRevision,
        string password,
        object? response)
    {
        var durable = await ReadDurableAsync(encryption);
        Assert.NotEqual(oldRevision, durable.Revision);
        Assert.Equal(password, durable.Password);
        Assert.Equal(password, manager.GetUsenetProviderConfig().Providers.Single().Pass);
        Assert.Equal(durable.Plaintext, JsonSerializer.Serialize(manager.GetUsenetProviderConfig()));
        if (response is not null)
            Assert.Equal(durable.Revision, ((UsenetSettingsResponse)response).Revision);
    }

    private static async Task<DurableState> ReadDurableAsync(ConfigEncryptionService encryption)
    {
        await using var context = new DavDatabaseContext();
        var row = await context.ConfigItems.AsNoTracking().SingleAsync(x => x.ConfigName == SetupConfigKeys.UsenetProviders);
        var plaintext = row.IsEncrypted
            ? encryption.Decrypt(SetupConfigKeys.UsenetProviders, row.ConfigValue).plaintext
            : row.ConfigValue;
        using var document = JsonDocument.Parse(plaintext);
        var provider = document.RootElement.GetProperty("Providers")[0];
        return new DurableState(
            row.ConfigValue,
            encryption.CreateOpaqueRevision(row.ConfigValue),
            provider.GetProperty("Pass").GetString()!,
            plaintext);
    }

    private static async Task<IDbContextTransaction> BeginFaultedAsync(
        DavDatabaseContext context,
        IsolationLevel isolation,
        CancellationToken cancellationToken,
        CommitFaultTransaction.FaultState fault)
        => new CommitFaultTransaction(
            await context.Database.BeginTransactionAsync(isolation, cancellationToken), fault);

    private static async Task<IActionResult> InvokeAsync(UsenetSettingsController controller, string? body)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = "/api/admin-settings/usenet";
        http.Request.Headers["x-api-key"] = "unit-api-key";
        if (body is null)
            http.Request.Method = "GET";
        else
        {
            http.Request.Method = "POST";
            var bytes = Encoding.UTF8.GetBytes(body);
            http.Request.ContentType = "application/json";
            http.Request.ContentLength = bytes.Length;
            http.Request.Body = new MemoryStream(bytes);
        }
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return await controller.HandleApiRequest();
    }

    private sealed record DurableState(string StoredValue, string Revision, string Password, string Plaintext);

    private sealed class CommitFaultTransaction(IDbContextTransaction inner, CommitFaultTransaction.FaultState fault) : IDbContextTransaction
    {
        internal enum Mode { CommitThenThrow, CommitThenCancel, SqlStateBeforeCommit }
        internal sealed class FaultState(Mode mode, string? sqlState = null)
        {
            internal Mode Mode { get; } = mode;
            internal string? SqlState { get; } = sqlState;
            internal bool Disposed { get; set; }
            internal bool RolledBack { get; set; }
        }

        public Guid TransactionId => inner.TransactionId;
        public bool SupportsSavepoints => inner.SupportsSavepoints;
        public void Commit() => CommitAsync(CancellationToken.None).GetAwaiter().GetResult();
        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            if (fault.Mode == Mode.SqlStateBeforeCommit)
                throw new PostgresException("injected provider abort", "ERROR", "ERROR", fault.SqlState!);
            await inner.CommitAsync(cancellationToken);
            if (fault.Mode == Mode.CommitThenCancel)
                throw new OperationCanceledException("commit acknowledgement was cancelled");
            throw new IOException("commit acknowledgement was lost");
        }

        public void Rollback() => RollbackAsync(CancellationToken.None).GetAwaiter().GetResult();
        public async Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            fault.RolledBack = true;
            await inner.RollbackAsync(cancellationToken);
        }
        public void CreateSavepoint(string name) => inner.CreateSavepoint(name);
        public Task CreateSavepointAsync(string name, CancellationToken cancellationToken = default) => inner.CreateSavepointAsync(name, cancellationToken);
        public void RollbackToSavepoint(string name) => inner.RollbackToSavepoint(name);
        public Task RollbackToSavepointAsync(string name, CancellationToken cancellationToken = default) => inner.RollbackToSavepointAsync(name, cancellationToken);
        public void ReleaseSavepoint(string name) => inner.ReleaseSavepoint(name);
        public Task ReleaseSavepointAsync(string name, CancellationToken cancellationToken = default) => inner.ReleaseSavepointAsync(name, cancellationToken);
        public ValueTask DisposeAsync() { fault.Disposed = true; return inner.DisposeAsync(); }
        public void Dispose() { fault.Disposed = true; inner.Dispose(); }
    }
}
