# Load Balancer Configuration for NZBDAV Horizontal Scaling

## Cache Affinity Routing

Route requests for the same content to the same NZBDAV streaming node
so that LiveSegmentCache hits are maximized.

### HAProxy (recommended)

```haproxy
backend nzbdav_streaming
    balance uri
    hash-type consistent
    server nzbdav1 10.0.0.11:8080 check
    server nzbdav2 10.0.0.12:8080 check
    server nzbdav3 10.0.0.13:8080 check
```

### Nginx

```nginx
upstream nzbdav_streaming {
    hash $request_uri consistent;
    server 10.0.0.11:8080;
    server 10.0.0.12:8080;
    server 10.0.0.13:8080;
}
```

### Traefik (docker labels)

```yaml
labels:
  - "traefik.http.services.nzbdav.loadbalancer.strategy=consistent-hash"
  - "traefik.http.services.nzbdav.loadbalancer.sticky.cookie=true"
```

## Endpoint Routing

| Path pattern | Route to |
|---|---|
| `/api/*`, WebDAV PROPFIND/GET/HEAD | Streaming nodes (consistent hash by URI) |
| `/api?mode=addfile`, `/api?mode=addurl` | Ingest node (direct) |
| `/health`, `/metrics` | Any node for generic uptime checks; query individual nodes directly when you need that node's local lease state |
| `/ws` | Any node (WebSocket sticky) |

## Private Network

All NZBDAV to load balancer traffic should use the private vRack network
(10.0.0.0/24) to avoid public bandwidth charges and reduce latency.

## NNTP Connection Budget

In explicit-role multi-node mode, set `usenet.max-download-connections` to the
provider's TOTAL connection limit, not a manual per-node slice. NZBDAV now
coordinates that shared budget through Postgres lease state while each node
keeps its own local NNTP sockets.

The current lease model is:

- Streaming nodes run with `NZBDAV_ROLE=streaming`.
- Ingest nodes run with `NZBDAV_ROLE=ingest`.
- Each node heartbeats demand into Postgres for each pooled provider.
- The allocator writes per-provider leases back into Postgres.
- Each node applies only its own local lease to its own local NNTP pool.

Under contention, the allocator reserves capacity per provider as:

- 70% for streaming demand
- 30% for ingest demand
- If one role is idle, the other role can consume the full provider budget

Examples for explicit-role deployments:

| Provider limit | Nodes | Setting |
|---|---|---|
| 30 connections | 2 streaming + 1 ingest | `usenet.max-download-connections = 30` |
| 50 connections | 3 streaming + 1 ingest | `usenet.max-download-connections = 50` |
| 50 connections | 4 streaming + 2 ingest | `usenet.max-download-connections = 50` |

Keep a few connections of provider-side headroom if you also use the same
account outside NZBDAV or if the provider is strict about bursty reconnects.

## Shared Postgres, Node-Local NNTP

Postgres is the cluster coordination plane:

- lease rows
- lease epochs
- node heartbeats
- queue and metadata state

NNTP connections stay node-local:

- each node opens and closes its own provider TCP sessions
- each node enforces only its own currently granted lease
- `/health` and `/metrics` on a node show that node's local applied lease state
- scrape or query each node directly when operators need current lease observability; a round-robin load balancer view is not sufficient for that

## Transitional Combined Fallback

Combined-role multi-node is intentionally still transitional:

- `NZBDAV_ROLE=combined` in multi-node mode continues to use the legacy connection coordinator path
- explicit-role `streaming` and `ingest` nodes use the new per-node lease path
- do not mix Combined-role operational assumptions with explicit-role lease observability

### Alternative: Separate provider accounts

For true isolation, give each streaming node its own NNTP provider
account. This eliminates connection budget coordination entirely
but costs more from the provider.
