using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Database.Interceptors;

public sealed class ContentIndexSnapshotInterceptor : SaveChangesInterceptor
{
    internal static readonly DebouncedSnapshotWriter SnapshotWriter = new();
    private static readonly ConditionalWeakTable<DbContext, PendingSnapshotMarker> PendingSnapshots = new();

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        MarkPendingSnapshot(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync
    (
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default
    )
    {
        MarkPendingSnapshot(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        MarkSnapshotDirty(eventData.Context);
        return base.SavedChanges(eventData, result);
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        MarkSnapshotDirty(eventData.Context);
        return base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        ClearPendingSnapshot(eventData.Context);
        base.SaveChangesFailed(eventData);
    }

    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        ClearPendingSnapshot(eventData.Context);
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    private static void MarkPendingSnapshot(DbContext? dbContext)
    {
        if (dbContext == null || !HasContentIndexChanges(dbContext)) return;
        PendingSnapshots.GetValue(dbContext, _ => new PendingSnapshotMarker());
    }

    private static void MarkSnapshotDirty(DbContext? dbContext)
    {
        if (dbContext == null) return;
        if (!PendingSnapshots.TryGetValue(dbContext, out _)) return;
        PendingSnapshots.Remove(dbContext);
        SnapshotWriter.MarkDirty();
    }

    private static void ClearPendingSnapshot(DbContext? dbContext)
    {
        if (dbContext == null) return;
        PendingSnapshots.Remove(dbContext);
    }

    private static bool HasContentIndexChanges(DbContext dbContext)
    {
        return dbContext.ChangeTracker.Entries().Any(entry =>
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
                return false;

            return entry.Entity switch
            {
                DavItem item => item.Path.StartsWith("/content/", StringComparison.Ordinal),
                DavNzbFile => true,
                DavRarFile => true,
                DavMultipartFile => true,
                _ => false
            };
        });
    }

    private sealed class PendingSnapshotMarker
    {
    }
}
