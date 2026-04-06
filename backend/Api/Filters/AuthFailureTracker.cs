using System.Collections.Concurrent;

namespace NzbWebDAV.Api.Filters;

/// <summary>
/// In-memory per-IP tracker for failed auth attempts.
/// Only counts FAILED attempts — successful requests are not tracked.
/// Blocks an IP after 10 failures within 60 seconds.
/// </summary>
public sealed class AuthFailureTracker
{
    private const int MaxFailures = 10;
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, FailureRecord> _failures = new();

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
        _failures.AddOrUpdate(
            ipAddress,
            _ => new FailureRecord(1, DateTime.UtcNow),
            (_, existing) => existing.IsExpired
                ? new FailureRecord(1, DateTime.UtcNow)
                : existing with { Count = existing.Count + 1 }
        );
    }

    private sealed record FailureRecord(int Count, DateTime WindowStart)
    {
        public bool IsExpired => DateTime.UtcNow - WindowStart > Window;
    }
}
