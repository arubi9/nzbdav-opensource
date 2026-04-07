# Spec: Multi-Node Hardening (Audit Items 3, 5, 6, 7)

*Draft 2026-04-06 — needs review before implementation plan*

This spec closes the four interlocked gaps that prevent NZBDAV's multi-node
deployment mode from being genuinely production-ready:

| # | Item | Current state | Target state |
|---|---|---|---|
| 5 | PgBouncer deployment | Each node has its own 50-slot EF Core pool; 3 nodes × 50 = 150 potential connections against default Postgres `max_connections=100` | PgBouncer transaction-pool sidecar multiplexes all app connections down to ~25 server-side connections; a second PgBouncer-session pool handles LISTEN/NOTIFY |
| 3 | WebsocketManager outbox | Each node keeps its own in-memory `_authenticatedSockets`; events fired on the ingest node never reach streaming-node UIs | New `WebsocketOutbox` table + LISTEN/NOTIFY fanout with seq-number catch-up on reconnect |
| 6 | Shared rate limiter | `AuthFailureTracker` is per-node; 3 nodes × 10 failures = effective 30-failure limit | New `AuthFailures` table with sliding window + periodic sweep; limit applies globally |
| 7 | NNTP connection budget | Operator manually divides provider's connection limit across nodes via `usenet.max-download-connections`; footgun on scale-out | New `ConnectionPoolClaim` table with heartbeats; nodes self-coordinate their slot allocation |

These four items share one architectural pattern: **shared state via the
existing Postgres instance**. They should land together because each decision
constrains the others — especially PgBouncer pooling mode, which dictates
how items 3/6/7 must interact with the database.

---

## Architecture: Shared State via Postgres

NZBDAV multi-node already has a shared Postgres. We're going to use it for
more than just user data — we're going to use it as the coordination layer
for cross-node state that was previously per-process.

### Why Postgres and not Redis / etcd / Consul

1. **It's already there.** Every multi-node deployment has a Postgres
   instance. Adding another coordination primitive doubles the number of
   things the operator has to manage, back up, and monitor.
2. **The state volumes are tiny.** Outbox rows: ~1 KB × ~100/sec peak.
   Rate-limit rows: ~100 bytes × ~number-of-unique-attacking-IPs. Connection
   claims: ~50 bytes × ~number-of-nodes. None of this remotely stresses
   Postgres.
3. **Postgres has the primitives we need.** LISTEN/NOTIFY for near-realtime
   fanout, transactional claims for connection budgeting, sliding windows
   via timestamp indexes. All native.
4. **SQLite single-node mode is unaffected.** The new code paths are gated
   on `DATABASE_URL` being set (i.e., Postgres mode). Single-container
   hobby installs keep using the existing in-memory paths, zero behavior
   change.

### Why not single-table-per-feature

An alternative is "one Postgres-backed coordination service" with a single
table that multiplexes outbox, rate limits, and claims. Rejected because:
- Each feature has its own access pattern (write-heavy outbox, read-heavy
  rate limiter, claim-heavy connection budget)
- Each has its own indexing and retention needs
- Debugging a unified coordination table is harder than debugging four
  focused tables
- No meaningful code reuse across the features

Four small tables, each owned by one subsystem.

### Decision: `DATABASE_URL` presence gates the new code paths

```csharp
public static class MultiNodeMode
{
    /// <summary>
    /// True when NZBDAV is running in multi-node-capable mode (Postgres
    /// backing). False when running in single-node SQLite mode. Used to
    /// gate the shared-state code paths added by this spec.
    /// </summary>
    public static bool IsEnabled =>
        !string.IsNullOrEmpty(
            EnvironmentUtil.GetEnvironmentVariable("DATABASE_URL"));
}
```

All four features below have a guard at their entry point: if
`MultiNodeMode.IsEnabled == false`, they fall back to the existing
in-memory implementation. This is the "single-node hobby users are
unaffected" guarantee.

---

## Item 5: PgBouncer Sidecar

### Problem

`DavDatabaseContext.cs:43` hardcodes `MaxPoolSize=50` in the Npgsql
connection string. With three nodes (2 streaming + 1 ingest), that's 150
potential app-side connections. Postgres's default `max_connections` is
100. First spike in load hits the limit and Postgres starts refusing
connections — cascading failures.

"Just bump `max_connections`" is the wrong fix. Postgres connections are
expensive (each one gets ~10 MB of backend memory, process-forked), and
NZBDAV's actual concurrent query count is well under 50 per node because
most work is CPU-bound deobfuscation or NNTP-bound streaming. The
connections mostly sit idle.

### Solution

Deploy PgBouncer as a sidecar in **transaction pooling mode**. App-side
connections become cheap (they're just TCP handshakes to PgBouncer on the
same host); PgBouncer multiplexes them down to ~25 stable server-side
connections against Postgres.

### Architecture

```
┌──────────────────────┐   ┌──────────────────────┐   ┌──────────────────────┐
│  nzbdav-stream-1     │   │  nzbdav-stream-2     │   │  nzbdav-ingest       │
│  EF Core pool ≤50    │   │  EF Core pool ≤50    │   │  EF Core pool ≤50    │
└────────┬─────────────┘   └────────┬─────────────┘   └────────┬─────────────┘
         │                          │                          │
         │ transaction-pool         │ transaction-pool         │ transaction-pool
         │ (DATABASE_URL)           │                          │
         │                          │                          │
         └──────────────┬───────────┴──────────────────────────┘
                        │
                 ┌──────▼──────────┐
                 │   PgBouncer     │
                 │   TX pool: 25   │
                 │   SESSION pool: │◄───── LISTEN/NOTIFY only (see Item 3)
                 │         5       │
                 └──────┬──────────┘
                        │ ≤25 connections
                        │
                 ┌──────▼──────────┐
                 │    Postgres 17  │
                 │  max_conn = 100 │
                 └─────────────────┘
```

### Pool mode choice: transaction, not session

| Pool mode | Connection release | Pros | Cons |
|---|---|---|---|
| **Session** | At `CLOSE` | Works with everything — prepared statements, session variables, `LISTEN/NOTIFY`, temp tables | Doesn't actually pool at the multi-app-connection scale we need (same as no PgBouncer) |
| **Transaction** | At `COMMIT/ROLLBACK` | Real multiplexing — 50 app connections → ~3 real connections | Breaks session-scoped features: prepared statements (Npgsql works around this), `LISTEN/NOTIFY` (breaks), temp tables (breaks), session variables (breaks) |
| Statement | After each statement | Most aggressive pooling | Breaks anything that uses a transaction |

**We need transaction pooling** for the scaling benefit on the normal EF Core
path, AND **session pooling** for the WebsocketManager outbox LISTEN/NOTIFY
path. PgBouncer supports running multiple pool modes on different ports.

### PgBouncer config

```ini
# /etc/pgbouncer/pgbouncer.ini

[databases]
nzbdav = host=postgres port=5432 dbname=nzbdav

[pgbouncer]
listen_addr = 0.0.0.0
listen_port = 6432          ; transaction-pool port — for normal EF Core
pool_mode = transaction
max_client_conn = 500
default_pool_size = 25
reserve_pool_size = 5
reserve_pool_timeout = 3
server_lifetime = 3600
server_idle_timeout = 600
log_connections = 0
log_disconnections = 0
log_pooler_errors = 1
```

A **second PgBouncer instance** (or same binary on a different port — the
latter is simpler) for `LISTEN/NOTIFY`:

```ini
# /etc/pgbouncer/pgbouncer-session.ini

[databases]
nzbdav = host=postgres port=5432 dbname=nzbdav

[pgbouncer]
listen_addr = 0.0.0.0
listen_port = 6433          ; session-pool port — for LISTEN/NOTIFY subscribers
pool_mode = session
max_client_conn = 20        ; small — only the WebsocketListener tasks connect here
default_pool_size = 5
server_lifetime = 3600
```

### EF Core compat audit

Transaction pooling requires auditing every EF Core code path for
session-scoped features. Known-compatible:

| Feature | Status | Notes |
|---|---|---|
| `DbContext.SaveChangesAsync` | ✅ Works | Single transaction per call — PgBouncer-safe |
| `IQueryable.ToListAsync` | ✅ Works | Implicit transaction wraps the read |
| `Database.ExecuteSqlRawAsync` | ✅ Works | Single statement in its own transaction |
| `Database.BeginTransactionAsync` | ✅ Works | PgBouncer holds the backend connection for the transaction lifetime |
| Npgsql prepared statements | ✅ Works | Npgsql detects PgBouncer via `Pooling=false;No Reset On Close=true` and auto-disables statement preparation — slight perf cost but correct |
| EF Core change tracking | ✅ Works | Tracking state lives in the DbContext, not the connection |
| Migrations (`dotnet ef database update`) | ⚠️ Run against Postgres directly | Use `DATABASE_URL` without PgBouncer for migration commands |
| `LISTEN/NOTIFY` | ❌ Broken under transaction mode | Requires session pool — see Item 3 |
| Temporary tables | ❌ Broken | None used in NZBDAV |
| Session variables (`SET LOCAL` is OK, `SET` is not) | ❌ Broken | None used in NZBDAV |
| Advisory locks (session-scoped) | ❌ Broken | None used in NZBDAV — Item 7 uses row-level locks instead |

**Required Npgsql connection string changes:**

```
# Before (DavDatabaseContext.cs:43):
Host={host};Port={port};Database={db};Username={u};Password={p};Pooling=true;MinPoolSize=5;MaxPoolSize=50

# After — transaction pool:
Host=pgbouncer;Port=6432;Database=nzbdav;Username=nzbdav;Password=nzbdav;Pooling=true;MinPoolSize=2;MaxPoolSize=50;No Reset On Close=true;Server Compatibility Mode=Redshift

# "No Reset On Close=true" — PgBouncer handles connection reset between
#   transactions; Npgsql shouldn't also try to reset on connection close.
# "Server Compatibility Mode=Redshift" — disables statement preparation
#   (Redshift and PgBouncer have overlapping incompatibility; this setting
#   covers both).
```

And for the session pool (used only by the websocket listener):

```
Host=pgbouncer;Port=6433;Database=nzbdav;Username=nzbdav;Password=nzbdav;Pooling=true;MinPoolSize=1;MaxPoolSize=3
```

### Deployment changes

Add a `pgbouncer` service to `docs/deployment/docker-compose.multi-node.yml`:

```yaml
  pgbouncer:
    image: edoburu/pgbouncer:latest
    restart: unless-stopped
    environment:
      DB_HOST: postgres
      DB_USER: nzbdav
      DB_PASSWORD: nzbdav
      DB_NAME: nzbdav
      POOL_MODE: transaction
      MAX_CLIENT_CONN: 500
      DEFAULT_POOL_SIZE: 25
      RESERVE_POOL_SIZE: 5
      SERVER_RESET_QUERY: ""   # PgBouncer handles this in transaction mode
    depends_on:
      postgres:
        condition: service_healthy
    healthcheck:
      test: ["CMD", "pg_isready", "-h", "localhost", "-p", "5432"]
      interval: 10s
      timeout: 3s
      retries: 3

  pgbouncer-session:
    image: edoburu/pgbouncer:latest
    restart: unless-stopped
    environment:
      DB_HOST: postgres
      DB_USER: nzbdav
      DB_PASSWORD: nzbdav
      DB_NAME: nzbdav
      POOL_MODE: session
      MAX_CLIENT_CONN: 20
      DEFAULT_POOL_SIZE: 5
      LISTEN_PORT: 5432
    depends_on:
      postgres:
        condition: service_healthy
```

Update each nzbdav-* service:

```yaml
    environment:
      DATABASE_URL: Host=pgbouncer;Port=5432;Database=nzbdav;Username=nzbdav;Password=nzbdav;Pooling=true;MinPoolSize=2;MaxPoolSize=50;No Reset On Close=true;Server Compatibility Mode=Redshift
      DATABASE_URL_SESSION: Host=pgbouncer-session;Port=5432;Database=nzbdav;Username=nzbdav;Password=nzbdav;Pooling=true;MinPoolSize=1;MaxPoolSize=3
```

`DATABASE_URL_SESSION` is new — read only by the websocket listener code
in Item 3.

### Migration execution

PgBouncer transaction mode DOES work with EF Core migrations because each
migration is a single transaction. However, some migrations use features
that require session state (advisory locks for concurrency protection).
Recommended practice: run migrations against Postgres DIRECTLY, not through
PgBouncer:

```bash
# In the multi-node compose, during deployment:
docker compose exec nzbdav-ingest sh -c \
  'DATABASE_URL="Host=postgres;Port=5432;..." dotnet NzbWebDAV.dll --db-migration'
```

This is documented in the setup guide.

### Files modified

| File | Change |
|---|---|
| `docs/deployment/docker-compose.multi-node.yml` | Add `pgbouncer` + `pgbouncer-session` services, update every `DATABASE_URL` to point at port 6432 |
| `backend/Database/DavDatabaseContext.cs` | Add `No Reset On Close` + `Server Compatibility Mode=Redshift` to the Npgsql connection string when `DATABASE_URL` points at PgBouncer (detected via `Host=pgbouncer` substring match, or just always set them when `DATABASE_URL` is set) |
| `backend/Utils/EnvironmentUtil.cs` | Add `DATABASE_URL_SESSION` getter |
| `docs/deployment/setup-guide.md` | Add PgBouncer section; update migration instructions to bypass PgBouncer |

---

## Item 3: WebsocketManager Outbox

### Problem

`WebsocketManager._authenticatedSockets` is an in-process dictionary on
each node. When the ingest node fires a WS event (queue progress, NZB
processed), only the web clients connected to the ingest node see it.
The streaming nodes where most UI clients actually live never receive
the event.

### Solution

Outbox table + LISTEN/NOTIFY fanout with sequence-number catch-up on
reconnect. Any node can publish a message; every node receives it
reliably even across transient disconnections.

### Schema

```sql
CREATE TABLE websocket_outbox (
    seq         BIGSERIAL PRIMARY KEY,
    topic       TEXT        NOT NULL,
    payload     JSONB       NOT NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX ix_websocket_outbox_created_at ON websocket_outbox(created_at);
```

**Retention:** A sweeper task on the ingest node runs every 30 seconds and
deletes rows older than 5 minutes. Five minutes is enough slack for any
streaming node to reconnect after the longest realistic transient failure
(Postgres restart, network blip, GC pause) and catch up without missing
events.

### Publish path

`WebsocketManager.SendMessage(topic, payload)` becomes:

```csharp
public async Task SendMessage(WebsocketTopic topic, object payload)
{
    if (!MultiNodeMode.IsEnabled)
    {
        // Single-node SQLite mode: fanout directly to local sockets
        // as today. No schema dependency, no DB round-trip.
        FanoutToLocalSockets(topic, payload);
        return;
    }

    // Multi-node Postgres mode: write to outbox table, then NOTIFY
    // to wake up all listening nodes. The local node will see its
    // own NOTIFY and fan out normally — no double-publish because
    // the listener path is the only one that calls FanoutToLocalSockets.
    await using var dbContext = new DavDatabaseContext();
    dbContext.WebsocketOutbox.Add(new WebsocketOutboxEntry
    {
        Topic = topic.ToString(),
        Payload = JsonSerializer.Serialize(payload),
    });
    await dbContext.SaveChangesAsync();

    // NOTIFY fires outside the transaction commit so all listeners
    // (including our own) see a consistent state.
    await dbContext.Database.ExecuteSqlRawAsync("NOTIFY websocket");
}
```

Note: in multi-node mode, the local-fanout call is REMOVED from the
publish path. The local node receives its own NOTIFY back via the
LISTEN loop and fans out from there. This guarantees every node sees
events in the same order (the outbox seq order).

### Subscribe path

A new `WebsocketOutboxListener` hosted service runs on every node in
multi-node mode. It:

1. On startup: reads `SELECT seq FROM websocket_outbox ORDER BY seq DESC
   LIMIT 1` to establish `_lastSeenSeq`. Or 0 if the table is empty.
2. Opens a dedicated session-mode connection via `DATABASE_URL_SESSION`
   (bypasses PgBouncer transaction pool — see Item 5) and issues
   `LISTEN websocket`.
3. Enters a loop: wait for notification OR 30-second timeout, then run a
   catch-up query:

   ```sql
   SELECT seq, topic, payload
   FROM websocket_outbox
   WHERE seq > @lastSeen
   ORDER BY seq;
   ```

4. For each returned row: fan out to local sockets via
   `WebsocketManager.FanoutToLocalSockets`, then update `_lastSeenSeq`.

The 30-second timeout is important — it covers the "NOTIFY was dropped
because the listener was briefly disconnected" case. Worst-case stale
event = 30 seconds.

### Reliability: why LISTEN/NOTIFY alone isn't enough

`LISTEN/NOTIFY` messages are not persisted. If a streaming node's listener
connection drops for 500ms (GC pause, TCP hiccup, Postgres restart), any
NOTIFY fired during that window is lost. The outbox table + periodic
catch-up poll ensures eventual delivery.

The 30-second polling interval is the upper bound on staleness. Under
normal conditions, LISTEN/NOTIFY delivers events within milliseconds and
the periodic poll is a no-op.

### Schema for the DbSet

```csharp
public class WebsocketOutboxEntry
{
    public long Seq { get; set; }
    public required string Topic { get; set; }
    public required string Payload { get; set; }
    public DateTime CreatedAt { get; set; }
}

// In DavDatabaseContext:
public DbSet<WebsocketOutboxEntry> WebsocketOutbox => Set<WebsocketOutboxEntry>();

// In OnModelCreating:
b.Entity<WebsocketOutboxEntry>(e =>
{
    e.ToTable("websocket_outbox");
    e.HasKey(x => x.Seq);
    e.Property(x => x.Seq).UseIdentityAlwaysColumn();
    e.Property(x => x.Topic).IsRequired();
    e.Property(x => x.Payload).HasColumnType("jsonb");
    e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
    e.HasIndex(x => x.CreatedAt);
});
```

### Sweeper

New `WebsocketOutboxSweeper : BackgroundService` registered on the ingest
node only. Runs every 30 seconds:

```sql
DELETE FROM websocket_outbox WHERE created_at < now() - INTERVAL '5 minutes';
```

Streaming nodes don't sweep because that would be redundant and could race.

### Files

**New:**
- `backend/Database/Models/WebsocketOutboxEntry.cs`
- `backend/Database/Migrations/NNN_AddWebsocketOutboxTable.cs`
- `backend/Services/WebsocketOutboxListener.cs` (hosted service, runs on all nodes)
- `backend/Services/WebsocketOutboxSweeper.cs` (hosted service, ingest only)
- `backend/Config/MultiNodeMode.cs`

**Modified:**
- `backend/Database/DavDatabaseContext.cs` — add DbSet + model config
- `backend/Websocket/WebsocketManager.cs` — `SendMessage` forks on `MultiNodeMode.IsEnabled`; add `FanoutToLocalSockets` helper that the listener can call
- `backend/Program.cs` — register `WebsocketOutboxListener` unconditionally when `MultiNodeMode.IsEnabled`, register `WebsocketOutboxSweeper` only on ingest nodes
- `backend/Utils/EnvironmentUtil.cs` — add `DATABASE_URL_SESSION` getter

---

## Item 6: Shared Rate Limiter

### Problem

`AuthFailureTracker` (backend/Api/Filters/AuthFailureTracker.cs) tracks
per-IP failure counts in an in-process dictionary. With N nodes and
HAProxy routing by URI hash, a single attacker can trivially distribute
failures across nodes to defeat the 10-per-60-seconds limit. Effective
threshold becomes `N × 10`.

### Solution

Move the counter into a Postgres table. All nodes read and write the same
rows, so the threshold applies globally.

### Schema

```sql
CREATE TABLE auth_failures (
    ip_address     TEXT        PRIMARY KEY,
    failure_count  INTEGER     NOT NULL,
    window_start   TIMESTAMPTZ NOT NULL
);

CREATE INDEX ix_auth_failures_window_start ON auth_failures(window_start);
```

### Write path: `RecordFailure(ip)`

```sql
-- Upsert the failure count for this IP. If the window has expired,
-- reset to 1; otherwise increment.
INSERT INTO auth_failures (ip_address, failure_count, window_start)
VALUES (@ip, 1, now())
ON CONFLICT (ip_address) DO UPDATE
SET failure_count = CASE
        WHEN auth_failures.window_start < now() - INTERVAL '60 seconds' THEN 1
        ELSE auth_failures.failure_count + 1
    END,
    window_start = CASE
        WHEN auth_failures.window_start < now() - INTERVAL '60 seconds' THEN now()
        ELSE auth_failures.window_start
    END;
```

One round-trip per failed auth attempt. Successes don't touch the DB.

### Read path: `IsBlocked(ip)`

```sql
SELECT 1 FROM auth_failures
WHERE ip_address = @ip
  AND failure_count >= 10
  AND window_start > now() - INTERVAL '60 seconds';
```

One round-trip per incoming API request. The index on primary key (ip)
makes this ~sub-millisecond.

**Optimization:** the vast majority of requests have no failure record.
Cache the "this IP has no entry" result in-memory with a 10-second TTL
to avoid hammering Postgres:

```csharp
private readonly MemoryCache _negativeCache = new(...);

public async Task<bool> IsBlocked(string ip)
{
    if (_negativeCache.TryGetValue(ip, out _))
        return false;  // Recently confirmed clean, don't re-query.

    var blocked = await QueryPostgres(ip);
    if (!blocked)
        _negativeCache.Set(ip, true, TimeSpan.FromSeconds(10));
    return blocked;
}
```

The 10-second TTL is short enough that a newly-attacking IP is detected
within 10 seconds of its 10th failure. That's fine for rate limiting —
we're not trying to block the exact 10th request, we're trying to
prevent sustained brute force.

### Sweeper

New `AuthFailuresSweeper : BackgroundService` on the ingest node, runs
every 60 seconds:

```sql
DELETE FROM auth_failures WHERE window_start < now() - INTERVAL '5 minutes';
```

5 minutes of retention is generous enough that rows never grow unbounded
even under sustained attack (distributed spray from millions of IPs
maxes out at ~millions of rows × ~100 bytes = ~100 MB, which Postgres
handles fine and the sweeper cleans up every minute).

### Hard cap still applies

The existing in-memory `MaxTrackedIps = 100_000` cap is removed — the
Postgres sweeper handles unbounded growth via time-based cleanup. The DB
index on `ip_address` is the primary growth limiter.

### Fallback to in-memory when DB unreachable

If the rate limiter can't talk to Postgres (network partition, Postgres
crash), fall back to the existing in-memory `AuthFailureTracker` for
that node with a warning log. Under partition, each node falls back to
its own local limiter — which is exactly what the current system does.
No regression.

```csharp
public async Task RecordFailure(string ip)
{
    try
    {
        await RecordFailureInPostgres(ip);
    }
    catch (Exception ex) when (IsTransientDbError(ex))
    {
        Log.Warning(ex, "AuthFailureTracker falling back to in-memory for this request");
        _inMemoryFallback.RecordFailure(ip);
    }
}
```

### Files

**New:**
- `backend/Database/Models/AuthFailureEntry.cs`
- `backend/Database/Migrations/NNN_AddAuthFailuresTable.cs`
- `backend/Api/Filters/PostgresAuthFailureTracker.cs` (new class, wraps the existing one as fallback)
- `backend/Services/AuthFailuresSweeper.cs`

**Modified:**
- `backend/Database/DavDatabaseContext.cs` — add DbSet
- `backend/Api/Filters/ApiKeyAuthFilter.cs` — inject `PostgresAuthFailureTracker` instead of `AuthFailureTracker` directly (DI glue)
- `backend/Program.cs` — register `PostgresAuthFailureTracker` when `MultiNodeMode.IsEnabled`, register the old `AuthFailureTracker` when not; register `AuthFailuresSweeper` on ingest nodes only

---

## Item 7: NNTP Connection Budget Coordination

### Problem

`docs/deployment/load-balancer.md` documents manual connection-budget
math: "If your provider allows 30 connections and you run 2 streaming
nodes, set `usenet.max-download-connections = 15` on each". Adding a
third node requires the operator to update every node's config
manually or they over-subscribe the provider.

### Solution

Claims table with heartbeats. On startup, each node claims a slice of
the provider's connection budget. It renews the claim every 10 seconds.
Claims older than 30 seconds are considered stale and any node can
release them.

### Schema

```sql
CREATE TABLE connection_pool_claims (
    node_id         TEXT        NOT NULL,
    provider_index  INTEGER     NOT NULL,
    claimed_slots   INTEGER     NOT NULL,
    heartbeat_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (node_id, provider_index)
);

CREATE INDEX ix_connection_pool_claims_heartbeat_at ON connection_pool_claims(heartbeat_at);
```

`node_id` is a UUID generated at node startup and persisted for the
lifetime of the process. If the process restarts, it gets a new id and
its old claims get cleaned up by whichever node runs the sweeper next.

### Claim algorithm

On startup (per provider):

```
1. Read the provider's configured total_slots (from the shared ConfigItem
   "usenet.providers" — same config that all nodes already see).
2. Inside a SERIALIZABLE transaction:
   a. SELECT SUM(claimed_slots) FROM connection_pool_claims
        WHERE provider_index = @p
          AND heartbeat_at > now() - INTERVAL '30 seconds';
   b. available = total_slots - sum_claimed
   c. desired = max(1, floor(total_slots / active_node_count))
      where active_node_count = SELECT COUNT(DISTINCT node_id) FROM claims
        WHERE heartbeat_at > now() - INTERVAL '30 seconds' + 1
      (the +1 represents ourselves as a new joiner)
   d. my_slots = min(desired, available)
   e. If my_slots < 1: retry in 5 seconds (another node is shrinking to
      make room)
   f. INSERT INTO connection_pool_claims VALUES (@node_id, @p, @my_slots, now())
3. COMMIT.
4. Use `my_slots` as the effective `max_download_connections` for this
   provider on this node.
```

Every 10 seconds:

```sql
UPDATE connection_pool_claims
SET heartbeat_at = now()
WHERE node_id = @node_id;
```

On shutdown:

```sql
DELETE FROM connection_pool_claims WHERE node_id = @node_id;
```

Every 30 seconds (sweeper, ingest node only):

```sql
DELETE FROM connection_pool_claims
WHERE heartbeat_at < now() - INTERVAL '30 seconds';
```

### Rebalance on node join/leave

When a new node joins, it sees the current claim total and claims the
remainder. If the remainder is zero because existing nodes already
claimed everything, the new node gets `my_slots = 0` and retries.

The "retry" path needs existing nodes to voluntarily shrink. The
cleanest way: every claim renewal also recomputes the fair share:

```csharp
var desired = max(1, total_slots / active_node_count);
if (desired < my_slots) {
    // Another node joined — shrink our claim.
    my_slots = desired;
    ExecuteSql("UPDATE claims SET claimed_slots = @my_slots WHERE node_id = @id");
}
```

This gives "eventually fair" behavior within ~10 seconds of a node join.

### Shrinking the local pool live

`ConnectionPool<INntpClient>` needs a `Resize(int newMax)` method that
updates its internal counter. When the claim-renewal loop sees its slot
count shrink, it calls `connectionPool.Resize(my_slots)`. Existing
connections beyond the new limit are not killed immediately — they
complete their current work and the pool refuses to hand them out
again.

### Fallback when DB unreachable

If the claims table is unreachable on startup, fall back to the
manually-configured `usenet.max-download-connections` value (same as
today). Log a warning. This keeps the system working even if the
coordination layer is broken.

### Files

**New:**
- `backend/Database/Models/ConnectionPoolClaim.cs`
- `backend/Database/Migrations/NNN_AddConnectionPoolClaimsTable.cs`
- `backend/Services/ConnectionPoolCoordinator.cs` (hosted service, runs on all nodes in multi-node mode, handles claim + heartbeat + shrink)
- `backend/Services/ConnectionPoolClaimSweeper.cs` (ingest only)

**Modified:**
- `backend/Clients/Usenet/Connections/ConnectionPool.cs` — add `Resize(int newMax)` method
- `backend/Clients/Usenet/UsenetStreamingClient.cs` — on config change, poll the coordinator for current slot count instead of reading `usenet.max-download-connections` directly
- `backend/Database/DavDatabaseContext.cs` — add DbSet
- `backend/Program.cs` — register services
- `docs/deployment/load-balancer.md` — update to say "set `usenet.max-download-connections` to the TOTAL provider limit; nodes auto-coordinate their share"

---

## Migrations

All three new tables get a single migration:

```
backend/Database/Migrations/NNN_AddMultiNodeCoordinationTables.cs
```

```csharp
public partial class AddMultiNodeCoordinationTables : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // websocket_outbox (Item 3)
        migrationBuilder.CreateTable(
            name: "websocket_outbox",
            columns: table => new
            {
                seq = table.Column<long>(nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy",
                        NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                topic = table.Column<string>(nullable: false),
                payload = table.Column<string>(type: "jsonb", nullable: false),
                created_at = table.Column<DateTime>(
                    nullable: false,
                    defaultValueSql: "now()")
            },
            constraints: table => table.PrimaryKey("pk_websocket_outbox", x => x.seq));

        migrationBuilder.CreateIndex(
            "ix_websocket_outbox_created_at",
            "websocket_outbox",
            "created_at");

        // auth_failures (Item 6)
        migrationBuilder.CreateTable(
            name: "auth_failures",
            columns: table => new
            {
                ip_address = table.Column<string>(nullable: false),
                failure_count = table.Column<int>(nullable: false),
                window_start = table.Column<DateTime>(nullable: false)
            },
            constraints: table => table.PrimaryKey("pk_auth_failures", x => x.ip_address));

        migrationBuilder.CreateIndex(
            "ix_auth_failures_window_start",
            "auth_failures",
            "window_start");

        // connection_pool_claims (Item 7)
        migrationBuilder.CreateTable(
            name: "connection_pool_claims",
            columns: table => new
            {
                node_id = table.Column<string>(nullable: false),
                provider_index = table.Column<int>(nullable: false),
                claimed_slots = table.Column<int>(nullable: false),
                heartbeat_at = table.Column<DateTime>(
                    nullable: false,
                    defaultValueSql: "now()")
            },
            constraints: table => table.PrimaryKey(
                "pk_connection_pool_claims",
                x => new { x.node_id, x.provider_index }));

        migrationBuilder.CreateIndex(
            "ix_connection_pool_claims_heartbeat_at",
            "connection_pool_claims",
            "heartbeat_at");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("connection_pool_claims");
        migrationBuilder.DropTable("auth_failures");
        migrationBuilder.DropTable("websocket_outbox");
    }
}
```

**Only applies when using Postgres.** SQLite migrations are separate
and this one doesn't run there (guarded by provider check in migration
bootstrapping).

---

## Testing Strategy

### Unit tests

**`WebsocketOutbox`:**
- Publish writes row with incrementing seq
- Listener catch-up: seeds `_lastSeenSeq` from max(seq) on startup
- Listener catch-up: returns rows with seq > lastSeen in seq order
- Listener dedup: doesn't fan out the same row twice
- Fallback to in-memory when `MultiNodeMode.IsEnabled == false`

**`PostgresAuthFailureTracker`:**
- Upsert increments when within window
- Upsert resets when window expired
- `IsBlocked` returns true only at ≥10 failures inside window
- Negative cache short-circuits known-clean IPs
- Postgres failure → falls back to in-memory, logs warning
- Sweeper removes rows older than 5 minutes

**`ConnectionPoolCoordinator`:**
- First claim: claims fair share of empty table
- Second claim: existing claim + new node → each gets half
- Third claim: existing 2 claims + new node → each gets third
- Stale claim cleanup: node with heartbeat > 30s ago has claim deleted
- Shrink on rebalance: existing node sees new joiner, reduces claim on next heartbeat
- Concurrent claims: SERIALIZABLE transaction prevents over-subscription
- Shutdown releases claim

**`PgBouncer compat`:** (integration test, runs against real containers)
- EF Core migration runs OK against Postgres DIRECTLY (not through PgBouncer)
- Normal EF Core queries run OK against PgBouncer transaction pool
- LISTEN/NOTIFY works only via the session-pool port
- Transaction pool connection count stays below `DEFAULT_POOL_SIZE` under load
- Statement preparation is disabled (verified via Npgsql logs)

### Integration tests

1. **Three-node fanout:** spin up 3 nodes + Postgres. Fire a websocket event
   on node A. Verify nodes B and C receive it within 500ms.
2. **Rate limit across nodes:** fire 5 failed auth attempts against node A
   and 5 against node B from the same IP. 11th attempt against node C
   should be blocked.
3. **Connection budget fairness:** start with 1 node, verify it claims
   100% of slots. Start a 2nd node, verify both converge to 50% within
   15 seconds. Stop the 2nd node, verify the 1st reclaims 100% within
   45 seconds (30s stale cleanup + 10s heartbeat).
4. **PgBouncer survives restart:** restart PgBouncer mid-workload. EF
   Core reconnects and continues. LISTEN/NOTIFY reconnects (streaming
   node catches up via periodic poll within 30 seconds).
5. **Postgres unreachable:** disable the DB. Verify auth limiter falls
   back to in-memory, websocket events fan out locally only, connection
   coordinator keeps its last-known claim.

### Load tests

1. **Websocket outbox:** 1000 publishes/sec for 5 minutes. Verify all
   three nodes see all messages, no dupes, no loss. Verify sweeper
   keeps table size bounded.
2. **Auth failures:** 10k distinct IPs spraying 5 failures each. Verify
   table size bounded by sweeper, query latency stays <5ms at the 99th
   percentile.
3. **Connection budget churn:** start/stop nodes every 30 seconds for
   10 minutes. Verify total claimed slots never exceeds total provider
   limit, no orphan claims remain at the end.

---

## Rollout

### Phase 1: PgBouncer (item 5)

1. Deploy PgBouncer sidecars to staging multi-node.
2. Update `DATABASE_URL` to point at PgBouncer transaction port.
3. Run the full existing test suite against the staging deployment.
4. Monitor Postgres `pg_stat_activity` to verify connection count stays
   below 25. Monitor p99 query latency.
5. Keep the session-pool container running but don't use it yet
   (it's inert until Item 3 lands).

**Success criteria:** p99 query latency increase < 5ms, server-side
connection count bounded, no EF Core errors.

### Phase 2: Connection budget coordination (item 7)

1. Deploy the migration (adds `connection_pool_claims` table).
2. Deploy the new `ConnectionPoolCoordinator` service.
3. Verify nodes converge on fair share within 15 seconds of any
   topology change.
4. Deprecate `usenet.max-download-connections` as a per-node setting —
   it becomes the TOTAL provider limit shared across all nodes.

**Success criteria:** operators no longer hand-tune per-node connection
limits; rebalance on scale-out is automatic and bounded.

### Phase 3: Shared rate limiter (item 6)

1. Deploy the migration (adds `auth_failures` table).
2. Deploy `PostgresAuthFailureTracker`.
3. Verify rate limit applies across nodes.

**Success criteria:** a single attacker hitting all nodes simultaneously
is blocked at the global 10-failure threshold, not the per-node one.

### Phase 4: Websocket outbox (item 3)

1. Deploy the migration (adds `websocket_outbox` table).
2. Deploy `WebsocketOutboxListener` + `WebsocketOutboxSweeper`.
3. Modify `WebsocketManager.SendMessage` to use outbox in multi-node
   mode.
4. Verify UI updates work across nodes.

**Success criteria:** a web client connected to any streaming node sees
events fired by any other node within ~500ms under normal conditions,
within 30 seconds under listener-disconnect conditions.

### Why this order

- PgBouncer first because it's a dependency — items 3, 6, 7 all write
  to Postgres under PgBouncer.
- Connection budget second because it's the highest-impact quality-of-
  life improvement (operators stop hand-tuning).
- Rate limiter third because it's a security improvement that's
  observable only under attack conditions — nice to validate in
  staging before going to production.
- Websocket outbox last because it's the most architecturally complex
  (LISTEN/NOTIFY + session pool + catch-up poll) and the safest to
  defer since single-node users aren't affected.

---

## Open Questions for Review

1. **PgBouncer image choice.** `edoburu/pgbouncer` is popular but not
   official. Alternative: build our own from the official `pgbouncer`
   source. Is the image source a concern for the target deployment
   audience?

2. **Outbox retention window.** 5 minutes covers most transient failures
   but not a Postgres restart that takes longer. Should it be 15
   minutes? An hour? Trade-off: retention × publish rate = table size.

3. **Rate limiter negative cache TTL.** 10 seconds means a newly-
   attacking IP has up to 10 seconds of grace before being detected.
   Should it be lower (1-2 seconds) for stricter security, or higher
   (60 seconds) for lower DB load?

4. **Connection budget: node_id persistence.** Currently proposed as a
   UUID per process. Should it be persisted to disk (`/config/node-id`)
   so a restart reclaims the same identity? Trade-off: simpler claim
   recovery vs. harder to reset a stuck state.

5. **Item 8 (object-storage L2 cache) — should it be part of this spec?**
   The audit ranked it as "the big one that unlocks real horizontal scale
   past 3-4 nodes". It's deferred from this spec because it's multi-week
   work, but the architecture decisions in this spec (especially how
   connection budgets are coordinated) may affect how Item 8 interacts
   with shared cache. Worth flagging that Item 8 will need its own spec
   after this one lands.

6. **Should `AuthFailureTrackerSweeper` be deleted?** The in-process
   sweeper from audit item I6 becomes redundant in multi-node mode
   (Postgres sweeper does the work). But it's still needed in single-
   node mode. Leave both in place, gate on `MultiNodeMode.IsEnabled`.

---

## Files Summary

### New Files (10)

| File | Item |
|---|---|
| `backend/Config/MultiNodeMode.cs` | all |
| `backend/Database/Models/WebsocketOutboxEntry.cs` | 3 |
| `backend/Database/Models/AuthFailureEntry.cs` | 6 |
| `backend/Database/Models/ConnectionPoolClaim.cs` | 7 |
| `backend/Database/Migrations/NNN_AddMultiNodeCoordinationTables.cs` | 3,6,7 |
| `backend/Services/WebsocketOutboxListener.cs` | 3 |
| `backend/Services/WebsocketOutboxSweeper.cs` | 3 |
| `backend/Api/Filters/PostgresAuthFailureTracker.cs` | 6 |
| `backend/Services/AuthFailuresSweeper.cs` | 6 |
| `backend/Services/ConnectionPoolCoordinator.cs` | 7 |
| `backend/Services/ConnectionPoolClaimSweeper.cs` | 7 |

### Modified Files (8)

| File | Items |
|---|---|
| `backend/Database/DavDatabaseContext.cs` | 3,5,6,7 — add DbSets, update conn string for PgBouncer compat |
| `backend/Websocket/WebsocketManager.cs` | 3 — fork `SendMessage` on `MultiNodeMode.IsEnabled` |
| `backend/Api/Filters/ApiKeyAuthFilter.cs` | 6 — inject new tracker interface |
| `backend/Clients/Usenet/Connections/ConnectionPool.cs` | 7 — add `Resize` |
| `backend/Clients/Usenet/UsenetStreamingClient.cs` | 7 — poll coordinator instead of config |
| `backend/Program.cs` | all — service registration |
| `backend/Utils/EnvironmentUtil.cs` | 5 — `DATABASE_URL_SESSION` getter |
| `docs/deployment/docker-compose.multi-node.yml` | 5 — add PgBouncer services |
| `docs/deployment/load-balancer.md` | 7 — update connection budget docs |
| `docs/deployment/setup-guide.md` | 5 — PgBouncer section |

### Total: 10 new + ~10 modified = 20 files touched

---

## Non-Goals

- **Not included:** Item 8 (object-storage L2 cache), Item 9 (Redis
  metadata cache). Those need their own spec round — the architecture
  decisions here (Postgres as coordination layer, PgBouncer for scale)
  don't materially affect either one.
- **Not included:** Audit items 13-14 (already landed in the tactical
  session).
- **Not included:** Changes to the single-node SQLite path. Single-node
  users see ZERO new code paths from this spec — all new features are
  gated on `MultiNodeMode.IsEnabled`.

---

## What This Unlocks

After this spec lands, NZBDAV's multi-node deployment goes from
"works with caveats documented in the audit" to "production-ready for
3-10 streaming nodes":

| Audit concern | Status after this spec |
|---|---|
| 4.1 AuthFailureTracker per-node | Fixed (Item 6) |
| 4.2 WebsocketManager per-node | Fixed (Item 3) |
| 4.3 NNTP connection budget manual | Fixed (Item 7) |
| 4.4 Postgres pool sizing overflow | Fixed (Item 5) |

**Still open after this spec:**
- 3.3 Inter-node segment cache duplication → Item 8 (object storage)
- 4.5 Snapshot on streaming nodes → Already fixed in tactical session
- LB SPOF → Already documented in tactical session (HA guide)

For scale beyond 10 nodes, Item 8 becomes mandatory. This spec handles
everything BELOW that threshold.
