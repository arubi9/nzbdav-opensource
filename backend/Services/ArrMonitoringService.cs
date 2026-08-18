using Microsoft.Extensions.Hosting;
using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Config;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// - This class takes care of monitoring Radarr/Sonarr instances
///   for stuck queue items which usually require manual intervention.
/// - NzbDAV can be configured to automatically remove these stuck items,
///   optionally block these stuck items, and optionally trigger a new
///   search for these stuck items.
/// </summary>
public class ArrMonitoringService : BackgroundService
{
    private readonly ConfigManager _configManager;

    public ArrMonitoringService(ConfigManager configManager)
    {
        _configManager = configManager;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Ensure delay runs on each iteration
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);

            // if all queue-actions are disabled, then do nothing
            var arrConfig = _configManager.GetArrConfig();
            if (arrConfig.QueueRules.All(x => x.Action == ArrConfig.QueueAction.DoNothing))
                continue;

            // otherwise, handle stuck queue items according to the config
            foreach (var arrClient in arrConfig.GetArrClients())
            {
                stoppingToken.ThrowIfCancellationRequested();
                await HandleStuckQueueItems(arrConfig, arrClient, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleStuckQueueItems(ArrConfig arrConfig, ArrClient client, CancellationToken cancellationToken)
    {
        try
        {
            var queueStatus = await client.GetQueueStatusAsync(cancellationToken).ConfigureAwait(false);
            if (queueStatus is { Warnings: false, UnknownWarnings: false }) return;
            var queue = await client.GetQueueAsync(cancellationToken).ConfigureAwait(false);
            var actionableStatuses = arrConfig.QueueRules.Select(x => x.Message);
            var stuckRecords = queue.Records.Where(x => actionableStatuses.Any(x.HasStatusMessage));
            foreach (var record in stuckRecords)
                await HandleStuckQueueItem(record, arrConfig, client, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            Log.Error($"Error occured while monitoring queue for `{client.Host}`: {e.Message}");
        }
    }

    private async Task HandleStuckQueueItem(ArrQueueRecord item, ArrConfig arrConfig, ArrClient client, CancellationToken cancellationToken)
    {
        // since there may be multiple status messages, multiple actions may apply.
        // in such case, always perform the strongest action.
        var action = arrConfig.QueueRules
            .Where(x => item.HasStatusMessage(x.Message))
            .Select(x => x.Action)
            .DefaultIfEmpty(ArrConfig.QueueAction.DoNothing)
            .Max();

        if (action is ArrConfig.QueueAction.DoNothing) return;
        await client.DeleteQueueRecord(item.Id, action, cancellationToken).ConfigureAwait(false);
        Log.Warning($"Resolved stuck queue item `{item.Title}` from `{client.Host}, with action `{action}`");
    }
}