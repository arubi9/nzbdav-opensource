using NzbWebDAV.Services;

namespace backend.Tests.Services;

public sealed class DebouncedSnapshotWriterTests
{
    [Fact]
    public async Task MarkDirtyCoalescesRapidTriggersIntoSingleWrite()
    {
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeCount = 0;

        using var writer = new DebouncedSnapshotWriter(
            debounceInterval: TimeSpan.FromMilliseconds(50),
            writeSnapshotAsync: async _ =>
            {
                Interlocked.Increment(ref writeCount);
                writeStarted.TrySetResult();
                await releaseWrite.Task.ConfigureAwait(false);
            }
        );

        for (var i = 0; i < 10; i++)
            writer.MarkDirty();

        await writeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        releaseWrite.TrySetResult();
        await writer.FlushAsync(CancellationToken.None);

        Assert.Equal(1, Volatile.Read(ref writeCount));
    }

    [Fact]
    public async Task FlushAsyncWritesPendingSnapshotImmediately()
    {
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeCount = 0;

        using var writer = new DebouncedSnapshotWriter(
            debounceInterval: TimeSpan.FromMinutes(1),
            writeSnapshotAsync: _ =>
            {
                Interlocked.Increment(ref writeCount);
                writeStarted.TrySetResult();
                return Task.CompletedTask;
            }
        );

        writer.MarkDirty();
        await writer.FlushAsync(CancellationToken.None);

        await writeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, Volatile.Read(ref writeCount));
    }
}
