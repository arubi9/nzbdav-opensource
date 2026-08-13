using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Setup.Core;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

[Collection(nameof(backend.Tests.Config.EnvironmentVariableCollection))]
public sealed class SetupRevocationRecoveryServiceTests
{
    [Fact]
    public async Task StartupRecovery_UsesProductionServiceAndRetiresPendingSession()
    {
        var configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-recovery-hosted-{Guid.NewGuid():N}");
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("CONFIG_PATH", configPath),
            ("DATABASE_URL", null),
            ("NZBDAV_FULL_STACK", "true"),
            ("SETUP_JELLYFIN_URL", "http://jellyfin.test"),
            ("NZBDAV_MASTER_KEY", Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))));
        Directory.CreateDirectory(configPath);

        await using var context = await CreateMigratedContextAsync();
        var manager = new ConfigManager(new ConfigEncryptionService());
        await manager.LoadConfig();
        var persistence = new SetupConfigPersistence(manager, context);
        await persistence.SaveAsync(new SetupSecretValues
        {
            JellyfinApiKey = "current-session",
            RevocationPending = true,
            RevocationPendingToken = "old-session",
        }, completed: false);

        var handler = new LogoutHandler();
        await using var serviceContext = await CreateMigratedContextAsync();
        var service = new SetupGrantService(
            serviceContext,
            manager,
            TimeProvider.System,
            () => new HttpClient(handler, disposeHandler: false));
        var hosted = new SetupRevocationRecoveryService(
            new SingleServiceScopeFactory(service),
            NullLogger<SetupRevocationRecoveryService>.Instance,
            TimeSpan.FromSeconds(5));

        await hosted.StartAsync(CancellationToken.None);
        await hosted.ExecutionCompleted;
        await hosted.StopAsync(CancellationToken.None);

        Assert.Equal("old-session", handler.LogoutToken);
        Assert.False(await service.IsRevocationPendingAsync());
        Assert.Equal("current-session", manager.GetSetupJellyfinApiKey());
        handler.Dispose();
    }

    [Fact]
    public async Task StartupRecovery_TimeoutLeavesDurablePendingMarkerAndReturns()
    {
        var configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-recovery-hosted-timeout-{Guid.NewGuid():N}");
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("CONFIG_PATH", configPath),
            ("DATABASE_URL", null),
            ("NZBDAV_FULL_STACK", "true"),
            ("SETUP_JELLYFIN_URL", "http://jellyfin.test"),
            ("NZBDAV_MASTER_KEY", Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))));
        Directory.CreateDirectory(configPath);

        await using var context = await CreateMigratedContextAsync();
        var manager = new ConfigManager(new ConfigEncryptionService());
        await manager.LoadConfig();
        var persistence = new SetupConfigPersistence(manager, context);
        await persistence.SaveAsync(new SetupSecretValues
        {
            JellyfinApiKey = "current-session",
            RevocationPending = true,
            RevocationPendingToken = "old-session",
        }, completed: false);

        var handler = new BlockingLogoutHandler();
        await using var serviceContext = await CreateMigratedContextAsync();
        var service = new SetupGrantService(
            serviceContext,
            manager,
            TimeProvider.System,
            () => new HttpClient(handler, disposeHandler: false));
        var hosted = new SetupRevocationRecoveryService(
            new SingleServiceScopeFactory(service),
            NullLogger<SetupRevocationRecoveryService>.Instance,
            TimeSpan.FromMilliseconds(50));

        await hosted.StartAsync(CancellationToken.None);
        await handler.Started.Task;
        await handler.Canceled.Task;
        await hosted.ExecutionCompleted;
        await hosted.StopAsync(CancellationToken.None);

        Assert.True(await service.IsRevocationPendingAsync());
        handler.Dispose();
    }

    private static async Task<DavDatabaseContext> CreateMigratedContextAsync()
    {
        var context = new DavDatabaseContext();
        await context.Database.MigrateAsync();
        return context;
    }

    private sealed class SingleServiceScopeFactory(SetupGrantService service) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new SingleServiceScope(service);
    }

    private sealed class SingleServiceScope(SetupGrantService service) : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; } = new SingleServiceProvider(service);

        public void Dispose()
        {
        }
    }

    private sealed class SingleServiceProvider(SetupGrantService service) : IServiceProvider
    {
        public object? GetService(Type serviceType)
            => serviceType == typeof(SetupGrantService) ? service : null;
    }

    private sealed class LogoutHandler : HttpMessageHandler
    {
        public string? LogoutToken { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/Sessions/Logout")
            {
                LogoutToken = request.Headers.GetValues("X-Emby-Token").Single();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class BlockingLogoutHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/Sessions/Logout")
            {
                Started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    Canceled.TrySetResult();
                    throw;
                }
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
