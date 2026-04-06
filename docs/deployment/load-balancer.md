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
| `/health`, `/metrics` | All nodes (round-robin) |
| `/ws` | Any node (WebSocket sticky) |

## Private Network

All NZBDAV to load balancer traffic should use the private vRack network
(10.0.0.0/24) to avoid public bandwidth charges and reduce latency.
