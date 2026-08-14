using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services;

public sealed class ContentIndexRecoveryService(ConfigManager configManager) : BackgroundService
{
    // EF spends one SQL variable per element of a Contains() list and SQLite caps
    // that at 32766, so every id lookup below is chunked. This mirrors
    // HealthCheckService.SegmentIdQueryChunkSize; a library of a few thousand
    // releases clears the cap on its own.
    private const int IdQueryChunkSize = 500;

    // Items carry no segment data, so they restore in large batches.
    internal const int DefaultItemBatchSize = 500;

    // File rows carry SegmentIds -- a single large release can hold ~80k of them
    // -- so they restore in small batches to keep the resident set bounded.
    internal const int DefaultFileBatchSize = 32;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RecoverAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to restore /content items from persisted snapshot.");
        }
    }

    internal Task RecoverAsync(CancellationToken cancellationToken)
    {
        return RecoverAsync(DefaultItemBatchSize, DefaultFileBatchSize, cancellationToken);
    }

    internal async Task RecoverAsync(int itemBatchSize, int fileBatchSize, CancellationToken cancellationToken)
    {
        var snapshotReadResult = await ContentIndexSnapshotStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        foreach (var warning in snapshotReadResult.Warnings)
            Log.Warning(warning);

        var summary = snapshotReadResult.Summary;
        if (summary == null || summary.ItemCount == 0) return;

        await using var dbContext = new DavDatabaseContext();
        var plan = await BuildRecoveryPlanAsync(dbContext, summary, configManager, cancellationToken).ConfigureAwait(false);
        if (!plan.NeedsRecovery) return;

        Log.Warning(
            "Recovering /content from snapshot '{SourcePath}'. Full restore: {RestoreAll}. Missing items: {MissingItems}. Missing metadata rows: {MissingMetadata}.",
            snapshotReadResult.SourcePath,
            plan.RestoreAllContentItems,
            plan.MissingItemIds.Count,
            plan.MissingNzbFileIds.Count + plan.MissingRarFileIds.Count + plan.MissingMultipartFileIds.Count
        );

        await RestoreAsync(
            dbContext,
            snapshotReadResult.SourcePath!,
            plan,
            itemBatchSize,
            fileBatchSize,
            cancellationToken
        ).ConfigureAwait(false);
        Log.Information("Content index recovery complete.");
    }

    /// <summary>
    /// Plans the recovery from the snapshot summary alone. The summary holds only
    /// ids, parents and types -- never SegmentIds -- so the plan costs a bounded
    /// amount of memory per item rather than per usenet segment.
    /// </summary>
    internal static async Task<RecoveryPlan> BuildRecoveryPlanAsync
    (
        DavDatabaseContext dbContext,
        ContentIndexSnapshotStore.ContentIndexSnapshotSummary summary,
        ConfigManager configManager,
        CancellationToken cancellationToken
    )
    {
        var currentItems = await dbContext.Items
            .AsNoTracking()
            .Where(x => x.Path.StartsWith("/content/"))
            .Select(x => new { x.Id, x.ParentId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (currentItems.Count == 0)
        {
            return new RecoveryPlan
            {
                RestoreAllContentItems = true,
            };
        }

        var snapshotItemsById = summary.ItemsById;
        var currentItemIds = currentItems.Select(x => x.Id).ToHashSet();
        var missingItemIds = new HashSet<Guid>();

        foreach (var item in currentItems)
        {
            if (item.ParentId == null || item.ParentId == DavItem.ContentFolder.Id) continue;
            if (currentItemIds.Contains(item.ParentId.Value)) continue;
            AddItemAndAncestors(item.ParentId.Value, snapshotItemsById, missingItemIds);
        }

        foreach (var linkedItemId in GetLinkedItemIds(configManager))
        {
            if (currentItemIds.Contains(linkedItemId)) continue;
            AddItemAndAncestors(linkedItemId, snapshotItemsById, missingItemIds);
        }

        var effectiveNzbFileIds = new List<Guid>();
        var effectiveRarFileIds = new List<Guid>();
        var effectiveMultipartFileIds = new List<Guid>();
        foreach (var (id, item) in snapshotItemsById)
        {
            if (!currentItemIds.Contains(id) && !missingItemIds.Contains(id)) continue;
            switch (item.Type)
            {
                case DavItem.ItemType.NzbFile:
                    effectiveNzbFileIds.Add(id);
                    break;
                case DavItem.ItemType.RarFile:
                    effectiveRarFileIds.Add(id);
                    break;
                case DavItem.ItemType.MultipartFile:
                    effectiveMultipartFileIds.Add(id);
                    break;
            }
        }

        var currentNzbIds = await GetExistingIdsAsync(
            effectiveNzbFileIds,
            chunk => dbContext.NzbFiles.AsNoTracking().Where(x => chunk.Contains(x.Id)).Select(x => x.Id),
            cancellationToken
        ).ConfigureAwait(false);
        var currentRarIds = await GetExistingIdsAsync(
            effectiveRarFileIds,
            chunk => dbContext.RarFiles.AsNoTracking().Where(x => chunk.Contains(x.Id)).Select(x => x.Id),
            cancellationToken
        ).ConfigureAwait(false);
        var currentMultipartIds = await GetExistingIdsAsync(
            effectiveMultipartFileIds,
            chunk => dbContext.MultipartFiles.AsNoTracking().Where(x => chunk.Contains(x.Id)).Select(x => x.Id),
            cancellationToken
        ).ConfigureAwait(false);

        return new RecoveryPlan
        {
            MissingItemIds = missingItemIds,
            MissingNzbFileIds = effectiveNzbFileIds.Where(x => !currentNzbIds.Contains(x)).ToHashSet(),
            MissingRarFileIds = effectiveRarFileIds.Where(x => !currentRarIds.Contains(x)).ToHashSet(),
            MissingMultipartFileIds = effectiveMultipartFileIds.Where(x => !currentMultipartIds.Contains(x)).ToHashSet(),
        };
    }

    /// <summary>
    /// Restores the planned rows by streaming the snapshot file and saving in
    /// bounded batches. The snapshot is never held in memory, and neither is the
    /// set of rows to insert.
    /// </summary>
    internal static Task RestoreAsync
    (
        DavDatabaseContext dbContext,
        string sourcePath,
        RecoveryPlan plan,
        CancellationToken cancellationToken
    )
    {
        return RestoreAsync(dbContext, sourcePath, plan, DefaultItemBatchSize, DefaultFileBatchSize, cancellationToken);
    }

    internal static async Task RestoreAsync
    (
        DavDatabaseContext dbContext,
        string sourcePath,
        RecoveryPlan plan,
        int itemBatchSize,
        int fileBatchSize,
        CancellationToken cancellationToken
    )
    {
        if (!plan.NeedsRecovery) return;

        var existingItemIds = await dbContext.Items
            .AsNoTracking()
            .Where(x => x.Path.StartsWith("/content/"))
            .Select(x => x.Id)
            .ToHashSetAsync(cancellationToken)
            .ConfigureAwait(false);

        // Items first, and in snapshot order: the writer emits them ordered by
        // path, so a parent always precedes its children and is committed in the
        // same batch or an earlier one.
        var pendingItems = 0;
        await foreach (var item in ContentIndexSnapshotStore
                           .ReadItemsAsync(sourcePath, cancellationToken)
                           .ConfigureAwait(false))
        {
            if (!plan.RestoreAllContentItems && !plan.MissingItemIds.Contains(item.Id)) continue;
            if (!existingItemIds.Add(item.Id)) continue;

            dbContext.Items.Add(Clone(item));
            if (++pendingItems < itemBatchSize) continue;

            await SaveBatchAsync(dbContext, cancellationToken).ConfigureAwait(false);
            pendingItems = 0;
        }

        if (pendingItems > 0)
            await SaveBatchAsync(dbContext, cancellationToken).ConfigureAwait(false);

        await RestoreFileRowsAsync(
            dbContext,
            ContentIndexSnapshotStore.ReadNzbFilesAsync(sourcePath, cancellationToken),
            x => x.Id,
            plan.RestoreAllContentItems ? null : plan.MissingNzbFileIds,
            existingItemIds,
            chunk => dbContext.NzbFiles.AsNoTracking().Where(x => chunk.Contains(x.Id)).Select(x => x.Id),
            x => new DavNzbFile { Id = x.Id, SegmentIds = x.SegmentIds },
            fileBatchSize,
            cancellationToken
        ).ConfigureAwait(false);

        await RestoreFileRowsAsync(
            dbContext,
            ContentIndexSnapshotStore.ReadRarFilesAsync(sourcePath, cancellationToken),
            x => x.Id,
            plan.RestoreAllContentItems ? null : plan.MissingRarFileIds,
            existingItemIds,
            chunk => dbContext.RarFiles.AsNoTracking().Where(x => chunk.Contains(x.Id)).Select(x => x.Id),
            x => new DavRarFile { Id = x.Id, RarParts = x.RarParts },
            fileBatchSize,
            cancellationToken
        ).ConfigureAwait(false);

        await RestoreFileRowsAsync(
            dbContext,
            ContentIndexSnapshotStore.ReadMultipartFilesAsync(sourcePath, cancellationToken),
            x => x.Id,
            plan.RestoreAllContentItems ? null : plan.MissingMultipartFileIds,
            existingItemIds,
            chunk => dbContext.MultipartFiles.AsNoTracking().Where(x => chunk.Contains(x.Id)).Select(x => x.Id),
            x => new DavMultipartFile { Id = x.Id, Metadata = x.Metadata },
            fileBatchSize,
            cancellationToken
        ).ConfigureAwait(false);
    }

    private static async Task RestoreFileRowsAsync<TSnapshotRow, TEntity>
    (
        DavDatabaseContext dbContext,
        IAsyncEnumerable<TSnapshotRow> snapshotRows,
        Func<TSnapshotRow, Guid> idSelector,
        IReadOnlySet<Guid>? idsToRestore,
        HashSet<Guid> existingItemIds,
        Func<Guid[], IQueryable<Guid>> existingRowQuery,
        Func<TSnapshotRow, TEntity> toEntity,
        int batchSize,
        CancellationToken cancellationToken
    ) where TEntity : class
    {
        var batch = new List<TSnapshotRow>(batchSize);

        await foreach (var row in snapshotRows.ConfigureAwait(false))
        {
            var id = idSelector(row);
            if (idsToRestore != null && !idsToRestore.Contains(id)) continue;
            if (!existingItemIds.Contains(id)) continue;

            batch.Add(row);
            if (batch.Count < batchSize) continue;

            await FlushFileBatchAsync(dbContext, batch, idSelector, existingRowQuery, toEntity, cancellationToken)
                .ConfigureAwait(false);
        }

        if (batch.Count > 0)
            await FlushFileBatchAsync(dbContext, batch, idSelector, existingRowQuery, toEntity, cancellationToken)
                .ConfigureAwait(false);
    }

    private static async Task FlushFileBatchAsync<TSnapshotRow, TEntity>
    (
        DavDatabaseContext dbContext,
        List<TSnapshotRow> batch,
        Func<TSnapshotRow, Guid> idSelector,
        Func<Guid[], IQueryable<Guid>> existingRowQuery,
        Func<TSnapshotRow, TEntity> toEntity,
        CancellationToken cancellationToken
    ) where TEntity : class
    {
        var existingIds = await GetExistingIdsAsync(
            batch.Select(idSelector).ToList(),
            existingRowQuery,
            cancellationToken
        ).ConfigureAwait(false);

        foreach (var row in batch)
        {
            if (!existingIds.Add(idSelector(row))) continue;
            dbContext.Set<TEntity>().Add(toEntity(row));
        }

        batch.Clear();
        await SaveBatchAsync(dbContext, cancellationToken).ConfigureAwait(false);
    }

    private static async Task SaveBatchAsync(DavDatabaseContext dbContext, CancellationToken cancellationToken)
    {
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Tracked entities keep every restored SegmentIds array alive for the
        // lifetime of the context, which is exactly what must not happen here.
        dbContext.ChangeTracker.Clear();
    }

    private static async Task<HashSet<Guid>> GetExistingIdsAsync
    (
        IReadOnlyCollection<Guid> ids,
        Func<Guid[], IQueryable<Guid>> query,
        CancellationToken cancellationToken
    )
    {
        var existing = new HashSet<Guid>();
        foreach (var chunk in ids.Chunk(IdQueryChunkSize))
        {
            var found = await query(chunk).ToListAsync(cancellationToken).ConfigureAwait(false);
            existing.UnionWith(found);
        }

        return existing;
    }

    private static void AddItemAndAncestors
    (
        Guid itemId,
        IReadOnlyDictionary<Guid, ContentIndexSnapshotStore.SnapshotItem> snapshotItemsById,
        ISet<Guid> missingItemIds
    )
    {
        var currentId = itemId;
        while (snapshotItemsById.TryGetValue(currentId, out var item))
        {
            if (!missingItemIds.Add(currentId)) break;
            if (item.ParentId == null || item.ParentId == DavItem.ContentFolder.Id) break;
            currentId = item.ParentId.Value;
        }
    }

    private static IEnumerable<Guid> GetLinkedItemIds(ConfigManager configManager)
    {
        var libraryDir = configManager.GetLibraryDir();
        if (string.IsNullOrWhiteSpace(libraryDir) || !Directory.Exists(libraryDir))
            return [];

        try
        {
            return OrganizedLinksUtil.GetLibraryDavItemLinks(configManager)
                .Select(x => x.DavItemId)
                .Distinct()
                .ToArray();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to inspect library links while evaluating /content recovery.");
            return [];
        }
    }

    private static DavItem Clone(DavItem item)
    {
        return new DavItem
        {
            Id = item.Id,
            IdPrefix = item.IdPrefix,
            CreatedAt = item.CreatedAt,
            ParentId = item.ParentId,
            Name = item.Name,
            FileSize = item.FileSize,
            Type = item.Type,
            Path = item.Path,
            ReleaseDate = item.ReleaseDate,
            LastHealthCheck = item.LastHealthCheck,
            NextHealthCheck = item.NextHealthCheck,
        };
    }

    internal sealed class RecoveryPlan
    {
        public bool RestoreAllContentItems { get; init; }
        public HashSet<Guid> MissingItemIds { get; init; } = [];
        public HashSet<Guid> MissingNzbFileIds { get; init; } = [];
        public HashSet<Guid> MissingRarFileIds { get; init; } = [];
        public HashSet<Guid> MissingMultipartFileIds { get; init; } = [];

        public bool NeedsRecovery =>
            RestoreAllContentItems
            || MissingItemIds.Count > 0
            || MissingNzbFileIds.Count > 0
            || MissingRarFileIds.Count > 0
            || MissingMultipartFileIds.Count > 0;
    }
}
