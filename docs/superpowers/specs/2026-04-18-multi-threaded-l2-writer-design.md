# Multi-threaded L2 object-storage writer — design

## Problem

`ObjectStorageSegmentCache.WriterLoopAsync` runs a single writer task that
sequentially awaits `PutObjectAsync` on the S3 backend. Observed in
production at 5 active streams: queue filled to the 2048 cap, 731 writes
dropped over a 5-second window. At 100+ concurrent streams the queue
will overflow continuously and the L2 cache will fail to grow — defeating
the cache layer's purpose.

Evidence from live Server B (2026-04-17 session):
- `nzbdav_l2_cache_queue_depth = 2048` (pinned at cap)
- `nzbdav_l2_cache_writes_total` growth rate: ~36 writes/sec
- `nzbdav_l2_cache_writes_dropped_total` growth rate: ~95 writes/sec
- `nzbdav_l2_cache_write_failures_total = 0` (timeout fix holding, but single writer can't drain fast enough)
- Each successful `PutObjectAsync` to OVH S3 takes ~500 ms wall time

## Root cause

`_writerTask = startWriter ? Task.Run(WriterLoopAsync) : Task.CompletedTask;`

One task. One in-flight PUT at a time. At 500 ms per PUT → theoretical
max 2 writes/sec per writer. We observed 36/sec only because short writes
complete faster than long ones. At high concurrency the queue cannot drain.

## Solution

Spawn N concurrent writer workers that dequeue from the same
`_writeQueue` and call `_writeAsync` independently. Each worker respects
the existing per-request timeout and counts successes/failures into the
shared counters.

```csharp
private const int DefaultWriterParallelism = 4;

private readonly List<Task> _writerTasks;

public ObjectStorageSegmentCache(
    string bucketName,
    int queueCapacity,
    Func<...> writeAsync,
    int writerParallelism = DefaultWriterParallelism,
    ...)
{
    // ...
    _writerTasks = startWriter
        ? Enumerable.Range(0, writerParallelism)
            .Select(_ => Task.Run(WriterLoopAsync))
            .ToList()
        : new List<Task> { Task.CompletedTask };
}
```

`WriterLoopAsync` body unchanged — each worker independently waits on
`_queueSignal`, dequeues one request, calls `_writeAsync`, increments
counters. `ConcurrentQueue<WriteRequest>` and `SemaphoreSlim` already
thread-safe for N consumers.

`Dispose()` waits for all workers:

```csharp
Task.WaitAll(_writerTasks.ToArray(), TimeSpan.FromSeconds(10));
```

## Config

Add `cache.l2.writer-parallelism` (default 4) to `ConfigManager`. Values
1-32 accepted. 1 preserves legacy single-threaded behaviour for
regression safety.

## Per-worker metric labelling

Optional: tag `nzbdav_l2_cache_writes_total{worker="0"}` etc. to see load
distribution. Low priority — aggregate is the important signal.

## Backpressure still matters

Multi-threaded writer delays but does not eliminate queue overflow. If
incoming enqueue rate > total writer throughput for long enough, queue
will still fill.

Measured per-worker throughput: ~2 writes/sec steady-state (bounded by
OVH S3 PUT latency). With 4 workers: ~8 writes/sec. At 100 streams
with 30% L1 miss + 50% L2 promotion = ~50 L2 writes/sec incoming. Still
underwater.

**Mitigations to pair with this fix:**
1. Queue capacity bump to 16384 (separate spec)
2. Move L2 to Cloudflare R2 (faster PUTs, ~100ms vs OVH S3's 500ms)
3. Writer parallelism up to 8-16 once R2 in place

With R2 + 8 workers: ~80 writes/sec throughput. Covers 100-stream target.

## Non-goals

- Per-segment deduplication at enqueue time (adds lookup cost; orphan writes are cheap)
- Retry-with-backoff on transient failures (keep existing drop-on-fail semantics)
- Writer priority queues (all writes equal for now)

## Verification

- **Unit test:** construct cache with `writerParallelism = 4`, enqueue 1000
  writes backed by a delegate that sleeps 100 ms. Assert all 1000 complete
  within ~25 s (single-threaded would take ~100 s).
- **Unit test:** `writerParallelism = 1` preserves legacy behaviour —
  existing tests unchanged.
- **Live:** deploy to Server B, observe `queue_depth` drops to near 0 at
  current stream levels; `writes_dropped_total` stops climbing.
- **Stress test:** synthetic 100-concurrent-cold-range generator;
  confirm writer pool drains inside 60s of test end.

## Metrics to add

- `nzbdav_l2_cache_writer_parallelism` (gauge, config value)
- `nzbdav_l2_cache_write_duration_seconds` histogram (p50/p95/p99 per
  worker to detect backend latency regression)
