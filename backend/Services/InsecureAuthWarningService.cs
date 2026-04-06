using Microsoft.Extensions.Hosting;
using Serilog;

namespace NzbWebDAV.Services;

public class InsecureAuthWarningService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            Log.Warning("WebDAV authentication is DISABLED. This is a security risk in production.");
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
