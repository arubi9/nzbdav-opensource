using Microsoft.Extensions.Hosting;
using Serilog;

namespace NzbWebDAV.Api.Filters;

/// <summary>
/// Periodic sweeper that removes expired entries from
/// <see cref="AuthFailureTracker"/>. Without this, expired entries from IPs
/// that never re-attempt would live in memory forever — a distributed attack
/// could grow the dictionary unbounded.
/// </summary>
public sealed class AuthFailureTrackerSweeper(AuthFailureTracker tracker) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    tracker.Sweep();
                }
                catch (Exception e)
                {
                    Log.Debug("AuthFailureTracker sweep failed: {Error}", e.Message);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }
}
