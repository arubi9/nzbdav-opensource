# Advanced production deployment: multi-node NZBDAV

> **Separate scope:** This is the advanced multi-node contract, not the fresh
> five-service all-in-one stack. Do not add Postgres, PgBouncer, MinIO, a load
> balancer, or extra NZBDAV nodes to `docker-compose.full-stack.yml`. Start with
> [Fresh all-in-one deployment](all-in-one.md) for a single Linux server.
> This multi-node path is a fresh deployment contract only; it does not adopt
> or convert a legacy NZBDAV/Arr/Jellyfin installation.

Multi-node operation requires explicit node roles and shared PostgreSQL state.
It is not a supported conversion of the five named-volume stack by changing a
few environment variables.

## Contract

- Use `NZBDAV_ROLE=streaming` on streaming nodes and
  `NZBDAV_ROLE=ingest` on ingest nodes.
- Point all nodes at the same shared Postgres database. Run migrations against
  the real `postgres` service, not transaction-pooled PgBouncer.
- Keep NNTP TCP connections node-local. Postgres coordinates leases and
  heartbeats; it does not proxy NNTP traffic.
- Route WebDAV/streaming API traffic to streaming nodes and add-file/add-url
  ingest traffic to ingest nodes.
- Set a unique, stable `NZBDAV_NODE_ID` for every node. If omitted, the
  hostname is used and must already be unique and stable.

Use the maintained [multi-node Compose example](deployment/docker-compose.multi-node.yml)
and the [load-balancing guide](deployment/load-balancer.md) as the deployment
starting points. The [HA guide](deployment/ha-load-balancing.md) covers the
additional failure and routing requirements.

`DATABASE_URL` may point at the PgBouncer transaction pool for operational
traffic. Set **this exact direct endpoint** on every NZBDAV node:

```dotenv
MIGRATION_DATABASE_URL=Host=postgres;Port=5432;Database=nzbdav;Username=nzbdav;Password=nzbdav;Pooling=true
```

`postgres` is the direct PostgreSQL service name, not `pgbouncer`. Startup
migration holds its advisory lock on one direct session and refuses to run
through PgBouncer. Keep operational traffic on the transaction-pooled
`DATABASE_URL`; never substitute it for `MIGRATION_DATABASE_URL`. The [multi-node
Compose example](deployment/docker-compose.multi-node.yml) sets this exact
value for every NZBDAV node.

## NNTP leasing

Each node heartbeats for each pooled provider. The allocator writes per-node
leases to Postgres, and each node applies only its local lease. Keep
`usenet.max-download-connections` at the provider account limit; do not
manually divide it by node count.

The policy favors streaming demand with 70% and ingest demand with 30%; an idle
role can use the full budget. Nodes within a role receive deterministic shares.
Inspect every node's `/health` (`node_role`, `nntp_leasing_mode`,
`nntp_local_leases`) and scrape `/metrics` lease gauges on every node. These are
node-local observations, not a database-wide summary.

`NZBDAV_ROLE=combined` is a transitional legacy coordinator path. Do not mix
Combined-role nodes with explicit streaming/ingest nodes against the same
provider budget; their coordination tables and behavior are not compatible.

## Shared and local state

Postgres holds cluster coordination, heartbeats, leases, queue state, and
shared metadata-cache state. Each node retains active NNTP sessions, local
connection-pool sizing, and its local live segment cache. A shared L2 object
cache and shared metadata cache are multi-node features only; they are not part
of the all-in-one deployment.

Back up Postgres and every node's local configuration according to the chosen
orchestration platform. Test restores and preserve the NZBDAV encryption key.
Do not treat a named-volume reset from the all-in-one guide as a multi-node
recovery procedure.
