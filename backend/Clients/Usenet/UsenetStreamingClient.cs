using NzbWebDAV.Clients.Usenet.Caching;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Config;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Clients.Usenet;

public class UsenetStreamingClient : WrappingNntpClient
{
    private readonly record struct PipelineResult(
        LiveSegmentCachingNntpClient Client,
        DownloadingNntpClient DownloadingClient,
        MultiProviderNntpClient MultiProviderClient,
        ConnectionPoolStats Stats,
        IReadOnlyList<MultiConnectionNntpClient> ProviderClients);
    private volatile ConnectionPoolStats? _poolStats;
    private volatile DownloadingNntpClient? _downloadingClient;
    private volatile MultiProviderNntpClient? _multiProviderClient;
    private volatile IReadOnlyList<MultiConnectionNntpClient> _providerClients = [];
    private readonly bool _usePerNodeLeasing;

    public ConnectionPoolStats? PoolStats => _poolStats;
    public bool HasAvailableProvider => _multiProviderClient?.HasAvailableProvider() ?? false;
    public int HealthyProviderCount => _multiProviderClient?.HealthyProviderCount ?? 0;
    public int TotalProviderCount => _multiProviderClient?.TotalProviderCount ?? 0;
    public int PooledProviderCount => _providerClients.Count;

    public UsenetStreamingClient
    (
        ConfigManager configManager,
        WebsocketManager websocketManager,
        LiveSegmentCache liveSegmentCache,
        bool? usePerNodeLeasing = null
    ) : this(
        CreatePipeline(
            configManager,
            websocketManager,
            liveSegmentCache,
            usePerNodeLeasing
            ?? NzbWebDAV.Services.NntpLeasing.NntpLeaseAgent.ShouldUsePerNodeLeasing(MultiNodeMode.IsEnabled, NodeRoleConfig.Current)),
        configManager,
        websocketManager,
        liveSegmentCache,
        usePerNodeLeasing
        ?? NzbWebDAV.Services.NntpLeasing.NntpLeaseAgent.ShouldUsePerNodeLeasing(MultiNodeMode.IsEnabled, NodeRoleConfig.Current)
    )
    {
    }

    private UsenetStreamingClient
    (
        PipelineResult pipeline,
        ConfigManager configManager,
        WebsocketManager websocketManager,
        LiveSegmentCache liveSegmentCache,
        bool usePerNodeLeasing
    ) : base(pipeline.Client)
    {
        _poolStats = pipeline.Stats;
        _downloadingClient = pipeline.DownloadingClient;
        _multiProviderClient = pipeline.MultiProviderClient;
        _providerClients = pipeline.ProviderClients;
        _usePerNodeLeasing = usePerNodeLeasing;

        configManager.OnConfigChanged += (_, configEventArgs) =>
        {
            if (!configEventArgs.ChangedConfig.ContainsKey("usenet.providers")) return;

            var nextPipeline = CreatePipeline(
                configManager,
                websocketManager,
                liveSegmentCache,
                _usePerNodeLeasing);
            ReplaceUnderlyingClient(nextPipeline.Client);
            _poolStats = nextPipeline.Stats;
            _downloadingClient = nextPipeline.DownloadingClient;
            _multiProviderClient = nextPipeline.MultiProviderClient;
            _providerClients = nextPipeline.ProviderClients;
        };
    }

    private static PipelineResult CreatePipeline
    (
        ConfigManager configManager,
        WebsocketManager websocketManager,
        LiveSegmentCache liveSegmentCache,
        bool usePerNodeLeasing
    )
    {
        var multiProviderResult = CreateMultiProviderClient(configManager, websocketManager, usePerNodeLeasing);
        var downloadingClient = new DownloadingNntpClient(multiProviderResult.Client, configManager, usePerNodeLeasing);
        var cachingClient = new LiveSegmentCachingNntpClient(downloadingClient, liveSegmentCache);
        return new PipelineResult(
            cachingClient,
            downloadingClient,
            multiProviderResult.Client,
            multiProviderResult.Stats,
            multiProviderResult.ProviderClients);
    }

    private static (MultiProviderNntpClient Client, ConnectionPoolStats Stats, IReadOnlyList<MultiConnectionNntpClient> ProviderClients) CreateMultiProviderClient
    (
        ConfigManager configManager,
        WebsocketManager websocketManager,
        bool usePerNodeLeasing
    )
    {
        var providerConfig = configManager.GetUsenetProviderConfig();
        var connectionPoolStats = new ConnectionPoolStats(providerConfig, websocketManager);
        var providerClients = providerConfig.Providers
            .Select((provider, index) => CreateProviderClient(
                provider,
                usePerNodeLeasing,
                connectionPoolStats.GetOnConnectionPoolChanged(index)
            ))
            .ToList();
        return (new MultiProviderNntpClient(providerClients), connectionPoolStats, providerClients);
    }

    private static MultiConnectionNntpClient CreateProviderClient
    (
        UsenetProviderConfig.ConnectionDetails connectionDetails,
        bool usePerNodeLeasing,
        EventHandler<ConnectionPoolStats.ConnectionPoolChangedEventArgs> onConnectionPoolChanged
    )
    {
        var connectionPool = CreateNewConnectionPool(
            maxConnections: usePerNodeLeasing ? 0 : connectionDetails.MaxConnections,
            connectionFactory: ct => CreateNewConnection(connectionDetails, ct),
            onConnectionPoolChanged
        );
        return new MultiConnectionNntpClient(connectionPool, connectionDetails.Type);
    }

    private static ConnectionPool<INntpClient> CreateNewConnectionPool
    (
        int maxConnections,
        Func<CancellationToken, ValueTask<INntpClient>> connectionFactory,
        EventHandler<ConnectionPoolStats.ConnectionPoolChangedEventArgs> onConnectionPoolChanged
    )
    {
        var initialMaxConnections = Math.Max(1, maxConnections);
        var connectionPool = new ConnectionPool<INntpClient>(initialMaxConnections, connectionFactory);
        // Keep warm connections ready for instant playback start.
        // 10 idle connections avoid cold-start latency when a user hits play.
        connectionPool.MinIdleConnections = 10;
        connectionPool.OnConnectionPoolChanged += onConnectionPoolChanged;
        if (maxConnections != initialMaxConnections)
            connectionPool.Resize(maxConnections);
        else
            onConnectionPoolChanged(connectionPool, new ConnectionPoolStats.ConnectionPoolChangedEventArgs(0, 0, maxConnections));
        return connectionPool;
    }

    public void ResizeProviderPool(int providerIndex, int maxConnections)
    {
        if (providerIndex < 0 || providerIndex >= _providerClients.Count)
            return;

        var prev = _providerClients[providerIndex].MaxConnections;
        _providerClients[providerIndex].Resize(maxConnections);

        // When the pool grows (e.g. first lease grant), pre-create warm connections
        // so the first stream request doesn't wait for NNTP handshakes.
        if (maxConnections > prev)
            _ = _providerClients[providerIndex].WarmUpAsync();
    }

    public int GetProviderPoolMaxConnections(int providerIndex)
    {
        if (providerIndex < 0 || providerIndex >= _providerClients.Count)
            return 0;

        return _providerClients[providerIndex].MaxConnections;
    }

    public int GetMaxDownloadConnections()
    {
        return _downloadingClient?.MaxDownloadConnections ?? 0;
    }

    public int GetPendingDownloadWaiters()
    {
        return _downloadingClient?.PendingDownloadWaiters ?? 0;
    }

    public void UpdateMaxDownloadConnections(int maxDownloadConnections)
    {
        _downloadingClient?.UpdateMaxDownloadConnections(maxDownloadConnections);
    }

    public static async ValueTask<INntpClient> CreateNewConnection
    (
        UsenetProviderConfig.ConnectionDetails connectionDetails,
        CancellationToken ct
    )
    {
        var connection = new BaseNntpClient();
        var host = connectionDetails.Host;
        var port = connectionDetails.Port;
        var useSsl = connectionDetails.UseSsl;
        var user = connectionDetails.User;
        var pass = connectionDetails.Pass;
        await connection.ConnectAsync(host, port, useSsl, ct).ConfigureAwait(false);
        await connection.AuthenticateAsync(user, pass, ct).ConfigureAwait(false);
        return connection;
    }
}
