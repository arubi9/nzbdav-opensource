# Standalone NZBDAV setup

This guide is only for NZBDAV on a Linux x86_64 host. For a fresh Jellyfin +
Sonarr + Radarr + Prowlarr server, use the [canonical all-in-one
guide](all-in-one.md). Do not combine this container with
`docker-compose.full-stack.yml`.

Standalone NZBDAV provides WebDAV and a SABnzbd-compatible API. It does not
install Jellyfin, Arr applications, a provider, an indexer, rclone, or a
reverse proxy.

## Prerequisites

- Docker Engine and Docker Compose v2 (or an equivalent Docker runtime).
- A Usenet provider account.
- A non-root host UID/GID. `1000:1000` is the image default.

## Persistent container

From an otherwise empty directory:

```bash
mkdir -p nzbdav
sudo chown "$(id -u):$(id -g)" nzbdav
docker run -d --name nzbdav \
  --restart unless-stopped \
  -e PUID="$(id -u)" \
  -e PGID="$(id -g)" \
  -p 127.0.0.1:3000:3000 \
  -v "$(pwd)/nzbdav:/config" \
  nzbdav/nzbdav:alpha
```

Open `http://localhost:3000` on the host. For remote access, keep the
loopback publish and use SSH local forwarding:

```bash
ssh -N -T -o ExitOnForwardFailure=yes \
  -L 3000:127.0.0.1:3000 admin@server.example
```

Then use `http://localhost:3000` in the remote browser. The application endpoint is plain HTTP; SSH protects this forwarded
connection, while the standalone container itself provides no public TLS. For
browser-facing HTTPS, use a separately secured TLS reverse proxy (for example,
see [this deployment example](deployment/tls-reverse-proxy.md)). Do not change
the publish to a LAN/VPN address unless you deliberately accept insecure
non-loopback HTTP, set `NZBDAV_INSECURE_DEV_COOKIES=true`, and have a separately
secured network/TLS design. A VPN protects its tunnel; it does not turn the
application's HTTP into HTTPS.

Create the NZBDAV administrator, enter and test the provider in Settings, and
configure WebDAV authentication before connecting an external client. Keep
provider credentials and generated secrets out of Compose files, shell
transcripts, logs, and bug reports. Pin an image version instead of `alpha` for
production.

The persistent mount contains `/config/data` (the backend data root) and
`/config/bootstrap-secrets` (generated master, API, and session secrets). Do
not mount another volume inside `/config/data`.

## Master key and standalone rotation

On first boot, leaving `NZBDAV_MASTER_KEY` unset generates and persists a
stable 32-byte base64 key. Generate a deliberate key without shell tracing if
required:

```bash
openssl rand -base64 32
# pass the resulting single-line value as NZBDAV_MASTER_KEY
```

Rotation is a maintenance operation, not a routine restart. This exact
procedure recreates a **standalone Docker container**, not a Compose service:

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

If the maintenance run fails, stop immediately. Keep the old key/config and
the pending file, leave the variable set, and retry after fixing the reported
problem. Never delete the config directory or pending file. If the original
key is lost, restore the original config and key together or reset the
installation and configure it again.

## Ownership and recovery

Do not recursively chown the whole `/config` mount. The image keeps `/config`
and `/config/bootstrap-secrets` root-owned, with secret and marker files
root-owned and mode 0600; `PUID:PGID` intentionally owns `/config/data`. Back
up the directory while the container is stopped, encrypt the backup, and test
a restore in a disposable directory before relying on it. A backup without the
master key cannot recover encrypted settings.
