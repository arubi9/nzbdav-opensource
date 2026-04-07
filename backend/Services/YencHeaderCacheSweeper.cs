using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using Serilog;

namespace NzbWebDAV.Services;

public sealed class YencHeaderCacheSweeper : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);
    private readonly ConfigManager _configManager;

    public YencHeaderCacheSweeper(ConfigManager configManager)
    {
        _configManager = configManager;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    public async Task SweepOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var retentionDays = GetMetadataRetentionDays();
            var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

            await using var dbContext = new DavDatabaseContext();
            await dbContext.YencHeaderCache
                .Where(entry => entry.CachedAt < cutoff)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Log.Warning(ex, "YencHeaderCacheSweeper failed");
        }
    }

    private int GetMetadataRetentionDays()
    {
        var value = TryGetConfigValue("cache.metadata-retention-days");
        return int.TryParse(value, out var retentionDays) && retentionDays > 0
            ? retentionDays
            : 90;
    }

    private string? TryGetConfigValue(string configName)
    {
        var method = typeof(ConfigManager).GetMethod(
            "GetConfigValue",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(string)],
            modifiers: null);

        return method?.Invoke(_configManager, [configName]) as string;
    }
}
