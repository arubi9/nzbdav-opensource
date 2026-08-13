# Jellyfin Quarantine Reconciliation Design

**Goal**

Keep the Jellyfin library mirror consistent with the NZBDAV manifest by quarantining stale NZBDAV-managed `.strm` and `.mediainfo.json` files instead of leaving drift behind.

**Problem**

The current `NzbdavLibrarySyncTask` only creates or refreshes active files. If content is renamed or removed from NZBDAV, the plugin leaves the old `.strm` and `.mediainfo.json` on disk. Jellyfin can keep scanning those stale entries, leading to ghost items and duplicated-looking entries after renames. The task also contains token-refresh logic that is dead because it writes API-key URLs rather than token URLs.

**Selected Approach**

Use the manifest as the single source of truth and perform local reconciliation in the plugin:

- If the manifest request returns `304 Not Modified`, do nothing.
- If the manifest changes:
  - Compute the expected active `.strm` relative paths from the current manifest video items.
  - Ensure each active `.strm` exists.
  - Ensure each active `.mediainfo.json` exists when `HasProbeData` is true.
  - Scan the library root for NZBDAV-managed `.strm` files.
  - Any managed `.strm` not in the expected set is stale and should be moved to `LibraryPath/.quarantine/...`.
  - If a matching `.mediainfo.json` exists beside the stale `.strm`, move it too.

**Safety Rules**

- Only quarantine files that are clearly NZBDAV-managed.
- A `.strm` counts as NZBDAV-managed only if its contents reference this plugin’s configured `NzbdavBaseUrl` and an `/api/stream/` URL.
- Preserve relative directory structure when moving files into `.quarantine`.
- Use a per-sync timestamped quarantine directory to avoid collisions and keep recovery simple.

**Token Logic**

- Remove the dead token-refresh branch in `NzbdavLibrarySyncTask`.
- This slice does not switch the plugin to signed token URLs; it only removes misleading code and reconciles stale files safely.

**Testing**

- Add plugin tests for:
  - identifying NZBDAV-managed `.strm` content
  - computing expected active relative `.strm` paths
  - deriving quarantine destinations
  - ignoring non-NZBDAV `.strm` files during reconciliation

**Out of Scope**

- Changing backend manifest or probe contracts
- Signed-token migration for plugin stream URLs
- Deleting quarantined files automatically

