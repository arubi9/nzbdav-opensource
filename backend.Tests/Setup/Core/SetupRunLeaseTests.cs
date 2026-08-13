using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Services;
using NzbWebDAV.Setup.Core;
using NzbWebDAV.Tests.Clients.Usenet.Caching;

namespace NzbWebDAV.Tests.Setup.Core;

[Collection(nameof(backend.Tests.Config.EnvironmentVariableCollection))]
public sealed class SetupRunLeaseTests
{
    [Fact]
    public async Task SQLite_LeaseBindsGrantAndPurpose_Heartbeats_ExpiresAndCasReleases()
    {
        using var environment = CreateEnvironment(databaseUrl: null);
        await RunLeaseScenarioAsync();
    }

    [Fact]
    public async Task SQLite_StaleGenerationCannotPublishProgressOrCompletion()
    {
        using var environment = CreateEnvironment(databaseUrl: null);
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var firstContext = await CreateMigratedContextAsync();
        await using var secondContext = await CreateMigratedContextAsync();
        var firstManager = new ConfigManager(new ConfigEncryptionService());
        await firstManager.LoadConfig();
        var firstPersistence = new SetupConfigPersistence(firstManager, firstContext);
        await firstPersistence.SaveAsync(new SetupSecretValues
        {
            Indexers = "[]",
            PluginApiKey = "plugin-key",
            JellyfinApiKey = "session",
        }, completed: false);

        var firstLeaseService = new SetupRunLeaseService(firstContext, clock, TimeSpan.FromMinutes(1), "owner-a");
        var secondLeaseService = new SetupRunLeaseService(secondContext, clock, TimeSpan.FromMinutes(1), "owner-b");
        var firstLease = await firstLeaseService.TryAcquireAsync("grant", SetupGrantConstants.NormalPurpose);
        Assert.NotNull(firstLease);
        var firstGrantService = new SetupGrantService(
            firstContext,
            firstManager,
            clock,
            () => new HttpClient(new NoopHandler(), disposeHandler: false),
            firstPersistence);
        var plan = await firstGrantService.PrepareCompletionLogoutUnderMutationGateAsync(
            CancellationToken.None,
            firstLease);

        clock.Advance(TimeSpan.FromMinutes(2));
        var takeover = await secondLeaseService.TryAcquireAsync("grant", SetupGrantConstants.NormalPurpose);
        Assert.NotNull(takeover);

        await Assert.ThrowsAsync<BadHttpRequestException>(() => firstPersistence.SaveWithLeaseAsync(
            new SetupSecretValues { RunProgress = "stale" },
            completed: false,
            firstLease,
            CancellationToken.None));
        await Assert.ThrowsAsync<BadHttpRequestException>(() => firstGrantService.CompleteAfterExternalLogoutUnderMutationGateAsync(
            plan,
            "[]",
            "plugin-key",
            null,
            null,
            CancellationToken.None,
            firstLease));

        await using var verify = new DavDatabaseContext();
        Assert.False(await verify.ConfigItems.AnyAsync(row =>
            row.ConfigName == SetupConfigKeys.Completed && row.ConfigValue == "true"));
        Assert.False(await verify.ConfigItems.AnyAsync(row =>
            row.ConfigName == SetupConfigKeys.RunProgress && row.ConfigValue == "stale"));
        Assert.True(await verify.SetupCompletionOperations.AnyAsync());
        await takeover.ReleaseAsync();
    }

    private static async Task RunLeaseScenarioAsync()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var firstContext = await CreateMigratedContextAsync();
        await using var secondContext = await CreateMigratedContextAsync();
        var first = new SetupRunLeaseService(firstContext, clock, TimeSpan.FromMinutes(1), "owner-a");
        var second = new SetupRunLeaseService(secondContext, clock, TimeSpan.FromMinutes(1), "owner-b");

        var firstLease = await first.TryAcquireAsync("grant-a", SetupGrantConstants.NormalPurpose);
        Assert.NotNull(firstLease);
        Assert.Equal(SetupGrantCrypto.ComputeIssuedTokenHash("grant-a"), firstLease.GrantHash);
        Assert.Equal(SetupGrantConstants.NormalPurpose, firstLease.Purpose);
        Assert.Equal(1, firstLease.Generation);

        Assert.Null(await second.TryAcquireAsync("grant-b", SetupGrantConstants.NormalPurpose));
        Assert.Null(await second.TryAcquireAsync("grant-a", SetupGrantConstants.RepairPurpose));
        Assert.True(await firstLease.HeartbeatAsync());

        clock.Advance(TimeSpan.FromMinutes(2));
        var takeover = await second.TryAcquireAsync("grant-a", SetupGrantConstants.NormalPurpose);
        Assert.NotNull(takeover);
        Assert.Equal(firstLease.Generation + 1, takeover.Generation);
        Assert.False(await firstLease.AssertCurrentAsync());
        Assert.False(await firstLease.HeartbeatAsync());
        Assert.True(await takeover.AssertCurrentAsync());

        // An expired/stale owner cannot release the replacement generation.
        await firstLease.ReleaseAsync();
        Assert.True(await takeover.AssertCurrentAsync());

        await takeover.ReleaseAsync();
        var afterRelease = await first.TryAcquireAsync("grant-a", SetupGrantConstants.NormalPurpose);
        Assert.NotNull(afterRelease);
        Assert.Equal(takeover.Generation + 1, afterRelease.Generation);
        await afterRelease.ReleaseAsync();
    }

    private static async Task<DavDatabaseContext> CreateMigratedContextAsync()
    {
        var context = new DavDatabaseContext();
        await context.Database.MigrateAsync();
        return context;
    }

    private static backend.Tests.Config.TemporaryEnvironment CreateEnvironment(string? databaseUrl)
    {
        var path = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-run-lease-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return new backend.Tests.Config.TemporaryEnvironment(
            ("CONFIG_PATH", path),
            ("DATABASE_URL", databaseUrl),
            ("NZBDAV_MASTER_KEY", Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))),
            ("NZBDAV_FULL_STACK", "true"));
    }

    private sealed class NoopHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NoContent));
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public void Advance(TimeSpan amount) => _now += amount;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}

[Collection(nameof(backend.Tests.Config.EnvironmentVariableCollection))]
public sealed class SetupRunLeasePostgresTests : IClassFixture<PostgresHeaderCacheFixture>
{
    private readonly PostgresHeaderCacheFixture _fixture;

    public SetupRunLeasePostgresTests(PostgresHeaderCacheFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task PostgreSql_StatusVerificationLeaseContendsAndCasReleases()
    {
        Assert.SkipUnless(_fixture.IsAvailable, "Docker is required for PostgreSQL verification lease tests.");
        await _fixture.ResetAsync();
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("DATABASE_URL", _fixture.ConnectionString),
            ("CONFIG_PATH", Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-run-lease-verification-pg-{Guid.NewGuid():N}")),
            ("NZBDAV_MASTER_KEY", Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))),
            ("NZBDAV_FULL_STACK", "true"));
        Directory.CreateDirectory(DavDatabaseContext.ConfigPath);

        await using var firstContext = new DavDatabaseContext();
        await using var secondContext = new DavDatabaseContext();
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var first = new SetupRunLeaseService(firstContext, clock, ownerId: "status-owner-a");
        var second = new SetupRunLeaseService(secondContext, clock, ownerId: "status-owner-b");

        var held = await first.TryAcquireAsync(string.Empty, "status-verification");
        Assert.NotNull(held);
        Assert.Equal("status-verification", held.Purpose);
        Assert.Null(await second.TryAcquireAsync(string.Empty, "status-verification"));

        clock.Advance(TimeSpan.FromMinutes(2));
        var replacement = await second.TryAcquireAsync(string.Empty, "status-verification");
        Assert.NotNull(replacement);
        Assert.Equal(held.Generation + 1, replacement.Generation);
        Assert.False(await held.AssertCurrentAsync());
        Assert.True(await replacement.AssertCurrentAsync());
        await replacement.ReleaseAsync();
    }

    [Fact]
    public async Task PostgreSql_IndependentContextsEnforceGenerationAndCasRelease()
    {
        Assert.SkipUnless(_fixture.IsAvailable, "Docker is required for PostgreSQL setup lease tests.");
        await _fixture.ResetAsync();
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("DATABASE_URL", _fixture.ConnectionString),
            ("CONFIG_PATH", Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-run-lease-pg-{Guid.NewGuid():N}")),
            ("NZBDAV_MASTER_KEY", Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))),
            ("NZBDAV_FULL_STACK", "true"));
        Directory.CreateDirectory(DavDatabaseContext.ConfigPath);

        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var firstContext = new DavDatabaseContext();
        await using var secondContext = new DavDatabaseContext();
        var first = new SetupRunLeaseService(firstContext, clock, TimeSpan.FromMinutes(1), "owner-a");
        var second = new SetupRunLeaseService(secondContext, clock, TimeSpan.FromMinutes(1), "owner-b");

        var firstLease = await first.TryAcquireAsync("grant", SetupGrantConstants.NormalPurpose);
        Assert.NotNull(firstLease);
        Assert.Null(await second.TryAcquireAsync("grant", SetupGrantConstants.NormalPurpose));
        clock.Advance(TimeSpan.FromMinutes(2));
        var takeover = await second.TryAcquireAsync("grant", SetupGrantConstants.NormalPurpose);
        Assert.NotNull(takeover);
        Assert.False(await firstLease.AssertCurrentAsync());
        await firstLease.ReleaseAsync();
        Assert.True(await takeover.AssertCurrentAsync());
        await takeover.ReleaseAsync();
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public void Advance(TimeSpan amount) => _now += amount;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
