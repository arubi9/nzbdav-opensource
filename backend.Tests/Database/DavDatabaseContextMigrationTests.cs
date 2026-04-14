using System.Data;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;

namespace backend.Tests.Database;

[Collection(nameof(backend.Tests.Config.EnvironmentVariableCollection))]
public sealed class DavDatabaseContextMigrationTests
{
    [Fact]
    public async Task MigrateAsync_CreatesRoleAwareNntpLeaseTables_OnSqlite()
    {
        var configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"nntp-lease-migration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(configPath);

        try
        {
            using var environment = new backend.Tests.Config.TemporaryEnvironment(
                ("DATABASE_URL", null),
                ("CONFIG_PATH", configPath));

            await using var dbContext = new DavDatabaseContext();
            await dbContext.Database.MigrateAsync();

            Assert.True(await TableExistsAsync(dbContext, "nntp_node_heartbeats"));
            Assert.True(await TableExistsAsync(dbContext, "nntp_connection_leases"));
        }
        finally
        {
            DeleteDatabaseFiles();
            if (Directory.Exists(configPath))
                Directory.Delete(configPath, recursive: true);
        }
    }

    private static void DeleteDatabaseFiles()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(DavDatabaseContext.DatabaseFilePath);
        File.Delete(DavDatabaseContext.DatabaseFilePath + "-wal");
        File.Delete(DavDatabaseContext.DatabaseFilePath + "-shm");
    }

    private static async Task<bool> TableExistsAsync(DavDatabaseContext dbContext, string tableName)
    {
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        if (command.Connection!.State != ConnectionState.Open)
            await command.Connection.OpenAsync();

        command.CommandText = @"
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table' AND name = @tableName;";

        var parameter = command.CreateParameter();
        parameter.ParameterName = "tableName";
        parameter.Value = tableName;
        command.Parameters.Add(parameter);

        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result) > 0;
    }
}
