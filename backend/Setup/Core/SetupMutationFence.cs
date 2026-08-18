using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Setup.Core;

/// <summary>Portable database-wide setup mutation fence.</summary>
internal static class SetupMutationFenceLock
{
    internal static async Task<SetupMutationFence> AcquireAsync(
        DavDatabaseContext context,
        IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(transaction);

        // The migration seeds this row. The fallback keeps old/partially
        // bootstrapped installations fail-safe and is itself serialized by the
        // transaction's serializable writer lock.
        var fence = context.SetupMutationFences.Local
            .SingleOrDefault(x => x.Id == SetupMutationFence.SingletonId)
            ?? await context.SetupMutationFences
                .SingleOrDefaultAsync(x => x.Id == SetupMutationFence.SingletonId, cancellationToken)
                .ConfigureAwait(false);
        if (fence is null)
        {
            fence = new SetupMutationFence { Id = SetupMutationFence.SingletonId };
            context.SetupMutationFences.Add(fence);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        // UPDATE is intentionally a no-op. PostgreSQL takes a row lock and
        // SQLite takes its serializable writer lock. Unlike an in-process
        // semaphore this protects every service/process using the database.
        await context.Database.ExecuteSqlRawAsync(
                "UPDATE \"setup_mutation_fence\" SET \"epoch\" = \"epoch\" WHERE \"id\" = 1",
                cancellationToken)
            .ConfigureAwait(false);
        // A long-lived context can have a stale tracked fence from an earlier
        // transaction. Refresh only after the row lock is held so reservation
        // decisions are based on the cross-process authoritative value.
        await context.Entry(fence).ReloadAsync(cancellationToken).ConfigureAwait(false);
        return fence;
    }

    internal static async Task<(IDbContextTransaction Transaction, SetupMutationFence Fence)> BeginAsync(
        DavDatabaseContext context,
        CancellationToken cancellationToken)
    {
        var transaction = await context.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var fence = await AcquireAsync(context, transaction, cancellationToken).ConfigureAwait(false);
            return (transaction, fence);
        }
        catch
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
