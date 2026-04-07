using Microsoft.Extensions.Hosting;
using NzbWebDAV.Database.Interceptors;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Flushes any pending content-index snapshot writes during graceful shutdown.
///
/// Previously this lived inside <c>app.Lifetime.ApplicationStopping.Register</c>
/// in <c>Program.cs</c> and used <c>.GetAwaiter().GetResult()</c> on the flush
/// task. That sync bridge could hang the shutdown thread on a slow disk,
/// causing <c>HostOptions.ShutdownTimeout</c> to fire and kill in-flight
/// requests ungracefully. Moving it to <see cref="IHostedService.StopAsync"/>
/// lets the host await the flush naturally under its own shutdown budget.
///
/// Registration order matters: this service is added LAST so its
/// <c>StopAsync</c> runs FIRST (the host stops hosted services in reverse
/// registration order). That way the snapshot write happens before the
/// database context or cache services are torn down.
/// </summary>
public sealed class SnapshotFlushOnShutdownService : IHostedService
{
    // Intentional no-op. This service exists only to hook into
    // IHostedService.StopAsync for graceful shutdown — there is no
    // foreground work to start. Static analyzers that flag "hosted
    // service with empty StartAsync" as dead code are wrong in this
    // case. See the class-level comment for the rationale.
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ContentIndexSnapshotInterceptor
                .FlushAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown budget exceeded — the host will log this and move on.
            // We don't re-throw because that would mark the host as errored.
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to flush content-index snapshot during shutdown.");
        }
    }
}
