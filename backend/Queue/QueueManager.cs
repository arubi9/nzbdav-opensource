using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Services.NntpLeasing;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Queue;

public class QueueManager : IDisposable
{
    private InProgressQueueItem? _inProgressQueueItem;

    private readonly UsenetStreamingClient _usenetClient;
    private readonly CancellationTokenSource? _cancellationTokenSource;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly ConfigManager _configManager;
    private readonly WebsocketManager _websocketManager;
    private readonly NntpLeaseState _leaseState;
    private readonly bool _usePerNodeLeasing;

    private CancellationTokenSource _sleepingQueueToken = new();
    private readonly Lock _sleepingQueueLock = new();

    public event EventHandler<NzbProcessedEventArgs>? OnNzbProcessed;

    public record NzbProcessedEventArgs(Guid QueueItemId, string JobName, string Category);

    public QueueManager(
        UsenetStreamingClient usenetClient,
        ConfigManager configManager,
        WebsocketManager websocketManager,
        NntpLeaseState leaseState
    )
    {
        _usenetClient = usenetClient;
        _configManager = configManager;
        _websocketManager = websocketManager;
        _leaseState = leaseState;
        _usePerNodeLeasing = NntpLeaseAgent.ShouldUsePerNodeLeasing(MultiNodeMode.IsEnabled, NodeRoleConfig.Current);
        _cancellationTokenSource = CancellationTokenSource
            .CreateLinkedTokenSource(SigtermUtil.GetCancellationToken());
        if (NodeRoleConfig.RunsIngest)
            _ = ProcessQueueAsync(_cancellationTokenSource.Token);
    }

    public (QueueItem? queueItem, int? progress) GetInProgressQueueItem()
    {
        return (_inProgressQueueItem?.QueueItem, _inProgressQueueItem?.ProgressPercentage);
    }

    public void AwakenQueue(DateTime? dateTime = null)
    {
        TimeSpan? cancelAfter = dateTime.HasValue ? (dateTime.Value - DateTime.Now) : null;
        lock (_sleepingQueueLock)
        {
            if (cancelAfter.HasValue && cancelAfter.Value > TimeSpan.Zero)
                _sleepingQueueToken.CancelAfter(cancelAfter.Value);
            else
                _sleepingQueueToken.Cancel();
        }
    }

    public async Task RemoveQueueItemsAsync
    (
        List<Guid> queueItemIds,
        DavDatabaseClient dbClient,
        CancellationToken ct = default
    )
    {
        await LockAsync(async () =>
        {
            var inProgressId = _inProgressQueueItem?.QueueItem?.Id;
            if (inProgressId is not null && queueItemIds.Contains(inProgressId.Value))
            {
                await _inProgressQueueItem!.CancellationTokenSource.CancelAsync().ConfigureAwait(false);
                await _inProgressQueueItem.ProcessingTask.ConfigureAwait(false);
                _inProgressQueueItem = null;
            }

            await dbClient.RemoveQueueItemsAsync(queueItemIds, ct).ConfigureAwait(false);
            await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        // When per-node leasing is active the NNTP pool starts at zero
        // connections.  The lease agent needs one or two ticks (~10-20s)
        // to write a heartbeat and receive a grant.  If the queue manager
        // tries to process items before that grant arrives, every attempt
        // fails, the provider circuit breaker trips, and the cascading
        // cooldown prevents downloads even after the grant lands.
        // Wait for the first non-zero grant before entering the loop.
        if (_usePerNodeLeasing)
        {
            while (!ct.IsCancellationRequested && _leaseState.GetTotalGrantedSlots() <= 0)
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // get the next queue-item from the database
                await using var dbContext = new DavDatabaseContext();
                var dbClient = new DavDatabaseClient(dbContext);
                var topItem = await LockAsync(() => dbClient.GetTopQueueItem(ct)).ConfigureAwait(false);
                if (topItem.queueItem is null || topItem.queueNzbStream is null)
                {
                    try
                    {
                        // if we're done with the queue, wait a minute before checking again.
                        // or wait until awoken by cancellation of _sleepingQueueToken
                        await Task.Delay(TimeSpan.FromMinutes(1), _sleepingQueueToken.Token).ConfigureAwait(false);
                    }
                    catch when (_sleepingQueueToken.IsCancellationRequested)
                    {
                        lock (_sleepingQueueLock)
                        {
                            if (!_sleepingQueueToken.TryReset())
                            {
                                _sleepingQueueToken.Dispose();
                                _sleepingQueueToken = new CancellationTokenSource();
                            }
                        }
                    }

                    continue;
                }

                // create an article-caching nntp-client.
                // the cache will be scoped only to this single queue-item.
                using var cachingUsenetClient = new ArticleCachingNntpClient(_usenetClient);

                // process the queue-item
                await using var queueNzbStream = topItem.queueNzbStream;
                using var queueItemCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
                await LockAsync(() =>
                {
                    // ReSharper disable twice AccessToDisposedClosure
                    _inProgressQueueItem = BeginProcessingQueueItem(dbClient, cachingUsenetClient,
                        topItem.queueItem, queueNzbStream, queueItemCancellationTokenSource);
                }).ConfigureAwait(false);
                var processedSuccessfully = await (_inProgressQueueItem?.ProcessingTask ?? Task.FromResult(false))
                    .ConfigureAwait(false);

                // Fire event after successful processing
                if (processedSuccessfully)
                {
                    try
                    {
                        OnNzbProcessed?.Invoke(this, new NzbProcessedEventArgs(
                            topItem.queueItem.Id,
                            topItem.queueItem.JobName,
                            topItem.queueItem.Category
                        ));
                    }
                    catch (Exception ex)
                    {
                        Log.Debug($"Error in NzbProcessed event handler: {ex.Message}");
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error($"An unexpected error occured while processing the queue: {e.Message}");
            }
            finally
            {
                await LockAsync(() => { _inProgressQueueItem = null; }).ConfigureAwait(false);
            }
        }
    }

    private InProgressQueueItem BeginProcessingQueueItem
    (
        DavDatabaseClient dbClient,
        INntpClient usenetClient,
        QueueItem queueItem,
        Stream queueNzbStream,
        CancellationTokenSource cts
    )
    {
        var progressHook = new Progress<int>();
        var task = new QueueItemProcessor(
            queueItem, queueNzbStream, dbClient, usenetClient,
            _configManager, _websocketManager, _leaseState, _usePerNodeLeasing, progressHook, cts.Token
        ).ProcessAsync();
        var inProgressQueueItem = new InProgressQueueItem()
        {
            QueueItem = queueItem,
            ProcessingTask = task,
            ProgressPercentage = 0,
            CancellationTokenSource = cts
        };
        var debounce = DebounceUtil.CreateDebounce(TimeSpan.FromMilliseconds(200));
        progressHook.ProgressChanged += (_, progress) =>
        {
            inProgressQueueItem.ProgressPercentage = progress;
            var message = $"{queueItem.Id}|{progress}";
            if (progress is 100 or 200) _websocketManager.SendMessage(WebsocketTopic.QueueItemProgress, message);
            else debounce(() => _websocketManager.SendMessage(WebsocketTopic.QueueItemProgress, message));
        };
        return inProgressQueueItem;
    }

    private async Task LockAsync(Func<Task> actionAsync)
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            await actionAsync().ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task<T> LockAsync<T>(Func<Task<T>> actionAsync)
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            return await actionAsync().ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task LockAsync(Action action)
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            action();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public void Dispose()
    {
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
    }

    private class InProgressQueueItem
    {
        public QueueItem QueueItem { get; init; }
        public int ProgressPercentage { get; set; }
        public Task<bool> ProcessingTask { get; init; }
        public CancellationTokenSource CancellationTokenSource { get; init; }
    }
}
