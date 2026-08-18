using System.Data;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NzbWebDAV.Api.Controllers;
using NzbWebDAV.Api.Controllers.UsenetSettings;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Setup.Core;

namespace NzbWebDAV.Tests.Api.Controllers.UsenetSettings;

[Collection(nameof(backend.Tests.Config.EnvironmentVariableCollection))]
public sealed class UsenetSettingsControllerTests
{
    [Fact]
    public async Task EditWithBlankPasswordPreservesStoredCredentialAndRedactsResponse()
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"nzbdav-tests/usenet-settings-{Guid.NewGuid():N}");
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("CONFIG_PATH", configPath), ("DATABASE_URL", null), ("FRONTEND_BACKEND_API_KEY", "unit-api-key"),
            ("NZBDAV_MASTER_KEY", Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))));
        Directory.CreateDirectory(configPath);
        await using var context = await CreateContextAsync();
        context.ConfigItems.Add(new ConfigItem
        {
            ConfigName = SetupConfigKeys.UsenetProviders,
            ConfigValue = "{\"Providers\":[{\"Type\":1,\"Host\":\"news.example.test\",\"Port\":563,\"UseSsl\":true,\"User\":\"alice\",\"Pass\":\"provider-secret\",\"MaxConnections\":10}]}",
            IsEncrypted = false,
        });
        await context.SaveChangesAsync();
        using var encryption = new ConfigEncryptionService();
        var manager = new ConfigManager(encryption);
        ConfigManager.ConfigEventArgs? changedEvent = null;
        manager.OnConfigChanged += (_, args) => changedEvent = args;
        var persistence = new SetupConfigPersistence(manager, context);
        var client = new DavDatabaseClient(context);

        var get = await InvokeAsync(new UsenetSettingsController(client, manager, encryption, persistence), null);
        var snapshot = Assert.IsType<UsenetSettingsResponse>(Assert.IsType<OkObjectResult>(get).Value);
        Assert.True(snapshot.Providers[0].HasPassword);
        Assert.DoesNotContain("provider-secret", JsonSerializer.Serialize(snapshot));

        var post = await InvokeAsync(new UsenetSettingsController(client, manager, encryption, persistence), JsonSerializer.Serialize(new
        {
            revision = snapshot.Revision,
            providers = new[] { new { id = snapshot.Providers[0].Id, host = "news.changed.test", port = 563, ssl = true, user = "alice", max = 10, type = 1, password = "" } },
        }));
        Assert.Equal(200, Assert.IsType<OkObjectResult>(post).StatusCode);
        var stored = await context.ConfigItems.SingleAsync(x => x.ConfigName == SetupConfigKeys.UsenetProviders);
        var plaintext = encryption.Decrypt(SetupConfigKeys.UsenetProviders, stored.ConfigValue).plaintext;
        Assert.Contains("provider-secret", plaintext);
        Assert.Contains("news.changed.test", plaintext);
        Assert.NotNull(changedEvent);
        var eventJson = JsonSerializer.Serialize(changedEvent!.ChangedConfig);
        Assert.DoesNotContain("provider-secret", eventJson);
        Assert.Contains("[redacted]", eventJson);
    }

    [Fact]
    public async Task StaleRevisionReturnsConflictWithoutWriting()
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"nzbdav-tests/usenet-settings-{Guid.NewGuid():N}");
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("CONFIG_PATH", configPath), ("DATABASE_URL", null), ("FRONTEND_BACKEND_API_KEY", "unit-api-key"),
            ("NZBDAV_MASTER_KEY", Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))));
        Directory.CreateDirectory(configPath);
        await using var context = await CreateContextAsync();
        using var encryption = new ConfigEncryptionService();
        var manager = new ConfigManager(encryption);
        var persistence = new SetupConfigPersistence(manager, context);
        var client = new DavDatabaseClient(context);
        var first = await InvokeAsync(new UsenetSettingsController(client, manager, encryption, persistence), JsonSerializer.Serialize(new
        {
            revision = encryption.CreateOpaqueRevision(""),
            providers = Array.Empty<object>(),
        }));
        Assert.Equal(200, Assert.IsType<OkObjectResult>(first).StatusCode);
        var before = await context.ConfigItems.SingleAsync(x => x.ConfigName == SetupConfigKeys.UsenetProviders);
        var stale = await InvokeAsync(new UsenetSettingsController(client, manager, encryption, persistence), JsonSerializer.Serialize(new
        {
            revision = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            providers = Array.Empty<object>(),
        }));
        var conflict = Assert.IsType<ConflictObjectResult>(stale);
        Assert.Equal(409, conflict.StatusCode);
        var after = await context.ConfigItems.SingleAsync(x => x.ConfigName == SetupConfigKeys.UsenetProviders);
        Assert.Equal(before.ConfigValue, after.ConfigValue);
    }

    [Fact]
    public async Task SQLite_commit_before_throw_is_re_read_and_published_once_as_success()
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"nzbdav-tests/usenet-commit-before-throw-{Guid.NewGuid():N}");
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("CONFIG_PATH", configPath), ("DATABASE_URL", null), ("FRONTEND_BACKEND_API_KEY", "unit-api-key"),
            ("NZBDAV_MASTER_KEY", Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))));
        Directory.CreateDirectory(configPath);
        await using var context = await CreateContextAsync();
        using var encryption = new ConfigEncryptionService();
        var manager = new ConfigManager(encryption);
        var eventCount = 0;
        manager.OnConfigChanged += (_, _) => eventCount++;
        var persistence = new SetupConfigPersistence(manager, context);
        var controller = new UsenetSettingsController(
            new DavDatabaseClient(context), manager, encryption, persistence,
            async (isolation, cancellationToken) => new CommitFaultTransaction(
                await context.Database.BeginTransactionAsync(isolation, cancellationToken),
                CommitFaultTransaction.Mode.CommitThenThrow));

        var initial = await InvokeAsync(controller, null);
        var snapshot = Assert.IsType<UsenetSettingsResponse>(Assert.IsType<OkObjectResult>(initial).Value);
        var post = await InvokeAsync(controller, JsonSerializer.Serialize(new
        {
            revision = snapshot.Revision,
            providers = new[] { new { host = "news.example.test", port = 563, ssl = true, user = "alice", max = 4, type = 1, password = "commit-secret" } },
        }));

        var ok = Assert.IsType<OkObjectResult>(post);
        Assert.Equal(200, ok.StatusCode);
        Assert.Equal(1, eventCount);
        var responseJson = JsonSerializer.Serialize(ok.Value);
        Assert.DoesNotContain("commit-secret", responseJson);
        Assert.Contains("news.example.test", responseJson);
    }

    [Fact]
    public async Task SQLite_cancellation_after_commit_is_a_recovery_conflict_without_echoing_secret()
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"nzbdav-tests/usenet-commit-cancel-{Guid.NewGuid():N}");
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("CONFIG_PATH", configPath), ("DATABASE_URL", null), ("FRONTEND_BACKEND_API_KEY", "unit-api-key"),
            ("NZBDAV_MASTER_KEY", Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))));
        Directory.CreateDirectory(configPath);
        await using var context = await CreateContextAsync();
        using var encryption = new ConfigEncryptionService();
        var manager = new ConfigManager(encryption);
        var persistence = new SetupConfigPersistence(manager, context);
        var controller = new UsenetSettingsController(
            new DavDatabaseClient(context), manager, encryption, persistence,
            async (isolation, cancellationToken) => new CommitFaultTransaction(
                await context.Database.BeginTransactionAsync(isolation, cancellationToken),
                CommitFaultTransaction.Mode.CommitThenCancel));

        var initial = await InvokeAsync(controller, null);
        var snapshot = Assert.IsType<UsenetSettingsResponse>(Assert.IsType<OkObjectResult>(initial).Value);
        var secret = "cancel-secret";
        var post = await InvokeAsync(controller, JsonSerializer.Serialize(new
        {
            revision = snapshot.Revision,
            providers = new[] { new { host = "news.example.test", port = 563, ssl = true, user = "alice", max = 4, type = 1, password = secret } },
        }));

        var conflict = Assert.IsType<ConflictObjectResult>(post);
        Assert.Equal(409, conflict.StatusCode);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(conflict.Value));
        var stored = await context.ConfigItems.SingleAsync(x => x.ConfigName == SetupConfigKeys.UsenetProviders);
        Assert.Contains(secret, encryption.Decrypt(SetupConfigKeys.UsenetProviders, stored.ConfigValue).plaintext);
    }

    private static async Task<DavDatabaseContext> CreateContextAsync()
    {
        var context = new DavDatabaseContext();
        await context.Database.MigrateAsync();
        return context;
    }

    private sealed class CommitFaultTransaction(IDbContextTransaction inner, CommitFaultTransaction.Mode mode) : IDbContextTransaction
    {
        public enum Mode { CommitThenThrow, CommitThenCancel }
        public Guid TransactionId => inner.TransactionId;
        public bool SupportsSavepoints => inner.SupportsSavepoints;

        public void Commit() => CommitAsync(CancellationToken.None).GetAwaiter().GetResult();
        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            await inner.CommitAsync(cancellationToken);
            throw mode == Mode.CommitThenCancel
                ? new OperationCanceledException("commit acknowledgement was cancelled")
                : new IOException("commit acknowledgement was lost");
        }

        public void Rollback() => RollbackAsync(CancellationToken.None).GetAwaiter().GetResult();
        public Task RollbackAsync(CancellationToken cancellationToken = default) => inner.RollbackAsync(cancellationToken);
        public void CreateSavepoint(string name) => inner.CreateSavepoint(name);
        public Task CreateSavepointAsync(string name, CancellationToken cancellationToken = default) => inner.CreateSavepointAsync(name, cancellationToken);
        public void RollbackToSavepoint(string name) => inner.RollbackToSavepoint(name);
        public Task RollbackToSavepointAsync(string name, CancellationToken cancellationToken = default) => inner.RollbackToSavepointAsync(name, cancellationToken);
        public void ReleaseSavepoint(string name) => inner.ReleaseSavepoint(name);
        public Task ReleaseSavepointAsync(string name, CancellationToken cancellationToken = default) => inner.ReleaseSavepointAsync(name, cancellationToken);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
        public void Dispose() => inner.Dispose();
    }

    private static async Task<IActionResult> InvokeAsync(UsenetSettingsController controller, string? body)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["x-api-key"] = "unit-api-key";
        if (body is null)
        {
            context.Request.Method = "GET";
        }
        else
        {
            context.Request.Method = "POST";
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = bytes.Length;
            context.Request.Body = new MemoryStream(bytes);
        }
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        return await controller.HandleApiRequest();
    }
}
