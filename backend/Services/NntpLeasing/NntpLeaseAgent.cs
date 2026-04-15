using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Metrics;
using NzbWebDAV.Models;
using NzbWebDAV.Queue;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services.NntpLeasing;

public sealed class NntpLeaseAgent : BackgroundService
{
    private static readonly TimeSpan DefaultTickInterval = TimeSpan.FromSeconds(10);

    private readonly ConfigManager _configManager;
    private readonly NntpLeaseState _leaseState;
    private readonly Action<int, int> _applyProviderGrant;
    private readonly Action<int> _applyDownloadLimit;
    private readonly Func<DavDatabaseContext> _dbContextFactory;
    private readonly Func<string> _nodeIdFactory;
    private readonly Func<DateTime> _utcNow;
    private readonly TimeSpan _tickInterval;
    private readonly NodeRole _nodeRole;
    private readonly string _region;
    private readonly Func<DavDatabaseContext, CancellationToken, Task<bool>> _hasDemandAsync;
    private readonly Lock _applyLock = new();
    private readonly HashSet<int> _lastAppliedProviderIndexes = [];

    public NntpLeaseAgent(
        ConfigManager configManager,
        NntpLeaseState leaseState,
        UsenetStreamingClient streamingClient,
        QueueManager queueManager,
        ReadAheadWarmingService warmingService)
        : this(
            configManager,
            leaseState,
            streamingClient.ResizeProviderPool,
            streamingClient.UpdateMaxDownloadConnections,
            () => new DavDatabaseContext(),
            () => EnvironmentUtil.GetEnvironmentVariable("NZBDAV_NODE_ID")
                  ?? EnvironmentUtil.GetEnvironmentVariable("HOSTNAME")
                  ?? Environment.MachineName,
            NodeRoleConfig.Current,
            EnvironmentUtil.GetEnvironmentVariable("NZBDAV_REGION") ?? "unknown",
            DefaultTickInterval,
            () => DateTime.UtcNow,
            async (dbContext, cancellationToken) =>
            {
                var hasActiveNntp = (streamingClient.PoolStats?.TotalActive ?? 0) > 0;
                return NodeRoleConfig.Current switch
                {
                    // Streaming nodes always have demand — they must stay ready to
                    // serve on-demand streams.  Without this the node starts with
                    // zero connections, reports no demand, and the allocator never
                    // grants slots (demand deadlock).
                    NodeRole.Streaming => true,
                    // Ingest nodes have demand whenever queue items exist,
                    // regardless of pause state.  Without this, temporarily
                    // paused items cause has_demand=false → allocator grants
                    // zero slots → items can never resume (demand deadlock).
                    NodeRole.Ingest => hasActiveNntp
                        || queueManager.GetInProgressQueueItem().queueItem is not null
                        || await dbContext.QueueItems
                            .AnyAsync(cancellationToken)
                            .ConfigureAwait(false),
                    _ => false
                };
            })
    {
    }

    public NntpLeaseAgent(
        ConfigManager configManager,
        NntpLeaseState leaseState,
        Action<int, int> applyProviderGrant,
        Action<int> applyDownloadLimit,
        Func<DavDatabaseContext> dbContextFactory,
        Func<string> nodeIdFactory,
        NodeRole nodeRole,
        string region,
        TimeSpan? tickInterval = null,
        Func<DateTime>? utcNow = null,
        Func<DavDatabaseContext, CancellationToken, Task<bool>>? hasDemandAsync = null)
    {
        if (nodeRole == NodeRole.Combined)
            throw new InvalidOperationException("Per-node NNTP leasing does not support the Combined role.");

        _configManager = configManager;
        _leaseState = leaseState;
        _applyProviderGrant = applyProviderGrant;
        _applyDownloadLimit = applyDownloadLimit;
        _dbContextFactory = dbContextFactory;
        _nodeIdFactory = nodeIdFactory;
        _nodeRole = nodeRole;
        _region = string.IsNullOrWhiteSpace(region) ? "unknown" : region;
        _tickInterval = tickInterval ?? DefaultTickInterval;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _hasDemandAsync = hasDemandAsync ?? ((_, _) => Task.FromResult(true));
        _configManager.OnConfigChanged += OnConfigChanged;
    }

    public static bool ShouldUsePerNodeLeasing(bool isMultiNode, NodeRole nodeRole)
    {
        return isMultiNode && nodeRole is NodeRole.Streaming or NodeRole.Ingest;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_tickInterval);

        try
        {
            await RunOnce(stoppingToken).ConfigureAwait(false);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                await RunOnce(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normal shutdown
        }
    }

    public async Task RunOnce(CancellationToken cancellationToken)
    {
        var pooledProviders = GetPooledProviders();
        var providerIndexes = pooledProviders.Select(x => x.providerIndex).ToHashSet();
        var now = _utcNow();
        var nodeId = _nodeIdFactory();

        try
        {
            await using var dbContext = _dbContextFactory();
            var existingHeartbeats = await dbContext.NntpNodeHeartbeats
                .Where(x => x.NodeId == nodeId)
                .AsTracking()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var staleHeartbeat in existingHeartbeats.Where(x => !providerIndexes.Contains(x.ProviderIndex)).ToList())
                dbContext.NntpNodeHeartbeats.Remove(staleHeartbeat);

            var hasDemand = await _hasDemandAsync(dbContext, cancellationToken).ConfigureAwait(false);

            foreach (var pooledProvider in pooledProviders)
            {
                var heartbeat = existingHeartbeats.SingleOrDefault(x => x.ProviderIndex == pooledProvider.providerIndex);
                if (heartbeat == null)
                {
                    heartbeat = new NntpNodeHeartbeat
                    {
                        NodeId = nodeId,
                        ProviderIndex = pooledProvider.providerIndex,
                        Region = _region
                    };
                    dbContext.NntpNodeHeartbeats.Add(heartbeat);
                }

                heartbeat.Role = _nodeRole;
                heartbeat.Region = _region;
                heartbeat.DesiredSlots = pooledProvider.provider.MaxConnections;
                heartbeat.ActiveSlots = 0;
                heartbeat.LiveSlots = 0;
                heartbeat.HasDemand = hasDemand;
                heartbeat.HeartbeatAt = now;
            }

            var leases = providerIndexes.Count == 0
                ? []
                : await dbContext.NntpConnectionLeases
                    .Where(x => x.NodeId == nodeId && x.Role == _nodeRole && providerIndexes.Contains(x.ProviderIndex))
                    .AsNoTracking()
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            RefreshLeaseState(providerIndexes, leases, now);
            ReapplyCurrentFreshLimits(providerIndexes, now);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ReapplyCurrentFreshLimits(providerIndexes, now);
            Log.Warning(ex, "NntpLeaseAgent lease pass failed for node {NodeId}", nodeId);
        }
    }

    private void OnConfigChanged(object? sender, ConfigManager.ConfigEventArgs e)
    {
        if (!e.ChangedConfig.ContainsKey("usenet.providers"))
            return;

        var providerIndexes = GetPooledProviders().Select(x => x.providerIndex).ToHashSet();
        _leaseState.PruneProviders(providerIndexes);
        ReapplyCurrentFreshLimits(providerIndexes, _utcNow());
    }

    private List<(UsenetProviderConfig.ConnectionDetails provider, int providerIndex)> GetPooledProviders()
    {
        return _configManager.GetUsenetProviderConfig().Providers
            .Select((provider, providerIndex) => (provider, providerIndex))
            .Where(x => x.provider.Type == ProviderType.Pooled)
            .ToList();
    }

    private void RefreshLeaseState(
        IReadOnlyCollection<int> providerIndexes,
        IEnumerable<NntpConnectionLease> leases,
        DateTime now)
    {
        _leaseState.PruneProviders(providerIndexes);
        var leaseByProviderIndex = leases.ToDictionary(x => x.ProviderIndex);

        foreach (var providerIndex in providerIndexes)
        {
            if (leaseByProviderIndex.TryGetValue(providerIndex, out var lease))
            {
                _leaseState.Apply(
                    lease.ProviderIndex,
                    lease.GrantedSlots,
                    lease.Epoch,
                    lease.LeaseUntil,
                    lease.ReservedSlots,
                    lease.BorrowedSlots);
                continue;
            }

            _leaseState.Apply(providerIndex, grantedSlots: 0, epoch: 0, leaseUntil: now);
        }
    }

    private void ReapplyCurrentFreshLimits(IReadOnlyCollection<int> providerIndexes, DateTime now)
    {
        lock (_applyLock)
        {
            foreach (var staleProviderIndex in _lastAppliedProviderIndexes.Except(providerIndexes).ToList())
            {
                _applyProviderGrant(staleProviderIndex, 0);
            }

            foreach (var providerIndex in providerIndexes)
            {
                _applyProviderGrant(providerIndex, _leaseState.GetFreshProviderGrant(providerIndex, now));
            }

            _lastAppliedProviderIndexes.Clear();
            _lastAppliedProviderIndexes.UnionWith(providerIndexes);
            _applyDownloadLimit(_leaseState.GetFreshTotalGrantedSlots(providerIndexes, now));
        }
    }

    public override void Dispose()
    {
        _configManager.OnConfigChanged -= OnConfigChanged;
        base.Dispose();
    }
}
