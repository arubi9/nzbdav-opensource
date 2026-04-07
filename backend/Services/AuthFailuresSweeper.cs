using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Database;
using Serilog;

namespace NzbWebDAV.Services;

public sealed class AuthFailuresSweeper : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RetentionWindow = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        try
        {
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
            var cutoff = DateTime.UtcNow.Subtract(RetentionWindow);

            await using var dbContext = new DavDatabaseContext();
            var deleted = await dbContext.AuthFailures
                .Where(x => x.WindowStart < cutoff)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);

            if (deleted > 0)
                Log.Debug("AuthFailuresSweeper removed {Count} expired rows.", deleted);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Log.Warning(ex, "AuthFailuresSweeper failed");
        }
    }
}
