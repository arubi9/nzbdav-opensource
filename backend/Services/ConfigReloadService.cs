using Microsoft.Extensions.Hosting;
using NzbWebDAV.Config;
using Serilog;

namespace NzbWebDAV.Services;

public sealed class ConfigReloadService(ConfigManager configManager) : BackgroundService
{
    private static readonly TimeSpan ReloadInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(ReloadInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await configManager.LoadConfig().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Config reload failed; will retry next cycle.");
            }
        }
    }
}
