# Fresh all-in-one deployment

This is the canonical fresh-server guide for a Linux x86_64 (Ubuntu or
Debian) host. It runs exactly **five services**: `nzbdav`, `jellyfin`,
`sonarr`, `radarr`, and `prowlarr`. It is intentionally fresh and single-host:
do not add rclone, manual API keys, a request manager, or extra services to
this Compose project. Existing Arr/Jellyfin/NZBDAV installations are not
adopted by this guide; use a separate migration plan instead.

## Prerequisites

Install Docker Engine and the Docker Compose v2 plugin using the official
instructions:

- [Docker Engine on Ubuntu](https://docs.docker.com/engine/install/ubuntu/)
- [Docker Engine on Debian](https://docs.docker.com/engine/install/debian/)
- [Docker Compose plugin on Linux](https://docs.docker.com/compose/install/linux/)

Use `docker compose` (the v2 plugin), not the obsolete `docker-compose`
executable. Verify the installation:

```bash
docker version
docker compose version
```

You also need a Usenet provider account and a Newznab-compatible indexer
account. Those accounts are not supplied by this project.

## Install and start

```bash
git clone https://github.com/nzbdav/nzbdav-opensource.git
cd nzbdav-opensource
cat > .env <<'EOF'
# Safe browser-facing default: publish every service on host loopback only.
BIND_ADDRESS=127.0.0.1
EOF
chmod 600 .env
docker compose -f docker-compose.full-stack.yml config
docker compose -f docker-compose.full-stack.yml up -d --build
```

The Compose file has separate host-port variables. The container ports and
safe host-port defaults are:

| Service | Host variable/default | Container port |
| --- | --- | ---: |
| NZBDAV | `NZBDAV_PORT` / `3000` | `3000` |
| Jellyfin | `JELLYFIN_PORT` / `8096` | `8096` |
| Sonarr | `SONARR_PORT` / `8989` | `8989` |
| Radarr | `RADARR_PORT` / `7878` | `7878` |
| Prowlarr | `PROWLARR_PORT` / `9696` | `9696` |

The safe-default contract is that every published mapping is loopback-bound
(for example `127.0.0.1:3000:3000`). Keep the explicit
`BIND_ADDRESS=127.0.0.1` in the operator-created `.env` so the deployment
remains safe if a future Compose default changes. Compose does not promise to
create or maintain `.env`; if it is absent, shell/Compose defaults apply.
After any checkout update, inspect `docker compose
-f docker-compose.full-stack.yml config` and stop if a published host address
is not `127.0.0.1`.
A host-port variable changes only the host side; service-to-service URLs stay
`http://nzbdav:8080`, `http://jellyfin:8096`, `http://sonarr:8989`,
`http://radarr:7878`, and `http://prowlarr:9696` on the Compose network.

### Deliberate non-loopback HTTP opt-in

Do not bind these services to `0.0.0.0` or a LAN/VPN address for convenience.
If a temporary, trusted-network test specifically needs non-loopback HTTP,
make the risk explicit in `.env`:

```dotenv
BIND_ADDRESS=10.8.0.2
NZBDAV_INSECURE_DEV_COOKIES=true
```

`10.8.0.2` is an example; use the host's actual VPN interface address. The
explicit `NZBDAV_INSECURE_DEV_COOKIES=true` opt-in is required for deliberate
non-loopback HTTP. This does **not** provide TLS. HTTP remains HTTP; a VPN may
protect the network tunnel, but it does not make the application protocol
itself encrypted. Never use this mode on an untrusted network or the public
Internet.

### Remote access with an SSH local forward

Leave all publishes on loopback and forward only the ports needed by an
operator. From the remote client, run this exact command (replace the SSH
account and host):

```bash
ssh -N -T -o ExitOnForwardFailure=yes \
  -L 3000:127.0.0.1:3000 \
  -L 8096:127.0.0.1:8096 \
  -L 8989:127.0.0.1:8989 \
  -L 7878:127.0.0.1:7878 \
  -L 9696:127.0.0.1:9696 \
  admin@server.example
```

While that SSH process is running, use these **localhost** URLs on the remote
client:

- NZBDAV: `http://localhost:3000`
- Jellyfin: `http://localhost:8096`
- Sonarr: `http://localhost:8989`
- Radarr: `http://localhost:7878`
- Prowlarr: `http://localhost:9696`

The SSH tunnel protects the forwarded connection, but the applications still
speak HTTP at their endpoints. For browser-facing HTTPS, terminate TLS in a
separately secured reverse proxy, forward only to the intended loopback
service, and configure the public origin/cookie settings consistently. A VPN
is an alternative access boundary, not an HTTPS terminator.
This stack does not provide TLS or a reverse proxy; see the
[TLS reverse-proxy example](deployment/tls-reverse-proxy.md) and validate its
assumptions before adapting it.

## First run and onboarding

1. Open `http://localhost:8096` (or use the SSH URL above) and complete
   Jellyfin's **native welcome wizard**. Create its administrator and desired
   users first.
2. Open `http://localhost:3000` and follow the NZBDAV states in this order:
   **Services → Jellyfin → Usenet → Indexers → Configure → Ready/Repair**.
3. **Services** reports dependency readiness. It is not the final Ready state.
   If a dependency is still starting, wait and refresh.
4. **Jellyfin** asks for the Jellyfin administrator username and password.
   **Continue is allowed** from the readiness page after the native wizard;
   being on that page does not mean setup is complete.
5. NZBDAV authenticates to Jellyfin, verifies administrator privileges, creates
   or reuses the local NZBDAV administrator with the **same username and
   password**, and obtains a dedicated Jellyfin API key that can be revoked.
   NZBDAV stores a salted local password hash and that revocable key (encrypted
   at rest), discards the raw Jellyfin password after the handoff, and does not
   log it. On every later
   NZBDAV login, use those same local NZBDAV username/password credentials;
   the Jellyfin password is not retrievable from NZBDAV.
6. **Usenet** and **Indexers** collect drafts only. The fields and credentials
   are not validated or wired into the other services at those steps.
7. **Configure** is the action step: click **Run configuration**. Only this
   run validates the provider/indexer drafts, discovers the Arr keys from the
   read-only bootstrap mounts, and wires the managed download clients,
   Prowlarr applications, root folders, NZBDAV settings, Jellyfin plugin, and
   libraries.
8. **Ready** is shown only after the configuration run and live verification
   of all five services succeed. A completed marker or a previously visited
   page is not Ready. Failed work names a step and can be retried; use
   **Repair** for a completed installation that later becomes unhealthy; do
   not delete volumes to retry onboarding.

No Arr API-key copying is required. The plugin uses the internal Compose URL
and its managed credential to refresh short-lived signed direct-stream tokens;
Jellyfin streams from NZBDAV rather than from a copied media file. Playback is
primarily direct play; the base stack has no rclone mount or local media-copy
requirement.

## Storage, ownership, and secrets

The stack has exactly **eight physical named volumes** and the backup helper
creates exactly eight archives (one per volume):

`nzbdav_config`, `nzbdav_media`, `completed_downloads`, `jellyfin_config`,
`jellyfin_cache`, `sonarr_config`, `radarr_config`, and `prowlarr_config`.

`completed_downloads` is one shared volume mounted at
`/data/completed-downloads` in NZBDAV, Sonarr, and Radarr. Its direct category
roots are `/data/completed-downloads/tv` and
`/data/completed-downloads/movies`; setup creates and assigns those roots to
Sonarr and Radarr respectively. The completed-downloads archive preserves
both direct `tv/` and `movies/` trees; it is not two nested or cross-selected
volumes.

- `/config` is the root-only ownership boundary (root-owned, mode 0755), and
  `/config/bootstrap-secrets` is root-only (mode 0700). Bootstrap secrets and
  marker files are root-owned and mode 0600. Never make them writable by the
  host account or application process.
- `/config/data` is intentionally owned by `PUID:PGID` (defaults `1000:1000`)
  for the NZBDAV database and runtime data.
- NZBDAV initializes `/media/nzbdav` and the completed-download mounts as
  `PUID:PGID` (defaults `1000:1000`). Jellyfin and Arr use their configured
  runtime identities on their writable mounts; the shared media volume is not
  NZBDAV-exclusive. Do not recursively chown `/config`.
- Sonarr writes `/data/completed-downloads/tv` and Radarr writes
  `/data/completed-downloads/movies`. NZBDAV consumes those exact paths. The
  plugin/Jellyfin library roots are `/media/nzbdav/tv` and
  `/media/nzbdav/movies` under the shared `/media/nzbdav` volume.
- The generated master, API, and session secrets persist in
  `nzbdav_config`. Losing the master key or its volume makes encrypted settings
  unrecoverable; restore both from the same recovery set.
- The optional L2 segment cache at `/l2` is a **bind mount, not a named
  volume**, so the eight-volume count and the eight backup archives above are
  unchanged. It is deliberately excluded from backups: it is a cache and is
  fully reconstructible from Usenet. It is disabled unless `NZBDAV_L2_PATH` is
  set. See [L2 segment cache](l2-cache.md).
- Jellyfin's artwork can live on the same NAS: the Jellyfin service binds
  `${NZBDAV_ARTWORK_HOST_PATH:-/mnt/nas-artwork}` at `/metadata`, and becomes
  active only when the operator sets Jellyfin's metadata path (Dashboard →
  General) to `/metadata`. Artwork is plain image files, safe over NFS and
  regenerable from metadata providers, so it is also excluded from backups.
  `jellyfin.db` must stay on `/config`: SQLite WAL does not work over NFS.

Do not put provider passwords, indexer keys, generated secrets, or a master key
in Git, a public Compose file, shell history, or bug reports. Do not enable
shell tracing while handling secrets.

## Quiesced backup and restore

A backup must be quiesced: stop the project first, and do not write to its
volumes until the operation is complete. `down` without `-v` preserves them;
**never use `docker compose down -v` here**: `-v` is a destructive volume
reset, not a backup or restore prerequisite. The bounded helper below uses root
tar containers with numeric ownership, ACL, xattr, and mode preservation. It
resolves the exact Compose project label from the existing `nzbdav` container,
then resolves each backup set by exact Compose labels and requires the project
prefix. It refuses a running project, missing/duplicate volumes, missing
archives, and glob-like volume selection.

```bash
docker compose -f docker-compose.full-stack.yml stop
bash docs/support/full-stack-volume-backup.sh backup ./backups/full-stack
```

Keep the resulting exactly eight `*.tar` files encrypted and
access-controlled. They contain databases, credentials, media state, and
caches. Do not upload them to an issue or untrusted storage. Restore validates
all archive members—including traversal, symlink targets, numeric ownership,
and modes—before clearing any target volume.

Restore is destructive to the contents of the target volumes: it clears each
set before extraction. Confirm the backup directory and project are correct;
never run it against a live project or an unrelated project with a reused
backup directory. First stop the same Compose project and use the same
repository revision (and the original master key if it was supplied
externally):

```bash
docker compose -f docker-compose.full-stack.yml stop
bash docs/support/full-stack-volume-backup.sh restore ./backups/full-stack
docker compose -f docker-compose.full-stack.yml config
docker compose -f docker-compose.full-stack.yml up -d
```

Restore replaces the contents of each target volume. Validate the result before
calling it recovered:

```bash
docker compose -f docker-compose.full-stack.yml ps
docker compose -f docker-compose.full-stack.yml exec -T nzbdav sh -eu -c \
  'stat -c "%u:%a" /config /config/data /config/bootstrap-secrets &&
   test "$(stat -c "%u:%a" /config/bootstrap-secrets/nzbdav-master-key)" = "0:600"'
curl -fsS http://127.0.0.1:3000/
```

Then log in, check the NZBDAV/Jellyfin plugin, open one library item, and
confirm Arr/Prowlarr health. Test restoration periodically in a disposable
Compose project with uniquely named volumes; never test by adding `-v` to the
real project.

## Standalone master-key rotation

This procedure is deliberately not Compose-only. For a standalone container,
keep the same image, bind mount, PUID/PGID, and port, but recreate the container
with a one-shot key override. Do not print the key or use shell tracing:

```bash
set -eu
export NZBDAV_MASTER_KEY="$(openssl rand -base64 32)"
docker stop nzbdav
docker rm nzbdav
docker run -d --name nzbdav --restart unless-stopped \
  -e PUID="$(id -u)" -e PGID="$(id -g)" \
  -e NZBDAV_MASTER_KEY="$NZBDAV_MASTER_KEY" \
  -p 127.0.0.1:3000:3000 -v "$(pwd)/nzbdav:/config" \
  nzbdav/nzbdav:alpha
for attempt in $(seq 1 120); do
  if docker exec nzbdav sh -eu -c 'test ! -e /config/bootstrap-secrets/nzbdav-master-key.pending'; then break; fi
  sleep 1
done
docker exec nzbdav sh -eu -c 'test ! -e /config/bootstrap-secrets/nzbdav-master-key.pending'
docker rm -f nzbdav
docker run -d --name nzbdav --restart unless-stopped \
  -e PUID="$(id -u)" -e PGID="$(id -g)" \
  -p 127.0.0.1:3000:3000 -v "$(pwd)/nzbdav:/config" \
  nzbdav/nzbdav:alpha
unset NZBDAV_MASTER_KEY
```

If maintenance fails, stop there: retain the old config and the pending key,
keep the environment variable set, and resolve the reported error before
retrying. Never delete `/config` or the pending file. A lost old key requires a
verified config-and-key restore or a deliberate fresh installation.

For the Compose stack, use the same one-shot principle with
`NZBDAV_MASTER_KEY` in the current shell, `up --force-recreate` for NZBDAV,
verify the pending marker is absent, unset the variable, and recreate once more.
Do not rotate by deleting volumes.

## NVIDIA (optional)

The base stack needs no GPU and uses direct play/CPU fallback. For GPU
transcoding, install matching host drivers and the
[ NVIDIA Container Toolkit](https://docs.nvidia.com/datacenter/cloud-native/container-toolkit/latest/install-guide.html).
Verify the host and Docker runtime with these exact probes:

```bash
nvidia-smi
docker run --rm --gpus all nvidia/cuda:12.6.3-base-ubuntu22.04 nvidia-smi
```

Compose supports `gpus: all`; the checked-in NVIDIA override applies that
setting only to Jellyfin:

```bash
docker compose -f docker-compose.full-stack.yml \
  -f docker-compose.nvidia.yml up -d --build
```

Use both Compose files on every later lifecycle command for that deployment.
Docker cannot install drivers or the toolkit. Do not add the override merely
for direct play.

## Operations and troubleshooting

```bash
docker compose -f docker-compose.full-stack.yml ps
docker compose -f docker-compose.full-stack.yml logs --no-color --tail=200 nzbdav
docker compose -f docker-compose.full-stack.yml restart
```

Wait for all health checks before onboarding. `setup-run-busy` means another
bounded configuration run is active; wait, refresh Services, and retry rather
than running concurrent browser actions. If a Ready installation becomes
stale, use the UI's repair flow and re-authenticate with the Jellyfin
administrator credentials. Inspect only the named service logs and redact
passwords, API keys, tokens, private hostnames, and secret-bearing URLs.

This guide is Linux fresh-scope documentation for one host. It does not claim
public TLS, firewalling, VPN encryption, migration of existing services,
provider/indexer accounts, or multi-node operation. See
[production deployment](production-deployment.md) for the separate multi-node
contract.
