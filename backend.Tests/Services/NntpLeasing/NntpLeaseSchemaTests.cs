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
                HeartbeatAt = DateTime.UtcNow
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
                LeaseUntil = DateTime.UtcNow.AddMinutes(5),
                UpdatedAt = DateTime.UtcNow
            });

            await dbContext.SaveChangesAsync();
        }

        await using (var dbContext = new DavDatabaseContext())
        {
            Assert.Equal(1, await dbContext.NntpNodeHeartbeats.CountAsync());
            Assert.Equal(1, await dbContext.NntpConnectionLeases.CountAsync());
        }
    }
}
