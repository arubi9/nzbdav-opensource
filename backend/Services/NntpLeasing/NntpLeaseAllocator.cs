using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using Serilog;

namespace NzbWebDAV.Services.NntpLeasing;

public sealed class NntpLeaseAllocator : BackgroundService
{
    private const int AdvisoryLockKeyPart1 = 0x4E4E5450; // NNTP
    private const int AdvisoryLockKeyPart2 = 0x4C454153; // LEAS
    private static readonly TimeSpan DefaultTickInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultHeartbeatTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultLeaseTtl = TimeSpan.FromSeconds(30);

    private readonly ConfigManager _configManager;
    private readonly Func<DavDatabaseContext> _dbContextFactory;
    private readonly Func<DavDatabaseContext, CancellationToken, ValueTask<bool>> _leadershipGate;
    private readonly TimeSpan _heartbeatTtl;
    private readonly TimeSpan _leaseTtl;
    private readonly TimeSpan _tickInterval;
    private readonly Func<DateTime> _utcNow;
    private readonly Dictionary<int, long> _lastEpochByProvider = [];

    public NntpLeaseAllocator(ConfigManager configManager)
        : this(
            configManager,
            () => new DavDatabaseContext(),
            TryAcquireLeadershipAsync,
            DefaultHeartbeatTtl,
            DefaultLeaseTtl,
            DefaultTickInterval,
            () => DateTime.UtcNow)
    {
    }

    public NntpLeaseAllocator(
        ConfigManager configManager,
        Func<DavDatabaseContext> dbContextFactory,
        Func<DavDatabaseContext, CancellationToken, ValueTask<bool>> leadershipGate,
        TimeSpan? heartbeatTtl = null,
        TimeSpan? leaseTtl = null,
        TimeSpan? tickInterval = null,
        Func<DateTime>? utcNow = null)
    {
        _configManager = configManager;
        _dbContextFactory = dbContextFactory;
        _leadershipGate = leadershipGate;
        _heartbeatTtl = heartbeatTtl ?? DefaultHeartbeatTtl;
        _leaseTtl = leaseTtl ?? DefaultLeaseTtl;
        _tickInterval = tickInterval ?? DefaultTickInterval;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_tickInterval);

        try
        {
            await AllocateOnce(stoppingToken).ConfigureAwait(false);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                await AllocateOnce(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normal shutdown
        }
    }

    public async Task AllocateOnce(CancellationToken cancellationToken)
    {
        var pooledProviders = _configManager.GetUsenetProviderConfig().Providers
            .Select((provider, providerIndex) => new { provider, providerIndex })
            .Where(x => x.provider.Type == ProviderType.Pooled)
            .ToList();
        var now = _utcNow();
        var heartbeatCutoff = now.Subtract(_heartbeatTtl);
        var leaseUntil = now.Add(_leaseTtl);
        var providerIndexes = pooledProviders.Select(x => x.providerIndex).ToHashSet();

        try
        {
            await using var dbContext = _dbContextFactory();
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            if (!await _leadershipGate(dbContext, cancellationToken).ConfigureAwait(false))
                return;

            var activeHeartbeats = await dbContext.NntpNodeHeartbeats
                .Where(x => providerIndexes.Contains(x.ProviderIndex) && x.HeartbeatAt >= heartbeatCutoff)
                .AsTracking()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var existingLeases = await dbContext.NntpConnectionLeases
                .AsTracking()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            SeedEpochCache(existingLeases);

            var staleProviderLeases = existingLeases
                .Where(x => !providerIndexes.Contains(x.ProviderIndex))
                .ToList();
            if (staleProviderLeases.Count > 0)
                dbContext.NntpConnectionLeases.RemoveRange(staleProviderLeases);

            foreach (var pooledProvider in pooledProviders)
            {
                AllocateProvider(
                    dbContext,
                    pooledProvider.providerIndex,
                    pooledProvider.provider.MaxConnections,
                    activeHeartbeats.Where(x => x.ProviderIndex == pooledProvider.providerIndex).ToList(),
                    existingLeases.Where(x => x.ProviderIndex == pooledProvider.providerIndex).ToList(),
                    now,
                    leaseUntil);
            }

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "NntpLeaseAllocator allocation pass failed");
        }
    }

    private void AllocateProvider(
        DavDatabaseContext dbContext,
        int providerIndex,
        int totalSlots,
        List<NntpNodeHeartbeat> activeHeartbeats,
        List<NntpConnectionLease> existingLeases,
        DateTime now,
        DateTime leaseUntil)
    {
        var observedEpoch = existingLeases.Count == 0 ? 0 : existingLeases.Max(x => x.Epoch);
        var nextEpoch = GetNextEpoch(providerIndex, observedEpoch);

        if (activeHeartbeats.Count == 0)
        {
            RememberEpoch(providerIndex, observedEpoch);
            if (existingLeases.Count > 0)
                dbContext.NntpConnectionLeases.RemoveRange(existingLeases);
            return;
        }

        var orderedHeartbeats = activeHeartbeats
            .OrderBy(x => x.NodeId, StringComparer.Ordinal)
            .ThenBy(x => x.Role)
            .Select(TryToLeaseHeartbeat)
            .Where(x => x != null)
            .Select(x => x!)
            .ToList();

        if (orderedHeartbeats.Count == 0)
        {
            RememberEpoch(providerIndex, observedEpoch);
            if (existingLeases.Count > 0)
                dbContext.NntpConnectionLeases.RemoveRange(existingLeases);
            return;
        }

        var grants = NntpLeasePolicy.Compute(
            totalSlots,
            orderedHeartbeats,
            nextEpoch);

        var leaseByNodeId = existingLeases.ToDictionary(x => x.NodeId, StringComparer.Ordinal);
        var activeNodeIds = orderedHeartbeats.Select(x => x.NodeId).ToHashSet(StringComparer.Ordinal);

        foreach (var staleLease in existingLeases.Where(x => !activeNodeIds.Contains(x.NodeId)))
            dbContext.NntpConnectionLeases.Remove(staleLease);

        foreach (var grant in grants)
        {
            if (!leaseByNodeId.TryGetValue(grant.NodeId, out var lease))
            {
                lease = new NntpConnectionLease
                {
                    NodeId = grant.NodeId,
                    ProviderIndex = providerIndex
                };
                dbContext.NntpConnectionLeases.Add(lease);
            }

            lease.Role = ToNodeRole(grant.NodeRole);
            lease.ReservedSlots = grant.GrantedSlots;
            lease.BorrowedSlots = 0;
            lease.GrantedSlots = grant.GrantedSlots;
            lease.Epoch = grant.Epoch;
            lease.LeaseUntil = leaseUntil;
            lease.UpdatedAt = now;
        }

        RememberEpoch(providerIndex, nextEpoch);
    }

    private void SeedEpochCache(IEnumerable<NntpConnectionLease> existingLeases)
    {
        foreach (var providerEpoch in existingLeases
                     .GroupBy(x => x.ProviderIndex)
                     .Select(x => new { ProviderIndex = x.Key, Epoch = x.Max(y => y.Epoch) }))
        {
            RememberEpoch(providerEpoch.ProviderIndex, providerEpoch.Epoch);
        }
    }

    private long GetNextEpoch(int providerIndex, long observedEpoch)
    {
        var cachedEpoch = _lastEpochByProvider.GetValueOrDefault(providerIndex);
        return Math.Max(cachedEpoch, observedEpoch) + 1;
    }

    private void RememberEpoch(int providerIndex, long epoch)
    {
        if (_lastEpochByProvider.TryGetValue(providerIndex, out var currentEpoch) && currentEpoch >= epoch)
            return;

        _lastEpochByProvider[providerIndex] = epoch;
    }

    private static NntpLeasePolicy.LeaseHeartbeat? TryToLeaseHeartbeat(NntpNodeHeartbeat heartbeat)
    {
        var nodeRole = TryToLeaseNodeRole(heartbeat.Role);
        if (nodeRole == null)
        {
            Log.Warning(
                "NntpLeaseAllocator skipping heartbeat for node {NodeId} provider {ProviderIndex} with unsupported role {Role}",
                heartbeat.NodeId,
                heartbeat.ProviderIndex,
                heartbeat.Role);
            return null;
        }

        return new NntpLeasePolicy.LeaseHeartbeat(
            heartbeat.NodeId,
            nodeRole.Value,
            heartbeat.HasDemand);
    }

    private static NntpLeaseNodeRole? TryToLeaseNodeRole(NodeRole role)
    {
        return role switch
        {
            NodeRole.Streaming => NntpLeaseNodeRole.Streaming,
            NodeRole.Ingest => NntpLeaseNodeRole.Ingest,
            _ => null
        };
    }

    private static NodeRole ToNodeRole(NntpLeaseNodeRole role)
    {
        return role switch
        {
            NntpLeaseNodeRole.Streaming => NodeRole.Streaming,
            NntpLeaseNodeRole.Ingest => NodeRole.Ingest,
            _ => throw new InvalidOperationException($"Unsupported NNTP lease node role '{role}'.")
        };
    }

    private static async ValueTask<bool> TryAcquireLeadershipAsync(DavDatabaseContext dbContext, CancellationToken cancellationToken)
    {
        if (!string.Equals(dbContext.Database.ProviderName, "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal))
            return true;

        await using var command = CreateLeadershipCommand(dbContext.Database.CurrentTransaction?.GetDbTransaction(), dbContext.Database.GetDbConnection());
        if (command.Connection!.State != System.Data.ConnectionState.Open)
            await command.Connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is bool acquired && acquired;
    }

    private static DbCommand CreateLeadershipCommand(DbTransaction? transaction, DbConnection connection)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT pg_try_advisory_xact_lock(@key1, @key2);";

        var key1 = command.CreateParameter();
        key1.ParameterName = "@key1";
        key1.Value = AdvisoryLockKeyPart1;
        command.Parameters.Add(key1);

        var key2 = command.CreateParameter();
        key2.ParameterName = "@key2";
        key2.Value = AdvisoryLockKeyPart2;
        command.Parameters.Add(key2);

        return command;
    }
}
