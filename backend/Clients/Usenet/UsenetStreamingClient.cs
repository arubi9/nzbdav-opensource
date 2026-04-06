using NzbWebDAV.Clients.Usenet.Caching;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Config;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Clients.Usenet;

public class UsenetStreamingClient : WrappingNntpClient
{
    private readonly record struct PipelineResult(LiveSegmentCachingNntpClient Client, ConnectionPoolStats Stats);
    private volatile ConnectionPoolStats? _poolStats;

    public ConnectionPoolStats? PoolStats => _poolStats;

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

        configManager.OnConfigChanged += (_, configEventArgs) =>
        {
            if (!configEventArgs.ChangedConfig.ContainsKey("usenet.providers")) return;

            var nextPipeline = CreatePipeline(configManager, websocketManager, liveSegmentCache);
            ReplaceUnderlyingClient(nextPipeline.Client);
            _poolStats = nextPipeline.Stats;
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
        return new PipelineResult(cachingClient, multiProviderResult.Stats);
    }

    private static (MultiProviderNntpClient Client, ConnectionPoolStats Stats) CreateMultiProviderClient
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
        return (new MultiProviderNntpClient(providerClients), connectionPoolStats);
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
