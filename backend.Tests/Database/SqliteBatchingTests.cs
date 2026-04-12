using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Database;

[Collection(nameof(SqliteBatchingCollection))]
public sealed class SqliteBatchingTests(SqliteBatchingFixture fixture)
{
    [Fact]
    public async Task SaveChangesAsync_PersistsLargeContentBatch_InSqlite()
    {
        await fixture.ResetAsync();
        await using var dbContext = new DavDatabaseContext();
        await dbContext.Database.MigrateAsync();

        var category = DavItem.New(
            Guid.NewGuid(),
            DavItem.ContentFolder,
            "movies",
            null,
            DavItem.ItemType.Directory,
            null,
            null);

        dbContext.Items.Add(category);

        for (var i = 0; i < 120; i++)
        {
            var item = DavItem.New(
                Guid.NewGuid(),
                category,
                $"Movie.Part.{i:D3}.mkv",
                1024 + i,
                DavItem.ItemType.NzbFile,
                DateTimeOffset.UtcNow,
                null);

            dbContext.Items.Add(item);
            dbContext.NzbFiles.Add(new DavNzbFile
            {
                Id = item.Id,
                SegmentIds = [$"segment-{i:D3}-a", $"segment-{i:D3}-b"]
            });
        }

        await dbContext.SaveChangesAsync();

        await using var verifyContext = new DavDatabaseContext();
        var savedItems = await verifyContext.Items.CountAsync(x => x.Path.StartsWith("/content/movies/"));
        var savedFiles = await verifyContext.NzbFiles.CountAsync();

        Assert.Equal(120, savedItems);
        Assert.Equal(120, savedFiles);
    }
}

public sealed class SqliteBatchingFixture : IAsyncLifetime
{
    private readonly string _configPath = Path.Join(Path.GetTempPath(), "nzbdav-tests", "sqlite-batching");

    public SqliteBatchingFixture()
    {
        Environment.SetEnvironmentVariable("CONFIG_PATH", _configPath);
    }

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_configPath);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => ResetAsync();

    public async Task ResetAsync()
    {
        await Task.Yield();
        SqliteConnection.ClearAllPools();
        DeleteIfExists(DavDatabaseContext.DatabaseFilePath);
        DeleteIfExists(DavDatabaseContext.DatabaseFilePath + "-wal");
        DeleteIfExists(DavDatabaseContext.DatabaseFilePath + "-shm");
    }

    private static void DeleteIfExists(string path)
    {
        if (!File.Exists(path))
            return;

        File.Delete(path);
    }
}

[CollectionDefinition(nameof(SqliteBatchingCollection), DisableParallelization = true)]
public sealed class SqliteBatchingCollection : ICollectionFixture<SqliteBatchingFixture>
{
}
