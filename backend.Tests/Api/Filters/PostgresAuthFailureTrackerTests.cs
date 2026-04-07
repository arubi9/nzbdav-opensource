using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Api.Filters;
using NzbWebDAV.Database;
using NzbWebDAV.Tests.Clients.Usenet.Caching;

namespace NzbWebDAV.Tests.Api.Filters;

[Collection(nameof(NzbWebDAV.Tests.Clients.Usenet.Caching.SharedHeaderCacheCollection))]
public sealed class PostgresAuthFailureTrackerTests : IClassFixture<PostgresHeaderCacheFixture>
{
    private readonly PostgresHeaderCacheFixture _fixture;

    public PostgresAuthFailureTrackerTests(PostgresHeaderCacheFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task RecordFailure_UsesSharedPostgresCounter()
    {
        Skip.IfNot(_fixture.IsAvailable, "Docker is required for this integration test.");

        await _fixture.ResetAsync();
        using var environment = new backend.Tests.Config.TemporaryEnvironment(("DATABASE_URL", _fixture.ConnectionString));
        var tracker = new PostgresAuthFailureTracker(new AuthFailureTracker());

        for (var i = 0; i < 10; i++)
            await tracker.RecordFailureAsync("203.0.113.5");

        Assert.True(await tracker.IsBlockedAsync("203.0.113.5"));
    }

    [Fact]
    public async Task RecordFailure_FallsBackToInMemory_OnDbError()
    {
        var fallback = new AuthFailureTracker();
        var tracker = new PostgresAuthFailureTracker(
            fallback,
            dbContextFactory: () => throw new InvalidOperationException("db down"));

        await tracker.RecordFailureAsync("203.0.113.8");

        Assert.Equal(1, fallback.TrackedIpCount);
        Assert.False(await tracker.IsBlockedAsync("203.0.113.9"));
    }

    [SkippableFact]
    public async Task SweepOnce_RemovesExpiredRows_ButKeepsFreshRows()
    {
        Skip.IfNot(_fixture.IsAvailable, "Docker is required for this integration test.");

        await _fixture.ResetAsync();
        using var environment = new backend.Tests.Config.TemporaryEnvironment(("DATABASE_URL", _fixture.ConnectionString));

        await using (var dbContext = new DavDatabaseContext())
        {
            dbContext.AuthFailures.Add(new NzbWebDAV.Database.Models.AuthFailureEntry
            {
                IpAddress = "old",
                FailureCount = 3,
                WindowStart = DateTime.UtcNow.AddMinutes(-10)
            });
            dbContext.AuthFailures.Add(new NzbWebDAV.Database.Models.AuthFailureEntry
            {
                IpAddress = "new",
                FailureCount = 3,
                WindowStart = DateTime.UtcNow.AddSeconds(-10)
            });
            await dbContext.SaveChangesAsync();
        }

        var sweeper = new NzbWebDAV.Services.AuthFailuresSweeper();
        await sweeper.SweepOnce(CancellationToken.None);

        await using var verifyContext = new DavDatabaseContext();
        Assert.False(await verifyContext.AuthFailures.AnyAsync(x => x.IpAddress == "old"));
        Assert.True(await verifyContext.AuthFailures.AnyAsync(x => x.IpAddress == "new"));
    }
}
