using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Tests.Clients.Usenet.Caching;

namespace backend.Tests.Services;

[Collection(nameof(NzbWebDAV.Tests.Clients.Usenet.Caching.SharedHeaderCacheCollection))]
public sealed class ConnectionPoolCoordinatorTests : IClassFixture<PostgresHeaderCacheFixture>
{
    private readonly PostgresHeaderCacheFixture _fixture;

    public ConnectionPoolCoordinatorTests(PostgresHeaderCacheFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task RebalanceAllOnce_CreatesClaimAndAppliesShare()
    {
        Skip.IfNot(_fixture.IsAvailable, "Docker is required for this integration test.");

        await _fixture.ResetAsync();
        using var environment = new backend.Tests.Config.TemporaryEnvironment(("DATABASE_URL", _fixture.ConnectionString));
        var configManager = CreateConfigManager(maxConnections: 12);
        var appliedClaims = new Dictionary<int, int>();
        var coordinator = new ConnectionPoolCoordinator(
            configManager,
            (providerIndex, claim) => appliedClaims[providerIndex] = claim,
            () => new DavDatabaseContext(),
            () => "node-a");

        await coordinator.RebalanceAllOnce(CancellationToken.None);

        Assert.Equal(12, appliedClaims[0]);
        await using var dbContext = new DavDatabaseContext();
        var claim = await dbContext.ConnectionPoolClaims.SingleAsync();
        Assert.Equal("node-a", claim.NodeId);
        Assert.Equal(12, claim.ClaimedSlots);
    }

    [Fact]
    public async Task RebalanceAllOnce_FallsBackToConfiguredSlots_WhenDatabaseUnavailable()
    {
        var configManager = CreateConfigManager(maxConnections: 8);
        var appliedClaims = new Dictionary<int, int>();
        var coordinator = new ConnectionPoolCoordinator(
            configManager,
            (providerIndex, claim) => appliedClaims[providerIndex] = claim,
            () => throw new InvalidOperationException("db down"),
            () => "node-a");

        await coordinator.RebalanceAllOnce(CancellationToken.None);

        Assert.Equal(8, appliedClaims[0]);
    }

    [SkippableFact]
    public async Task RebalanceAllOnce_DoesNotOversubscribe_WhenMoreNodesThanSlots()
    {
        Skip.IfNot(_fixture.IsAvailable, "Docker is required for this integration test.");

        await _fixture.ResetAsync();
        using var environment = new backend.Tests.Config.TemporaryEnvironment(("DATABASE_URL", _fixture.ConnectionString));
        var configManager = CreateConfigManager(maxConnections: 2);

        await using (var dbContext = new DavDatabaseContext())
        {
            dbContext.ConnectionPoolClaims.Add(new NzbWebDAV.Database.Models.ConnectionPoolClaim
            {
                NodeId = "node-a",
                ProviderIndex = 0,
                ClaimedSlots = 1,
                HeartbeatAt = DateTime.UtcNow
            });
            dbContext.ConnectionPoolClaims.Add(new NzbWebDAV.Database.Models.ConnectionPoolClaim
            {
                NodeId = "node-b",
                ProviderIndex = 0,
                ClaimedSlots = 1,
                HeartbeatAt = DateTime.UtcNow
            });
            await dbContext.SaveChangesAsync();
        }

        var appliedClaims = new Dictionary<int, int>();
        var coordinator = new ConnectionPoolCoordinator(
            configManager,
            (providerIndex, claim) => appliedClaims[providerIndex] = claim,
            () => new DavDatabaseContext(),
            () => "node-c");

        await coordinator.RebalanceAllOnce(CancellationToken.None);

        Assert.Equal(0, appliedClaims[0]);
        await using var verifyContext = new DavDatabaseContext();
        var totalClaimed = await verifyContext.ConnectionPoolClaims.SumAsync(x => x.ClaimedSlots);
        Assert.Equal(2, totalClaimed);
    }

    private static ConfigManager CreateConfigManager(int maxConnections)
    {
        var configManager = new ConfigManager();
        var providerConfig = new UsenetProviderConfig
        {
            Providers =
            [
                new UsenetProviderConfig.ConnectionDetails
                {
                    Type = ProviderType.Pooled,
                    Host = "news.example.test",
                    Port = 563,
                    UseSsl = true,
                    User = "user",
                    Pass = "pass",
                    MaxConnections = maxConnections
                }
            ]
        };

        configManager.UpdateValues(
        [
            new NzbWebDAV.Database.Models.ConfigItem
            {
                ConfigName = "usenet.providers",
                ConfigValue = JsonSerializer.Serialize(providerConfig)
            }
        ]);
        return configManager;
    }
}
