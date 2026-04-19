# Caddy HTTP/2 + HTTP/3 enablement — design

## Problem

Both Caddy instances (Server B for `stream.*`, Server C for
`jellyfin.*`) default to HTTP/1.1 + HTTP/2. HTTP/3 (QUIC) is not
enabled.

At hundreds of concurrent viewers each making many Range requests per
stream, three characteristics of HTTP/3 / h2 become meaningful:
1. **Connection reuse** — Range multiplexing over one conn saves
   N × TLS handshakes per viewer
2. **Independent streams** — head-of-line blocking on HTTP/1.1 serializes
   Range fetches; h2 multiplexes them
3. **QUIC packet loss handling** — HTTP/3 avoids TCP head-of-line
   blocking across streams. Big win on lossy client connections (mobile,
   congested Wi-Fi, far-region viewers).

## Goal

Enable `h1+h2+h3` on both Caddy frontends. Keep h1 as fallback for
clients that don't support h3.

## Config

### Server B (stream.flixmango.site Caddyfile)

```caddy
stream.flixmango.site {
    servers {
        protocols h1 h2 h3
    }
    reverse_proxy nzbdav:8080 {
        # Range requests pass through cleanly
        flush_interval -1
    }
    # Advertise HTTP/3 via Alt-Svc so clients upgrade
    header Alt-Svc `h3=":443"; ma=86400`
}
```

### Server C (jellyfin.flixmango.site Caddyfile)

```caddy
jellyfin.flixmango.site {
    servers {
        protocols h1 h2 h3
    }
    reverse_proxy jellyfin:8096
    header Alt-Svc `h3=":443"; ma=86400`
}
```

## Port requirements

HTTP/3 uses **UDP/443**. Firewall must allow UDP inbound.
- OVH: default security groups allow all TCP/UDP — no change
- Hetzner Cloud: default firewall policy wide open — no change
- Docker: ensure compose exposes `443/udp` alongside `443/tcp`

Current compose likely only maps `443:443` which defaults to TCP. Must
explicitly add:
```yaml
ports:
  - "443:443/tcp"
  - "443:443/udp"
```

## TLS certificate

No change. Caddy's ACME automation covers h3 using the same cert. QUIC
uses TLS 1.3 embedded.

## Client compatibility

- Chrome / Edge / Safari (recent): native HTTP/3 with auto-upgrade via Alt-Svc
- Firefox: native HTTP/3 with Alt-Svc
- Jellyfin mobile apps (iOS/Android WebView): inherit system TLS stack; auto-upgrade
- Older / embedded clients (older Fire TV, some smart TVs): fall back to h2 or h1 transparently

No client breakage risk — h3 is strictly additive.

## Performance expectations

Measured benefit patterns (not our stack, but typical for range-heavy
video delivery):
- p50 click-to-first-byte: **~15% lower** over HTTP/3 vs HTTP/1.1 (one
  fewer handshake)
- Scrub responsiveness on lossy links (5% packet loss): **~40% lower**
  jitter
- Multi-range concurrent fetches: **2-3× faster** completion due to
  stream multiplexing

On well-peered low-loss paths (e.g. BHS client on good fibre), gain is
marginal. On long-RTT or lossy paths (APAC, mobile, bad Wi-Fi), gain
is significant.

## Verification

- `curl --http3 -I https://stream.flixmango.site/` from a h3-capable
  curl (7.88+ built with ngtcp2) → returns 200 with `alt-svc` header
- Chrome DevTools → Network panel → Protocol column shows `h3` for Range
  requests after initial Alt-Svc advertisement
- `nstat` on Caddy host shows `UdpInDatagrams` counter climbing with
  h3 clients
- Watch `nzbdav_cache_hits_total` rate during h3 rollout — should not
  regress; ideally improves slightly from better connection reuse

## Rollback

Set `protocols h1 h2` in Caddyfile and reload. Clients fall back to h2
automatically. No state to clean up.

## Non-goals

- 0-RTT (early data) — risks replay attacks on non-idempotent requests;
  h3 without 0-RTT still gives the win
- HTTP/3 to backend (Caddy → NZBDAV) — internal hop is already fast,
  h1 to backend is fine
- Enabling h3 on Server A's ingest API — not client-facing
