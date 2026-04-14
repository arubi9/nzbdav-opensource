using System.Text.Json;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Queue;
using NzbWebDAV.Services.NntpLeasing;

namespace backend.Tests.Queue;

public sealed class BackgroundNntpConcurrencyTests
{
    [Fact]
    public void GetEffectiveConcurrency_ForQueueWork_UsesFreshLeaseGrants_WhenPerNodeLeasingIsEnabled()
    {
        var configManager = CreateConfigManager(
            (ProviderType.Pooled, 10),
            (ProviderType.BackupOnly, 8),
            (ProviderType.Pooled, 6));
        var leaseState = new NntpLeaseState();
        var now = new DateTime(2026, 4, 14, 12, 0, 0, DateTimeKind.Utc);
        leaseState.Apply(providerIndex: 0, grantedSlots: 3, epoch: 1, now.AddSeconds(30));
        leaseState.Apply(providerIndex: 2, grantedSlots: 5, epoch: 2, now.AddSeconds(-1));

        var concurrency = BackgroundNntpConcurrency.GetEffectiveConcurrency(
            configManager,
            leaseState,
            usePerNodeLeasing: true,
            legacyFallback: 99,
            utcNow: now);

        Assert.Equal(3, concurrency);
    }

    [Fact]
    public void GetEffectiveConcurrency_ForHealthChecks_FallsBackToLegacyValue_WhenPerNodeLeasingIsDisabled()
    {
        var configManager = CreateConfigManager((ProviderType.Pooled, 12));
        var leaseState = new NntpLeaseState();
        var now = new DateTime(2026, 4, 14, 12, 0, 0, DateTimeKind.Utc);
        leaseState.Apply(providerIndex: 0, grantedSlots: 0, epoch: 1, now.AddSeconds(30));

        var concurrency = BackgroundNntpConcurrency.GetEffectiveConcurrency(
            configManager,
            leaseState,
            usePerNodeLeasing: false,
            legacyFallback: 7,
            utcNow: now);

        Assert.Equal(7, concurrency);
    }

    private static ConfigManager CreateConfigManager(params (ProviderType Type, int MaxConnections)[] providers)
    {
        var configManager = new ConfigManager();
        var providerConfig = new UsenetProviderConfig
        {
            Providers = providers
                .Select((provider, index) => new UsenetProviderConfig.ConnectionDetails
                {
                    Type = provider.Type,
                    Host = $"news-{index}.example.test",
                    Port = 563,
                    UseSsl = true,
                    User = "user",
                    Pass = "pass",
                    MaxConnections = provider.MaxConnections
                })
                .ToList()
        };

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
}
