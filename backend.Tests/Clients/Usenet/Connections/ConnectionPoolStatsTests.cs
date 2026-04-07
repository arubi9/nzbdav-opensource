using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Config;
using NzbWebDAV.Models;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Tests.Clients.Usenet.Connections;

public sealed class ConnectionPoolStatsTests
{
    [Fact]
    public void OnConnectionPoolChanged_UpdatesMaxPooled_FromRuntimeResize()
    {
        var providerConfig = new UsenetProviderConfig
        {
            Providers =
            [
                new UsenetProviderConfig.ConnectionDetails
                {
                    Type = ProviderType.Pooled,
                    Host = "example.test",
                    Port = 563,
                    UseSsl = true,
                    User = "user",
                    Pass = "pass",
                    MaxConnections = 5
                }
            ]
        };

        var stats = new ConnectionPoolStats(providerConfig, new WebsocketManager());
        var onChanged = stats.GetOnConnectionPoolChanged(0);
        onChanged.Invoke(
            null,
            new ConnectionPoolStats.ConnectionPoolChangedEventArgs(live: 1, idle: 0, max: 2));

        Assert.Equal(2, stats.MaxPooled);
    }
}
