using NzbWebDAV.Database;
using Serilog;

namespace NzbWebDAV.Services;

public sealed class DebouncedSnapshotWriter : IDisposable
{
    private readonly Func<CancellationToken, Task> _writeSnapshotAsync;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _triggerLock = new();
    private CancellationTokenSource? _debounceCts;
    private long _debounceIntervalTicks;
    private int _pendingWrites;
    private bool _disposed;

    public DebouncedSnapshotWriter(
        TimeSpan? debounceInterval = null,
        Func<CancellationToken, Task>? writeSnapshotAsync = null
    )
    {
        _debounceIntervalTicks = (debounceInterval ?? TimeSpan.FromSeconds(5)).Ticks;
        _writeSnapshotAsync = writeSnapshotAsync ?? WriteSnapshotWithFreshContextAsync;
    }

    public void SetDebounceInterval(TimeSpan debounceInterval)
    {
        if (debounceInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(debounceInterval));
        Interlocked.Exchange(ref _debounceIntervalTicks, debounceInterval.Ticks);
    }

    public void MarkDirty()
    {
        ThrowIfDisposed();

        lock (_triggerLock)
        {
            _pendingWrites = 1;
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = new CancellationTokenSource();
            var debounceToken = _debounceCts.Token;
            _ = Task.Run(() => DebouncedWriteAsync(debounceToken), CancellationToken.None);
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (Volatile.Read(ref _pendingWrites) == 0) return;

        lock (_triggerLock)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = null;
        }

        await WritePendingAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DebouncedWriteAsync(CancellationToken debounceToken)
    {
        try
        {
            var debounceInterval = TimeSpan.FromTicks(Interlocked.Read(ref _debounceIntervalTicks));
            await Task.Delay(debounceInterval, debounceToken).ConfigureAwait(false);
            await WritePendingAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer mark-dirty or flush.
        }
        catch (ObjectDisposedException)
        {
            // Writer was disposed while the timer task was still unwinding.
        }
    }

    private async Task WritePendingAsync(CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Interlocked.Exchange(ref _pendingWrites, 0) == 0) return;
            await _writeSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Interlocked.Exchange(ref _pendingWrites, 1);
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to write debounced content index snapshot.");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_triggerLock)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = null;
        }

        GC.SuppressFinalize(this);
    }

    private static async Task WriteSnapshotWithFreshContextAsync(CancellationToken cancellationToken)
    {
        await using var dbContext = new DavDatabaseContext();
        await ContentIndexSnapshotStore.WriteAsync(dbContext, cancellationToken).ConfigureAwait(false);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
