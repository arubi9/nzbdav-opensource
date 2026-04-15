using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services;

public static class DatabaseInitialization
{
    public static async Task InitializeAsync(
        DavDatabaseContext databaseContext,
        CancellationToken cancellationToken,
        string? targetMigration = null)
    {
        var isPostgres = !string.IsNullOrEmpty(EnvironmentUtil.GetDatabaseUrl());

        if (isPostgres)
            await BootstrapMigrationHistoryIfNeededAsync(databaseContext, cancellationToken).ConfigureAwait(false);

        await databaseContext.Database
            .MigrateAsync(targetMigration, cancellationToken)
            .ConfigureAwait(false);

        if (isPostgres)
            await SeedPostgresBootstrapDataAsync(databaseContext, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Legacy Postgres databases were created with EnsureCreatedAsync which does
    /// not create __EFMigrationsHistory.  Without the history table MigrateAsync
    /// tries to re-apply every migration and fails on the first CREATE TABLE.
    /// This method detects that case and seeds the history so that MigrateAsync
    /// only applies genuinely new migrations.
    /// </summary>
    private static async Task BootstrapMigrationHistoryIfNeededAsync(
        DavDatabaseContext databaseContext,
        CancellationToken cancellationToken)
    {
        var conn = databaseContext.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var cmd = conn.CreateCommand();

        // Check if migration history table already exists with entries
        cmd.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM information_schema.tables
                WHERE table_name = '__EFMigrationsHistory'
            )
            """;
        var historyTableExists = (bool)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;

        if (historyTableExists)
        {
            cmd.CommandText = """SELECT COUNT(*) FROM "__EFMigrationsHistory" """;
            var count = (long)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
            if (count > 0)
                return; // Already bootstrapped — MigrateAsync will handle pending migrations
        }

        // Check if this is an existing DB (has application tables from EnsureCreated)
        cmd.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM information_schema.tables
                WHERE table_name = 'items'
            )
            """;
        var hasExistingTables = (bool)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;

        if (!hasExistingTables)
            return; // Fresh DB — MigrateAsync will create everything from scratch

        // Legacy DB: create migration history and mark all known migrations as applied
        Log.Information("Bootstrapping __EFMigrationsHistory for legacy Postgres database");

        if (!historyTableExists)
        {
            cmd.CommandText = """
                CREATE TABLE "__EFMigrationsHistory" (
                    "MigrationId" VARCHAR(150) NOT NULL,
                    "ProductVersion" VARCHAR(32) NOT NULL,
                    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
                )
                """;
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var allMigrations = databaseContext.Database.GetMigrations().ToList();
        foreach (var migrationId in allMigrations)
        {
            cmd.CommandText = $"""
                INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                VALUES ('{migrationId}', '8.0.0')
                ON CONFLICT DO NOTHING
                """;
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        Log.Information("Bootstrapped {Count} migration entries into __EFMigrationsHistory", allMigrations.Count);
    }

    private static async Task SeedPostgresBootstrapDataAsync(
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
        await databaseContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
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

        await databaseContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
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
