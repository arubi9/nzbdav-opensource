using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Tests.Clients.Usenet.Caching;

namespace backend.Tests.Database;

[Collection(nameof(backend.Tests.Config.EnvironmentVariableCollection))]
public sealed class DavDatabaseContextPostgresTests : IClassFixture<PostgresHeaderCacheFixture>
{
    private const string HistoricalCutoff = "20260419000000_FixYencLayoutColumnTypes";

    private readonly PostgresHeaderCacheFixture _fixture;

    public DavDatabaseContextPostgresTests(PostgresHeaderCacheFixture fixture) => _fixture = fixture;

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

    [Fact]
    public async Task RuntimeInitialization_PooledOperationalUrl_UsesDirectMigrationUrl()
    {
        Assert.True(_fixture.IsAvailable, "Docker with PostgreSQL 17 is required for this integration test.");
        await ResetSchemaAsync();
        var directConnectionString = _fixture.ConnectionString!;

        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("DATABASE_URL", "Host=pgbouncer;Port=5432;Database=nzbdav;Username=nzbdav;Password=nzbdav"),
            ("MIGRATION_DATABASE_URL", directConnectionString));
        var operationalOptions = DavDatabaseContextOptionsFactory.CreatePostgresOptions<DavDatabaseContext>(
            "Host=pgbouncer;Port=5432;Database=nzbdav;Username=nzbdav;Password=nzbdav");
        await using var databaseContext = new DavDatabaseContext(operationalOptions);

        await DatabaseInitialization.InitializeAsync(databaseContext, CancellationToken.None, "0");

        await using var verifyContext = new PostgresDavMigrationContext(
            new DbContextOptionsBuilder<PostgresDavMigrationContext>()
                .UseNpgsql(directConnectionString)
                .Options);
        Assert.False(await TableExistsAsync(verifyContext, "__EFMigrationsHistory"));
        Assert.False(await TableExistsAsync(verifyContext, "DavItems"));
    }

    [Fact]
    public async Task RuntimeInitialization_PgBouncerWithoutDirectMigrationUrl_IsRejectedBeforeConnection()
    {
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("DATABASE_URL", "Host=pgbouncer;Port=5432;Database=nzbdav;Username=nzbdav;Password=nzbdav"),
            ("MIGRATION_DATABASE_URL", null));
        var operationalOptions = DavDatabaseContextOptionsFactory.CreatePostgresOptions<DavDatabaseContext>(
            "Host=pgbouncer;Port=5432;Database=nzbdav;Username=nzbdav;Password=nzbdav");
        await using var databaseContext = new DavDatabaseContext(operationalOptions);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DatabaseInitialization.InitializeAsync(databaseContext, CancellationToken.None, "0"));
        Assert.Contains("direct PostgreSQL endpoint", exception.Message, StringComparison.Ordinal);
        Assert.Contains("MIGRATION_DATABASE_URL", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimeInitialization_FreshPostgres_UsesExactlyThePostgresChainAndSeedsOnce()
    {
        Assert.True(_fixture.IsAvailable, "Docker with PostgreSQL 17 is required for this integration test.");
        await ResetSchemaAsync();

        await using var databaseContext = new DavDatabaseContext();
        await DatabaseInitialization.InitializeAsync(databaseContext, CancellationToken.None);

        await using var migrationContext = CreateMigrationContext();
        var expected = migrationContext.Database.GetMigrations().ToArray();
        var actual = await migrationContext.Database.GetAppliedMigrationsAsync();

        Assert.Equal(32, expected.Length);
        Assert.Equal(expected, actual);
        Assert.Equal(5, await databaseContext.Items.CountAsync(x =>
            x.Id == DavItem.Root.Id
            || x.Id == DavItem.NzbFolder.Id
            || x.Id == DavItem.ContentFolder.Id
            || x.Id == DavItem.SymlinkFolder.Id
            || x.Id == DavItem.IdsFolder.Id));
        Assert.Equal(1, await databaseContext.ConfigItems.CountAsync(x => x.ConfigName == "api.key"));
        Assert.Equal(1, await databaseContext.ConfigItems.CountAsync(x => x.ConfigName == "api.strm-key"));
    }

    [Fact]
    public async Task RuntimeInitialization_ExistingCutoffHistory_AppliesOnlySetupAndPreservesSentinel()
    {
        Assert.True(_fixture.IsAvailable, "Docker with PostgreSQL 17 is required for this integration test.");
        await ResetSchemaAsync();

        await using (var migrationContext = CreateMigrationContext())
            await migrationContext.Database.MigrateAsync(HistoricalCutoff);

        await using var databaseContext = new DavDatabaseContext();
        databaseContext.ConfigItems.Add(new ConfigItem
        {
            ConfigName = "migration-sentinel",
            ConfigValue = "survives",
            IsEncrypted = false
        });
        await databaseContext.SaveChangesAsync();

        await DatabaseInitialization.InitializeAsync(databaseContext, CancellationToken.None);

        await using var verifyContext = CreateMigrationContext();
        var expected = verifyContext.Database.GetMigrations().ToArray();
        var actual = await verifyContext.Database.GetAppliedMigrationsAsync();
        Assert.Equal(expected, actual);
        Assert.Equal("survives", await databaseContext.ConfigItems
            .Where(x => x.ConfigName == "migration-sentinel")
            .Select(x => x.ConfigValue)
            .SingleAsync());
        Assert.True(await TableExistsAsync(verifyContext, "setup_grants"));
        Assert.True(await TableExistsAsync(verifyContext, "setup_mutation_fence"));
        Assert.True(await TableExistsAsync(verifyContext, "setup_completion_operations"));
        Assert.True(await TableExistsAsync(verifyContext, "setup_run_leases"));
    }

    [Fact]
    public async Task RuntimeInitialization_TwoIndependentFreshInitializers_ShareOneHistoryChain()
    {
        Assert.True(_fixture.IsAvailable, "Docker with PostgreSQL 17 is required for this integration test.");
        await ResetSchemaAsync();

        var first = InitializeWithNewContextAsync();
        var second = InitializeWithNewContextAsync();
        await Task.WhenAll(first, second);

        await using var verifyContext = CreateMigrationContext();
        Assert.Equal(32, (await verifyContext.Database.GetAppliedMigrationsAsync()).Count());
        await using var databaseContext = new DavDatabaseContext();
        Assert.Equal(1, await databaseContext.Items.CountAsync(x => x.Id == DavItem.Root.Id));
        Assert.Equal(1, await databaseContext.ConfigItems.CountAsync(x => x.ConfigName == "api.key"));
        Assert.Equal(1, await databaseContext.ConfigItems.CountAsync(x => x.ConfigName == "api.strm-key"));
    }

    [Fact]
    public async Task RuntimeInitialization_TargetThenLatest_UsesPostgresMigrationContext()
    {
        Assert.True(_fixture.IsAvailable, "Docker with PostgreSQL 17 is required for this integration test.");
        await ResetSchemaAsync();

        await using var databaseContext = new DavDatabaseContext();
        await DatabaseInitialization.InitializeAsync(databaseContext, CancellationToken.None, HistoricalCutoff);

        await using (var cutoffContext = CreateMigrationContext())
        {
            var appliedAtCutoff = await cutoffContext.Database.GetAppliedMigrationsAsync();
            Assert.Equal(28, appliedAtCutoff.Count());
            Assert.False(await TableExistsAsync(cutoffContext, "setup_grants"));
        }

        await DatabaseInitialization.InitializeAsync(databaseContext, CancellationToken.None);
        await using var latestContext = CreateMigrationContext();
        Assert.Equal(32, (await latestContext.Database.GetAppliedMigrationsAsync()).Count());
    }

    [Fact]
    public async Task RuntimeInitialization_CutoffAfterDroppingHistory_IsRejectedUnchanged()
    {
        Assert.True(_fixture.IsAvailable, "Docker with PostgreSQL 17 is required for this integration test.");
        await ResetSchemaAsync();

        await using (var migrationContext = CreateMigrationContext())
            await migrationContext.Database.MigrateAsync(HistoricalCutoff);

        await using (var databaseContext = new DavDatabaseContext())
        {
            databaseContext.ConfigItems.Add(new ConfigItem
            {
                ConfigName = "migration-sentinel",
                ConfigValue = "survives",
                IsEncrypted = false
            });
            await databaseContext.SaveChangesAsync();
            await databaseContext.Database.ExecuteSqlRawAsync("DROP TABLE \"__EFMigrationsHistory\";");
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => DatabaseInitialization.InitializeAsync(databaseContext, CancellationToken.None));
            Assert.Contains("without migration history is unsupported", exception.Message, StringComparison.Ordinal);
        }

        await using var verifyContext = CreateMigrationContext();
        Assert.False(await TableExistsAsync(verifyContext, "__EFMigrationsHistory"));
        Assert.False(await TableExistsAsync(verifyContext, "setup_grants"));
        await using var checkContext = new DavDatabaseContext();
        Assert.Equal("survives", await checkContext.ConfigItems
            .Where(x => x.ConfigName == "migration-sentinel")
            .Select(x => x.ConfigValue)
            .SingleAsync());
    }

    private async Task InitializeWithNewContextAsync()
    {
        await using var databaseContext = new DavDatabaseContext();
        await DatabaseInitialization.InitializeAsync(databaseContext, CancellationToken.None);
    }

    private PostgresDavMigrationContext CreateMigrationContext()
    {
        var options = new DbContextOptionsBuilder<PostgresDavMigrationContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .Options;
        return new PostgresDavMigrationContext(options);
    }

    private async Task ResetSchemaAsync()
    {
        await using var databaseContext = new DavDatabaseContext();
        await using var command = databaseContext.Database.GetDbConnection().CreateCommand();
        if (command.Connection!.State != ConnectionState.Open)
            await command.Connection.OpenAsync();
        command.CommandText = "DROP SCHEMA public CASCADE; CREATE SCHEMA public;";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<bool> TableExistsAsync(PostgresDavMigrationContext context, string tableName)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        if (command.Connection!.State != ConnectionState.Open)
            await command.Connection.OpenAsync();
        command.CommandText = @"
            SELECT EXISTS (
                SELECT 1 FROM information_schema.tables
                WHERE table_schema = 'public' AND table_name = @tableName);";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "tableName";
        parameter.Value = tableName;
        command.Parameters.Add(parameter);
        return Convert.ToBoolean(await command.ExecuteScalarAsync());
    }
}
