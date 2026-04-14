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

    public ConnectionPoolStats? PoolStats => _poolStats;
    public bool HasAvailableProvider => _multiProviderClient?.HasAvailableProvider() ?? false;
    public int HealthyProviderCount => _multiProviderClient?.HealthyProviderCount ?? 0;
    public int TotalProviderCount => _multiProviderClient?.TotalProviderCount ?? 0;
    public int PooledProviderCount => _providerClients.Count;

    public UsenetStreamingClient
    (
        ConfigManager configManager,
        WebsocketManager websocketManager,
        LiveSegmentCache liveSegmentCache
    ) : this(
        CreatePipeline(configManager, websocketManager, liveSegmentCache),
        configManager,
        websocketManager,
        liveSegmentCache
    )
    {
    }

    private UsenetStreamingClient
    (
        PipelineResult pipeline,
        ConfigManager configManager,
        WebsocketManager websocketManager,
        LiveSegmentCache liveSegmentCache
    ) : base(pipeline.Client)
    {
        _poolStats = pipeline.Stats;
        _downloadingClient = pipeline.DownloadingClient;
        _multiProviderClient = pipeline.MultiProviderClient;
        _providerClients = pipeline.ProviderClients;

        configManager.OnConfigChanged += (_, configEventArgs) =>
        {
            if (!configEventArgs.ChangedConfig.ContainsKey("usenet.providers")) return;

            var nextPipeline = CreatePipeline(configManager, websocketManager, liveSegmentCache);
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
        LiveSegmentCache liveSegmentCache
    )
    {
        var multiProviderResult = CreateMultiProviderClient(configManager, websocketManager);
        var downloadingClient = new DownloadingNntpClient(multiProviderResult.Client, configManager);
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
        WebsocketManager websocketManager
    )
    {
        var providerConfig = configManager.GetUsenetProviderConfig();
        var connectionPoolStats = new ConnectionPoolStats(providerConfig, websocketManager);
        var providerClients = providerConfig.Providers
            .Select((provider, index) => CreateProviderClient(
                provider,
                connectionPoolStats.GetOnConnectionPoolChanged(index)
            ))
            .ToList();
        return (new MultiProviderNntpClient(providerClients), connectionPoolStats, providerClients);
    }

    private static MultiConnectionNntpClient CreateProviderClient
    (
        UsenetProviderConfig.ConnectionDetails connectionDetails,
        EventHandler<ConnectionPoolStats.ConnectionPoolChangedEventArgs> onConnectionPoolChanged
    )
    {
        var connectionPool = CreateNewConnectionPool(
            maxConnections: connectionDetails.MaxConnections,
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
        var connectionPool = new ConnectionPool<INntpClient>(maxConnections, connectionFactory);
        connectionPool.OnConnectionPoolChanged += onConnectionPoolChanged;
        var args = new ConnectionPoolStats.ConnectionPoolChangedEventArgs(0, 0, maxConnections);
        onConnectionPoolChanged(connectionPool, args);
        return connectionPool;
    }

    public void ResizeProviderPool(int providerIndex, int maxConnections)
    {
        if (providerIndex < 0 || providerIndex >= _providerClients.Count)
            return;

        _providerClients[providerIndex].Resize(maxConnections);
    }

    public int GetProviderPoolMaxConnections(int providerIndex)
    {
        if (providerIndex < 0 || providerIndex >= _providerClients.Count)
            return 0;

        return _providerClients[providerIndex].MaxConnections;
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
