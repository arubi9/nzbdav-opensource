# L2 object-storage writer stall — design

## Problem

`ObjectStorageSegmentCache` writer loop blocks indefinitely on a single
`PutObjectAsync` call when the remote S3 endpoint's TCP socket goes
half-open. No per-request timeout is configured on the Minio client, so
a stalled PUT never completes and never throws — the single-threaded
writer stops draining the queue.

## Evidence (Server B, 2026-04-17 run)

- `nzbdav_l2_cache_writes_total = 12` — frozen for ~55 min of uptime.
- `nzbdav_l2_cache_write_failures_total = 0`.
- `nzbdav_l2_cache_writes_dropped_total = 0`.
- `L2 first-segment warm complete: warmed=752 already-present=0` —
  confirming 752 `EnqueueWrite` calls during startup warm alone.
- `nzbdav_l2_cache_misses_total = 2760` — each miss path enqueues an
  L2 write, so total enqueues during uptime ≥ 2760 + 752.
- Successful `curl -I` to `s3.us-east-va.io.cloud.ovh.us` from inside
  the container proves the endpoint is reachable *now*, i.e. the stall
  is a pre-existing socket, not a global outage.

Counter froze, no failures, no drops, endpoint reachable → the writer
task is parked inside a single `await PutObjectAsync` whose TCP read
will never return. The queue depth is growing silently up to the
2048 cap (at which point future enqueues would finally increment
`writes_dropped_total`).

## Root cause

`ObjectStorageSegmentCache.CreateWriteDelegate` calls
`client.PutObjectAsync(args, ct)` with only the shutdown cancellation
token. The Minio client was constructed without `.WithTimeout(...)` and
without a custom `HttpClient` timeout. Under a half-open socket the
`HttpClient` `SendAsync` never fires a timeout, so the task awaits
forever. Writer loop is single-threaded; one stuck request parks all
future writes.

## Fix

1. **Bound every S3 request with a timeout.**  Wrap `PutObjectAsync`
   and `GetObjectAsync` calls in a linked CTS:
   ```csharp
   using var timeoutCts = new CancellationTokenSource(_requestTimeout);
   using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
       ct, timeoutCts.Token);
   await client.PutObjectAsync(args, linkedCts.Token).ConfigureAwait(false);
   ```
   Default timeout: 30 s (read-path), 60 s (write-path — L2 segments
   are ~750 KB, 60 s is >10× nominal). Expose as config keys
   `cache.l2.read-timeout-seconds` and `cache.l2.write-timeout-seconds`.

2. **Expose queue depth as a metric** so a stall is visible in
   Grafana within one scrape interval:
   `nzbdav_l2_cache_queue_depth` (gauge, reflects `_queueCount`).

3. **Expose last-successful-write timestamp:**
   `nzbdav_l2_cache_last_write_unixtime` (gauge) —
   `time() - nzbdav_l2_cache_last_write_unixtime > 120` becomes an
   alertable predicate.

4. **Writer health guard (defense-in-depth):** log a `Log.Warning`
   when a single write exceeds `_requestTimeout / 2` so we capture the
   segment id + bucket that stalled before the cancellation fires.

## Non-goals

- Multi-threaded writer — unnecessary complexity once timeouts unblock
  the single writer.
- Retrying timed-out writes — dropping is fine (L2 is an opportunistic
  cache; the next miss re-enqueues). Track as `write_failures_total`.

## Verification

- Unit test: inject a `_writeAsync` that never returns, assert the
  writer cancels via timeout and increments `_l2WriteFailures`.
- Unit test: queue depth gauge reflects enqueue/dequeue.
- Live: deploy to Server B, observe `writes_total` rising in step
  with `warm complete` + steady-state playback.
