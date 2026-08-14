namespace NzbWebDAV.Services;

/// <summary>
/// Identifies the current state of the /content tree as the manifest reports it.
///
/// A paged manifest cannot hash its own bytes to produce a whole-tree ETag without
/// re-reading every page, so the version is maintained at the points where the tree
/// actually changes instead: any /content database mutation
/// (<c>ContentIndexSnapshotInterceptor</c>) and any probe sidecar write
/// (<c>MediaProbeService</c>). Probe writes must bump it because the manifest reports
/// <c>HasProbeData</c> from disk, so a probe can appear with no database change at all.
///
/// The counter lives in memory and restarts at zero. That is safe but not sufficient
/// on its own: a client holding a version from a previous process could match a reused
/// number and skip a sync it needed. The instance id makes that impossible, at the cost
/// of one redundant resync per restart.
/// </summary>
public static class ManifestVersion
{
    private static long _current;

    /// <summary>Distinguishes counter values from different processes.</summary>
    public static Guid InstanceId { get; } = Guid.NewGuid();

    public static long Current => Interlocked.Read(ref _current);

    public static void Bump() => Interlocked.Increment(ref _current);

    /// <summary>An ETag-safe token that changes whenever the manifest would.</summary>
    public static string Token => $"{InstanceId:N}-{Current}";
}
