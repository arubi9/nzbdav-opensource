using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NzbWebDAV.Setup.Core;

namespace NzbWebDAV.Services;

/// <summary>
/// Performs one bounded recovery attempt for durable setup-session cleanup or
/// candidate-session journal state after process startup. It is intentionally
/// not a general setup runner and never issues or re-enables a setup grant.
/// </summary>
public sealed class SetupRevocationRecoveryService : BackgroundService
{
    private static readonly TimeSpan StartupRecoveryTimeout = TimeSpan.FromSeconds(5);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SetupRevocationRecoveryService> _logger;
    private readonly TimeSpan _startupRecoveryTimeout;
    private readonly TaskCompletionSource _executionCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task ExecutionCompleted => _executionCompleted.Task;

    public SetupRevocationRecoveryService(
        IServiceScopeFactory scopeFactory,
        ILogger<SetupRevocationRecoveryService> logger)
        : this(scopeFactory, logger, StartupRecoveryTimeout)
    {
    }

    internal SetupRevocationRecoveryService(
        IServiceScopeFactory scopeFactory,
        ILogger<SetupRevocationRecoveryService> logger,
        TimeSpan startupRecoveryTimeout)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _startupRecoveryTimeout = startupRecoveryTimeout > TimeSpan.Zero
            ? startupRecoveryTimeout
            : throw new ArgumentOutOfRangeException(nameof(startupRecoveryTimeout));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The linked token is the one and only budget for this attempt. Pass it
        // through the recovery service so both upstream logout and phase-3 DB
        // cleanup stop at the same hosted-startup deadline.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(_startupRecoveryTimeout);

        try
        {
            // Let the host finish starting its request pipeline before doing
            // the bounded external cleanup attempt. The timeout starts before
            // this yield so startup work cannot extend the five-second bound.
            await Task.Yield();

            await using var scope = _scopeFactory.CreateAsyncScope();
            var grants = scope.ServiceProvider.GetRequiredService<SetupGrantService>();
            if (await grants.RecoverPendingAsync(timeout.Token).ConfigureAwait(false))
                _logger.LogInformation("Recovered pending setup session cleanup during startup.");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            // The durable pending marker is intentionally retained when the
            // hosted budget expires. Authenticated recovery or a later restart
            // can retry it; this service itself remains bounded.
            _logger.LogWarning("Pending setup session cleanup timed out during startup; cleanup remains pending.");
        }
        catch (Exception exception)
        {
            // A failed attempt deliberately leaves the durable marker in place
            // for the authenticated recovery endpoint or the next restart.
            _logger.LogWarning(exception, "Pending setup session cleanup could not be completed during startup.");
        }
        finally
        {
            _executionCompleted.TrySetResult();
        }
    }
}
