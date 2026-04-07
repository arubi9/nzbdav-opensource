using NzbWebDAV.Clients.Usenet.Connections;

namespace NzbWebDAV.Tests.Clients.Usenet.Connections;

public sealed class ConnectionPoolTests
{
    [Fact]
    public async Task Resize_UpdatesMaxConnections_WhileBorrowedConnectionStaysValid()
    {
        using var pool = new ConnectionPool<IDisposable>(2, _ => new ValueTask<IDisposable>(new MemoryStream()));
        using var connectionLock = await pool.GetConnectionLockAsync(NzbWebDAV.Clients.Usenet.Concurrency.SemaphorePriority.High);

        pool.Resize(1);

        Assert.Equal(1, pool.MaxConnections);
        Assert.Equal(1, pool.LiveConnections);
    }

    [Fact]
    public async Task ResizeUp_WakesQueuedWaiter()
    {
        using var pool = new ConnectionPool<IDisposable>(1, _ => new ValueTask<IDisposable>(new MemoryStream()));
        using var firstLock = await pool.GetConnectionLockAsync(NzbWebDAV.Clients.Usenet.Concurrency.SemaphorePriority.High);
        var secondTask = pool.GetConnectionLockAsync(NzbWebDAV.Clients.Usenet.Concurrency.SemaphorePriority.High);

        await Task.Delay(50);
        Assert.False(secondTask.IsCompleted);

        pool.Resize(2);

        var secondLock = await secondTask.WaitAsync(TimeSpan.FromSeconds(1));
        secondLock.Dispose();
    }
}
