# All-in-One Jellyfin and NZBDAV Stack Design

**Date:** 2026-08-09
**Status:** Approved

## Goal

Provide a fresh-server deployment where an operator runs:

```bash
docker compose -f docker-compose.full-stack.yml up -d
```

The operator completes Jellyfin's native user setup, then uses NZBDAV's onboarding wizard to configure Usenet providers and indexers. NZBDAV automatically connects and configures Jellyfin, Sonarr, Radarr, and Prowlarr. No manual copying of API keys or configuration across applications is required.

## Scope

### Included

- Single-host Linux Docker deployment
- NZBDAV, Jellyfin, Sonarr, Radarr, and Prowlarr
- LAN/VPN access through service ports
- Existing NZBDAV support for multiple Usenet providers and Arr connections
- One or more indexers configured during onboarding
- Native Jellyfin welcome flow for administrator and user creation
- Automatic installation and configuration of the NZBDAV Jellyfin plugin
- Automatic cross-service discovery and configuration
- Optional NVIDIA Compose override for transcoding fallback; direct play remains the primary path
- Fresh persistent volumes only
- Retryable, idempotent setup

### Excluded

- Migration or adoption of existing Jellyfin/Arr installations
- Public ingress, domains, TLS, or reverse proxies
- Rclone; Jellyfin streams directly through the NZBDAV plugin
- Local media libraries
- Jellyseerr or other request-management applications
- Bundled Usenet-provider or indexer accounts
- Multi-node NZBDAV deployment

## Deployment Architecture

Add `docker-compose.full-stack.yml` with five long-running services. Add `docker-compose.nvidia.yml` as an optional override for hosts where the NVIDIA driver and Container Toolkit are installed:

| Service | Purpose |
|---|---|
| NZBDAV | Onboarding, NZB ingestion, virtual filesystem, and direct streaming |
| Jellyfin | Media server and native user management |
| Sonarr | TV-series automation |
| Radarr | Movie automation |
| Prowlarr | Indexer management and synchronization |

All services use a private Docker network and persistent named volumes. Their LAN ports remain published for setup, maintenance, and direct use. Service-to-service configuration uses Docker DNS names rather than host addresses.

NZBDAV mounts the Sonarr, Radarr, and Prowlarr configuration volumes read-only at fixed bootstrap paths. This allows it to discover their automatically generated API keys without requiring the operator to copy secrets. NZBDAV does not receive Docker socket access.

A small Jellyfin image layer installs the existing NZBDAV plugin at build time. Jellyfin receives persistent configuration and cache volumes plus a plugin-managed library directory. The base deployment has no GPU requirement and always supports direct play and CPU fallback. Operators with a prepared NVIDIA host enable device access by including `docker-compose.nvidia.yml`; Compose cannot safely auto-install host drivers or dynamically add devices.

On first startup, NZBDAV generates its master encryption key and internal frontend/backend key using cryptographically secure randomness and persists them under `/config`. Explicit environment values remain supported as operator overrides.

## First-Run Experience

1. The operator starts the Compose project and opens NZBDAV.
2. NZBDAV's onboarding page displays readiness for Jellyfin, Sonarr, Radarr, and Prowlarr.
3. NZBDAV links to Jellyfin's native welcome flow, where the operator creates the Jellyfin administrator and any desired users.
4. After Jellyfin setup, the operator returns to NZBDAV and submits the Jellyfin administrator credentials once.
5. NZBDAV authenticates to Jellyfin, obtains a revocable API key, and creates its local administrator with the same username and password. The raw password is not logged or retained; NZBDAV stores only its normal password hash.
6. The authenticated NZBDAV wizard presents the existing provider model so the operator can add and test one or more Usenet providers.
7. The wizard allows one or more Newznab-compatible indexers to be added and tested.
8. The operator reviews the detected services and starts automatic configuration.
9. The completion screen reports `Ready` or names the exact failed step and offers `Retry`.

The wizard preserves existing NZBDAV provider and Arr capabilities rather than introducing a reduced deployment-only model.

## Automatic Configuration

After authentication and successful connection tests, NZBDAV performs these idempotent operations:

1. Read generated Sonarr, Radarr, and Prowlarr API keys from their read-only configuration mounts.
2. Add or update the supplied indexers in Prowlarr.
3. Add or update Sonarr and Radarr as Prowlarr applications.
4. Register NZBDAV as a SABnzbd-compatible download client in Sonarr and Radarr using its internal Docker address and API key.
5. Add Sonarr and Radarr to NZBDAV's existing Arr configuration.
6. Create Movies and TV root directories and configure the corresponding Arr root folders.
7. Apply the repository's recommended NZBDAV queue-handling defaults.
8. Configure the preinstalled Jellyfin plugin with NZBDAV's internal URL and a dedicated API key.
9. Create Movies and TV Jellyfin libraries backed by plugin-generated `.strm` files.
10. Trigger initial plugin synchronization and verify an end-to-end health report.

Resources are matched by stable names and internal URLs. Rerunning setup updates matching resources rather than creating duplicates. The automation never deletes unrelated user configuration.

## Backend Design

Extend the existing onboarding API instead of introducing a separate bootstrap container.

The backend owns:

- Service readiness checks
- Jellyfin one-time authentication
- Local administrator creation
- Arr API-key discovery
- Provider and indexer connection tests
- Cross-service API calls
- Step state and retry behavior
- Final verification

Setup follows two authorization phases:

1. Before a local administrator exists, only readiness and Jellyfin-authentication/account-creation operations are available.
2. Immediately after account creation, all remaining setup calls require the authenticated NZBDAV session.

Deployment-specific addresses and read-only configuration paths are supplied through fixed Compose environment settings. The setup API must not accept arbitrary internal service URLs from an unauthenticated client.

Persist only durable configuration and completion state. Do not persist transient progress that can be recomputed from the target services. Each operation checks current external state before applying a change.

## Frontend Design

Convert `frontend/app/routes/onboarding/route.tsx` from the current single registration form into a focused multi-step flow:

1. **Services** — health status and link to Jellyfin setup
2. **Jellyfin** — one-time administrator authentication
3. **Usenet** — existing provider fields and connection tests
4. **Indexers** — repeatable Newznab URL/API-key fields and tests
5. **Configure** — detected-service summary and progress
6. **Complete** — end-to-end status and links to Jellyfin/NZBDAV

The UI submits secrets only to server actions. Secret values must never be returned to the browser after submission. Progress is based on named setup steps, and failed steps display actionable upstream errors without exposing credentials.

## Secrets and Security

- Generate NZBDAV bootstrap secrets with cryptographically secure randomness.
- Persist the NZBDAV master key with owner-only filesystem permissions.
- Encrypt provider, indexer, Jellyfin, and Arr API keys through NZBDAV's existing encrypted configuration mechanism.
- Never log passwords, API keys, provider usernames, or provider connection strings.
- Keep Arr configuration mounts read-only.
- Do not mount `/var/run/docker.sock`.
- Use dedicated API keys where supported rather than reusing administrator credentials.
- Discard the raw Jellyfin password after authentication and local password hashing.
- Reject setup mutations after onboarding unless the caller is authenticated.
- Bound all external calls with timeouts and cancellation.

## Failure and Recovery

Every setup step returns one of: `pending`, `running`, `complete`, `warning`, or `failed`.

- Service startup delays remain retryable and do not fail onboarding permanently.
- Invalid external credentials identify the affected provider/indexer without echoing secrets.
- Missing or malformed Arr configuration files identify the service and expected mount.
- Unsupported Jellyfin/plugin versions stop before library creation and explain the compatibility issue.
- The base deployment starts without NVIDIA support. When the NVIDIA override is selected, missing host drivers or Container Toolkit is reported as a host-prerequisite error by Docker.
- Container restarts resume by inspecting actual service state.
- Retry starts at the first incomplete step.
- Completed steps are safe to run again.
- Existing unrelated service configuration is retained.

## Validation

### Automated tests

- Setup authorization before and after local administrator creation
- Setup-state transitions and retry behavior
- Secret encryption and log redaction
- Jellyfin authentication, API-key creation, plugin configuration, and library creation using a fake HTTP server
- Sonarr, Radarr, and Prowlarr API-key discovery from representative configuration files
- Idempotent create/update behavior for all external resources
- Provider and indexer connection failures
- Missing/unhealthy service behavior
- CPU/direct-play behavior in the base deployment

### Deployment checks

- `docker compose -f docker-compose.full-stack.yml config` succeeds.
- A fresh-volume smoke test reaches healthy state for all five services.
- The full onboarding flow completes without manually copying an API key.
- A second configuration run creates no duplicate applications, indexers, download clients, root folders, or libraries.
- Jellyfin sees plugin-generated Movies and TV libraries.
- An NZB queued through Sonarr or Radarr becomes streamable through Jellyfin.
- Direct play succeeds in the base deployment; the NVIDIA override is separately verified on a compatible Linux host.

## Documentation

Add a concise fresh-server guide covering:

- Docker Engine and Compose prerequisites
- Recommended Ubuntu/Debian host setup
- Optional NVIDIA Container Toolkit installation
- Starting and stopping the stack
- The Jellyfin welcome-flow handoff
- Persistent volume backup and reset commands
- LAN ports
- Expected external Usenet-provider and indexer credentials

Public HTTPS and reverse-proxy guidance remains a future deployment extension.