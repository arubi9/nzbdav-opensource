using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using Serilog;

namespace NzbWebDAV.Services;

public sealed class YencHeaderCacheSweeper(ConfigManager configManager) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        try
        {
            await SweepOnce(stoppingToken).ConfigureAwait(false);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                await SweepOnce(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normal shutdown
        }
    }

    public async Task SweepOnce(CancellationToken cancellationToken)
    {
        try
        {
            var retentionDays = configManager.GetMetadataRetentionDays();
            // Use a parameterized UTC cutoff instead of Postgres-specific
            // date arithmetic so this cleanup stays portable across SQLite
            // tests and Postgres production runs.
            var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

            await using var dbContext = new DavDatabaseContext();
            var deleted = await dbContext.Database.ExecuteSqlInterpolatedAsync($@"
                DELETE FROM yenc_header_cache
                WHERE cached_at < {cutoff};
            ", cancellationToken).ConfigureAwait(false);

            if (deleted > 0)
            {
                Log.Debug(
                    "YencHeaderCacheSweeper removed {Count} expired entries (retention {Days} days)",
                    deleted,
                    retentionDays);
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Log.Warning(ex, "YencHeaderCacheSweeper failed");
        }
    }
}
