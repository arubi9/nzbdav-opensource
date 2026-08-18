using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Services;

public static class DatabaseInitialization
{
    public static async Task InitializeAsync(
        DavDatabaseContext databaseContext,
        CancellationToken cancellationToken,
        string? targetMigration = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (databaseContext.Database.IsNpgsql())
        {
            await using var migrationContext = CreatePostgresMigrationContext();
            ValidateTargetMigration(migrationContext, targetMigration);
            await MigratePostgresAsync(migrationContext, targetMigration, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (databaseContext.Database.IsSqlite())
        {
            ValidateTargetMigration(databaseContext, targetMigration);
            var isEmptyDatabase = await ValidateDatabaseStateAsync(databaseContext, cancellationToken)
                .ConfigureAwait(false);
            if (!(isEmptyDatabase && IsZeroTarget(targetMigration)))
            {
                await databaseContext.Database
                    .MigrateAsync(targetMigration, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        else
        {
            throw new InvalidOperationException(
                $"Unsupported database provider '{databaseContext.Database.ProviderName}'.");
        }

        // An explicit target is a schema operation only. In particular, target
        // 0 must leave a fresh database empty and an old target must not query or
        // write rows using the current model. Normal startup (null target)
        // migrates to the latest schema and then performs idempotent bootstrap.
        if (targetMigration is null)
        {
            if (databaseContext.Database.IsSqlite())
            {
                await EnableSqliteWriteAheadLoggingAsync(databaseContext, cancellationToken)
                    .ConfigureAwait(false);
            }

            await SeedBootstrapDataAsync(databaseContext, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Switches SQLite to write-ahead logging.
    ///
    /// The default journal_mode=DELETE takes a database-wide exclusive lock for
    /// every write, which blocks concurrent readers: the health check's item
    /// count, the plugin manifest query, and every in-flight stream's metadata
    /// lookup all stall behind ingest. WAL lets readers run during writes.
    ///
    /// This runs here rather than in a connection interceptor because
    /// journal_mode is persisted in the database header, so it only needs to be
    /// set once - and because setting it is itself a write, which fails while EF
    /// is still probing whether the database file exists.
    /// </summary>
    private static async Task EnableSqliteWriteAheadLoggingAsync(
        DavDatabaseContext databaseContext,
        CancellationToken cancellationToken)
    {
        var connection = databaseContext.Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere)
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode = WAL;";
            var mode = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;

            // An in-memory database cannot use WAL, and that is fine. Anything
            // else silently staying on DELETE is a performance cliff worth
            // surfacing rather than swallowing.
            if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(mode, "memory", StringComparison.OrdinalIgnoreCase))
            {
                Serilog.Log.Warning(
                    "SQLite journal_mode is {Mode}, not WAL. Writes will block concurrent readers.",
                    mode ?? "unknown");
            }
        }
        finally
        {
            if (openedHere)
                await connection.CloseAsync().ConfigureAwait(false);
        }
    }

    private static PostgresDavMigrationContext CreatePostgresMigrationContext()
    {
        // DATABASE_URL may intentionally point at a transaction-pooled
        // PgBouncer endpoint for operational queries. Migration locking needs a
        // direct endpoint, supplied through MIGRATION_DATABASE_URL when the
        // operational URL is pooled.
        var databaseUrl = EnvironmentUtil.GetMigrationDatabaseUrl()
            ?? EnvironmentUtil.GetDatabaseUrl();
        if (string.IsNullOrWhiteSpace(databaseUrl))
            throw new InvalidOperationException("PostgreSQL migration context requires DATABASE_URL or MIGRATION_DATABASE_URL.");

        if (DavDatabaseContextOptionsFactory.IsPgbouncerConnection(databaseUrl))
        {
            throw new InvalidOperationException(
                "PostgreSQL migrations require a direct PostgreSQL endpoint. Set MIGRATION_DATABASE_URL to the direct database endpoint; PgBouncer and transaction-pool endpoints are not supported for migrations.");
        }

        var options = DavDatabaseContextOptionsFactory
            .CreatePostgresOptions<PostgresDavMigrationContext>(databaseUrl);
        return new PostgresDavMigrationContext(options);
    }

    private static void ValidateTargetMigration(DbContext context, string? targetMigration)
    {
        if (string.IsNullOrWhiteSpace(targetMigration)
            || string.Equals(targetMigration, "0", StringComparison.Ordinal))
            return;

        if (!context.Database.GetMigrations().Contains(targetMigration, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Unknown database migration '{targetMigration}'. Use a migration ID returned by the provider migration chain.");
        }
    }

    private static async Task MigratePostgresAsync(
        PostgresDavMigrationContext migrationContext,
        string? targetMigration,
        CancellationToken cancellationToken)
    {
        // Keep this connection open for the complete lock -> preflight -> EF
        // migration sequence. A session advisory lock is only useful when every
        // command runs on this one direct Npgsql session.
        await migrationContext.Database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var lockAcquired = false;
        try
        {
            await migrationContext.Database
                .ExecuteSqlRawAsync(
                    "SELECT pg_advisory_lock(hashtextextended('nzbdav-migrations', 0));",
                    cancellationToken)
                .ConfigureAwait(false);
            lockAcquired = true;

            var isEmptyDatabase = await ValidateDatabaseStateAsync(migrationContext, cancellationToken)
                .ConfigureAwait(false);
            if (!(isEmptyDatabase && IsZeroTarget(targetMigration)))
            {
                await migrationContext.Database
                    .MigrateAsync(targetMigration, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            if (lockAcquired)
            {
                // Unlock with a non-cancelable token so cancellation cannot leave
                // the direct session holding the migration lock. Do not replace a
                // migration failure with an unlock failure (the connection is
                // closed immediately afterwards in either case).
                try
                {
                    await migrationContext.Database
                        .ExecuteSqlRawAsync(
                            "SELECT pg_advisory_unlock(hashtextextended('nzbdav-migrations', 0));",
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // The session may already be broken after a failed or
                    // canceled migration; disposing it releases the lock.
                }
            }

            try
            {
                await migrationContext.Database.CloseConnectionAsync()
                    .ConfigureAwait(false);
            }
            catch
            {
                // Disposal below still releases a broken Npgsql connection.
            }
        }
    }

    private static bool IsZeroTarget(string? targetMigration)
        => string.Equals(targetMigration, "0", StringComparison.Ordinal);

    private static async Task<bool> ValidateDatabaseStateAsync(
        DbContext context,
        CancellationToken cancellationToken)
    {
        var connection = context.Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere)
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var hasHistoryTable = await TableExistsAsync(
                connection,
                context.Database.IsNpgsql(),
                "__EFMigrationsHistory",
                cancellationToken).ConfigureAwait(false);
            if (hasHistoryTable && await HasHistoryRowsAsync(connection, cancellationToken).ConfigureAwait(false))
                return false;

            var applicationTables = await GetApplicationTablesAsync(
                connection,
                context.Database.IsNpgsql(),
                cancellationToken).ConfigureAwait(false);
            if (applicationTables.Count > 0)
            {
                throw new InvalidOperationException(
                    "Database contains existing application tables but no EF migration history. Automatic migration of existing installs without migration history is unsupported; restore the migration history or use a fresh database.");
            }

            return !hasHistoryTable;
        }
        finally
        {
            if (openedHere)
                await connection.CloseAsync().ConfigureAwait(false);
        }
    }

    private static async Task<bool> HasHistoryRowsAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM \"__EFMigrationsHistory\";";
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) > 0;
    }

    private static async Task<HashSet<string>> GetApplicationTablesAsync(
        DbConnection connection,
        bool isPostgres,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = isPostgres
            ? "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE' AND table_name <> '__EFMigrationsHistory';"
            : "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND name <> '__EFMigrationsHistory';";

        var tables = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            tables.Add(reader.GetString(0));
        return tables;
    }

    private static async Task<bool> TableExistsAsync(
        DbConnection connection,
        bool isPostgres,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = isPostgres
            ? "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = @tableName);"
            : "SELECT EXISTS (SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @tableName);";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "tableName";
        parameter.Value = tableName;
        command.Parameters.Add(parameter);
        return Convert.ToBoolean(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async Task SeedBootstrapDataAsync(
        DavDatabaseContext databaseContext,
        CancellationToken cancellationToken)
    {
        await EnsureDavRootsAsync(databaseContext, cancellationToken).ConfigureAwait(false);
        await EnsureConfigKeysAsync(databaseContext, cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureDavRootsAsync(
        DavDatabaseContext databaseContext,
        CancellationToken cancellationToken)
    {
        var requiredRoots = new[]
        {
            CreateRootItem(DavItem.Root.Id, null, DavItem.Root.Name, DavItem.Root.Type, DavItem.Root.Path),
            CreateRootItem(DavItem.NzbFolder.Id, DavItem.Root.Id, DavItem.NzbFolder.Name, DavItem.NzbFolder.Type, DavItem.NzbFolder.Path),
            CreateRootItem(DavItem.ContentFolder.Id, DavItem.Root.Id, DavItem.ContentFolder.Name, DavItem.ContentFolder.Type, DavItem.ContentFolder.Path),
            CreateRootItem(DavItem.SymlinkFolder.Id, DavItem.Root.Id, DavItem.SymlinkFolder.Name, DavItem.SymlinkFolder.Type, DavItem.SymlinkFolder.Path),
            CreateRootItem(DavItem.IdsFolder.Id, DavItem.Root.Id, DavItem.IdsFolder.Name, DavItem.IdsFolder.Type, DavItem.IdsFolder.Path)
        };

        var existingIds = await databaseContext.Items
            .Where(x => requiredRoots.Select(root => root.Id).Contains(x.Id))
            .Select(x => x.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var missingRoots = requiredRoots
            .Where(root => !existingIds.Contains(root.Id))
            .ToList();

        if (missingRoots.Count == 0)
            return;

        databaseContext.Items.AddRange(missingRoots);
        try
        {
            await databaseContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException) when (missingRoots.Count > 0)
        {
            // Another node may have completed this idempotent bootstrap between
            // the read and insert. The unique keys make the result durable; do
            // not hide unrelated migration/database failures.
            databaseContext.ChangeTracker.Clear();
            var nowExisting = await databaseContext.Items
                .Where(x => requiredRoots.Select(root => root.Id).Contains(x.Id))
                .Select(x => x.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            if (requiredRoots.Any(root => !nowExisting.Contains(root.Id)))
                throw;
        }
    }

    private static async Task EnsureConfigKeysAsync(
        DavDatabaseContext databaseContext,
        CancellationToken cancellationToken)
    {
        var existingKeys = await databaseContext.ConfigItems
            .Where(x => x.ConfigName == "api.key" || x.ConfigName == "api.strm-key")
            .Select(x => x.ConfigName)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!existingKeys.Contains("api.key", StringComparer.Ordinal))
        {
            databaseContext.ConfigItems.Add(new ConfigItem
            {
                ConfigName = "api.key",
                ConfigValue = GuidUtil.GenerateSecureGuid().ToString("N")
            });
        }

        if (!existingKeys.Contains("api.strm-key", StringComparer.Ordinal))
        {
            databaseContext.ConfigItems.Add(new ConfigItem
            {
                ConfigName = "api.strm-key",
                ConfigValue = GuidUtil.GenerateSecureGuid().ToString("N")
            });
        }

        try
        {
            await databaseContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException) when (databaseContext.ChangeTracker.Entries<ConfigItem>().Any())
        {
            databaseContext.ChangeTracker.Clear();
            var missingKeys = await databaseContext.ConfigItems
                .Where(x => x.ConfigName == "api.key" || x.ConfigName == "api.strm-key")
                .Select(x => x.ConfigName)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!missingKeys.Contains("api.key", StringComparer.Ordinal)
                || !missingKeys.Contains("api.strm-key", StringComparer.Ordinal))
                throw;
        }
    }

    private static DavItem CreateRootItem(
        Guid id,
        Guid? parentId,
        string name,
        DavItem.ItemType type,
        string path)
    {
        return new DavItem
        {
            Id = id,
            IdPrefix = id.GetFiveLengthPrefix(),
            CreatedAt = DateTime.UtcNow,
            ParentId = parentId,
            Name = name,
            FileSize = null,
            Type = type,
            Path = path
        };
    }
}
