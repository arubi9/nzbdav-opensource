using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Npgsql;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using Serilog;

namespace NzbWebDAV.Services;

public sealed class ConnectionPoolCoordinator : BackgroundService
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ActiveWindow = TimeSpan.FromSeconds(30);

    private readonly ConfigManager _configManager;
    private readonly Action<int, int> _applyClaim;
    private readonly Func<DavDatabaseContext> _dbContextFactory;
    private readonly string _nodeId;
    private readonly Dictionary<int, int> _lastKnownClaims = [];

    public ConnectionPoolCoordinator(ConfigManager configManager, UsenetStreamingClient streamingClient)
        : this(
            configManager,
            streamingClient.ResizeProviderPool,
            () => new DavDatabaseContext(),
            () => Guid.NewGuid().ToString("N"))
    {
    }

    public ConnectionPoolCoordinator(
        ConfigManager configManager,
        Action<int, int> applyClaim,
        Func<DavDatabaseContext> dbContextFactory,
        Func<string> nodeIdFactory)
    {
        _configManager = configManager;
        _applyClaim = applyClaim;
        _dbContextFactory = dbContextFactory;
        _nodeId = nodeIdFactory();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(HeartbeatInterval);
        try
        {
            await RebalanceAllOnce(stoppingToken).ConfigureAwait(false);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                await RebalanceAllOnce(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normal shutdown
        }
        finally
        {
            await ReleaseClaims(CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async Task RebalanceAllOnce(CancellationToken cancellationToken)
    {
        var providerConfig = _configManager.GetUsenetProviderConfig();
        for (var providerIndex = 0; providerIndex < providerConfig.Providers.Count; providerIndex++)
        {
            var provider = providerConfig.Providers[providerIndex];
            if (provider.Type != ProviderType.Pooled)
                continue;

            await RebalanceProvider(providerIndex, provider.MaxConnections, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RebalanceProvider(int providerIndex, int totalSlots, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                await using var dbContext = _dbContextFactory();
                await using var transaction = await dbContext.Database
                    .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                    .ConfigureAwait(false);

                var cutoff = DateTime.UtcNow.Subtract(ActiveWindow);
                var activeClaims = await dbContext.ConnectionPoolClaims
                    .Where(x => x.ProviderIndex == providerIndex && x.HeartbeatAt > cutoff)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

                var activeNodeIds = activeClaims
                    .Select(x => x.NodeId)
                    .Append(_nodeId)
                    .Distinct(StringComparer.Ordinal)
                    .Count();
                var desiredClaim = Math.Max(1, totalSlots / Math.Max(1, activeNodeIds));

                var myClaim = activeClaims.FirstOrDefault(x => x.NodeId == _nodeId);
                if (myClaim == null)
                {
                    dbContext.ConnectionPoolClaims.Add(new ConnectionPoolClaim
                    {
                        NodeId = _nodeId,
                        ProviderIndex = providerIndex,
                        ClaimedSlots = desiredClaim,
                        HeartbeatAt = DateTime.UtcNow
                    });
                }
                else
                {
                    myClaim.ClaimedSlots = desiredClaim;
                    myClaim.HeartbeatAt = DateTime.UtcNow;
                }

                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                _lastKnownClaims[providerIndex] = desiredClaim;
                _applyClaim(providerIndex, desiredClaim);
                return;
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.SerializationFailure)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var degradedClaim = _lastKnownClaims.TryGetValue(providerIndex, out var previousClaim)
                    ? previousClaim
                    : Math.Max(1, Math.Min(1, totalSlots));
                _applyClaim(providerIndex, degradedClaim);
                Log.Warning(ex,
                    "ConnectionPoolCoordinator falling back to degraded claim {Claim} for provider {ProviderIndex}",
                    degradedClaim,
                    providerIndex);
                return;
            }
        }

        var fallbackClaim = _lastKnownClaims.TryGetValue(providerIndex, out var cachedClaim)
            ? cachedClaim
            : Math.Max(1, Math.Min(1, totalSlots));
        _applyClaim(providerIndex, fallbackClaim);
    }

    private async Task ReleaseClaims(CancellationToken cancellationToken)
    {
        try
        {
            await using var dbContext = _dbContextFactory();
            await dbContext.ConnectionPoolClaims
                .Where(x => x.NodeId == _nodeId)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ConnectionPoolCoordinator failed to release claims for node {NodeId}", _nodeId);
        }
    }
}
