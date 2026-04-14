# Production Deployment

This is the current production model for multi-node NZBDAV:

- Run explicit node roles with `NZBDAV_ROLE=streaming` on streaming nodes and `NZBDAV_ROLE=ingest` on ingest nodes.
- Put all nodes on the same shared Postgres database.
- Keep NNTP connections node-local. Postgres coordinates leases and heartbeats; it does not proxy NNTP traffic for you.
- Route WebDAV and streaming API traffic to streaming nodes, and route add-file/add-url ingest traffic to ingest nodes.

## Role-Aware NNTP Leasing

For explicit-role multi-node deployments, NZBDAV now uses per-node NNTP leases:

- Each node writes heartbeats into Postgres for each pooled provider.
- The allocator computes leases per provider and writes the granted slot count back to Postgres.
- Each node applies only its own local lease and resizes only its own local NNTP pools.
- `usenet.max-download-connections` should stay at the provider's real account limit, not a hand-divided per-node slice.

Under contention, the lease policy is:

- Streaming demand gets 70% of the provider budget.
- Ingest demand gets 30% of the provider budget.
- If one role is idle, the other role can take the full provider budget.
- Within a role, slots are split deterministically by node ID so lease churn stays predictable.

## Shared State vs Local State

Multi-node production now has two layers of state:

- Shared Postgres state: node heartbeats, lease rows, epochs, queue state, metadata cache, and other cluster coordination data.
- Node-local state: active NNTP TCP sessions, local connection pool sizes, local `LiveSegmentCache`, and the node's currently applied lease clamp.

That split is intentional. Postgres gives the cluster a shared source of truth, while each node still owns its own local NNTP sockets and backpressure decisions.

## Observability

Operators should inspect lease state on each node, not only the database:

- `/health` now includes `node_role`, `nntp_leasing_mode`, and `nntp_local_leases`.
- `/metrics` now exports `nzbdav_nntp_lease_*` gauges for the node's local applied lease state.
- These values are local-to-node by design. Scrape every streaming and ingest node separately.

## Transitional Combined Fallback

Multi-node `NZBDAV_ROLE=combined` is intentionally still on the legacy coordinator path:

- `ConnectionPoolCoordinator` and `ConnectionPoolClaimSweeper` remain the active mechanism for Combined-role multi-node nodes.
- The new explicit-role lease path applies only to `streaming` and `ingest` multi-node nodes.
- Do not assume a Combined-role node will expose or follow the same lease behavior as an explicit-role node yet.

## Related

- `docs/deployment/docker-compose.multi-node.yml`
- `docs/deployment/load-balancer.md`
- `docs/deployment/ha-load-balancing.md`
