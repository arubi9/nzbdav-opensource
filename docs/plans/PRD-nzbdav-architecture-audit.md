# NZBDAV: Product Requirements Document & Architecture Audit

*Principal architecture review — April 2026*

---

## 1. System Overview

### What NZBDAV Is

NZBDAV is a WebDAV server that virtualizes NZB documents as a browsable, streamable filesystem without downloading content to local storage. It acts as a transparent proxy between media management tools (Radarr/Sonarr/Jellyfin/Plex) and NNTP (Usenet) providers, enabling "infinite library" media setups where content is streamed on-demand from Usenet.

### What It Is Trying to Achieve

1. **Zero-storage media library** — mount thousands of movies/shows without using disk space
2. **Instant-feeling playback** — sub-2-second time-to-first-byte for video streams
3. **Drop-in SABnzbd replacement** — Radarr/Sonarr think they're talking to SABnzbd
4. **Self-healing content** — automatic health checks and repair when NNTP articles expire
5. **Scale to hundreds of concurrent streams** — multi-user Jellyfin/Plex installations

### Target Audience

- Self-hosted media enthusiasts running Radarr/Sonarr/Jellyfin/Plex stacks
- Small-to-medium media sharing communities (10-500 users)
- Infrastructure operators who want to minimize storage costs

---

## 2. Architecture Map

### Process Topology (Current)

```
                    Internet
                       │
         ┌─────────────┼──────────────┐
         │             │              │
    ┌────▼────┐  ┌─────▼─────┐  ┌────▼────┐
    │ Radarr/ │  │ Jellyfin/ │  │  NZBDAV │
    │ Sonarr  │  │   Plex    │  │ Frontend│
    └────┬────┘  └─────┬─────┘  │ (React) │
         │             │        └────┬────┘
         │        ┌────▼────┐       │
         │        │  rclone │       │
         │        │  (FUSE) │       │
         │        └────┬────┘       │
         │             │            │
    ┌────▼─────────────▼────────────▼────┐
    │          NZBDAV Backend            │
    │  ┌──────────────────────────────┐  │
    │  │ Kestrel HTTP Server          │  │
    │  │  ├── /api     (SABnzbd API)  │  │
    │  │  ├── /ws      (WebSocket)    │  │
    │  │  ├── /health  (healthcheck)  │  │
    │  │  ├── PROPFIND (NWebDav)      │  │
    │  │  └── GET/HEAD (streaming)    │  │
    │  └──────────────────────────────┘  │
    │  ┌──────────────────────────────┐  │
    │  │ Background Services          │  │
    │  │  ├── QueueManager            │  │
    │  │  ├── HealthCheckService      │  │
    │  │  ├── ArrMonitoringService    │  │
    │  │  ├── BlobCleanupService      │  │
    │  │  ├── SmallFilePrecacheService│  │
    │  │  └── ContentIndexRecovery    │  │
    │  └──────────────────────────────┘  │
    │  ┌──────────────────────────────┐  │
    │  │ Streaming Pipeline           │  │
    │  │  LiveSegmentCachingNntpClient│  │
    │  │  → DownloadingNntpClient     │  │
    │  │    → MultiConnectionNntpClient│ │
    │  │      → MultiProviderNntpClient│ │
    │  │        → ConnectionPool<T>   │  │
    │  │          → BaseNntpClient    │  │
    │  └──────────────────────────────┘  │
    │  ┌──────────────────────────────┐  │
    │  │ Storage                      │  │
    │  │  ├── SQLite (db.sqlite)      │  │
    │  │  ├── LiveSegmentCache (NVMe) │  │
    │  │  └── Content snapshot (JSON) │  │
    │  └──────────────────────────────┘  │
    └────────────────┬───────────────────┘
                     │
            ┌────────▼────────┐
            │ NNTP Providers  │
            │ (Usenet servers)│
            └─────────────────┘
```

### Request Lifecycle: Video Stream

```
1. Jellyfin → rclone FUSE read → HTTP GET /content/movies/Movie/movie.mkv
2. Kestrel receives → ExceptionMiddleware → WebDAV Basic Auth
3. NWebDav routes GET → GetAndHeadHandlerPatch
4. DatabaseStore.GetItemAsync → SQLite query (ParentId + Name walk)
5. DatabaseStoreNzbFile.GetStreamAsync:
   a. Set SegmentFetchContext (category + owner)
   b. Query DavNzbFile for segment IDs
   c. Create NzbFileStream(segmentIds, fileSize, streamingBufferSettings)
   d. Start ReadAheadWarmingService session
   e. Wrap in DisposableCallbackStream for cleanup
6. GetAndHeadHandlerPatch:
   a. Parse Range header
   b. Set Content-Length, Content-Range, Accept-Ranges
   c. CopyRangeToPooledAsync → stream.Seek() + stream.ReadAsync()
7. NzbFileStream.ReadAsync → MultiSegmentStream.ReadAsync:
   a. Channel<Task<Stream>> prefetch buffer
   b. SemaphoreSlim-gated download window (ramp from 2 to max)
   c. DownloadSegment → DecodedBodyWithFallbackAsync:
      i.  LiveSegmentCache.TryReadBody → disk cache hit? Return immediately
      ii. LiveSegmentCache.GetOrAddBodyAsync → inflight dedup via Lazy<Task>
      iii. DownloadingNntpClient → PrioritizedSemaphore wait (High priority)
      iv. MultiConnectionNntpClient → ConnectionPool.GetConnectionLockAsync
      v.  MultiProviderNntpClient → try primary, failover to backup providers
      vi. BaseNntpClient → TCP/SSL to NNTP server, BODY command, yEnc decode
      vii. Write decoded body to LiveSegmentCache disk + .meta sidecar
8. Response bytes flow: NNTP → LiveSegmentCache → MultiSegmentStream → NzbFileStream → Kestrel → rclone → Jellyfin → user
```

### NNTP Client Decorator Chain

```
UsenetStreamingClient (singleton, config-change aware)
  └── LiveSegmentCachingNntpClient (disk cache intercept + dedup)
        └── DownloadingNntpClient (PrioritizedSemaphore: max concurrent downloads)
              └── MultiConnectionNntpClient (ConnectionPool per provider)
                    └── MultiProviderNntpClient (failover across providers)
                          └── BaseNntpClient (raw TCP/SSL + NNTP protocol)
```

Each layer adds one concern. The decorator chain is clean and each layer is independently testable. This is good architecture.

### Database Schema (13 tables)

| Table | Purpose | Hot path? |
|-------|---------|-----------|
| DavItems | Virtual filesystem tree (parent-child) | Every WebDAV request |
| DavNzbFiles | Segment ID arrays for NZB files | Every stream open |
| DavRarFiles | RAR archive part metadata | Every RAR stream |
| DavMultipartFiles | Multipart file metadata + AES params | Every multipart stream |
| QueueItems | NZB processing queue | Queue loop (background) |
| QueueNzbContents | Raw NZB XML blobs | Queue processing only |
| HistoryItems | Completed/failed job history | API queries |
| ConfigItems | Key-value configuration store | Startup + config changes |
| HealthCheckResults | Per-item health check history | Background service |
| HealthCheckStats | Aggregated health statistics | Dashboard queries |
| Accounts | WebDAV/API authentication | Login only |
| BlobCleanupItems | Deferred blob deletion queue | Background service |

### Background Services (6)

| Service | Purpose | Cycle | Resource impact |
|---------|---------|-------|----------------|
| QueueManager | Process NZB queue (deobfuscation, mounting) | Continuous | High (NNTP connections) |
| HealthCheckService | Verify segment availability, auto-repair | Continuous | Medium (NNTP STAT commands) |
| ArrMonitoringService | Handle stuck Radarr/Sonarr queue items | 10s poll | Low (HTTP to Arr APIs) |
| BlobCleanupService | Delete orphaned NZB blobs from DB | 10s poll | Low (DB deletes) |
| SmallFilePrecacheService | Pre-cache posters/subtitles after NZB processing | Event-driven | Low (NNTP, low priority) |
| ContentIndexRecoveryService | Restore /content tree from snapshot on startup | Once at startup | Medium (DB writes) |

---

## 3. Code Path Analysis

### Strengths

**NNTP client decorator chain** — Clean separation of concerns. Each layer (caching → rate limiting → connection pooling → failover → protocol) is independently replaceable and testable. The `WrappingNntpClient` base makes this ergonomic.

**LiveSegmentCache** — Well-designed disk cache with:
- Inflight request deduplication via `Lazy<Task<CacheEntry>>`
- Reference counting prevents eviction of in-use segments
- Tiered eviction (video → unknown → small files)
- Persistent `.meta` sidecars survive restarts
- Cache-aware seeks avoid re-fetching from NNTP

**PrioritizedSemaphore** — Elegant solution for streaming vs. queue bandwidth sharing. The accumulated-odds algorithm prevents starvation of either priority level.

**Streaming buffer ramp** — `StreamingBufferSettings.LiveDefault` starts with 2 prefetch slots, ramps to max after 2 consumed segments. This avoids wasting NNTP connections on abandoned streams (user navigated away).

**ETag + conditional requests** — `GetAndHeadHandlerPatch` supports `If-None-Match` for cache validation. Rclone and Jellyfin can avoid full re-downloads.

### Weaknesses

**Single-process architecture** — Everything runs in one ASP.NET process: HTTP serving, WebDAV, queue processing, health checks, NNTP connections, disk cache. No way to scale streaming independently of ingestion.

**SQLite single-writer** — The queue processor, health check service, Arr monitor, and blob cleanup service all write to the same SQLite file. Under load, writes serialize behind SQLite's single-writer lock. The WAL mode helps reads but writes still contend.

**No observability** — No metrics endpoint, no structured telemetry, no distributed tracing. The only monitoring is Serilog text logs and WebSocket messages to the frontend dashboard. At scale, you're flying blind.

**Config in DB** — `ConfigManager` loads all config from SQLite on startup and holds it in a `Dictionary<string, string>` in memory. Config changes go through `UpdateValues` → SQLite write → in-memory update → `OnConfigChanged` event. This works but means config is tied to the database, making it harder to externalize (env vars partially supported but not consistently).

**HealthCheckService static state** — `_missingSegmentIds` is a `static HashSet<string>` protected by a `lock`. This is process-global mutable state that leaks across tests and prevents multi-instance deployment. At scale, each instance would have its own incomplete view of missing segments.

**No request-level timeout** — Kestrel's `MaxRequestBodySize` is set but there's no per-request timeout for streaming operations. A slow NNTP provider could hold a connection open indefinitely, tying up a Kestrel connection and a semaphore slot.

**WebSocket authentication** — `WebsocketManager.Authenticate` reads the first message as the API key. This is a custom auth protocol that doesn't integrate with ASP.NET's auth pipeline, making it fragile and hard to extend.

---

## 4. Infrastructure Review

### Current Deployment Model

Single Docker container running:
- .NET 10 backend (Kestrel on port 8080)
- Node.js frontend (React on port 3000, proxied)
- SQLite database at `/config/db.sqlite`
- Segment cache at `/config/stream-cache/`

Alongside:
- Rclone sidecar container (FUSE mount)
- Radarr/Sonarr containers
- Jellyfin/Plex container

All on a single host, communicating via Docker network.

### What's Missing

**No reverse proxy** — Kestrel is exposed directly. No TLS termination, no rate limiting, no request buffering, no connection limits per IP.

**No container health sophistication** — `/health` exists but doesn't check NNTP connectivity, cache health, or queue state. A "healthy" container might have zero working NNTP connections.

**No persistent volume strategy** — Both SQLite and the segment cache are on the container's filesystem. If the container is recreated without proper volume mounts, all data is lost. The `content-index.snapshot.json` recovery mechanism is a band-aid for this.

**No backup strategy** — SQLite database contains the entire virtual filesystem. If lost, all Radarr/Sonarr imports must be re-processed. The snapshot mechanism helps but isn't a proper backup.

**No resource limits** — No memory limits on the cache, no connection limits per client, no rate limiting on the API. A single misbehaving client can exhaust all NNTP connections.

---

## 5. Scaling & Performance Review

### Current Scaling Ceiling

| Resource | Limit | Bottleneck at |
|----------|-------|--------------|
| NNTP connections | 30-50 per provider account | ~15-25 concurrent streams (2 connections/stream) |
| SQLite writes | ~1000 TPS (WAL mode) | Not a bottleneck for streaming |
| Thread pool | 2x cores min, 50x cores max | ~100 concurrent streams before thread starvation |
| Segment cache disk I/O | NVMe: ~500K IOPS | Not a bottleneck |
| Kestrel connections | Default 5000 | Not a practical bottleneck |
| Memory | ~50MB per active stream buffer | ~200 streams per 10GB RAM |

**The hard ceiling is NNTP connections.** At 30 connections with 80% streaming priority, you get ~24 connections for streaming. With read-ahead warming consuming 1 connection per active stream plus the stream itself consuming 1 during prefetch, you max out at roughly 12-15 simultaneous active streams per provider account.

### Scaling Strategy

**Vertical (current: works to ~50 users):**
- More NNTP connections (multiple provider accounts)
- Larger segment cache (more NVMe)
- More RAM (bigger prefetch buffers)

**Horizontal (required for 50-500 users):**
- Multiple NZBDAV streaming nodes behind a load balancer
- Shared or partitioned segment cache
- PostgreSQL for shared state
- Separate ingest node for queue processing
- NNTP connection budget coordination

---

## 6. Reliability & Security Review

### Reliability Risks

| Risk | Severity | Current mitigation |
|------|----------|-------------------|
| SQLite corruption | High | Content index snapshot + recovery service |
| NNTP provider outage | Medium | MultiProviderNntpClient failover with cooldown |
| Segment cache full | Low | Tiered LRU eviction (video → unknown → small files) |
| Process crash during NZB processing | Medium | Queue item preserved in DB, retried on restart |
| Rclone FUSE mount failure | High | `depends_on: service_healthy` in docker-compose |
| Memory pressure | Medium | ArrayPool-based streaming, no large allocations |
| Orphaned NNTP connections | Low | ConnectionPool idle sweep (30s timeout) |

**Biggest reliability gap:** No graceful degradation under load. If NNTP connections are exhausted, new streams queue behind the PrioritizedSemaphore indefinitely. There's no backpressure signal to the client (HTTP 503), no circuit breaker, no load shedding.

### Security Risks

| Risk | Severity | Status |
|------|----------|--------|
| API key in query string | Medium | SABnzbd compatibility requires `?apikey=` in URL — logged, cached, visible in browser history |
| Basic auth over HTTP | Medium | `AllowInsecureProtocol = true` — credentials sent in cleartext unless behind TLS proxy |
| No CSRF protection | Low | API is stateless, but WebSocket auth uses a custom protocol |
| SQLite injection | None | EF Core parameterizes all queries |
| Path traversal | None | WebDAV paths resolved through `DatabaseStore`, no filesystem access |
| Config secrets in DB | Medium | NNTP passwords, Arr API keys stored in SQLite — no encryption at rest |
| No rate limiting | Medium | A single client can monopolize all NNTP connections |
| WebDAV auth disable via env var | High for production | `DISABLE_WEBDAV_AUTH=true` removes all authentication |

---

## 7. Recommended Target Architecture

### Phase 1: Production-Ready Single Node (Now → 1 month)

```
┌─────────────────────────────────────────┐
│              Reverse Proxy              │
│  (Caddy/Traefik — TLS, rate limiting)  │
└──────────────┬──────────────────────────┘
               │
┌──────────────▼──────────────────────────┐
│           NZBDAV Backend               │
│  ┌─────────────────────────────────┐   │
│  │ HTTP Pipeline                    │   │
│  │  + Request timeout middleware    │   │
│  │  + Prometheus /metrics endpoint  │   │
│  │  + Rich /health (NNTP, cache)   │   │
│  └─────────────────────────────────┘   │
│  ┌─────────────────────────────────┐   │
│  │ SQLite + WAL pragmas            │   │
│  │ LiveSegmentCache (50-200GB NVMe)│   │
│  └─────────────────────────────────┘   │
└─────────────────────────────────────────┘
```

**Work items:**
- Add Prometheus metrics endpoint (cache hit rate, active streams, NNTP pool utilization, queue depth)
- Enrich `/health` to check NNTP connectivity and cache state
- Add request timeout middleware (30s for metadata, 5min for streams)
- Add per-client connection limiting (max 10 concurrent streams per IP)
- Apply SQLite WAL pragmas (`journal_mode=WAL`, `synchronous=NORMAL`, `cache_size=-64000`)
- Document TLS reverse proxy setup (Caddy auto-HTTPS)

### Phase 2: Eliminate Rclone (1-2 months)

```
┌──────────────┐     ┌──────────────┐
│   Jellyfin   │     │   Radarr/    │
│   + Plugin   │     │   Sonarr     │
└──────┬───────┘     └──────┬───────┘
       │ HTTP (direct)      │ SABnzbd API
       │                    │
┌──────▼────────────────────▼────────┐
│         NZBDAV Backend             │
│  + Streaming API (/stream/{id})    │
│  + Metadata API (/meta/{id})       │
└────────────────────────────────────┘
```

**Work items:**
- Build a Jellyfin media provider plugin that:
  - Implements `IMediaSourceProvider` to serve streams via HTTP directly from NZBDAV
  - Implements library scanning by querying NZBDAV's WebDAV PROPFIND
  - Serves posters/subtitles/NFOs from NZBDAV's SmallFilePrecache
  - Eliminates the rclone FUSE mount entirely
- Add a lightweight REST streaming API to NZBDAV:
  - `GET /stream/{davItemId}` — direct binary stream with Range support
  - `GET /meta/{davItemId}` — JSON metadata (file name, size, type, parent)
  - `GET /browse/{path}` — directory listing as JSON
  - Authentication via Bearer token (in addition to existing Basic auth)
- Remove symlink/rclonelink machinery (only needed for rclone FUSE compatibility)

### Phase 3: Horizontal Scaling (2-4 months)

```
                    ┌─────────────────┐
                    │   Load Balancer  │
                    │  (sticky by ID) │
                    └───┬─────────┬───┘
                        │         │
           ┌────────────▼──┐  ┌──▼────────────┐
           │ NZBDAV Node 1 │  │ NZBDAV Node 2 │
           │ (streaming)   │  │ (streaming)    │
           │ Cache: A-M    │  │ Cache: N-Z     │
           └───────┬───────┘  └───────┬────────┘
                   │                  │
           ┌───────▼──────────────────▼────────┐
           │         PostgreSQL                │
           │   (shared state: items, config)   │
           └───────────────────────────────────┘
                   │
           ┌───────▼───────┐
           │ NZBDAV Ingest │
           │ (queue only)  │
           └───────┬───────┘
                   │
           ┌───────▼───────┐
           │ NNTP Providers│
           └───────────────┘
```

**Work items:**
- **PostgreSQL migration:**
  - Switch EF Core provider (`UseSqlite` → `UseNpgsql`)
  - Run `dotnet ef migrations add SwitchToPostgres`
  - Convert `string[]` SegmentIds to PostgreSQL `text[]` array type
  - Replace recursive CTE syntax for PostgreSQL compatibility
  - Move `HealthCheckService._missingSegmentIds` from static HashSet to DB or Redis

- **Node role separation:**
  - `NZBDAV_ROLE=streaming` — serves WebDAV + streaming API, no queue processing
  - `NZBDAV_ROLE=ingest` — runs QueueManager + ArrMonitoring + BlobCleanup, no WebDAV
  - `NZBDAV_ROLE=combined` — current behavior (default)
  - Each role registers only its relevant background services

- **Cache partitioning:**
  - Consistent hashing by `DavItem.Id` → routes requests to the node whose cache partition covers that ID
  - Load balancer uses `X-DavItem-Id` header or path-based routing for affinity
  - Cache miss on a streaming node: fetch from NNTP (don't cross-query other nodes — complexity not worth it)

- **NNTP connection budget:**
  - New `NntpConnectionBudgetService` — coordination service (could be a simple Redis counter)
  - Each streaming node checks out N connections from the global budget on startup
  - When a node goes down, its budget is reclaimed after a timeout
  - Alternative: each node gets its own NNTP provider account (simpler, costs more)

- **Jellyfin horizontal scaling:**
  - Switch Jellyfin to PostgreSQL backend
  - Shared metadata/image cache via NFS or object storage
  - Load balancer with sticky sessions (cookie-based) for active streams
  - Centralized user auth via shared PostgreSQL

### Phase 4: Edge Optimization (4-6 months)

- **CDN for cached content** — popular segments served from edge cache, NZBDAV only fetches from NNTP on first access
- **Predictive warming** — analyze access patterns to pre-warm popular content during off-peak hours
- **Transcoding offload** — dedicated GPU nodes for Jellyfin transcoding, separate from streaming
- **Multi-region** — NZBDAV nodes in different datacenters, each with local NNTP providers and segment cache

---

## 8. Prioritized Issues

### Critical (blocks scaling or causes data loss)

| # | Issue | Impact | Fix |
|---|-------|--------|-----|
| C1 | No TLS termination | Credentials sent in cleartext | Deploy behind Caddy/Traefik with auto-HTTPS |
| C2 | No request timeout | Slow NNTP holds connections forever | Add timeout middleware (configurable) |
| C3 | No load shedding | All connections exhausted = all users stall | Return HTTP 503 when semaphore queue exceeds threshold |

### High (significantly impacts performance or operability)

| # | Issue | Impact | Fix |
|---|-------|--------|-----|
| H1 | No metrics/observability | Can't diagnose production issues | Add Prometheus endpoint with cache/NNTP/stream metrics |
| H2 | Rclone FUSE in data path | Double-caching, FUSE overhead, can't scale | Build Jellyfin plugin (Phase 2) or NFS export (immediate) |
| H3 | SQLite single-writer | Blocks multi-node deployment | PostgreSQL migration (Phase 3) |
| H4 | Static `_missingSegmentIds` | Process-global state, no multi-instance | Move to DB table or Redis set |
| H5 | DownloadingNntpClient default priority still `Low` in worktree copies | Streaming starved by queue | Verify main branch has `High` default (may already be fixed) |

### Medium (impacts user experience or maintainability)

| # | Issue | Impact | Fix |
|---|-------|--------|-----|
| M1 | No per-client rate limiting | One user can monopolize NNTP connections | IP-based connection limit middleware |
| M2 | Config secrets unencrypted in SQLite | Security risk if DB file is accessed | Encrypt sensitive config values at rest |
| M3 | `DavMultipartFileStream.Seek` still sync-disposes | Thread blocking on RAR file seeks | Apply `_seekPending` pattern (same as NzbFileStream) |
| M4 | No structured health check | `/health` returns 200 even with dead NNTP | Check NNTP pool, cache health, DB connectivity |
| M5 | WebSocket custom auth protocol | Fragile, not integrated with ASP.NET auth | Migrate to ASP.NET auth pipeline |
| M6 | Background services poll (10s loops) | Wasted CPU cycles when idle | Switch to event-driven (Channels, DB change notifications) |

### Low (polish, future optimization)

| # | Issue | Impact | Fix |
|---|-------|--------|-----|
| L1 | No graceful shutdown for active streams | Streams cut off on deploy | Drain connections before shutdown |
| L2 | `QueueNzbContents` stores raw NZB XML | Bloats DB, queried only once | Move to filesystem blob store |
| L3 | No API versioning | Breaking changes affect Arr integrations | Version the SABnzbd-compatible API |
| L4 | Frontend and backend in same Docker image | Can't scale independently | Separate containers, CDN for frontend |

---

## 9. Phased Execution Plan

### Phase 1: Harden (2 weeks)

**Goal:** Production-safe single node.

| Week | Work | Files |
|------|------|-------|
| 1 | Prometheus metrics endpoint (cache stats, NNTP pool, active streams) | New: `backend/Api/Controllers/MetricsController.cs` |
| 1 | Rich health check (NNTP connectivity, cache state) | Modify: `backend/Services/HealthCheckService.cs` |
| 1 | Request timeout middleware (30s metadata, 5min streams) | New: `backend/Middlewares/RequestTimeoutMiddleware.cs` |
| 2 | Load shedding (503 when semaphore queue > threshold) | Modify: `backend/Clients/Usenet/DownloadingNntpClient.cs` |
| 2 | Per-IP connection limiting | New: `backend/Middlewares/ConnectionLimitMiddleware.cs` |
| 2 | SQLite WAL pragmas in `DavDatabaseContext` | Modify: `backend/Database/DavDatabaseContext.cs` |
| 2 | TLS reverse proxy documentation (Caddy example) | Modify: `docs/setup-guide.md` |

### Phase 2: Fuse (6 weeks)

**Goal:** Eliminate rclone, direct Jellyfin-to-NZBDAV streaming.

| Week | Work |
|------|------|
| 1-2 | Add REST streaming API to NZBDAV (`/stream/{id}`, `/meta/{id}`, `/browse/{path}`) |
| 2-3 | Build Jellyfin plugin scaffold (media provider, library scanner) |
| 3-4 | Implement Jellyfin `IMediaSourceProvider` backed by NZBDAV REST API |
| 4-5 | Implement library scanning (PROPFIND → Jellyfin item creation) |
| 5-6 | Integration testing, Jellyfin client compatibility matrix |
| 6 | Documentation, plugin distribution |

### Phase 3: Scale (4 weeks)

**Goal:** Multi-node deployment supporting 200+ concurrent streams.

| Week | Work |
|------|------|
| 1 | PostgreSQL migration (EF Core provider swap, migrations, testing) |
| 1-2 | Node role separation (`NZBDAV_ROLE` env var, conditional service registration) |
| 2-3 | Load balancer configuration (consistent hash by file ID, sticky sessions) |
| 3 | NNTP connection budget coordination (Redis counter or config-based) |
| 3-4 | Jellyfin PostgreSQL switch + multi-node Jellyfin deployment |
| 4 | Load testing (100, 200, 500 concurrent streams), capacity planning documentation |

### Phase 4: Optimize (ongoing)

- Predictive cache warming based on Jellyfin "continue watching" data
- CDN integration for frequently-accessed segments
- GPU transcoding pool for Jellyfin web client users
- Multi-region deployment documentation

---

## 10. Success Metrics

| Metric | Target | How to measure |
|--------|--------|---------------|
| Time to first byte (stream) | < 2 seconds (cache hit), < 5 seconds (cold) | Prometheus histogram |
| Cache hit rate | > 80% for repeated content | LiveSegmentCacheStats |
| Concurrent streams (single node) | 50+ without degradation | Load test with k6/vegeta |
| Concurrent streams (3-node cluster) | 200+ without degradation | Load test |
| Stream startup failure rate | < 0.1% | Prometheus counter |
| NNTP connection utilization | < 90% sustained | Prometheus gauge |
| Seek latency (cached) | < 200ms | Prometheus histogram |
| Seek latency (uncached) | < 3 seconds | Prometheus histogram |
| Library scan time (1000 items) | < 30 seconds | Jellyfin logs |
| Recovery from NNTP outage | < 60 seconds (failover) | Health check + alerting |

---

## 11. Technology Stack Decisions

### Keep

- **.NET 10 / Kestrel** — excellent async I/O performance, mature ecosystem, good for high-concurrency streaming
- **NWebDav** — solid WebDAV implementation, custom GET/HEAD handler pattern works well
- **EF Core** — clean ORM with provider-swappable architecture (SQLite → PostgreSQL)
- **Serilog** — good structured logging, but needs metrics complement
- **React frontend** — appropriate for the admin dashboard

### Add

- **Prometheus client** (`prometheus-net.AspNetCore`) — metrics export
- **Caddy or Traefik** — reverse proxy with automatic TLS
- **PostgreSQL** — when scaling beyond single node
- **Redis** — session state, NNTP connection budget, missing segment cache (Phase 3)

### Remove (when Jellyfin plugin replaces rclone)

- **Rclone sidecar** — eliminated by direct HTTP integration
- **Symlink/rclonelink generation** — only needed for FUSE mount compatibility
- **`rclone.mount-dir` config** — replaced by direct API access

### Avoid

- **MongoDB/NoSQL** — data is inherently relational (parent-child tree)
- **gRPC** — Jellyfin and Arr tools expect HTTP/REST
- **Kubernetes** — over-engineering for this workload; Docker Compose or Nomad is sufficient
- **Microservices** — the node-role separation (streaming vs. ingest) is sufficient; further decomposition adds complexity without benefit
