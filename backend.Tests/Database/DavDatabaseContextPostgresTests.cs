using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NzbWebDAV.Database;
using NzbWebDAV.Tests.Clients.Usenet.Caching;

namespace backend.Tests.Database;

[Collection(nameof(backend.Tests.Config.EnvironmentVariableCollection))]
public sealed class DavDatabaseContextPostgresTests : IClassFixture<PostgresHeaderCacheFixture>
{
    private readonly PostgresHeaderCacheFixture _fixture;

    public DavDatabaseContextPostgresTests(PostgresHeaderCacheFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void DatabaseUrl_UsesPgbouncerCompatibilityFlags_ForUrlFormat()
    {
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("DATABASE_URL", "postgres://user:pass@pgbouncer:5432/nzbdav"));

        using var dbContext = new DavDatabaseContext();
        var connectionString = dbContext.Database.GetDbConnection().ConnectionString;
        var builder = new NpgsqlConnectionStringBuilder(connectionString);

        Assert.Contains("Host=pgbouncer", connectionString);
        Assert.Contains("No Reset On Close=true", connectionString);
        Assert.Contains("Server Compatibility Mode=Redshift", connectionString);
        Assert.Equal(2, builder.MinPoolSize);
        Assert.Equal(50, builder.MaxPoolSize);
    }

    [Fact]
    public void DirectPostgresConnectionString_RemainsUntouched()
    {
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("DATABASE_URL", "Host=postgres;Port=5432;Database=nzbdav;Username=user;Password=pass"));

        using var dbContext = new DavDatabaseContext();
        var connectionString = dbContext.Database.GetDbConnection().ConnectionString;

        Assert.DoesNotContain("No Reset On Close=true", connectionString);
        Assert.DoesNotContain("Server Compatibility Mode=Redshift", connectionString);
    }

    [SkippableFact]
    public async Task LatestMigration_CreatesCoordinationTables()
    {
        Skip.IfNot(_fixture.IsAvailable, "Docker is required for this integration test.");

        await _fixture.ResetAsync();

        await using var dbContext = new DavDatabaseContext();
        Assert.True(await TableExistsAsync(dbContext, "websocket_outbox"));
        Assert.True(await TableExistsAsync(dbContext, "auth_failures"));
        Assert.True(await TableExistsAsync(dbContext, "connection_pool_claims"));
    }

    private static async Task<bool> TableExistsAsync(DavDatabaseContext dbContext, string tableName)
    {
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        if (command.Connection!.State != ConnectionState.Open)
            await command.Connection.OpenAsync();

        command.CommandText = @"
            SELECT COUNT(*)
            FROM information_schema.tables
            WHERE table_schema = 'public' AND table_name = @tableName;";

        var parameter = command.CreateParameter();
        parameter.ParameterName = "tableName";
        parameter.Value = tableName;
        command.Parameters.Add(parameter);

        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result) > 0;
    }
}
