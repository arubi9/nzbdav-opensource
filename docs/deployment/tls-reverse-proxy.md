# TLS Reverse Proxy Setup

NZBDAV serves HTTP on port 8080. **All traffic must go through a TLS-terminating reverse proxy in production.** API keys, stream tokens, and WebDAV credentials are sent in HTTP headers and query strings — without TLS, they're visible to anyone on the network.

## Caddy (recommended — automatic HTTPS)

```
# Caddyfile
nzbdav.example.com {
    reverse_proxy nzbdav:8080

    # Restrict /metrics to internal monitoring
    @metrics path /metrics
    handle @metrics {
        respond 403
    }
}
```

```yaml
# docker-compose addition
caddy:
  image: caddy:2-alpine
  restart: unless-stopped
  ports:
    - "80:80"
    - "443:443"
  volumes:
    - ./Caddyfile:/etc/caddy/Caddyfile:ro
    - caddy_data:/data
  networks:
    - internal
```

Caddy automatically obtains and renews Let's Encrypt certificates.

## Traefik

```yaml
# docker-compose labels on nzbdav service
labels:
  - "traefik.enable=true"
  - "traefik.http.routers.nzbdav.rule=Host(`nzbdav.example.com`)"
  - "traefik.http.routers.nzbdav.entrypoints=websecure"
  - "traefik.http.routers.nzbdav.tls.certresolver=letsencrypt"
  - "traefik.http.services.nzbdav.loadbalancer.server.port=8080"
```

## Nginx

```nginx
server {
    listen 443 ssl http2;
    server_name nzbdav.example.com;

    ssl_certificate /etc/ssl/certs/nzbdav.pem;
    ssl_certificate_key /etc/ssl/private/nzbdav.key;

    # Large body for NZB uploads
    client_max_body_size 100M;

    # Long timeouts for video streaming
    proxy_read_timeout 300s;
    proxy_send_timeout 300s;

    location / {
        proxy_pass http://nzbdav:8080;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_buffering off;
    }

    # Block public access to metrics
    location /metrics {
        deny all;
    }
}
```

## What to protect

| Endpoint | Contains | Risk without TLS |
|----------|----------|-----------------|
| `/api/*` | API key in header or query string | Key interception |
| `/api/stream/*?token=` | HMAC-signed stream token | Token replay |
| WebDAV `/*` | Basic auth (base64 encoded) | Credential theft |
| `/ws` | API key in first WebSocket message | Key interception |
| `/metrics` | Internal system state | Information disclosure |

## For the Jellyfin plugin

Set `NzbdavBaseUrl` to the **HTTPS** URL of your reverse proxy:

```
https://nzbdav.example.com
```

Not `http://nzbdav:8080` (which is the internal Docker network address).
