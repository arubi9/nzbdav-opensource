using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Contexts;
using Serilog;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Extensions;
using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet;

/// <summary>
/// This client is only responsible for limiting download operations (BODY/ARTICLE)
/// to the configured number of maximum download connections.
/// </summary>
/// <param name="usenetClient"></param>
public class DownloadingNntpClient : WrappingNntpClient
{
    private readonly ConfigManager _configManager;
    private readonly PrioritizedSemaphore _semaphore;
    private readonly bool _usePerNodeLeasing;
    private volatile int _maxDownloadConnections;
    private volatile int _maxPendingDownloads;
    private int _overloadLogged;
    public int MaxDownloadConnections => _maxDownloadConnections;
    public int PendingDownloadWaiters => _semaphore.PendingCount;

    public DownloadingNntpClient(INntpClient usenetClient, ConfigManager configManager, bool? usePerNodeLeasing = null) : base(usenetClient)
    {
        _usePerNodeLeasing = usePerNodeLeasing
            ?? NzbWebDAV.Services.NntpLeasing.NntpLeaseAgent.ShouldUsePerNodeLeasing(MultiNodeMode.IsEnabled, NodeRoleConfig.Current);
        var maxDownloadConnections = _usePerNodeLeasing ? 0 : configManager.GetMaxDownloadConnections();
        var streamingPriority = configManager.GetStreamingPriority();
        _configManager = configManager;
        _maxDownloadConnections = maxDownloadConnections;
        _maxPendingDownloads = configManager.GetMaxPendingDownloads();
        _semaphore = new PrioritizedSemaphore(maxDownloadConnections, maxDownloadConnections, streamingPriority);
        configManager.OnConfigChanged += OnConfigChanged;
    }

    private void OnConfigChanged(object? sender, ConfigManager.ConfigEventArgs e)
    {
        if (!_usePerNodeLeasing && e.ChangedConfig.ContainsKey("usenet.max-download-connections"))
        {
            var maxDownloadConnections = _configManager.GetMaxDownloadConnections();
            UpdateMaxDownloadConnections(maxDownloadConnections);
            _maxPendingDownloads = _configManager.GetMaxPendingDownloads();
        }

        if (e.ChangedConfig.ContainsKey("usenet.max-pending-downloads"))
        {
            _maxPendingDownloads = _configManager.GetMaxPendingDownloads();
        }

        if (e.ChangedConfig.ContainsKey("usenet.streaming-priority"))
        {
            var streamingPriority = _configManager.GetStreamingPriority();
            _semaphore.UpdatePriorityOdds(streamingPriority);
        }
    }

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(SegmentId segmentId,
        CancellationToken cancellationToken)
    {
        return DecodedBodyAsync(segmentId, onConnectionReadyAgain: null, cancellationToken);
    }

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(SegmentId segmentId,
        CancellationToken cancellationToken)
    {
        return DecodedArticleAsync(segmentId, onConnectionReadyAgain: null, cancellationToken);
    }

    public override async Task<UsenetDecodedBodyResponse> DecodedBodyAsync(SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain, CancellationToken cancellationToken)
    {
        await AcquireExclusiveConnectionAsync(onConnectionReadyAgain, cancellationToken).ConfigureAwait(false);
        return await base.DecodedBodyAsync(segmentId, OnConnectionReadyAgain, cancellationToken).ConfigureAwait(false);

        void OnConnectionReadyAgain(ArticleBodyResult articleBodyResult)
        {
            _semaphore.Release();
            onConnectionReadyAgain?.Invoke(articleBodyResult);
        }
    }

    public override async Task<UsenetDecodedArticleResponse> DecodedArticleAsync(SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain, CancellationToken cancellationToken)
    {
        await AcquireExclusiveConnectionAsync(onConnectionReadyAgain, cancellationToken).ConfigureAwait(false);
        return await base.DecodedArticleAsync(segmentId, OnConnectionReadyAgain, cancellationToken)
            .ConfigureAwait(false);

        void OnConnectionReadyAgain(ArticleBodyResult articleBodyResult)
        {
            _semaphore.Release();
            onConnectionReadyAgain?.Invoke(articleBodyResult);
        }
    }

    private async Task AcquireExclusiveConnectionAsync(Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken)
    {
        try
        {
            await AcquireExclusiveConnectionAsync(cancellationToken);
        }
        catch
        {
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
            throw;
        }
    }

    private Task AcquireExclusiveConnectionAsync(CancellationToken cancellationToken)
    {
        ThrowIfOverloaded();
        var downloadPriorityContext = cancellationToken.GetContext<DownloadPriorityContext>();
        var semaphorePriority = downloadPriorityContext?.Priority ?? SemaphorePriority.High;
        return _semaphore.WaitAsync(semaphorePriority, cancellationToken);
    }

    /// <summary>
    /// Sheds load when the queue behind the connection budget grows without
    /// bound. This is deliberately logged: the exception is invisible to the
    /// caller once a stream's response has started (ExceptionMiddleware can no
    /// longer rewrite the status code), so without this line an overloaded
    /// server truncates playback with no server-side trace at all.
    /// </summary>
    private void ThrowIfOverloaded()
    {
        var maxPending = _maxPendingDownloads;
        var pending = _semaphore.PendingCount;
        if (pending <= maxPending)
        {
            // Reset once the queue drains so the next episode is logged again.
            Interlocked.Exchange(ref _overloadLogged, 0);
            return;
        }

        // Log once per overload episode; a sustained overload would otherwise
        // emit a line per rejected segment.
        if (Interlocked.Exchange(ref _overloadLogged, 1) == 0)
        {
            Log.Warning(
                "Shedding download requests: {Pending} pending exceeds {MaxPending} " +
                "(connections={Connections}). Streams already sending bytes will be truncated.",
                pending, maxPending, _maxDownloadConnections);
        }

        throw new NzbWebDAV.Exceptions.ServiceOverloadedException();
    }

    public void UpdateMaxDownloadConnections(int maxDownloadConnections)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxDownloadConnections);
        _maxDownloadConnections = maxDownloadConnections;
        _semaphore.UpdateMaxAllowed(maxDownloadConnections);
    }

    public override async Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync
    (
        string segmentId,
        CancellationToken cancellationToken
    )
    {
        await AcquireExclusiveConnectionAsync(cancellationToken).ConfigureAwait(false);
        return new UsenetExclusiveConnection(_ => _semaphore.Release());
    }

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(SegmentId segmentId,
        UsenetExclusiveConnection exclusiveConnection, CancellationToken cancellationToken)
    {
        var onConnectionReadyAgain = exclusiveConnection.OnConnectionReadyAgain;
        return base.DecodedBodyAsync(segmentId, onConnectionReadyAgain, cancellationToken);
    }

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(SegmentId segmentId,
        UsenetExclusiveConnection exclusiveConnection, CancellationToken cancellationToken)
    {
        var onConnectionReadyAgain = exclusiveConnection.OnConnectionReadyAgain;
        return base.DecodedArticleAsync(segmentId, onConnectionReadyAgain, cancellationToken);
    }

    public override void Dispose()
    {
        _configManager.OnConfigChanged -= OnConfigChanged;
        base.Dispose();
        GC.SuppressFinalize(this);
    }
}
