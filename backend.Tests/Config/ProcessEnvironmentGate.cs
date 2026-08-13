namespace backend.Tests.Config;

/// <summary>Serializes only fixtures which mutate process-global environment variables.</summary>
public sealed class ProcessEnvironmentFixture : IAsyncLifetime
{
    private bool _held;

    public async ValueTask InitializeAsync()
    {
        await ProcessEnvironmentGate.Instance.WaitAsync();
        _held = true;
    }

    public ValueTask DisposeAsync()
    {
        if (_held)
        {
            ProcessEnvironmentGate.Instance.Release();
            _held = false;
        }

        return ValueTask.CompletedTask;
    }
}

internal static class ProcessEnvironmentGate
{
    internal static SemaphoreSlim Instance { get; } = new(1, 1);
}
