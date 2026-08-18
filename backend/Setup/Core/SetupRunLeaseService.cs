using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Setup.Core;

/// <summary>
/// Durable, cross-process setup-run ownership. The grant is represented only
/// by its existing one-way hash; a worker must hold the current generation
/// before it may publish progress or perform another managed mutation.
/// </summary>
public sealed class SetupRunLeaseService
{
    public static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromSeconds(45);

    private readonly DavDatabaseContext _dbContext;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _leaseDuration;
    private readonly string _ownerId;

    public SetupRunLeaseService(
        DavDatabaseContext dbContext,
        TimeProvider? timeProvider = null,
        TimeSpan? leaseDuration = null,
        string? ownerId = null)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _leaseDuration = leaseDuration ?? DefaultLeaseDuration;
        if (_leaseDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        _ownerId = string.IsNullOrWhiteSpace(ownerId) ? Guid.NewGuid().ToString("N") : ownerId;
    }

    public string OwnerId => _ownerId;

    public Task<SetupRunLeaseHandle?> TryAcquireAsync(
        string grant,
        string purpose,
        CancellationToken cancellationToken = default)
    {
        var grantHash = SetupGrantCrypto.ComputeIssuedTokenHash(grant ?? string.Empty);
        ValidatePurpose(purpose);
        return ExecuteWithRetriesAsync(
            () => TryAcquireCoreAsync(grantHash, purpose, cancellationToken),
            cancellationToken);
    }

    private async Task<SetupRunLeaseHandle?> TryAcquireCoreAsync(
        string grantHash,
        string purpose,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _dbContext.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await SetupMutationFenceLock.AcquireAsync(_dbContext, transaction, cancellationToken).ConfigureAwait(false);
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var row = await _dbContext.SetupRunLeases
                .SingleOrDefaultAsync(item => item.Id == SetupRunLease.SingletonId, cancellationToken)
                .ConfigureAwait(false);
            var live = row is not null && row.LeaseUntilUtc > now;
            // A live row is already owned, even when the request comes from
            // this process. Re-acquisition must not create two handles for one
            // generation; all contention is decided by the durable lease row,
            // never by a process-local semaphore.
            if (live)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return null;
            }

            if (row is null)
            {
                row = new SetupRunLease { Id = SetupRunLease.SingletonId, Generation = 1 };
                _dbContext.SetupRunLeases.Add(row);
            }
            else if (!live || row.OwnerId != _ownerId || row.GrantHash != grantHash || row.Purpose != purpose)
            {
                row.Generation = checked(row.Generation + 1);
            }

            row.OwnerId = _ownerId;
            row.GrantHash = grantHash;
            row.Purpose = purpose;
            row.LeaseUntilUtc = now + _leaseDuration;
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _dbContext.ChangeTracker.Clear();
            return new SetupRunLeaseHandle(this, _ownerId, grantHash, purpose, row.Generation);
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            _dbContext.ChangeTracker.Clear();
            throw;
        }
    }

    public Task<bool> IsOwnerAsync(SetupRunLeaseHandle lease, CancellationToken cancellationToken = default)
        => ExecuteWithRetriesAsync(() => CheckAsync(lease, renew: false, cancellationToken), cancellationToken);

    /// <summary>Returns whether a live orchestration/verification run owns the durable row.</summary>
    public Task<bool> IsActiveSetupRunAsync(CancellationToken cancellationToken = default)
        => ExecuteWithRetriesAsync(() => IsActiveSetupRunCoreAsync(cancellationToken), cancellationToken);

    /// <summary>
    /// Reports only a live grant issuance/renewal lease. Callers use this for
    /// one short handoff-boundary retry; it is not permission to wait for or
    /// take over any other active setup worker.
    /// </summary>
    public Task<bool> IsActiveGrantOperationAsync(CancellationToken cancellationToken = default)
        => ExecuteWithRetriesAsync(() => IsActiveGrantOperationCoreAsync(cancellationToken), cancellationToken);

    private async Task<bool> IsActiveGrantOperationCoreAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await _dbContext.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await SetupMutationFenceLock.AcquireAsync(_dbContext, transaction, cancellationToken).ConfigureAwait(false);
            var row = await _dbContext.SetupRunLeases.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == SetupRunLease.SingletonId, cancellationToken)
                .ConfigureAwait(false);
            var active = row is not null
                         && row.LeaseUntilUtc > _timeProvider.GetUtcNow().UtcDateTime
                         && row.Purpose.StartsWith("grant-", StringComparison.Ordinal);
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return active;
        }
        finally
        {
            _dbContext.ChangeTracker.Clear();
        }
    }

    private async Task<bool> IsActiveSetupRunCoreAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await _dbContext.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await SetupMutationFenceLock.AcquireAsync(_dbContext, transaction, cancellationToken).ConfigureAwait(false);
            var row = await _dbContext.SetupRunLeases.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == SetupRunLease.SingletonId, cancellationToken)
                .ConfigureAwait(false);
            var active = row is not null
                         && row.LeaseUntilUtc > _timeProvider.GetUtcNow().UtcDateTime
                         && !row.Purpose.StartsWith("grant-", StringComparison.Ordinal);
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return active;
        }
        finally
        {
            _dbContext.ChangeTracker.Clear();
        }
    }

    public Task<bool> HeartbeatAsync(SetupRunLeaseHandle lease, CancellationToken cancellationToken = default)
        => ExecuteWithRetriesAsync(() => CheckAsync(lease, renew: true, cancellationToken), cancellationToken);

    public Task ReleaseAsync(SetupRunLeaseHandle lease, CancellationToken cancellationToken = default)
        => ExecuteWithRetriesAsync(() => ReleaseCoreAsync(lease, cancellationToken), cancellationToken);

    private async Task ReleaseCoreAsync(SetupRunLeaseHandle lease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        await using var transaction = await _dbContext.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await SetupMutationFenceLock.AcquireAsync(_dbContext, transaction, cancellationToken).ConfigureAwait(false);
            var row = await _dbContext.SetupRunLeases.SingleOrDefaultAsync(
                item => item.Id == SetupRunLease.SingletonId
                         && item.OwnerId == lease.OwnerId
                         && item.Generation == lease.Generation
                         && item.GrantHash == lease.GrantHash
                         && item.Purpose == lease.Purpose,
                cancellationToken).ConfigureAwait(false);
            if (row is not null)
                row.LeaseUntilUtc = _timeProvider.GetUtcNow().UtcDateTime;
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _dbContext.ChangeTracker.Clear();
        }
    }

    private async Task<bool> CheckAsync(SetupRunLeaseHandle lease, bool renew, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        await using var transaction = await _dbContext.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await SetupMutationFenceLock.AcquireAsync(_dbContext, transaction, cancellationToken).ConfigureAwait(false);
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var row = await _dbContext.SetupRunLeases.AsNoTracking().SingleOrDefaultAsync(
                item => item.Id == SetupRunLease.SingletonId,
                cancellationToken).ConfigureAwait(false);
            var owned = row is not null
                        && row.OwnerId == lease.OwnerId
                        && row.Generation == lease.Generation
                        && row.GrantHash == lease.GrantHash
                        && row.Purpose == lease.Purpose
                        && row.LeaseUntilUtc > now;
            if (owned && renew)
            {
                var tracked = await _dbContext.SetupRunLeases.SingleAsync(item => item.Id == SetupRunLease.SingletonId, cancellationToken).ConfigureAwait(false);
                tracked.LeaseUntilUtc = now + _leaseDuration;
                await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return owned;
        }
        finally
        {
            _dbContext.ChangeTracker.Clear();
        }
    }

    /// <summary>
    /// Checks and renews a lease while the caller already holds the setup
    /// mutation fence. This is the atomic guard used by progress and
    /// completion writes; a separate heartbeat immediately before a write
    /// would leave a stale-generation race window.
    /// </summary>
    internal async Task<bool> AssertCurrentWithinTransactionAsync(
        SetupRunLeaseHandle lease,
        IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(transaction);
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var renewedUntil = now + _leaseDuration;
        var updated = await _dbContext.SetupRunLeases
            .Where(item => item.Id == SetupRunLease.SingletonId
                        && item.OwnerId == lease.OwnerId
                        && item.Generation == lease.Generation
                        && item.GrantHash == lease.GrantHash
                        && item.Purpose == lease.Purpose
                        && item.LeaseUntilUtc > now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.LeaseUntilUtc, renewedUntil), cancellationToken)
            .ConfigureAwait(false);
        return updated == 1;
    }

    private static async Task ExecuteWithRetriesAsync(
        Func<Task> operation,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await operation().ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (attempt < 4
                                               && SetupTransactionOutcome.IsRetryableWriteAbort(exception))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task<T> ExecuteWithRetriesAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (Exception exception) when (attempt < 4
                                               && SetupTransactionOutcome.IsRetryableWriteAbort(exception))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void ValidatePurpose(string purpose)
    {
        if (string.IsNullOrWhiteSpace(purpose) || purpose.Length > 128 || purpose.Any(char.IsControl))
            throw new ArgumentException("A bounded setup lease purpose is required.", nameof(purpose));
    }
}

public sealed class SetupRunLeaseHandle
{
    private readonly SetupRunLeaseService _service;

    internal SetupRunLeaseHandle(SetupRunLeaseService service, string ownerId, string grantHash, string purpose, long generation)
    {
        _service = service;
        OwnerId = ownerId;
        GrantHash = grantHash;
        Purpose = purpose;
        Generation = generation;
    }

    public string OwnerId { get; }
    public string GrantHash { get; }
    public string Purpose { get; }
    public long Generation { get; }

    public Task<bool> AssertCurrentAsync(CancellationToken cancellationToken = default)
        => _service.IsOwnerAsync(this, cancellationToken);

    public Task<bool> HeartbeatAsync(CancellationToken cancellationToken = default)
        => _service.HeartbeatAsync(this, cancellationToken);

    internal Task<bool> AssertCurrentWithinTransactionAsync(
        IDbContextTransaction transaction,
        CancellationToken cancellationToken = default)
        => _service.AssertCurrentWithinTransactionAsync(this, transaction, cancellationToken);

    public Task ReleaseAsync(CancellationToken cancellationToken = default)
        => _service.ReleaseAsync(this, cancellationToken);
}
