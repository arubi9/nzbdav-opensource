using System.Collections.Concurrent;

namespace NzbWebDAV.Clients.Usenet.Contexts;

/// <summary>
/// Associates arbitrary context values with a specific <see cref="CancellationToken"/>
/// so deeper call sites (inside the NNTP client) can read them without changing
/// method signatures. Primarily used to flow <c>DownloadPriorityContext</c> from a
/// WebDAV request handler down into the prioritized connection semaphore.
///
/// LIFECYCLE INVARIANT
/// -------------------
/// Entries in the static <see cref="Context"/> dictionary are owned by exactly
/// one <see cref="CancellationTokenContext"/> instance, and each instance is
/// owned by exactly one <c>ContextualCancellationTokenSource</c>, which is
/// owned by exactly one stream (currently only <c>MultiSegmentStream</c>).
///
/// The ASP.NET response pipeline guarantees stream disposal, so the chain
/// holds: stream dispose → CTS dispose → each inner CancellationTokenContext
/// dispose → Context dictionary Remove. No entry can outlive its owning
/// stream if disposal runs.
///
/// This is verified safe as of commit cf4672c-era audit. If you add a new
/// caller that creates a <see cref="ContextualCancellationTokenSource"/>
/// outside of a stream's lifecycle, YOU are responsible for ensuring it is
/// always disposed on all paths, including exceptions. Otherwise entries
/// leak for the lifetime of the process.
/// </summary>
public class CancellationTokenContext : IDisposable
{
    private static readonly ConcurrentDictionary<LookupKey, object?> Context = new();

    private LookupKey _lookupKey;

    private CancellationTokenContext(LookupKey lookupKey)
    {
        _lookupKey = lookupKey;
    }

    public static CancellationTokenContext SetContext<T>(CancellationToken ct, T? value)
    {
        var lookupKey = new LookupKey() { CancellationToken = ct, Type = typeof(T) };
        Context[lookupKey] = value;
        return new CancellationTokenContext(lookupKey);
    }

    public static T? GetContext<T>(CancellationToken ct)
    {
        var lookupKey = new LookupKey() { CancellationToken = ct, Type = typeof(T) };
        return Context.TryGetValue(lookupKey, out var result) && result is T context ? context : default;
    }

    public void Dispose()
    {
        Context.Remove(_lookupKey, out _);
    }

    private record struct LookupKey
    {
        public CancellationToken CancellationToken;
        public Type Type;
    }
}