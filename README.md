<p align="center">
  <img width="1101" height="238" alt="NzbDav" src="https://github.com/user-attachments/assets/b14165f4-24ff-4abe-8af6-3ca852e781d4" />
</p>

# NzbDav

NzbDav is a WebDAV server that mounts and streams NZB content without first
downloading it. It also provides a SABnzbd-compatible API and a Jellyfin
plugin integration.

## Recommended fresh deployment

For a new Linux x86_64 (Ubuntu or Debian) server, use the canonical
**[all-in-one deployment guide](docs/all-in-one.md)**. It runs exactly five
services: NZBDAV, Jellyfin, Sonarr, Radarr, and Prowlarr. The guide covers
loopback-safe HTTP, SSH/VPN access, first-run onboarding, backups, recovery,
troubleshooting, and the optional NVIDIA override.

The stack needs no rclone, manual Arr API-key copying, request manager, or
reverse proxy. Provider and indexer accounts are external prerequisites and
are entered in the NZBDAV wizard.

To cache far more than local disk holds, see the optional
**[L2 segment cache](docs/l2-cache.md)**, which can be backed by a mounted
disk, a NAS over NFS, or S3.

Wondering whether a Raspberry Pi or mini-PC is enough? See
**[deploying on small hardware](docs/small-hardware.md)** for measured
sizing and what not to run there.

## Standalone NZBDAV

See the **[standalone setup guide](docs/setup-guide.md)** for a persistent
standalone container. Its example publishes exactly
`127.0.0.1:3000:3000`; use SSH forwarding or HTTPS termination for remote
access. Do not expose plain HTTP directly to the Internet.

A minimal non-persistent trial is (the published port is loopback-only):

```bash
docker run --rm -it -p 127.0.0.1:3000:3000 nzbdav/nzbdav:alpha
```

Use only legally obtained content. The project maintainers do not condone
copyright infringement.
