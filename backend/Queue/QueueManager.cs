using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<Guid, InProgressQueueItem> _inProgressItems = new();

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
        var first = _inProgressItems.Values.FirstOrDefault();
        return (first?.QueueItem, first?.ProgressPercentage);
    }

    public IReadOnlyList<(QueueItem queueItem, int progress)> GetInProgressQueueItems()
    {
        return _inProgressItems.Values
            .Select(x => (x.QueueItem, x.ProgressPercentage))
            .ToList();
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
        // Hold the lock across cancel + await + DB delete so that no worker
        // can re-pick a cancelled item before it has been removed from the DB.
        await LockAsync(async () =>
        {
            // Snapshot matching in-progress items under the lock
            var matchingInProgress = queueItemIds
                .Select(id => _inProgressItems.TryGetValue(id, out var item) ? item : null)
                .Where(item => item is not null)
                .Select(item => item!)
                .ToList();

            // Cancel all matching items first so their processor loops terminate
            foreach (var inProgress in matchingInProgress)
                await inProgress.CancellationTokenSource.CancelAsync().ConfigureAwait(false);

            // Wait for each cancelled processor to return and proactively
            // evict from the dict. The worker's finally block will no-op
            // TryRemove after this.
            foreach (var inProgress in matchingInProgress)
            {
                try
                {
                    await inProgress.ProcessingTask.ConfigureAwait(false);
                }
                catch
                {
                    // Worker loop already logs; swallow here.
                }
                _inProgressItems.TryRemove(inProgress.QueueItem.Id, out _);
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

        var parallelism = Math.Max(1, _configManager.GetQueueParallelism());
        if (parallelism > 1)
            Log.Information($"Queue processing with parallelism={parallelism}");

        var workers = Enumerable.Range(0, parallelism)
            .Select(workerId => ProcessWorkerAsync(workerId, ct))
            .ToArray();
        await Task.WhenAll(workers).ConfigureAwait(false);
    }

    private async Task ProcessWorkerAsync(int workerId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Declared at loop scope so the outer finally always disposes them,
            // even if LockAsync throws after partially populating them.
            InProgressQueueItem? inProgress = null;
            ArticleCachingNntpClient? cachingUsenetClient = null;
            Stream? queueNzbStream = null;
            CancellationTokenSource? itemCts = null;
            bool shouldSleep = false;
            bool processedSuccessfully = false;

            try
            {
                await using var dbContext = new DavDatabaseContext();
                var dbClient = new DavDatabaseClient(dbContext);

                // Critical section: snapshot in-progress IDs, query next
                // available item, and register it as in-progress atomically
                // so two workers cannot pick the same item.
                var captured = await LockAsync<CapturedResources>(async () =>
                {
                    var excludedIds = _inProgressItems.Keys.ToList();
                    var topItem = await dbClient.GetTopQueueItem(excludedIds, ct).ConfigureAwait(false);
                    if (topItem.queueItem is null || topItem.queueNzbStream is null)
                    {
                        topItem.queueNzbStream?.Dispose();
                        return new CapturedResources();
                    }

                    // the cache is scoped only to this single queue-item.
                    var caching = new ArticleCachingNntpClient(_usenetClient);
                    var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

                    var item = BeginProcessingQueueItem(dbClient, caching,
                        topItem.queueItem, topItem.queueNzbStream, cts);

                    // Lock + excluded-IDs filter guarantees no other worker has
                    // this same item registered. Indexer set is safe here.
                    _inProgressItems[topItem.queueItem.Id] = item;
                    return new CapturedResources
                    {
                        InProgress = item,
                        Caching = caching,
                        NzbStream = topItem.queueNzbStream,
                        Cts = cts,
                    };
                }).ConfigureAwait(false);

                inProgress = captured.InProgress;
                cachingUsenetClient = captured.Caching;
                queueNzbStream = captured.NzbStream;
                itemCts = captured.Cts;

                if (inProgress is null)
                {
                    shouldSleep = true;
                }
                else
                {
                    // Process outside the lock so other workers can pick items concurrently.
                    processedSuccessfully = await inProgress.ProcessingTask.ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                Log.Error($"An unexpected error occured while processing the queue (worker {workerId}): {e.Message}");

                // Back off briefly so a persistent failure does not spin the loop.
                try { await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { /* shutting down */ }
            }
            finally
            {
                // Unregister and dispose resources regardless of success/failure.
                if (inProgress is not null)
                    _inProgressItems.TryRemove(inProgress.QueueItem.Id, out _);

                cachingUsenetClient?.Dispose();
                if (queueNzbStream is not null)
                {
                    try { await queueNzbStream.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception ex) { Log.Debug($"queueNzbStream dispose error: {ex.Message}"); }
                }
                itemCts?.Dispose();
            }

            if (processedSuccessfully && inProgress is not null)
            {
                try
                {
                    OnNzbProcessed?.Invoke(this, new NzbProcessedEventArgs(
                        inProgress.QueueItem.Id,
                        inProgress.QueueItem.JobName,
                        inProgress.QueueItem.Category
                    ));
                }
                catch (Exception ex)
                {
                    Log.Debug($"Error in NzbProcessed event handler: {ex.Message}");
                }
            }

            if (shouldSleep)
                await SleepUntilAwokenAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task SleepUntilAwokenAsync(CancellationToken ct)
    {
        // Capture the current sleep token reference so the catch filter
        // matches against the same token that was passed to Task.Delay,
        // even if another worker replaces _sleepingQueueToken concurrently.
        CancellationTokenSource sleepCts;
        lock (_sleepingQueueLock)
            sleepCts = _sleepingQueueToken;

        try
        {
            // if we're done with the queue, wait a minute before checking again
            // or wait until awoken by cancellation of _sleepingQueueToken.
            await Task.Delay(TimeSpan.FromMinutes(1), sleepCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (sleepCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Reset the shared token so the next idle period can sleep again.
            // Only the first waking worker succeeds; others find it already reset.
            lock (_sleepingQueueLock)
            {
                if (ReferenceEquals(_sleepingQueueToken, sleepCts) && _sleepingQueueToken.IsCancellationRequested)
                {
                    if (!_sleepingQueueToken.TryReset())
                    {
                        _sleepingQueueToken.Dispose();
                        _sleepingQueueToken = new CancellationTokenSource();
                    }
                }
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
        public QueueItem QueueItem { get; init; } = null!;
        public int ProgressPercentage { get; set; }
        public Task<bool> ProcessingTask { get; init; } = null!;
        public CancellationTokenSource CancellationTokenSource { get; init; } = null!;
    }

    private sealed class CapturedResources
    {
        public InProgressQueueItem? InProgress { get; init; }
        public ArticleCachingNntpClient? Caching { get; init; }
        public Stream? NzbStream { get; init; }
        public CancellationTokenSource? Cts { get; init; }
    }
}
