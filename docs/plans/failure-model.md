# NZBDAV Failure Model

*How the system degrades, recovers, and what operators/users experience in each scenario.*

This document is the authoritative reference for how NZBDAV behaves under failure. Every production-facing component should implement behavior consistent with this model.

---

## 1. NNTP Provider Failure

### Scenario: Single provider goes down

**System behavior:**
- `MultiProviderNntpClient` detects the failure on the first failed request
- The provider enters cooldown (exponential backoff via `ProviderHealth.RegisterFailure`)
- Subsequent requests route to backup providers
- When cooldown expires, the provider is retried automatically

**User experience:** No impact if backup providers are configured. If only one provider exists, all uncached streams stall until cooldown retries succeed or timeout fires.

**Operator signal:** `nzbdav_nntp_connections_active` drops to 0 for the failed provider. Log entry: "Blocking NNTP provider {Type} for {Cooldown}".

### Scenario: ALL providers are down

**System behavior:**
- All providers are in cooldown simultaneously
- New segment fetches fail immediately with connection errors
- `LiveSegmentCache` continues serving cached segments — playback of cached content is unaffected
- Uncached segment requests fail, surfacing as stream errors
- The `PrioritizedSemaphore` queue does NOT fill (requests fail fast, not block)
- Load shedding (503) activates if queue depth exceeds threshold from transient retries
- Health check returns `Unhealthy` → load balancer stops routing new requests to this node

**User experience:**
- Cached content: plays normally
- Uncached content: playback error after timeout (30s metadata, 5min stream)
- Jellyfin shows "Playback error" — user can retry when providers recover

**Operator signal:** Health check `Unhealthy`. All provider cooldowns active in logs. `nzbdav_nntp_connections_active = 0` across all providers.

**Recovery:** Automatic. When any provider's cooldown expires and a connection succeeds, the system resumes normal operation. No manual intervention required.

---

## 2. Restart Mid-Stream

### Scenario: Container killed during active streams

**System behavior:**
1. `ApplicationStopping` fires
2. Health check returns `Unhealthy` immediately → LB stops sending new requests
3. Kestrel's `ShutdownTimeout` (30s) begins connection draining
4. Active streams continue for up to 30s — most short reads complete
5. `ContentIndexSnapshotInterceptor.SnapshotWriter.FlushAsync()` persists pending snapshot
6. After 30s, remaining connections are force-closed
7. `ReadAheadWarmingService` sessions are abandoned (not persisted)
8. `LiveSegmentCache` disk files persist (`.meta` + body files survive)

**On restart:**
1. `LiveSegmentCache.RehydrateFromDisk()` restores cached segments
2. `ContentIndexRecoveryService` restores `/content` tree from snapshot if DB was lost
3. Health check returns `Healthy` → LB resumes routing
4. Warming sessions restart on first stream request (not from scratch — cache is warm)

**User experience:**
- During drain (0-30s): active streams may complete or may buffer briefly
- After kill: Jellyfin shows playback error on any still-active stream
- On resume: Jellyfin's player retries automatically. If behind LB, retry hits a healthy node. If single-node, retry succeeds after restart (~5-10s for .NET startup + cache rehydration)

**Operator signal:** Container restart event. Health check transitions: `Healthy` → `Unhealthy` → (restart) → `Healthy`.

**What is NOT recovered:** Warming session positions. These are ephemeral optimization state — losing them means the first few seconds of resumed playback may not be pre-warmed. This is acceptable.

---

## 3. Cache Fills Up

### Scenario: `_cachedBytes` exceeds `_maxCacheSizeBytes`

**System behavior:**
- Tiered eviction runs on every `GetOrAddBodyAsync` call
- Eviction order: expired entries → video segments (LRU) → unknown (LRU) → small files (LRU)
- Referenced segments (actively being read) are skipped during eviction
- If all segments are referenced (extreme case: more concurrent streams than cache capacity), new segments continue being written beyond the configured limit
- The filesystem becomes the hard boundary — when disk space runs out, `FileStream` writes throw `IOException`

**Degradation path:**
1. `cache_bytes / cache_max_bytes > 0.85` → operator alert threshold
2. `cache_bytes / cache_max_bytes > 1.0` → eviction is behind, but system still functions
3. Filesystem < 1GB free → writes may fail, new streams get errors, cached streams continue
4. Filesystem full → new segment writes fail with `IOException`, caught by `FetchAndStoreBodyAsync`, segment fetch fails, stream gets error

**User experience:**
- Normal eviction: no impact (eviction is fast, sub-millisecond per entry)
- Behind on eviction: slight increase in cache misses for older content, no errors
- Disk full: new uncached streams fail, cached streams continue playing

**Operator signal:** `nzbdav_cache_bytes` gauge rising. `nzbdav_cache_evictions_total` rate increasing. Health check returns `Degraded` when utilization > 90%.

**Remediation:**
1. Increase `cache.max-size-gb` (if disk space available)
2. Reduce `cache.max-age-hours` (evict sooner)
3. Add a streaming node (splits the load, each node's cache handles fewer streams)

**What the system should NOT do:** Force-evict referenced segments. This would corrupt active streams to save disk space, trading a disk problem for a user-facing error. The correct behavior is to let the filesystem be the hard limit and surface errors on new requests, not existing ones.

---

## 4. PostgreSQL Unreachable (Multi-Node)

### Scenario: Database connection fails

**System behavior:**
- EF Core connection pool retries automatically (Npgsql default: 2 retries with backoff)
- WebDAV path resolution queries fail → 500 errors on all directory listings and file lookups
- Streaming of files where the `DavItem` is not yet resolved fails
- The `LiveSegmentCache` continues operating (it's filesystem-only, no DB dependency)
- Cached segment reads succeed, but the mapping from "file path → segment IDs" requires DB
- Queue processing pauses (can't read queue items)
- Config changes are lost (can't write to ConfigItems)

**User experience:** All new navigation and playback attempts fail. Already-playing streams that have their `MultiSegmentStream` fully prefetched may continue briefly.

**Operator signal:** Health check `Unhealthy` (DB connectivity check fails). EF Core logs connection errors.

**Recovery:** Automatic when PostgreSQL comes back. No data loss — the DB is the source of truth, and the snapshot provides disaster recovery for the `/content` tree.

**Mitigation:** PostgreSQL HA (primary + standby with automatic failover). This is an infrastructure concern, not an application concern.

---

## 5. Jellyfin Plugin: NZBDAV Unreachable During Library Sync

### Scenario: `NzbdavLibrarySyncTask` runs but NZBDAV is down

**System behavior:**
- `NzbdavApiClient.BrowseAsync` throws `HttpRequestException`
- Task catches the exception, logs a warning, and completes without modifying the library
- Existing Jellyfin items with `NzbdavId` are preserved — no deletion on sync failure
- Next scheduled sync (15 minutes) retries automatically

**What the sync must guarantee:**
- **Idempotent:** Running the sync twice produces the same result
- **Additive:** Sync only creates items, never deletes them (deletion is manual via Jellyfin UI)
- **Partial-failure tolerant:** If one mount folder fails to process, others continue
- **Stale-aware:** Track last successful sync timestamp so operators know if sync is stuck

**User experience:** No impact on existing library. New content from NZBDAV won't appear until the next successful sync.

---

## 6. Client Disconnects Mid-Stream

### Scenario: User closes browser/app while video is playing

**System behavior:**
- Kestrel detects TCP connection close → `HttpContext.RequestAborted` fires
- `CancellationToken` propagates through `StreamExecutionService` → `NzbFileStream` → `MultiSegmentStream`
- `MultiSegmentStream.DisposeAsync` cancels the download task and drains the channel
- `ReadAheadWarmingService` session is stopped via `DisposableCallbackStream.onDispose`
- `SegmentFetchContext` is disposed in `finally` block
- NNTP connection is returned to pool (not destroyed, unless the download was mid-transfer)
- `nzbdav_streams_active` gauge decrements

**This path is already correct.** The cancellation chain is fully wired. No resource leaks.

---

## Summary: Failure → Signal → User Impact → Recovery

| Failure | Health check | User sees | Recovery |
|---------|-------------|-----------|----------|
| One NNTP provider down | Healthy | Nothing (failover) | Automatic (cooldown expiry) |
| All NNTP providers down | Unhealthy | Playback error (uncached) | Automatic |
| Container restart | Unhealthy → Healthy | Brief playback error | Automatic (5-10s) |
| Cache 85% full | Degraded | Nothing | Operator increases size |
| Cache 100% + disk full | Degraded | New streams fail | Operator adds disk/node |
| PostgreSQL down | Unhealthy | All navigation/playback fails | Automatic when DB returns |
| NZBDAV down during sync | N/A (Jellyfin-side) | New content delayed | Automatic (next sync) |
| Client disconnect | Healthy | Nothing (expected) | N/A |

---

## Operational Alerts

| Alert | Condition | Severity | Action |
|-------|-----------|----------|--------|
| Cache filling | `cache_bytes / cache_max_bytes > 0.85` | Warning | Increase cache size or add node |
| NNTP pool saturated | `nntp_active / nntp_max > 0.9` for 5m | Warning | Add connections or node |
| All providers down | Health check `Unhealthy` | Critical | Check provider status |
| High error rate | `rate(http_5xx) > 0.05` for 5m | Critical | Check logs |
| Sync stale | Last successful sync > 1h ago | Warning | Check NZBDAV availability |
| Stream startup slow | `p99(stream_first_byte) > 10s` | Warning | Check cache hit rate |
