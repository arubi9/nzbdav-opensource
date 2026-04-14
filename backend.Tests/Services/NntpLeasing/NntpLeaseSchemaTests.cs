using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Tests.Clients.Usenet.Caching;

namespace backend.Tests.Services.NntpLeasing;

[Collection(nameof(backend.Tests.Config.EnvironmentVariableCollection))]
public sealed class NntpLeaseSchemaTests : IClassFixture<PostgresHeaderCacheFixture>
{
    private readonly PostgresHeaderCacheFixture _fixture;

    public NntpLeaseSchemaTests(PostgresHeaderCacheFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task SaveChanges_PersistsHeartbeatAndLeaseRows()
    {
        Skip.IfNot(_fixture.IsAvailable, "Docker is required for this integration test.");

        await _fixture.ResetAsync();
        using var environment = new backend.Tests.Config.TemporaryEnvironment(("DATABASE_URL", _fixture.ConnectionString));

        var heartbeatAt = DateTime.UtcNow;
        var leaseUntil = heartbeatAt.AddMinutes(5);
        var updatedAt = heartbeatAt.AddSeconds(1);

        await using (var dbContext = new DavDatabaseContext())
        {
            dbContext.NntpNodeHeartbeats.Add(new NntpNodeHeartbeat
            {
                NodeId = "node-a",
                ProviderIndex = 0,
                Role = NodeRole.Streaming,
                Region = "us-east-1",
                DesiredSlots = 2,
                ActiveSlots = 1,
                LiveSlots = 1,
                HasDemand = true,
                HeartbeatAt = heartbeatAt
            });

            dbContext.NntpConnectionLeases.Add(new NntpConnectionLease
            {
                NodeId = "node-a",
                ProviderIndex = 0,
                Role = NodeRole.Streaming,
                ReservedSlots = 2,
                BorrowedSlots = 1,
                GrantedSlots = 3,
                Epoch = 42,
                LeaseUntil = leaseUntil,
                UpdatedAt = updatedAt
            });

            dbContext.Set<NntpLeaseEpoch>().Add(new NntpLeaseEpoch
            {
                ProviderIndex = 0,
                Epoch = 42
            });

            await dbContext.SaveChangesAsync();
        }

        await using (var dbContext = new DavDatabaseContext())
        {
            var heartbeat = await dbContext.NntpNodeHeartbeats.AsNoTracking().SingleAsync();
            var lease = await dbContext.NntpConnectionLeases.AsNoTracking().SingleAsync();
            var leaseEpoch = await dbContext.Set<NntpLeaseEpoch>().AsNoTracking().SingleAsync();

            Assert.Equal("node-a", heartbeat.NodeId);
            Assert.Equal(0, heartbeat.ProviderIndex);
            Assert.Equal(NodeRole.Streaming, heartbeat.Role);
            Assert.Equal("us-east-1", heartbeat.Region);
            Assert.Equal(2, heartbeat.DesiredSlots);
            Assert.Equal(1, heartbeat.ActiveSlots);
            Assert.Equal(1, heartbeat.LiveSlots);
            Assert.True(heartbeat.HasDemand);
            Assert.Equal(heartbeatAt, heartbeat.HeartbeatAt, TimeSpan.FromMilliseconds(1));

            Assert.Equal("node-a", lease.NodeId);
            Assert.Equal(0, lease.ProviderIndex);
            Assert.Equal(NodeRole.Streaming, lease.Role);
            Assert.Equal(2, lease.ReservedSlots);
            Assert.Equal(1, lease.BorrowedSlots);
            Assert.Equal(3, lease.GrantedSlots);
            Assert.Equal(42, lease.Epoch);
            Assert.Equal(leaseUntil, lease.LeaseUntil, TimeSpan.FromMilliseconds(1));
            Assert.Equal(updatedAt, lease.UpdatedAt, TimeSpan.FromMilliseconds(1));

            Assert.Equal(0, leaseEpoch.ProviderIndex);
            Assert.Equal(42, leaseEpoch.Epoch);
        }
    }
}
