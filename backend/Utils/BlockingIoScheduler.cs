using System.Threading.Tasks;

namespace NzbWebDAV.Utils;

/// <summary>
/// A bounded <see cref="TaskScheduler"/> used exclusively to run sync-over-
/// async stream reads that can't be implemented as genuinely sync (they need
/// to wait on NNTP fetches or cached-segment loads).
///
/// WHY THIS EXISTS
/// ---------------
/// Several stream types — <c>DavMultipartFileStream</c>, <c>MultipartFileStream</c>,
/// <c>CancellableStream</c>, <c>AesDecoderStream</c> — have to implement the
/// sync <see cref="System.IO.Stream.Read(byte[], int, int)"/> method because
/// some WebDAV clients (older rclone, Windows WebClient, some third-party
/// archive libraries) call it directly instead of <c>ReadAsync</c>. The
/// underlying I/O is fundamentally async (an NNTP round-trip), so the sync
/// method has to bridge it.
///
/// The naive bridge is <c>ReadAsync(...).GetAwaiter().GetResult()</c>. This
/// blocks a thread-pool thread for the full duration of the NNTP fetch
/// (~100-500 ms). At ~1000 max thread-pool workers, ~200 concurrent sync
/// readers can drain the pool, which then starves ALL other requests —
/// including async ones — because they can't get a worker to continue on.
///
/// This scheduler isolates blocking I/O from the default thread-pool. Sync
/// reads are dispatched onto <see cref="Scheduler"/> via
/// <c>Task.Factory.StartNew</c>. The scheduler's concurrency is capped at
/// <see cref="MaxConcurrencyLevel"/>, so when the N+1th sync reader shows up
/// it queues on the scheduler — not on the default thread-pool — and async
/// callers continue to flow unaffected.
///
/// This is the same pattern used by Grpc.AspNetCore for sync unary handlers
/// and by SQL Server ADO.NET for blocking protocol reads.
/// </summary>
public static class BlockingIoScheduler
{
    /// <summary>
    /// Maximum number of simultaneously-blocked sync-read operations. Beyond
    /// this many concurrent sync readers, additional readers queue on the
    /// scheduler's internal work list. Tune upward if you expect heavy
    /// concurrent sync-WebDAV-client load; tune downward if the default
    /// thread-pool size is constrained.
    /// </summary>
    public const int MaxConcurrencyLevel = 32;

    private static readonly ConcurrentExclusiveSchedulerPair _pair =
        new ConcurrentExclusiveSchedulerPair(
            TaskScheduler.Default,
            maxConcurrencyLevel: MaxConcurrencyLevel);

    /// <summary>
    /// The bounded scheduler. Use with <c>Task.Factory.StartNew</c>.
    /// </summary>
    public static TaskScheduler Scheduler => _pair.ConcurrentScheduler;

    /// <summary>
    /// Run an async read operation under the bounded blocking-I/O scheduler
    /// and block for its result. Use this from sync <c>Stream.Read</c>
    /// overrides that bridge to an async implementation.
    ///
    /// The caller's thread-pool thread IS still blocked — that's unavoidable
    /// for a sync method — but the async continuations run on the bounded
    /// scheduler, so they don't recursively steal more thread-pool workers.
    /// </summary>
    public static T RunBlocking<T>(Func<Task<T>> asyncWork)
    {
        return Task.Factory.StartNew(
            asyncWork,
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            Scheduler
        ).Unwrap().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Overload for <see cref="ValueTask{TResult}"/>-producing async work.
    /// </summary>
    public static T RunBlocking<T>(Func<ValueTask<T>> asyncWork)
    {
        return Task.Factory.StartNew(
            () => asyncWork().AsTask(),
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            Scheduler
        ).Unwrap().GetAwaiter().GetResult();
    }
}
