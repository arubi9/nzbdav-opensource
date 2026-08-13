using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace backend.Tests.Services;

[Collection(nameof(backend.Tests.Config.EnvironmentVariableCollection))]
public sealed class HealthCheckSegmentIdChunkingTests
{
    // A 56GB release carries far more segments than SQLite's 32766 variable
    // limit, and EF spends one variable per element of a Contains() list. Before
    // chunking, importing such an NZB failed with
    // "SQLite Error 1: 'too many SQL variables'".
    private const int SegmentCount = 40000;

    [Fact]
    public async Task CheckMissingSegmentIdsAsync_HandlesMoreSegmentsThanTheSqlVariableLimit()
    {
        var configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"segment-chunk-{Guid.NewGuid():N}");
        Directory.CreateDirectory(configPath);
        using var _ = new backend.Tests.Config.TemporaryEnvironment(
            ("DATABASE_URL", null),
            ("CONFIG_PATH", configPath));

        await using var dbContext = new DavDatabaseContext();
        await dbContext.Database.MigrateAsync();

        var segmentIds = Enumerable.Range(0, SegmentCount)
            .Select(index => $"segment-{index}@example.test")
            .ToArray();

        // No rows recorded as missing, so nothing should throw and every id
        // must be queried rather than silently truncated to one chunk.
        await HealthCheckService.CheckMissingSegmentIdsAsync(dbContext, segmentIds, CancellationToken.None);

        // Recording the last id as missing must still be observed, which only
        // holds if chunks beyond the first are actually queried.
        dbContext.Set<MissingSegmentId>().Add(new MissingSegmentId
        {
            SegmentId = segmentIds[^1],
            DetectedAt = DateTimeOffset.UtcNow,
        });
        await dbContext.SaveChangesAsync();

        await Assert.ThrowsAsync<NzbWebDAV.Exceptions.UsenetArticleNotFoundException>(
            () => HealthCheckService.CheckMissingSegmentIdsAsync(
                dbContext, [segmentIds[^1]], CancellationToken.None));
    }
}
