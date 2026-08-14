# Deploying on small hardware (Raspberry Pi, mini-PC)

Sizing notes for running the full stack on a Raspberry Pi 5 or a small x86
box. The numbers below are measured, not estimated: they come from load
testing the stack on a 12-core i5-12600KF Proxmox host (see
`tools/stream-concurrency.sh` and `tools/l2-bench.sh`).

## Direct play is not free in this stack

In a conventional Jellyfin deployment direct play costs almost nothing: the
file is on local disk and the kernel copies bytes. Here every streamed byte
is fetched from Usenet, TLS-decrypted, and yEnc-decoded in userspace by
NZBDAV before it reaches the client. Measured decode throughput is roughly
**75 MB/s per i5-12600KF core** (SIMD yEnc via rapidyenc; a `linux-arm64`
NEON build ships in the image, so ARM is not on a slow path).

Per stream that is still small: a 4K remux at ~54 Mbps is ~6.8 MB/s, about
0.09 of an i5 core, roughly 0.3 of a Pi 5 core.

## Raspberry Pi 5 (8 GB, NVMe HAT) budget

| Resource | What the stack needs | Pi 5 reality |
|---|---|---|
| CPU (streaming) | ~0.3 core per 4K direct-play stream | 3–4 concurrent 4K streams |
| RAM | the whole stack ran a 20-stream load test inside an 8 GB container | 8 GB model required |
| Network | a cold stream costs ~2x its bitrate: NNTP ingress and client egress share the NIC | 1 Gbps NIC caps at **~7–8 concurrent cold 4K streams** |
| Storage | L1 cache churn (tens of GB) plus SQLite WAL fsyncs | NVMe required; an SD card will be destroyed |

The NIC is the binding constraint, and it is effectively halved because the
bytes have to arrive (NNTP or L2/NFS) on the same interface they leave on.
A NAS-backed L2 cache does not help if the NAS sits behind the same NIC.

## What does not make sense on a Pi

1. **Any transcode fallback.** One client that cannot direct-play forces a
   transcode a Pi cannot do at 4K. This is not hypothetical: an image-based
   subtitle track (PGSSUB) auto-selected by a user profile forces burn-in
   transcoding. Set user subtitle mode to None or enforce direct-play-capable
   clients as policy.
2. **Bulk library imports.** Queue ingest (`NZBDAV_QUEUE_PARALLELISM`) and
   Jellyfin's metadata refresh both default to core-count parallelism. A 10k
   import that takes hours on 12 fast cores takes days on 4 slow ones. Run
   imports before migrating, or on the bigger box.
3. **A fast uplink.** A 5 Gbps connection is stranded behind a 1 Gbps NIC.
4. **SD-card storage** for `/config` or the L1 cache, in any configuration.

## Suggested settings on small hardware

```env
NZBDAV_CACHE_MAX_SIZE_GB=15     # default 10, big-box deployment uses 50
NZBDAV_QUEUE_PARALLELISM=1      # serial ingest; bulk imports do not belong here
```

Keep the file-descriptor ulimit from the compose file (65536): it protects
concurrent streams regardless of host size.

## The better answers

- **N100-class mini-PC** (~same price and power draw as a Pi 5 kit): faster
  cores, AVX2 yEnc, usually 2.5 GbE. It simply beats a Pi at this job.
- **Split roles.** The backend supports `NZBDAV_ROLE=streaming` /
  `NZBDAV_ROLE=ingest` against a shared Postgres. A small streaming-only node
  can live wherever the users are while ingest, bulk imports, and Sonarr/
  Radarr/Prowlarr stay on the big box. This is the reference architecture of
  the production VPS deployment (`docs/production-deployment.md`).

## Household verdict

For 1–4 direct-play streams: a Pi 5 (8 GB, NVMe) genuinely works. For many
concurrent users or a multi-gigabit uplink, the NIC disqualifies it before
CPU or RAM ever do.
