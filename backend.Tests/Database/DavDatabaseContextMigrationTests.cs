using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;

namespace backend.Tests.Database;

[Collection(nameof(backend.Tests.Config.EnvironmentVariableCollection))]
public sealed class DavDatabaseContextMigrationTests
{
    private const string HistoricalCutoff = "20260419000000_FixYencLayoutColumnTypes";
    private const string SetupGrantsMigration = "20260809120000_AddSetupGrants";
    private const string SetupMutationFenceMigration = "20260809130000_AddSetupMutationFenceAndCompletion";
    private const string LatestMigration = "20260809140000_AddSetupRunLease";

    private static readonly string[] ExpectedMigrationIds =
    [
        "20250529081501_InitializeDatabase",
        "20250529102749_Add-CreatedAt-To-DavItem",
        "20250605030729_Add-Accounts-Table",
        "20250710072717_AddConfigItemsTable",
        "20250722004955_Ensure-Api-Key-Exists",
        "20250818232259_Add-DownloadDirId-To-HistoryItem",
        "20250819091746_Add-Ids-Root-Directory",
        "20250819221618_Add-Path-To-DavItem",
        "20250824100609_Add-IdPrefix-To-DavItems",
        "20251010192643_Add-Healthcheck-Columns-To-DavItems",
        "20251014060520_Add-HealthCheckResults-Table",
        "20251014062743_Add-HealthCheckQueue-Index",
        "20251024003729_Add-DavMultipartFiles-Table",
        "20251030163510_Add-QueueNzbContents-Table",
        "20251105050845_Add-HealthCheckStats-Table",
        "20251105200722_Add-CreatedAt-Index-To-HealthCheckResults-Table",
        "20251106165542_Ensure-Strm-Key-Exists",
        "20251113081523_Populate-Usenet-Providers-Config",
        "20260108054841_Add-BlobCleanupItems-Table",
        "20260108061241_Add-Trigger-To-QueueItems-Table",
        "20260121033824_Populate-Blocklisted-Files-Setting",
        "20260122220920_Populate-Health-Check-Categories-Setting",
        "20260406191008_AddMissingSegmentIds",
        "20260407185930_AddDeferredSpecsSchema",
        "20260414120000_AddRoleAwareNntpLeases",
        "20260414133000_AddNntpLeaseEpochs",
        "20260418120000_AddYencLayoutMetadata",
        HistoricalCutoff,
        SetupGrantsMigration,
        SetupMutationFenceMigration,
        LatestMigration,
    ];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OperationalSqliteOptions_AddSnapshotInterceptorOnlyForIngest(bool runsIngest)
    {
        var options = DavDatabaseContextOptionsFactory.CreateSqliteOptions<DavDatabaseContext>(
            "options-test.db",
            runsIngest);

        Assert.Equal(runsIngest, HasContentIndexSnapshotInterceptor(options));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OperationalPostgresOptions_AddSnapshotInterceptorOnlyForIngest(bool runsIngest)
    {
        var options = DavDatabaseContextOptionsFactory.CreatePostgresOptions<DavDatabaseContext>(
            "Host=localhost;Database=nzbdav",
            runsIngest);

        Assert.Equal(runsIngest, HasContentIndexSnapshotInterceptor(options));
    }

    [Fact]
    public async Task MigrateAsync_CreatesSetupSchemaAndFenceSeed_OnSqlite()
    {
        var configPath = CreateConfigPath("setup-migration");

        try
        {
            using var environment = new backend.Tests.Config.TemporaryEnvironment(
                ("DATABASE_URL", null),
                ("CONFIG_PATH", configPath));

            await using var dbContext = new DavDatabaseContext();
            await dbContext.Database.MigrateAsync();
            await AssertMigrationHistoryAsync(dbContext, ExpectedMigrationIds);

            Assert.Equal(
                new[]
                {
                    "id", "granted_token_hash", "issued_at_utc", "expires_at_utc", "is_revoked",
                    "revoked_at_utc", "issued_by_username", "purpose", "repair_session_ciphertext",
                    "repair_session_operation_id",
                },
                await ColumnNamesAsync(dbContext, "setup_grants"));
            Assert.Equal(
                new[] { "id", "epoch", "reserved_candidate_operation_id" },
                await ColumnNamesAsync(dbContext, "setup_mutation_fence"));
            Assert.Equal(
                new[]
                {
                    "id", "operation_id", "created_at_utc", "revocation_pending", "active_session_ciphertext",
                    "revocation_session_ciphertext", "candidate_session_ciphertext", "candidate_operation_ciphertext",
                    "emergency_session_ciphertext", "emergency_operation_ciphertext",
                },
                await ColumnNamesAsync(dbContext, "setup_completion_operations"));
            Assert.Equal(
                new[] { "id", "owner_id", "grant_hash", "purpose", "generation", "lease_until_utc" },
                await ColumnNamesAsync(dbContext, "setup_run_leases"));
            Assert.Contains(
                "IX_setup_grants_expires_at_utc",
                await IndexNamesAsync(dbContext, "setup_grants"));
            Assert.Equal(1L, await ScalarAsync(dbContext, "SELECT COUNT(*) FROM setup_mutation_fence WHERE id = 1 AND epoch = 0;"));
            Assert.Equal(
                1L,
                await ScalarAsync(
                    dbContext,
                    "SELECT COUNT(*) FROM __EFMigrationsHistory WHERE MigrationId = '20260809140000_AddSetupRunLease';"));
        }
        finally
        {
            DeleteDatabaseFiles();
            DeleteConfigPath(configPath);
        }
    }

    [Fact]
    public async Task SetupMigrations_PreserveBaselineRows_ThroughUpgradeAndDowngrade_OnSqlite()
    {
        var configPath = CreateConfigPath("setup-migration-roundtrip");

        try
        {
            using var environment = new backend.Tests.Config.TemporaryEnvironment(
                ("DATABASE_URL", null),
                ("CONFIG_PATH", configPath));

            await using var dbContext = new DavDatabaseContext();
            await dbContext.Database.MigrateAsync(HistoricalCutoff);
            await AssertMigrationHistoryAsync(dbContext, ExpectedMigrationIds.TakeWhile(id => id != SetupGrantsMigration));
            await ExecuteAsync(
                dbContext,
                "INSERT INTO ConfigItems (ConfigName, ConfigValue, IsEncrypted) VALUES ('migration-sentinel', 'survives', 0);");

            await dbContext.Database.MigrateAsync();
            await AssertMigrationHistoryAsync(dbContext, ExpectedMigrationIds);
            Assert.True(await TableExistsAsync(dbContext, "setup_run_leases"));
            Assert.Equal("survives", await ScalarAsync(dbContext, "SELECT ConfigValue FROM ConfigItems WHERE ConfigName = 'migration-sentinel';"));

            await dbContext.Database.MigrateAsync(SetupMutationFenceMigration);
            await AssertMigrationHistoryAsync(dbContext, ExpectedMigrationIds.Take(ExpectedMigrationIds.Length - 1));
            Assert.True(await TableExistsAsync(dbContext, "setup_grants"));
            Assert.True(await TableExistsAsync(dbContext, "setup_mutation_fence"));
            Assert.True(await TableExistsAsync(dbContext, "setup_completion_operations"));
            Assert.False(await TableExistsAsync(dbContext, "setup_run_leases"));

            await dbContext.Database.MigrateAsync(SetupGrantsMigration);
            Assert.True(await TableExistsAsync(dbContext, "setup_grants"));
            Assert.False(await TableExistsAsync(dbContext, "setup_mutation_fence"));
            Assert.False(await TableExistsAsync(dbContext, "setup_completion_operations"));
            await AssertMigrationHistoryAsync(dbContext, ExpectedMigrationIds.Take(ExpectedMigrationIds.Length - 2));

            await dbContext.Database.MigrateAsync(HistoricalCutoff);
            Assert.False(await TableExistsAsync(dbContext, "setup_grants"));
            await AssertMigrationHistoryAsync(dbContext, ExpectedMigrationIds.TakeWhile(id => id != SetupGrantsMigration));
            Assert.Equal("survives", await ScalarAsync(dbContext, "SELECT ConfigValue FROM ConfigItems WHERE ConfigName = 'migration-sentinel';"));

            await dbContext.Database.MigrateAsync();
            await AssertMigrationHistoryAsync(dbContext, ExpectedMigrationIds);
            Assert.True(await TableExistsAsync(dbContext, "setup_run_leases"));
            Assert.Equal("survives", await ScalarAsync(dbContext, "SELECT ConfigValue FROM ConfigItems WHERE ConfigName = 'migration-sentinel';"));
        }
        finally
        {
            DeleteDatabaseFiles();
            DeleteConfigPath(configPath);
        }
    }

    [Fact]
    public async Task MigrationScripts_UpgradeAndDowngradeThroughHistoricalCutoff_OnSqlite()
    {
        var configPath = CreateConfigPath("migration-scripts");
        var scriptDatabasePath = Path.Combine(configPath, "script.db");

        try
        {
            using var environment = new backend.Tests.Config.TemporaryEnvironment(
                ("DATABASE_URL", null),
                ("CONFIG_PATH", configPath));

            await using var sourceContext = new DavDatabaseContext();
            var migrator = sourceContext.Database.GetService<IMigrator>();
            var upgradeScript = migrator.GenerateScript(null, LatestMigration);
            AssertMigrationScriptOrder(upgradeScript, ExpectedMigrationIds);

            var downgradeScript = migrator.GenerateScript(LatestMigration, HistoricalCutoff);
            AssertMigrationScriptOrder(
                downgradeScript,
                new[] { LatestMigration, SetupMutationFenceMigration, SetupGrantsMigration });

            var scriptOptions = DavDatabaseContextOptionsFactory.CreateSqliteOptions<DavDatabaseContext>(
                scriptDatabasePath,
                addContentIndexSnapshotInterceptor: false);
            await using var scriptedContext = new DavDatabaseContext(scriptOptions);
            // The full upgrade script contains legacy migration SQL batches without
            // statement terminators. Build this disposable database through EF's
            // genuine migrator, then execute the generated downgrade script, whose
            // provider-generated batches are directly executable by SQLite.
            await scriptedContext.Database.MigrateAsync();
            await AssertMigrationHistoryAsync(scriptedContext, ExpectedMigrationIds);
            Assert.True(await TableExistsAsync(scriptedContext, "setup_run_leases"));

            await ExecuteScriptAsync(scriptedContext, downgradeScript);
            await AssertMigrationHistoryAsync(
                scriptedContext,
                ExpectedMigrationIds.TakeWhile(id => id != SetupGrantsMigration));
            await AssertHistoricalSchemaAsync(scriptedContext);
        }
        finally
        {
            DeleteDatabaseFiles();
            DeleteConfigPath(configPath);
        }
    }

    [Fact]
    public async Task MigrateAsync_CreatesRoleAwareNntpLeaseTables_OnSqlite()
    {
        var configPath = CreateConfigPath("nntp-lease-migration");

        try
        {
            using var environment = new backend.Tests.Config.TemporaryEnvironment(
                ("DATABASE_URL", null),
                ("CONFIG_PATH", configPath));

            await using var dbContext = new DavDatabaseContext();
            await dbContext.Database.MigrateAsync();

            Assert.True(await TableExistsAsync(dbContext, "nntp_node_heartbeats"));
            Assert.True(await TableExistsAsync(dbContext, "nntp_connection_leases"));
            Assert.True(await TableExistsAsync(dbContext, "nntp_lease_epochs"));
        }
        finally
        {
            DeleteDatabaseFiles();
            DeleteConfigPath(configPath);
        }
    }

    private static bool HasContentIndexSnapshotInterceptor<TContext>(DbContextOptions<TContext> options)
        where TContext : DbContext
    {
        return options.FindExtension<CoreOptionsExtension>()?.Interceptors?
            .OfType<ContentIndexSnapshotInterceptor>()
            .Any() == true;
    }

    private static async Task AssertMigrationHistoryAsync(
        DavDatabaseContext dbContext,
        IEnumerable<string> expectedMigrationIds)
    {
        var actualMigrationIds = new List<string>();
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        if (command.Connection!.State != ConnectionState.Open)
            await command.Connection.OpenAsync();

        command.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId;";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            actualMigrationIds.Add(reader.GetString(0));

        Assert.Equal(expectedMigrationIds, actualMigrationIds);
    }

    private static void AssertMigrationScriptOrder(string script, IEnumerable<string> migrationIds)
    {
        Assert.False(string.IsNullOrWhiteSpace(script));
        var previousIndex = -1;
        foreach (var migrationId in migrationIds)
        {
            var index = script.IndexOf(migrationId, StringComparison.Ordinal);
            Assert.True(index >= 0, $"Migration {migrationId} was not present in the generated script.");
            Assert.True(index > previousIndex, $"Migration {migrationId} was out of order in the generated script.");
            previousIndex = index;
        }
    }

    private static async Task ExecuteScriptAsync(DavDatabaseContext dbContext, string script)
    {
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        if (command.Connection!.State != ConnectionState.Open)
            await command.Connection.OpenAsync();
        command.CommandText = script;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertHistoricalSchemaAsync(DavDatabaseContext dbContext)
    {
        Assert.False(await TableExistsAsync(dbContext, "setup_grants"));
        Assert.False(await TableExistsAsync(dbContext, "setup_mutation_fence"));
        Assert.False(await TableExistsAsync(dbContext, "setup_completion_operations"));
        Assert.False(await TableExistsAsync(dbContext, "setup_run_leases"));
        Assert.True(await TableExistsAsync(dbContext, "yenc_header_cache"));
        Assert.True(await TableExistsAsync(dbContext, "nntp_node_heartbeats"));
        Assert.True(await TableExistsAsync(dbContext, "nntp_connection_leases"));
        Assert.True(await TableExistsAsync(dbContext, "nntp_lease_epochs"));
    }

    private static string CreateConfigPath(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteConfigPath(string configPath)
    {
        if (Directory.Exists(configPath))
            Directory.Delete(configPath, recursive: true);
    }

    private static void DeleteDatabaseFiles()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(DavDatabaseContext.DatabaseFilePath);
        File.Delete(DavDatabaseContext.DatabaseFilePath + "-wal");
        File.Delete(DavDatabaseContext.DatabaseFilePath + "-shm");
    }

    private static async Task<IReadOnlyList<string>> ColumnNamesAsync(DavDatabaseContext dbContext, string tableName)
    {
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        if (command.Connection!.State != ConnectionState.Open)
            await command.Connection.OpenAsync();

        command.CommandText = $"PRAGMA table_info(\"{tableName}\");";
        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(1));
        return columns;
    }

    private static async Task<IReadOnlyList<string>> IndexNamesAsync(DavDatabaseContext dbContext, string tableName)
    {
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        if (command.Connection!.State != ConnectionState.Open)
            await command.Connection.OpenAsync();

        command.CommandText = $"PRAGMA index_list(\"{tableName}\");";
        var indexes = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            indexes.Add(reader.GetString(1));
        return indexes;
    }

    private static async Task<object?> ScalarAsync(DavDatabaseContext dbContext, string sql)
    {
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        if (command.Connection!.State != ConnectionState.Open)
            await command.Connection.OpenAsync();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static async Task ExecuteAsync(DavDatabaseContext dbContext, string sql)
    {
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        if (command.Connection!.State != ConnectionState.Open)
            await command.Connection.OpenAsync();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
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
