using System.Collections.Concurrent;
using Serilog;

namespace NzbWebDAV.Api.Filters;

public interface IAuthFailureTracker
{
    Task<bool> IsBlockedAsync(string ipAddress);
    Task RecordFailureAsync(string ipAddress);
}

/// <summary>
/// In-memory per-IP tracker for failed auth attempts.
/// Only counts FAILED attempts — successful requests are not tracked.
/// Blocks an IP after 10 failures within 60 seconds.
///
/// Memory safety: the dictionary is hard-capped at <see cref="MaxTrackedIps"/>
/// entries and swept periodically by <see cref="AuthFailureTrackerSweeper"/>.
/// A distributed attack spraying single failures from millions of IPs can
/// grow the dictionary, but never past the cap — beyond that, new entries
/// are dropped silently (the attacker already can't achieve blocking at
/// single-failure rates, so there's nothing to track).
/// </summary>
public sealed class AuthFailureTracker : IAuthFailureTracker
{
    private const int MaxFailures = 10;
    private const int MaxTrackedIps = 100_000;
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, FailureRecord> _failures = new();

    public int TrackedIpCount => _failures.Count;

    public bool IsBlocked(string ipAddress)
    {
        if (!_failures.TryGetValue(ipAddress, out var record))
            return false;

        // Clean expired entries on read
        if (record.IsExpired)
        {
            _failures.TryRemove(ipAddress, out _);
            return false;
        }

        return record.Count >= MaxFailures;
    }

    public void RecordFailure(string ipAddress)
    {
        // Fast path: IP is already tracked → just bump the counter (or reset
        // if its window expired). This does NOT grow the dictionary.
        if (_failures.TryGetValue(ipAddress, out _))
        {
            _failures.AddOrUpdate(
                ipAddress,
                _ => new FailureRecord(1, DateTime.UtcNow),
                (_, existing) => existing.IsExpired
                    ? new FailureRecord(1, DateTime.UtcNow)
                    : existing with { Count = existing.Count + 1 }
            );
            return;
        }

        // Slow path: new IP. Respect the hard cap. If we're at the cap, try
        // an inline sweep to free expired entries before giving up.
        if (_failures.Count >= MaxTrackedIps)
        {
            Sweep();
            if (_failures.Count >= MaxTrackedIps)
            {
                // Dictionary is full of live entries — drop this record. A
                // distributed single-failure flood that saturates the cap
                // cannot reach the 10-failure block threshold anyway, so
                // there's nothing to protect by tracking them.
                return;
            }
        }

        _failures.TryAdd(ipAddress, new FailureRecord(1, DateTime.UtcNow));
    }

    /// <summary>
    /// Remove all expired entries. Called periodically by
    /// <see cref="AuthFailureTrackerSweeper"/> and opportunistically on a
    /// cap-full <see cref="RecordFailure"/>.
    /// </summary>
    public int Sweep()
    {
        var removed = 0;
        foreach (var kvp in _failures)
        {
            if (kvp.Value.IsExpired && _failures.TryRemove(kvp.Key, out _))
                removed++;
        }

        if (removed > 0)
            Log.Debug("AuthFailureTracker swept {Count} expired entries ({Remaining} remaining)",
                removed, _failures.Count);

        return removed;
    }

    public Task<bool> IsBlockedAsync(string ipAddress)
    {
        return Task.FromResult(IsBlocked(ipAddress));
    }

    public Task RecordFailureAsync(string ipAddress)
    {
        RecordFailure(ipAddress);
        return Task.CompletedTask;
    }

    private sealed record FailureRecord(int Count, DateTime WindowStart)
    {
        public bool IsExpired => DateTime.UtcNow - WindowStart > Window;
    }
}
