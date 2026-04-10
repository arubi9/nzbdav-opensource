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
        if (string.IsNullOrEmpty(EnvironmentUtil.GetDatabaseUrl()))
        {
            await databaseContext.Database
                .MigrateAsync(targetMigration, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!string.IsNullOrEmpty(targetMigration))
            throw new InvalidOperationException("Targeted migrations are not supported for PostgreSQL bootstrap mode.");

        await databaseContext.Database
            .EnsureCreatedAsync(cancellationToken)
            .ConfigureAwait(false);

        await SeedPostgresBootstrapDataAsync(databaseContext, cancellationToken).ConfigureAwait(false);
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
