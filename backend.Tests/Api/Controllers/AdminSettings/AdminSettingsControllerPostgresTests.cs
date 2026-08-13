using System.Data;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NzbWebDAV.Api.Controllers.AdminSettings;
using NzbWebDAV.Utils;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Tests.Clients.Usenet.Caching;

namespace NzbWebDAV.Tests.Api.Controllers.AdminSettings;

[Collection(nameof(backend.Tests.Config.EnvironmentVariableCollection))]
public sealed class AdminSettingsControllerPostgresTests : IClassFixture<PostgresHeaderCacheFixture>
{
    private static readonly CommitFaultTransaction.FaultState CommitThrowFault = new(CommitFaultTransaction.Mode.CommitThenThrow);
    private static readonly CommitFaultTransaction.FaultState CommitCancelFault = new(CommitFaultTransaction.Mode.CommitThenCancel);
    private static readonly AdminSettingsPostgresFactory CommitThrowFactory = new(CommitThrowFault);
    private static readonly AdminSettingsPostgresFactory CommitCancelFactory = new(CommitCancelFault);
    private static readonly AdminSettingsPostgresFactory DefaultFactory = new(null);

    private readonly PostgresHeaderCacheFixture _fixture;

    public AdminSettingsControllerPostgresTests(PostgresHeaderCacheFixture fixture)
        => _fixture = fixture;

    [Fact]
    public async Task Postgres_commit_then_throw_is_reconciled_from_fresh_read_and_cached_once()
    {
        using var environment = await CreateEnvironmentAsync("admin-postgres-throw");
        var factory = CommitThrowFactory;
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var manager = factory.Services.GetRequiredService<ConfigManager>();
        var cacheUpdated = 0;
        EventHandler<ConfigManager.ConfigEventArgs> changed = (_, __) => cacheUpdated++;
        manager.OnConfigChanged += changed;

        var response = await SendUpdateRequestAsync(
            client,
            password: "postgres-commit-throw");
        manager.OnConfigChanged -= changed;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var responsePayload = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("postgres-commit-throw", responsePayload, StringComparison.Ordinal);
        Assert.Equal(1, cacheUpdated);

        var body = JsonSerializer.Deserialize<AdminSettingsResponse>(
            responsePayload,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(body);
        Assert.True(body.HasSecrets.WebdavPass);

        await using var verify = new DavDatabaseContext();
        var row = await verify.ConfigItems.AsNoTracking().SingleAsync(x => x.ConfigName == "webdav.pass");
        using var encryption = new ConfigEncryptionService();
        var stored = row.IsEncrypted
            ? encryption.Decrypt("webdav.pass", row.ConfigValue).plaintext
            : row.ConfigValue;

        var cacheValue = manager.GetConfigValueForWrite("webdav.pass");
        Assert.NotNull(cacheValue);
        Assert.Equal(stored, cacheValue);
        Assert.True(PasswordUtil.Verify(stored, "postgres-commit-throw"));
        Assert.True(CommitThrowFault.Disposed);
    }

    [Fact]
    public async Task Postgres_commit_then_cancel_is_reconciled_as_success_and_not_rolled_back()
    {
        var environment = await CreateEnvironmentAsync("admin-postgres-cancel");
        var factory = CommitCancelFactory;
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var manager = factory.Services.GetRequiredService<ConfigManager>();
        var cacheUpdated = 0;
        EventHandler<ConfigManager.ConfigEventArgs> changed = (_, __) => cacheUpdated++;
        manager.OnConfigChanged += changed;

        var response = await SendUpdateRequestAsync(client, password: "postgres-commit-cancel");
        manager.OnConfigChanged -= changed;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var verify = new DavDatabaseContext();
        var row = await verify.ConfigItems.AsNoTracking().SingleAsync(x => x.ConfigName == "webdav.pass");
        using var encryption = new ConfigEncryptionService();
        var stored = row.IsEncrypted
            ? encryption.Decrypt("webdav.pass", row.ConfigValue).plaintext
            : row.ConfigValue;

        Assert.Equal(1, cacheUpdated);
        var cacheValue = manager.GetConfigValueForWrite("webdav.pass");
        Assert.NotNull(cacheValue);
        Assert.Equal(stored, cacheValue);
        Assert.True(PasswordUtil.Verify(stored, "postgres-commit-cancel"));
        Assert.True(CommitCancelFault.Disposed);
    }

    [Fact]
    public async Task Postgres_concurrent_writes_apply_all_committed_states()
    {
        using var environment = await CreateEnvironmentAsync("admin-postgres-concurrent");
        var factory = DefaultFactory;
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var manager = factory.Services.GetRequiredService<ConfigManager>();
        var cacheUpdated = 0;
        EventHandler<ConfigManager.ConfigEventArgs> changed = (_, __) => cacheUpdated++;
        manager.OnConfigChanged += changed;

        var first = SendUpdateRequestAsync(client, password: "postgres-concurrent-a");
        var second = SendUpdateRequestAsync(client, password: "postgres-concurrent-b");
        await Task.WhenAll(first, second);

        manager.OnConfigChanged -= changed;
        Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await second).StatusCode);
        Assert.Equal(2, cacheUpdated);

        await using var verify = new DavDatabaseContext();
        var row = await verify.ConfigItems.AsNoTracking().SingleAsync(x => x.ConfigName == "webdav.pass");
        using var encryption = new ConfigEncryptionService();
        var stored = row.IsEncrypted
            ? encryption.Decrypt("webdav.pass", row.ConfigValue).plaintext
            : row.ConfigValue;

        var cacheValue = manager.GetConfigValueForWrite("webdav.pass");
        Assert.NotNull(cacheValue);
        Assert.Equal(stored, cacheValue);
    }

    private async Task<HttpResponseMessage> SendUpdateRequestAsync(HttpClient client, string password)
    {
        var body = JsonSerializer.Serialize(new
        {
            config = new Dictionary<string, string?> { ["webdav.pass"] = password },
            clearSecrets = Array.Empty<string>(),
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin-settings")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("x-api-key", "unit-api-key");

        return await client.SendAsync(request);
    }

    private async Task<backend.Tests.Config.TemporaryEnvironment> CreateEnvironmentAsync(string directoryTag)
    {
        Assert.SkipUnless(_fixture.IsAvailable, "PostgreSQL 17 fixture is required for this production-controller suite.");

        await _fixture.ResetAsync();
        var path = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"{directoryTag}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);

        var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("CONFIG_PATH", path),
            ("DATABASE_URL", _fixture.ConnectionString),
            ("FRONTEND_BACKEND_API_KEY", "unit-api-key"),
            ("NZBDAV_MASTER_KEY", Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))),
            ("NZBDAV_MASTER_KEY_OLD", null));

        await using var context = new DavDatabaseContext();
        await DatabaseInitialization.InitializeAsync(context, CancellationToken.None);
        return environment;
    }

    private sealed class AdminSettingsPostgresFactory(CommitFaultTransaction.FaultState? fault) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                if (fault is null)
                    return;

                services.RemoveAll<Func<IsolationLevel, CancellationToken, Task<IDbContextTransaction>>>();
                services.AddTransient<Func<IsolationLevel, CancellationToken, Task<IDbContextTransaction>>>(sp =>
                    async (isolation, cancellationToken) =>
                    {
                        var context = sp.GetRequiredService<DavDatabaseContext>();
                        var tx = await context.Database.BeginTransactionAsync(isolation, cancellationToken).ConfigureAwait(false);
                        return new CommitFaultTransaction(tx, fault);
                    });
            });
        }
    }

    private sealed class CommitFaultTransaction(IDbContextTransaction inner, CommitFaultTransaction.FaultState fault) : IDbContextTransaction
    {
        internal enum Mode { CommitThenThrow, CommitThenCancel }

        internal sealed class FaultState(Mode mode)
        {
            internal Mode Mode { get; } = mode;
            internal bool Disposed { get; private set; }

            internal void MarkDisposed() => Disposed = true;
        }

        public Guid TransactionId => inner.TransactionId;
        public bool SupportsSavepoints => inner.SupportsSavepoints;

        public void Commit()
            => CommitAsync(CancellationToken.None).GetAwaiter().GetResult();

        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            await inner.CommitAsync(cancellationToken).ConfigureAwait(false);
            if (fault.Mode == Mode.CommitThenThrow)
                throw new IOException("commit acknowledgement was lost");
            if (fault.Mode == Mode.CommitThenCancel)
                throw new OperationCanceledException("commit acknowledgement was cancelled");
        }

        public void Rollback()
            => RollbackAsync(CancellationToken.None).GetAwaiter().GetResult();

        public Task RollbackAsync(CancellationToken cancellationToken = default)
            => inner.RollbackAsync(cancellationToken);

        public void CreateSavepoint(string name)
            => inner.CreateSavepoint(name);

        public Task CreateSavepointAsync(string name, CancellationToken cancellationToken = default)
            => inner.CreateSavepointAsync(name, cancellationToken);

        public void RollbackToSavepoint(string name)
            => inner.RollbackToSavepoint(name);

        public Task RollbackToSavepointAsync(string name, CancellationToken cancellationToken = default)
            => inner.RollbackToSavepointAsync(name, cancellationToken);

        public void ReleaseSavepoint(string name)
            => inner.ReleaseSavepoint(name);

        public Task ReleaseSavepointAsync(string name, CancellationToken cancellationToken = default)
            => inner.ReleaseSavepointAsync(name, cancellationToken);

        public ValueTask DisposeAsync()
        {
            fault.MarkDisposed();
            return inner.DisposeAsync();
        }

        public void Dispose()
        {
            fault.MarkDisposed();
            inner.Dispose();
        }
    }
}
