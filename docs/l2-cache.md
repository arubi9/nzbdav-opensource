# L2 segment cache

L2 is the deep tier of the segment cache. Reads resolve **L1 (local disk) →
L2 → NNTP**; on an L2 hit the segment is promoted into L1, and on an L2 miss
the NNTP fetch is queued for an L2 write.

L2 exists to avoid re-fetching from Usenet, not to beat local disk. Local disk
wins on throughput at every concurrency level and always will. What L2 buys is
capacity: a single 4K remux is larger than a typical L1 budget, so without a
deep tier every playback of it is a cold NNTP fetch.

L2 is optional and disabled by default. It is **not** multi-node-only; a
single-node or all-in-one deployment can use it.

## Backends

Set `cache.l2.enabled` to `true`, then choose exactly one backend.

| | selected by | use when |
|---|---|---|
| **Filesystem** | `cache.l2.path` / `NZBDAV_L2_PATH` | the deep tier is a mounted disk or NAS |
| **S3** | `cache.l2.endpoint` + access/secret keys | the deep tier is real object storage |

The filesystem backend takes precedence when both are configured.

### Do not front a filesystem with an S3 gateway

If the bytes are already reachable through a mount, use the filesystem
backend. Putting an S3 gateway in front of a mount is not merely slower — with
some gateways it is silently wrong.

`rclone serve s3` states that "metadata will only be saved in memory other
than the rclone `mtime` metadata". NZBDAV stores each segment's yEnc header as
object metadata, so after a gateway restart every header is gone. The read
path then:

1. gets the body back, and counts an L2 **hit**,
2. cannot parse the missing `x-amz-meta-yenc-header`,
3. falls back to NNTP.

The result is a cache that reports an excellent hit rate while doing nothing.
An observed production instance showed 94% L2 hits alongside 352,653 yEnc
fast-path misses for exactly this reason. If you must use an S3 gateway,
verify durability first: write one object with a custom `x-amz-meta-*` header,
restart the gateway, and read it back. If the header is gone, L2 is a no-op.

The filesystem backend avoids this by owning its format: each segment is a
body file plus a `.meta` sidecar, the same pattern L1 already uses.

## Configuration

| key | env override | default | meaning |
|---|---|---|---|
| `cache.l2.enabled` | — | `false` | master switch |
| `cache.l2.path` | `NZBDAV_L2_PATH` | unset | filesystem root; selects the filesystem backend |
| `cache.l2.endpoint` | — | unset | S3 endpoint; selects the S3 backend |
| `cache.l2.bucket-name` | — | `nzbdav-segments` | S3 bucket |
| `cache.l2.access-key` / `.secret-key` | — | unset | S3 credentials |
| `cache.l2.ssl` | — | `true` | S3 TLS |
| `cache.l2.storage-class` | — | `STANDARD` | S3 only; AWS-specific classes are not portable |
| `cache.l2.writer-parallelism` | — | `4` | concurrent writes draining the queue |
| `cache.l2.write-queue-capacity` | — | `16384` | queued writes, by item count |
| `cache.l2.read-timeout-seconds` | — | `30` | |
| `cache.l2.write-timeout-seconds` | — | `60` | |
| `cache.l2.prewarm-policy` | — | `first-middle-last` | which segments to seed |

`NZBDAV_L2_PATH` overrides the shared database value, because a mount point is
a property of the node rather than of the cluster. Nodes without the mount
must not inherit it.

## On-disk layout

```
<root>/segments/<2-hex>/<64-hex>        segment body
<root>/segments/<2-hex>/<64-hex>.meta   yEnc header + category + owner
<root>/owners/<owner-nzb-id>/<64-hex>   deletion index
```

The key is a SHA-256 of the segment id, sharded 256 ways, so a large cache
stays at a few thousand entries per directory.

Writes are body-then-sidecar, each via a temporary file and an atomic rename.
A crash between the two leaves an orphaned body, which reads treat as a miss
and the next write replaces. A body count slightly above the sidecar count is
therefore normal and self-correcting, not drift.

The owner index keeps deletion proportional to one NZB's segments. The S3
backend instead lists the entire bucket and filters client-side, which over a
mounted NAS would mean walking every cached segment on every content removal.

## Capacity and the absence of eviction

**L2 has no size cap and no age-based eviction.** Segments are removed only
when their owning content is deleted. L2 grows until the backing store is
full.

Bound it at the storage layer — a share quota, a dedicated dataset, or a
dedicated volume. The current deployment uses a **5 TB quota** on the NAS
share.

When the backing store fills, every write fails. NZBDAV degrades rather than
breaks: playback is unaffected because those bytes were already served from
NNTP, `nzbdav_l2_cache_write_failures_total` climbs, and the failure is logged
once per episode rather than once per write. Reads continue to hit whatever is
already cached. Plan for the quota to be reached and treat it as steady state,
not an incident — but note that a full L2 stops absorbing new content, so hit
rate decays as the library turns over.

**Do not monitor a share quota with `df`.** A NAS-side quota is generally
invisible to the NFS client, which reports the underlying pool instead. On the
current deployment `df` shows 44 TB available against a 5 TB quota, so free
space looks healthy until the moment writes start failing. Track the tier with
`du -sh` on the L2 root, the NAS UI, or
`nzbdav_l2_cache_write_failures_total` — not with client-side free space.

The in-memory write queue is bounded by **both** item count and bytes
(512 MB). The item count alone is not a safe bound: 16384 queued 716 KB video
segments is roughly 11.7 GB.

## NAS over NFS

A mounted NAS is the intended shape for a large L2. Mount it once on the host
and bind it into the container; do not mount NFS inside the container.

```
# /etc/fstab on the host
<nas>:<export> /mnt/nas-nzb nfs vers=3,hard,noatime,rsize=1048576,wsize=1048576,_netdev 0 0
```

Use `hard`. With `soft`, an unresponsive NAS surfaces as truncated reads
rather than as a stall.

In Compose the tier is a bind mount rather than a named volume, because the
backing store is host-managed and NZBDAV must be able to detect that the mount
is absent:

```yaml
    environment:
      NZBDAV_L2_PATH: "${NZBDAV_L2_PATH:-}"
    volumes:
      - "${NZBDAV_L2_HOST_PATH:-/mnt/nas-l2}:/l2"
```

Both halves are required. Without the env var the mount is inert; without the
mount the path resolves to container-local disk. `NZBDAV_L2_PATH` empty
disables the tier, so the bind mount is harmless on hosts without a NAS.

This is why the all-in-one stack still has exactly **eight named volumes**:
`/l2` is a bind mount and is deliberately excluded from the backup set. It is
a cache and is fully reconstructible from Usenet.

NZBDAV logs a warning at startup if the configured L2 root is not a mount
point. Docker materializes a missing bind-mount source as an empty local
directory, so a NAS that failed to mount would otherwise fill the host disk
silently. The warning is advisory: pointing L2 at a large local disk is a
legitimate configuration.

### UniFi UNAS Pro

Verified against a UNAS Pro. Two things differ from the vendor documentation:

- **NFSv4 is not exported.** Mount with `vers=3`.
- **The documented `/var/nfs/shared/<Drive>` path does not work.** Use the
  path from `showmount -e <nas>`, which looks like
  `/volume/<uuid>/.srv/.unifi-drive/<Drive>/.data`.

Enabling the NFS service is not sufficient. Add an **NFS connection granting
the client IP** read-write access to the shared drive, otherwise the export
list is empty and mounts fail with `access denied by server`. The client IP is
the host that performs the mount. Personal Drives cannot be exported; use a
Shared Drive.

Keep the default **All Squash**. The alternative (No Root Squash / Isolated)
is irreversible and removes the share from the Drive UI and SMB.

There is no official UniFi Drive API. The API key works for some reads but not
for writes, so this configuration is UI-only.

### Unprivileged LXC containers

With All Squash, every client identity maps to a single NAS-side uid. That uid
is outside an unprivileged container's mapped range, so the files display as
`nobody` (65534) inside the container. This is cosmetic: NFSv3 resolves
permissions with an ACCESS RPC evaluated by the server under the squashed
identity, so a container process can create directories, write, rename over,
and delete normally.

Create the L2 directory mode `0777` on the host. The container's uid is not
the squashed owner, so it needs the `other` bits to traverse and write.

## Measured performance

UNAS Pro over NFSv3 on a 10 GbE link, 716,800-byte segments — the real yEnc
part size, which is the only block size that predicts playback behaviour.

| tier | 1 reader | 16 readers | p95 single-segment |
|---|---|---|---|
| L1 local NVMe | 302 MB/s | 1774 MB/s | 4 ms |
| L2 on NAS | 189 MB/s | 913 MB/s | 4–8 ms |
| NNTP (cold) | — | 77 MB/s | 500–1000 ms |

Reads scale to roughly 7.3 Gbps and become *more* stable under load
(±1% spread at 32 readers).

**Writes flatline at about 108 MB/s** regardless of concurrency, and removing
per-segment `fsync` does not change it, so it is a property of the array
rather than of the client. Consequence: during many simultaneous *cold*
streams, NNTP delivers faster than the NAS absorbs, and some L2 writes are
shed. Playback is unaffected; the cache simply warms more slowly. Reads are
the operation that matters and they have ample headroom — 20 concurrent 4K
remux streams need about 145 MB/s aggregate.

`tools/l2-bench.sh <directory>` reproduces this on any candidate store.

## Verifying a deployment

`tools/l2-restart-proof.sh` performs the end-to-end check. It empties L1,
restarts the container, and re-reads a range that was cached beforehand — L1
must be emptied first or it serves the range and L2 is never consulted.

A correct result shows L2 hits, no yEnc parse failures, and read throughput
well above the cold-NNTP rate:

```
l2_cache_hits_total 150    misses 1    write_failures 0
HTTP 206   104857600 bytes   161 MB/s
yEnc header parse failures: 0
```

Relevant metrics:

- `nzbdav_l2_cache_enabled`
- `nzbdav_l2_cache_hits_total` / `_misses_total`
- `nzbdav_l2_cache_writes_total` / `_write_failures_total` / `_writes_dropped_total`
- `nzbdav_l2_cache_queue_depth`
- `nzbdav_yenc_fast_path_hits_total` / `_misses_total`

Read `nzbdav_l2_cache_hits_total` alongside the yEnc fast-path counters. A high
hit rate with high fast-path misses is the metadata-loss signature described
above, not a healthy cache.

Note that the shipped image runs at `LOG_LEVEL=warning`, so `Log.Information`
lines — including the one naming the selected L2 backend — are suppressed.
Confirm the backend from metrics and from files appearing under the configured
root, not from the absence of log lines.
