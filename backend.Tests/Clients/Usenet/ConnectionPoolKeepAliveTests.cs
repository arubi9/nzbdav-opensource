using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Connections;
using Xunit;

namespace NzbWebDAV.Tests.Clients.Usenet;

/// <summary>
/// The idle sweeper's warm-floor rescue used to re-stamp an expired
/// connection's timestamp without touching the socket, so connections the
/// server had already dropped stayed in the pool forever and playback after
/// a long idle period burned its retries on corpses ("Invalid NNTP
/// Response" bursts). With a keep-alive probe configured, rescue must
/// validate the connection and dispose it when the probe fails.
/// </summary>
public sealed class ConnectionPoolKeepAliveTests
{
    private sealed class FakeConnection : IDisposable
    {
        public bool Alive = true;
        public bool Disposed;
        public int Probes;

        public void Dispose() => Disposed = true;
    }

    private static ConnectionPool<FakeConnection> CreatePool(bool withKeepAlive)
    {
        // 10-minute idle timeout: the background sweeper never fires during
        // the test; expiry is forced via ExpireIdleConnectionsForTest().
        return new ConnectionPool<FakeConnection>(
            maxConnections: 4,
            connectionFactory: _ => ValueTask.FromResult(new FakeConnection()),
            idleTimeout: TimeSpan.FromMinutes(10),
            keepAlive: withKeepAlive
                ? (conn, _) =>
                {
                    conn.Probes++;
                    return ValueTask.FromResult(conn.Alive);
                }
                : null);
    }

    /// <summary>
    /// Leases must be held concurrently before returning: a sequential
    /// lease/return pair would just pop the same connection back off the
    /// idle stack.
    /// </summary>
    private static async Task<FakeConnection[]> CreateIdleConnectionsAsync(
        ConnectionPool<FakeConnection> pool, int count)
    {
        var locks = new List<ConnectionLock<FakeConnection>>();
        for (var i = 0; i < count; i++)
            locks.Add(await pool.GetConnectionLockAsync(SemaphorePriority.High, CancellationToken.None));
        var connections = locks.Select(l => l.Connection).ToArray();
        foreach (var l in locks)
            l.Dispose(); // returns to the idle stack
        return connections;
    }

    [Fact]
    public async Task RescueDisposesDeadConnectionsInsteadOfReStampingThem()
    {
        await using var pool = CreatePool(withKeepAlive: true);
        pool.MinIdleConnections = 2;

        var connections = await CreateIdleConnectionsAsync(pool, 2);
        var dead = connections[0];
        var alive = connections[1];
        dead.Alive = false;

        pool.ExpireIdleConnectionsForTest();
        await pool.SweepOnceAsync();

        Assert.True(dead.Disposed);
        Assert.False(alive.Disposed);
        Assert.True(alive.Probes >= 1);
        Assert.Equal(1, pool.IdleConnections);
        Assert.Equal(1, pool.LiveConnections);
    }

    [Fact]
    public async Task RescuedLiveConnectionIsProbedEverySweepSoTheServerIdleTimerResets()
    {
        await using var pool = CreatePool(withKeepAlive: true);
        pool.MinIdleConnections = 1;

        var conn = (await CreateIdleConnectionsAsync(pool, 1))[0];

        pool.ExpireIdleConnectionsForTest();
        await pool.SweepOnceAsync();
        pool.ExpireIdleConnectionsForTest();
        await pool.SweepOnceAsync();

        Assert.Equal(2, conn.Probes);
        Assert.False(conn.Disposed);
        Assert.Equal(1, pool.IdleConnections);
    }

    [Fact]
    public async Task WithoutAKeepAliveProbeRescueKeepsTheLegacyReStampBehavior()
    {
        await using var pool = CreatePool(withKeepAlive: false);
        pool.MinIdleConnections = 1;

        var conn = (await CreateIdleConnectionsAsync(pool, 1))[0];

        pool.ExpireIdleConnectionsForTest();
        await pool.SweepOnceAsync();

        Assert.False(conn.Disposed);
        Assert.Equal(1, pool.IdleConnections);
    }

    [Fact]
    public async Task ExpiredConnectionsBeyondTheWarmFloorAreStillDisposedWithoutProbing()
    {
        await using var pool = CreatePool(withKeepAlive: true);
        pool.MinIdleConnections = 1;

        var connections = await CreateIdleConnectionsAsync(pool, 2);

        pool.ExpireIdleConnectionsForTest();
        await pool.SweepOnceAsync();

        // Only one connection is rescued to meet the floor; the other is
        // simply expired and disposed without a probe.
        Assert.Equal(1, pool.IdleConnections);
        Assert.Equal(1, connections.Count(c => c.Disposed));
        Assert.Equal(1, connections.Count(c => c.Probes > 0));
    }
}
