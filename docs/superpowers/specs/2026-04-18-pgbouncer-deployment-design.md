# PgBouncer deployment — design

## Problem

`SharedHeaderCache` and other Postgres-backed components (ConfigItems
reads, `DavItems` lookups, NntpLeaseState rows, authentication checks)
each open Postgres connections directly from NZBDAV nodes. At the
current small scale this is fine. At hundreds of concurrent streams
across multiple streaming nodes the connection count explodes:

- 5 streaming nodes × 100 connections each = 500 conn pool
- Postgres `max_connections` default = 100 — already insufficient
- Raising Postgres `max_connections` to 500+ works but each idle
  connection costs ~10 MB memory + scheduling overhead

## Goal

Put **PgBouncer** between NZBDAV nodes and Postgres.
- NZBDAV opens a small pool to PgBouncer (100 conns / node)
- PgBouncer shares a tiny pool to Postgres (25 conns total)
- NZBDAV sees unlimited pool, Postgres sees 25 busy backends

Transaction-level pooling is the correct mode — NZBDAV does short
read-heavy queries, rarely multi-statement transactions with
`PREPARE`/`SAVEPOINT` state.

## Topology

Current:
```
NZBDAV streaming node A ─┐
NZBDAV streaming node B ─┼─► Postgres (port 5432, 100 conn cap)
NZBDAV ingest node       ─┘
```

Proposed:
```
NZBDAV A ─┐
NZBDAV B ─┼─► PgBouncer (port 6432) ─► Postgres (port 5432, 100 conn cap)
NZBDAV ingest ─┘
```

PgBouncer runs either:
- **Option A:** On the Postgres host (same box, sidecar container)
- **Option B:** On each NZBDAV streaming node (outbound-pool)
- **Option C:** Dedicated node (resilient, +1 machine)

**Pick Option A** for now — single PgBouncer co-located with Postgres.
Simple, low-latency backend fan-in. Can split to Option C later if
scale requires.

## Config

### `pgbouncer.ini`

```ini
[databases]
nzbdav = host=nzbdav-postgres port=5432 dbname=nzbdav

[pgbouncer]
listen_addr = 0.0.0.0
listen_port = 6432
auth_type = scram-sha-256
auth_file = /etc/pgbouncer/userlist.txt

pool_mode = transaction
max_client_conn = 500
default_pool_size = 25
min_pool_size = 5
reserve_pool_size = 10
reserve_pool_timeout = 3

server_idle_timeout = 180
server_lifetime = 3600
query_wait_timeout = 120

# Stats — exposed for Prometheus via postgres_exporter
stats_period = 60
```

### `userlist.txt`

```
"nzbdav" "SCRAM-SHA-256$..."   # hashed from NZBDAV's Postgres creds
```

### Docker compose addition (on Postgres host)

```yaml
pgbouncer:
  image: edoburu/pgbouncer:latest
  environment:
    DATABASE_URL: "postgresql://nzbdav@nzbdav-postgres:5432/nzbdav"
    POOL_MODE: transaction
    MAX_CLIENT_CONN: "500"
    DEFAULT_POOL_SIZE: "25"
    AUTH_TYPE: scram-sha-256
  ports:
    - "6432:6432"
  depends_on:
    - nzbdav-postgres
```

### NZBDAV connection string change

`DATABASE_URL` environment variable on each NZBDAV node:
- Before: `postgresql://nzbdav:pw@10.0.0.5:5432/nzbdav`
- After: `postgresql://nzbdav:pw@10.0.0.5:6432/nzbdav`

No code change. Npgsql handles transaction pool mode transparently
provided NZBDAV doesn't rely on session-scoped features (it doesn't —
verified: no advisory locks, no prepared statements, no temp tables,
no SET LOCAL outside transactions).

## Prepared statement concern

`pool_mode = transaction` prohibits driver-side prepared-statement
caching across transactions. Npgsql auto-prepare default is
`Max Auto Prepare=0` (off) — safe. If anyone flips auto-prepare on,
must set `No Reset On Close=true` + move to `pool_mode = session`
or accept the error.

## Benefits quantified

At 5-node, 100-stream fleet:
- Postgres busy backends: ~500 → **25** (95% reduction)
- Postgres memory usage: ~5 GB → **300 MB** for backends
- Query latency p50: unchanged (PgBouncer adds ~0.1 ms hop)
- Connection-storm tolerance: NZBDAV reconnect storms absorbed by
  PgBouncer; Postgres sees steady load

## Monitoring

PgBouncer exposes `SHOW STATS`, `SHOW POOLS`, `SHOW CLIENTS` on the
admin port. Wire into Prometheus via `postgres_exporter` with
`--extend.query-path` pointing at PgBouncer queries.

Key metrics:
- `pgbouncer_pools_server_active` — should stay ≤ `default_pool_size`
- `pgbouncer_pools_cl_waiting` — non-zero means clients waiting for
  a backend; bump `default_pool_size` if persistent
- `pgbouncer_stats_queries_pool_rate` — queries per pool per second

## Failover

PgBouncer is a single-point-of-failure in this design. Mitigations:
- `depends_on: nzbdav-postgres` + `restart: unless-stopped` in compose
- Watchtower pulls image updates
- Second PgBouncer replica for HA is spec-next (not in this doc)

## Verification

- `psql -h pgbouncer-host -p 6432 -U nzbdav` from an NZBDAV node →
  returns SELECT 1 successfully
- Load test: spawn 200 concurrent `SELECT` queries from NZBDAV →
  PgBouncer `SHOW POOLS` shows `cl_active = 200`, `sv_active <= 25`
- `SharedHeaderCache` hit/miss rate unchanged post-rollout
- `NntpLeaseState` writes succeed — confirms transaction-mode compat
- Postgres `pg_stat_activity` backend count falls from ~100 to ~25

## Rollback

Flip `DATABASE_URL` port back to 5432 on every NZBDAV node, restart.
Instant. PgBouncer container can stay running; NZBDAV just bypasses it.

## Non-goals

- Query-routing to read replicas (single Postgres for now)
- Pgpool for load balancing (heavier, more complex; PgBouncer covers
  the pooling need alone)
- PgBouncer HA cluster (defer until single instance becomes a visible
  reliability problem)
