using System.Text.Json;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Caching;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Tests.Clients.Usenet;

public sealed class UsenetStreamingClientTests
{
    [Fact]
    public void PoolStats_IsExposedOnConstruction()
    {
        using var cacheScope = new TempCacheScope();
        var configManager = CreateConfigManager(CreateProviderConfig(maxConnections: 3));
        using var liveCache = new LiveSegmentCache(cacheScope.Path);
        var websocketManager = new WebsocketManager();

        using var client = new UsenetStreamingClient(configManager, websocketManager, liveCache);

        Assert.NotNull(client.PoolStats);
        Assert.Equal(3, client.PoolStats!.MaxPooled);
        Assert.Equal(0, client.PoolStats.TotalLive);
        Assert.Equal(0, client.PoolStats.TotalIdle);
        Assert.Equal(0, client.PoolStats.TotalActive);
    }

    [Fact]
    public void PoolStats_RefreshesWhenProviderConfigChanges()
    {
        using var cacheScope = new TempCacheScope();
        var configManager = CreateConfigManager(CreateProviderConfig(maxConnections: 2));
        using var liveCache = new LiveSegmentCache(cacheScope.Path);
        var websocketManager = new WebsocketManager();

        using var client = new UsenetStreamingClient(configManager, websocketManager, liveCache);
        var initialStats = client.PoolStats;

        configManager.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = "usenet.providers",
                ConfigValue = JsonSerializer.Serialize(CreateProviderConfig(maxConnections: 5))
            }
        ]);

        Assert.NotNull(client.PoolStats);
        Assert.NotSame(initialStats, client.PoolStats);
        Assert.Equal(5, client.PoolStats!.MaxPooled);
    }

    private static ConfigManager CreateConfigManager(UsenetProviderConfig providerConfig)
    {
        var configManager = new ConfigManager();
        configManager.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = "usenet.providers",
                ConfigValue = JsonSerializer.Serialize(providerConfig)
            }
        ]);
        return configManager;
    }

    private static UsenetProviderConfig CreateProviderConfig(int maxConnections)
    {
        return new UsenetProviderConfig
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
                    MaxConnections = maxConnections
                }
            ]
        };
    }

    private sealed class TempCacheScope : IDisposable
    {
        public TempCacheScope()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (!Directory.Exists(Path))
                return;

            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best-effort temp cleanup.
            }
        }
    }
}
