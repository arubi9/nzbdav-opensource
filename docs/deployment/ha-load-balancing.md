# HA Load Balancing for NZBDAV Multi-Node Deployments

## The Gap This Document Closes

The reference multi-node compose file at
`docs/deployment/docker-compose.multi-node.yml` runs a single HAProxy
container as the ingress for all client traffic. That HAProxy is a
**single point of failure**. If the HAProxy container or its host
crashes, your entire NZBDAV deployment becomes unreachable — even
though all streaming and ingest nodes are still healthy.

**Multi-node NZBDAV as shipped provides horizontal scale, not high
availability.** Horizontal scale means "you can serve more concurrent
users by adding nodes". High availability means "no single component
failure takes the system down". Those are different properties and
NZBDAV's stock multi-node compose only delivers the first one.

This document describes three ways to get actual HA at the ingress
layer. Pick the one that matches your environment.

---

## Option A — Active-Passive HAProxy with keepalived + VRRP (bare metal / VMs)

**Use when:** You have two or more dedicated hosts on the same L2
network segment. This is the classic on-prem HA answer. It's battle-
tested, uses no cloud services, and costs nothing beyond the extra
host.

### Architecture

```
   Clients → Virtual IP (VIP) ──┐
                                ├── HAProxy primary   (Host A)
                                │       ↓
                                │    NZBDAV backends
                                │
                                └── HAProxy standby   (Host B)
                                        ↓ (takes over on failure)
                                     Same NZBDAV backends
```

The VIP (e.g., `10.0.0.100`) floats between Host A and Host B. At any
moment, exactly one host holds the VIP. If the primary fails, the
standby grabs the VIP via VRRP (Virtual Router Redundancy Protocol)
within ~1-3 seconds. Clients see a brief connection reset, reconnect,
and land on the standby. HAProxy on the standby already has its config
loaded, so it resumes serving immediately.

### Requirements

- Two Linux hosts on the same L2 segment (same broadcast domain)
- An unused IP on that segment to use as the VIP
- Ability to run keepalived as a system daemon (not inside Docker, or
  inside Docker with host networking and `NET_ADMIN` capability)
- HAProxy installed on both hosts, with identical `haproxy.cfg`
- The NZBDAV streaming and ingest nodes reachable from both hosts

### Setup sketch

On both hosts, install HAProxy and keepalived:

```bash
sudo apt install -y haproxy keepalived
```

Deploy the same `haproxy.cfg` to both hosts (use the version in
`docs/deployment/haproxy.cfg` as a starting point). Start HAProxy on
both:

```bash
sudo systemctl enable --now haproxy
```

On Host A, create `/etc/keepalived/keepalived.conf` (the **MASTER**):

```
vrrp_script chk_haproxy {
    script "/usr/bin/pgrep haproxy"
    interval 2
    weight -20
    fall 2
    rise 2
}

vrrp_instance NZBDAV_VIP {
    state MASTER
    interface eth0             # your network interface
    virtual_router_id 51       # must match between master/backup
    priority 150               # higher than backup
    advert_int 1
    authentication {
        auth_type PASS
        auth_pass <a shared secret>
    }
    virtual_ipaddress {
        10.0.0.100/24          # the VIP
    }
    track_script {
        chk_haproxy
    }
}
```

On Host B, same config but `state BACKUP` and `priority 100`.

Start keepalived on both:

```bash
sudo systemctl enable --now keepalived
```

Point your clients (Jellyfin, Radarr, Sonarr, browsers) at the VIP
`10.0.0.100:8080`. The active host serves traffic; the standby sits
idle. On primary failure, VRRP advertises the VIP from the standby
within ~3 seconds, HAProxy there starts accepting connections, and
service resumes.

### What you get

- **Failure recovery:** ~1-3 seconds on primary crash (VRRP convergence)
- **Zero-downtime HAProxy config reloads:** do it one host at a time
- **No cloud dependencies:** works entirely on your own network

### What you don't get

- **Geographic failover:** both hosts must be on the same L2 segment,
  so a datacenter-level outage still takes you down.
- **Automatic scaling:** still a 2-host pair; you add more HAProxy
  instances manually.

### Gotchas

- **virtual_router_id must be unique on your network.** If another
  keepalived cluster on the same subnet uses the same id, you'll get
  VIP flapping. Pick an unused number.
- **Docker + keepalived is fiddly.** keepalived needs low-level network
  access (`CAP_NET_ADMIN`, usually host networking). Most operators run
  keepalived and HAProxy directly on the host, and run NZBDAV in
  Docker behind them. The alternative is `docker run --cap-add
  NET_ADMIN --network host`, which defeats container isolation.
- **Split-brain is possible** if VRRP advertisements are dropped by a
  buggy switch or firewall. Both hosts think they're primary and both
  claim the VIP. Use a conservative `advert_int` and monitor keepalived
  logs.
- **The NZBDAV backends need a stable hostname from both hosts.** DNS
  or `/etc/hosts` entries on each HAProxy host pointing at the
  nzbdav-stream-* containers.

---

## Option B — External Cloud Load Balancer

**Use when:** You're already running NZBDAV on a cloud provider or
have access to a managed load balancer. This is the simplest and most
reliable path if the cloud cost fits your budget.

### Architecture

```
   Clients → Cloud LB (AWS ALB, GCP LB, DigitalOcean LB,
                       Cloudflare Load Balancer, Azure Front Door, …)
                ↓
             NZBDAV streaming + ingest backends
```

You drop HAProxy entirely (or use it as an internal LB for the
consistent-hash URI routing that NZBDAV's streaming backends
benefit from — see "When to keep HAProxy as an internal LB" below).

### Setup sketch (AWS ALB example)

1. Create a target group for the streaming backends with health check
   path `/ready` (returns 503 when the node is saturated — see audit
   item 14).
2. Create a target group for the ingest backend with the same
   `/ready` health check.
3. Create an Application Load Balancer in the subnet your backends
   live in.
4. Add two listener rules:
   - `PathPattern: /api/*` AND `QueryString: mode=addfile` OR
     `mode=addurl` → ingest target group
   - `*` (default) → streaming target group
5. Point your clients at the ALB's DNS name.

The ALB's health-check cadence determines failover speed — typically
every 10-30 seconds with a 2-failure threshold, so expect 30-60
seconds to drain an unhealthy backend.

### What you get

- **Fully managed HA:** the cloud LB itself is redundant across the
  provider's own datacenters. You don't have to worry about it failing.
- **TLS termination:** most cloud LBs handle certificates for free.
- **Geographic distribution:** (with Cloudflare, Azure Front Door, AWS
  Global Accelerator) possible multi-region failover.
- **WAF / DDoS protection:** most cloud LBs bolt this on cheaply.

### What you don't get

- **Consistent-hash URI routing.** Most cloud LBs default to round-
  robin or least-connections. NZBDAV streaming nodes benefit from
  consistent-hash routing because it keeps the same stream URL pinned
  to the same node (so the segment cache stays warm). See "When to
  keep HAProxy as an internal LB" below.
- **Free tier pricing.** Cloud LBs cost money. For a hobby setup this
  adds $15-25/month.

### When to keep HAProxy as an internal LB

If the cache-affinity benefit matters for your workload (more than 3-4
concurrent streaming users), keep HAProxy inside your network as an
**internal** LB for the streaming backends specifically, and put the
cloud LB in front of HAProxy:

```
Clients → Cloud LB (HA) → HAProxy (internal) → NZBDAV streaming
                       ↳ directly               NZBDAV ingest
```

Run two HAProxy instances behind the cloud LB, both configured
identically. The cloud LB handles the HA; HAProxy just provides the
consistent-hash URI routing. Each HAProxy instance is stateless and
failure of one is handled by the cloud LB marking it unhealthy.

---

## Option C — Accept the SPOF (current stock deployment)

**Use when:** You're running NZBDAV as a hobby server for yourself or
your household, and the cost/complexity of true HA outweighs the risk
of a 30-second outage after a crash.

No changes to the reference compose file. You get:

- **Horizontal scale:** multiple streaming nodes share traffic via
  HAProxy's consistent-hash URI routing, cache stays warm per node
- **Quick HAProxy recovery:** Docker's restart policy brings HAProxy
  back after a crash within a few seconds
- **Single point of failure at the ingress:** if the HAProxy container
  dies and Docker fails to restart it, your entire deployment is
  unreachable until you intervene

**This is the correct choice for a self-hosted hobby deployment.**
The HA options above add real operational complexity (keepalived
config, cloud LB bills, DNS plumbing) that isn't worth it for a system
that's used by a few people and where a few minutes of downtime during
a restart isn't a crisis.

To make Option C more robust without going full HA:

- Use `restart: unless-stopped` on the HAProxy service in your compose
  file so Docker automatically restarts it on crash
- Add `healthcheck` to the HAProxy service so Docker's supervisor
  kills and restarts it if it becomes unresponsive
- Run the compose stack under systemd (`systemctl enable
  docker-compose@nzbdav`) so the whole stack auto-starts after a
  reboot
- Monitor the HAProxy stats page (`/stats` on port 8404) with
  something cheap like UptimeRobot or a cron that `curl`s every
  minute and alerts you on failure

These steps turn Option C from "single point of failure" into "single
point of failure with a fast-restart and alerting". Not HA, but close
enough for hobby use.

---

## Decision Matrix

| Your situation | Pick |
|---|---|
| Self-hosted hobby server, 1-10 concurrent users, running on one box or a small home cluster | **Option C** |
| On-prem deployment, two dedicated hosts available, need zero-downtime failover | **Option A** |
| Already on AWS/GCP/Azure/DigitalOcean or have a budget for cloud services | **Option B** |
| Option B for HA + need the cache-affinity benefit of consistent-hash routing | **Option B with internal HAProxy** |

---

## What NZBDAV Does to Support Load-Balanced Deployment

- **`/health` endpoint** at port 8080 returns a JSON diagnostic payload
  and HTTP 200 (even when Degraded). Hit this from operator dashboards
  and Prometheus, not from load balancers.
- **`/ready` endpoint** at port 8080 returns HTTP 200 when healthy,
  HTTP 503 when Degraded (cache or NNTP pool >90% utilization) or
  Unhealthy (provider down, database unreachable). **Point your load
  balancer's health check at `/ready`**, not `/health`.
- **`/metrics` endpoint** at port 8080 is the Prometheus scrape target.
- **Graceful shutdown:** on SIGTERM the node stops accepting new
  requests, lets in-flight requests finish, and shuts down within the
  30-second `HostOptions.ShutdownTimeout`. `/ready` returns 503
  immediately on shutdown so the LB drains the node.
- **Forwarded headers:** NZBDAV honors `X-Forwarded-For` and
  `X-Forwarded-Proto` so rate limiting and URL generation work
  correctly behind a proxy.

## Related

- `docs/deployment/docker-compose.multi-node.yml` — the stock
  multi-node compose (uses Option C, single HAProxy)
- `docs/deployment/haproxy.cfg` — reference HAProxy config with
  consistent-hash URI routing and `/ready` health checks
- `docs/deployment/load-balancer.md` — NNTP connection budget
  coordination notes (orthogonal to HA)
