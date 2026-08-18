#!/bin/sh
# Focused deployment tests for bootstrap-secrets.sh. Run as root on Linux.
set -eu

ROOT=$(CDPATH= cd -- "$(dirname "$0")" && pwd)
SCRIPT=$ROOT/bootstrap-secrets.sh
MASTER='VwJb7F5zWKx30P0yOsJJ1+ZvSEEhN9wJNq+2+PTnsOg='
MASTER2='BwJb7F5zWKx30P0yOsJJ1+ZvSEEhN9wJNq+2+PTnsOg='
API='api-override-with-more-than-16-chars'
API2='api-override-rotated-with-more-than-16-chars'
SESSION='session-override-with-more-than-16-chars'
SESSION2='session-override-rotated-with-more-than-16-chars'
TMP=${TMPDIR:-/tmp}/nzbdav-bootstrap-tests.$$
mkdir -p "$TMP"
trap 'rm -rf "$TMP"' EXIT HUP INT TERM

run_bootstrap() {
    config=$1
    shift
    env -u NZBDAV_MASTER_KEY -u FRONTEND_BACKEND_API_KEY -u SESSION_KEY \
        CONFIG_PATH=$config "$@" sh -c ". '$SCRIPT'"
}

fresh=$TMP/fresh
mkdir "$fresh"
run_bootstrap "$fresh" env
secret_dir=$fresh/bootstrap-secrets
[ "$(stat -c '%u:%a' "$secret_dir")" = 0:700 ]
for file in nzbdav-master-key frontend-backend-api-key session-key; do
    [ "$(stat -c '%u:%a' "$secret_dir/$file")" = 0:600 ]
done
master_inode=$(stat -c '%i' "$secret_dir/nzbdav-master-key")
api_inode=$(stat -c '%i' "$secret_dir/frontend-backend-api-key")
session_inode=$(stat -c '%i' "$secret_dir/session-key")
run_bootstrap "$fresh" env
[ "$master_inode" = "$(stat -c '%i' "$secret_dir/nzbdav-master-key")" ]
[ "$api_inode" = "$(stat -c '%i' "$secret_dir/frontend-backend-api-key")" ]
[ "$session_inode" = "$(stat -c '%i' "$secret_dir/session-key")" ]

# A production-shaped /config + /config/data volume migrates every known
# backend state path while retaining the root-only control plane.
legacy=$TMP/legacy-volume
mkdir -p "$legacy/data" "$legacy/blobs/aa/bb" "$legacy/data-protection" "$legacy/stream-cache"
printf blob >"$legacy/blobs/aa/bb/item"
printf protection >"$legacy/data-protection/key"
printf snapshot >"$legacy/content-index.snapshot.json"
printf backup >"$legacy/content-index.snapshot.backup.json"
printf scratch >"$legacy/content-index.snapshot.json.tmp"
printf cache >"$legacy/stream-cache/segment"
printf journal >"$legacy/db.sqlite-journal"
run_bootstrap "$legacy/data" env
for migrated in blobs/aa/bb/item data-protection/key content-index.snapshot.json content-index.snapshot.backup.json content-index.snapshot.json.tmp stream-cache/segment db.sqlite-journal; do
    [ -f "$legacy/data/$migrated" ]
done
[ ! -e "$legacy/blobs" ] && [ ! -e "$legacy/data-protection" ] && [ ! -e "$legacy/stream-cache" ]
[ "$(stat -c '%u:%a' "$legacy")" = 0:755 ]
[ "$(stat -c '%u:%a' "$legacy/data")" = 0:700 ]

# A same-filesystem bind mount must be rejected from the legacy tree even
# though stat(2) reports the same device. The names exercise every mountinfo
# escape that can be represented portably here (space, backslash, and tab).
# Unprivileged CI may not have CAP_SYS_ADMIN; the final Alpine suite runs this
# section privileged and therefore exercises the actual kernel mount table.
mount_legacy=$TMP/mount-legacy
mount_source=$TMP/mount-source
mount_space="$mount_legacy/blobs/foo bar"
mount_backslash="$mount_legacy/blobs/foo\\bar"
mount_tab="$mount_legacy/blobs/$(printf 'foo\\bar\t')"
mkdir -p "$mount_legacy/data" "$mount_legacy/blobs" "$mount_space" "$mount_backslash" "$mount_tab" "$mount_source"
printf source >"$mount_source/sentinel"
chown 0:0 "$mount_source" "$mount_source/sentinel"
chmod 755 "$mount_source"
chmod 600 "$mount_source/sentinel"
mounted_space=0
mounted_backslash=0
mounted_tab=0
if command -v mount >/dev/null 2>&1 && mount --bind "$mount_source" "$mount_space" 2>/dev/null; then
    mounted_space=1
    if mount --bind "$mount_source" "$mount_backslash" 2>/dev/null; then
        mounted_backslash=1
        if mount --bind "$mount_source" "$mount_tab" 2>/dev/null; then
            mounted_tab=1
        fi
    fi
fi
if [ "$mounted_space" -eq 1 ] && [ "$mounted_backslash" -eq 1 ] && [ "$mounted_tab" -eq 1 ]; then
    source_before=$(stat -c '%u:%a:%h' "$mount_source/sentinel")
    if CONFIG_PATH="$mount_legacy/data" sh -c ". '$SCRIPT'" >/dev/null 2>&1; then
        exit 1
    fi
    [ "$source_before" = "$(stat -c '%u:%a:%h' "$mount_source/sentinel")" ]
    [ -d "$mount_legacy/blobs" ]
    [ ! -e "$mount_legacy/data/blobs" ]
    umount "$mount_tab"
    umount "$mount_backslash"
    umount "$mount_space"
else
    [ "$mounted_tab" -eq 0 ] || umount "$mount_tab"
    [ "$mounted_backslash" -eq 0 ] || umount "$mount_backslash"
    [ "$mounted_space" -eq 0 ] || umount "$mount_space"
fi
rm -rf "$mount_legacy" "$mount_source"

# Same-value requests are exported but are true no-ops on disk.
same_master=$(cat "$secret_dir/nzbdav-master-key")
same_api=$(cat "$secret_dir/frontend-backend-api-key")
same_session=$(cat "$secret_dir/session-key")
same_master_stat=$(stat -c '%i:%Y:%s' "$secret_dir/nzbdav-master-key")
same_api_stat=$(stat -c '%i:%Y:%s' "$secret_dir/frontend-backend-api-key")
same_session_stat=$(stat -c '%i:%Y:%s' "$secret_dir/session-key")
CONFIG_PATH=$fresh NZBDAV_MASTER_KEY=$same_master FRONTEND_BACKEND_API_KEY=$same_api SESSION_KEY=$same_session \
    sh -c ". '$SCRIPT'"
[ "$same_master_stat" = "$(stat -c '%i:%Y:%s' "$secret_dir/nzbdav-master-key")" ]
[ "$same_api_stat" = "$(stat -c '%i:%Y:%s' "$secret_dir/frontend-backend-api-key")" ]
[ "$same_session_stat" = "$(stat -c '%i:%Y:%s' "$secret_dir/session-key")" ]

# Every requested value is compared with the request captured before reading
# persisted state; all three files must be replaced, not merely re-exported.
old_api_inode=$(stat -c '%i' "$secret_dir/frontend-backend-api-key")
old_session_inode=$(stat -c '%i' "$secret_dir/session-key")
CONFIG_PATH=$fresh NZBDAV_MASTER_KEY=$MASTER FRONTEND_BACKEND_API_KEY=$API SESSION_KEY=$SESSION \
    sh -c ". '$SCRIPT'; bootstrap_promote_master_key"
CONFIG_PATH=$fresh NZBDAV_MASTER_KEY=$MASTER2 FRONTEND_BACKEND_API_KEY=$API2 SESSION_KEY=$SESSION2 \
    sh -c ". '$SCRIPT'; test \"\$BOOTSTRAP_MASTER_ROTATION_REQUIRED\" = 1; bootstrap_promote_master_key"

# A persisted stage survives a failed first attempt and is reused on restart.
CONFIG_PATH=$fresh NZBDAV_MASTER_KEY=$MASTER FRONTEND_BACKEND_API_KEY=$API2 SESSION_KEY=$SESSION2 \
    sh -c ". '$SCRIPT'"
CONFIG_PATH=$fresh sh -c ". '$SCRIPT'; test \"\$BOOTSTRAP_MASTER_ROTATION_REQUIRED\" = 1; test \"\$BOOTSTRAP_STAGED_MASTER_KEY\" = \"$MASTER\""
[ -f "$secret_dir/nzbdav-master-key.pending" ]
CONFIG_PATH=$fresh sh -c ". '$SCRIPT'; bootstrap_promote_master_key"
[ "$(cat "$secret_dir/nzbdav-master-key")" = "$MASTER" ]
[ "$(cat "$secret_dir/frontend-backend-api-key")" = "$API2" ]
[ "$(cat "$secret_dir/session-key")" = "$SESSION2" ]
[ "$old_api_inode" != "$(stat -c '%i' "$secret_dir/frontend-backend-api-key")" ]
[ "$old_session_inode" != "$(stat -c '%i' "$secret_dir/session-key")" ]

# A failed prepare never replaces an existing destination.
old_inode=$(stat -c '%i' "$secret_dir/nzbdav-master-key")
if CONFIG_PATH=$fresh NZBDAV_MASTER_KEY=invalid sh -c ". '$SCRIPT'" >/dev/null 2>&1; then exit 1; fi
[ "$old_inode" = "$(stat -c '%i' "$secret_dir/nzbdav-master-key")" ]

# Every atomic-promotion failure is deterministic and restartable. The old
# canonical master/API/session files remain byte-for-byte durable, the staged
# candidate remains private, and no temporary file is left behind. This uses
# the real hooks in bootstrap-secrets.sh rather than source-text assertions.
run_promotion_failure_case() {
    hook=$1
    case_dir=$TMP/failure-$hook
    mkdir -p "$case_dir"
    CONFIG_PATH=$case_dir NZBDAV_MASTER_KEY=$MASTER FRONTEND_BACKEND_API_KEY=$API SESSION_KEY=$SESSION \
        sh -c ". '$SCRIPT'" >/dev/null 2>&1
    CONFIG_PATH=$case_dir NZBDAV_MASTER_KEY=$MASTER2 sh -c ". '$SCRIPT'" >/dev/null 2>&1
    case_secret_dir=$case_dir/bootstrap-secrets
    old_master=$(cat "$case_secret_dir/nzbdav-master-key")
    old_api=$(cat "$case_secret_dir/frontend-backend-api-key")
    old_session=$(cat "$case_secret_dir/session-key")
    staged=$(cat "$case_secret_dir/nzbdav-master-key.pending")
    [ "$staged" = "$MASTER2" ]

    if env CONFIG_PATH=$case_dir "BOOTSTRAP_TEST_FAIL_$hook=1" sh -c ". '$SCRIPT'; bootstrap_promote_master_key" >/dev/null 2>&1; then
        exit 1
    fi
    [ "$(cat "$case_secret_dir/nzbdav-master-key")" = "$old_master" ]
    [ "$(cat "$case_secret_dir/frontend-backend-api-key")" = "$old_api" ]
    [ "$(cat "$case_secret_dir/session-key")" = "$old_session" ]
    [ "$(cat "$case_secret_dir/nzbdav-master-key.pending")" = "$staged" ]
    [ "$(wc -c <"$case_secret_dir/nzbdav-master-key")" -gt 0 ]
    [ "$(wc -c <"$case_secret_dir/nzbdav-master-key.pending")" -gt 0 ]
    [ "$(find "$case_secret_dir" -maxdepth 1 -name '.secret.*' -print -quit)" = '' ]
    [ ! -e "$case_secret_dir/.master-key.rollback" ]

    # A clean restart consumes the retained stage and converges without
    # exposing any secret in the test output.
    CONFIG_PATH=$case_dir sh -c ". '$SCRIPT'; bootstrap_promote_master_key" >/dev/null 2>&1
    [ "$(cat "$case_secret_dir/nzbdav-master-key")" = "$MASTER2" ]
    [ "$(cat "$case_secret_dir/frontend-backend-api-key")" = "$old_api" ]
    [ "$(cat "$case_secret_dir/session-key")" = "$old_session" ]
    [ ! -e "$case_secret_dir/nzbdav-master-key.pending" ]
    [ ! -e "$case_secret_dir/.master-key.rollback" ]
    [ "$(wc -c <"$case_secret_dir/nzbdav-master-key")" -gt 0 ]
    [ "$(stat -c '%u:%a:%h' "$case_secret_dir/nzbdav-master-key")" = 0:600:1 ]
}
run_promotion_failure_case WRITE
run_promotion_failure_case RENAME
run_promotion_failure_case FSYNC
run_promotion_failure_case FILE_FSYNC
run_promotion_failure_case DIR_FSYNC
run_promotion_failure_case UNLINK

# Symlink substitution is rejected before any secret is read.
symlink_dir=$TMP/symlink-dir
mkdir -p "$symlink_dir"
ln -s "$TMP" "$symlink_dir/bootstrap-secrets"
if CONFIG_PATH=$symlink_dir sh -c ". '$SCRIPT'" >/dev/null 2>&1; then exit 1; fi

# Canonical secrets and pending stages cannot be hardlinked, even when their
# mode and ownership otherwise look private.
hardlink_dir=$TMP/hardlink-dir
mkdir -p "$hardlink_dir"
run_bootstrap "$hardlink_dir" env
ln "$hardlink_dir/bootstrap-secrets/frontend-backend-api-key" "$hardlink_dir/api-copy"
if CONFIG_PATH=$hardlink_dir sh -c ". '$SCRIPT'" >/dev/null 2>&1; then exit 1; fi

# Production entrypoint has fixed image paths and performs real maintenance
# before promotion; source/path override hooks are forbidden.
entrypoint=$ROOT/entrypoint.sh
grep -q '^BOOTSTRAP_SCRIPT=/bootstrap-secrets.sh$' "$entrypoint"
grep -q -- '--encryption-maintenance' "$entrypoint"
! grep -q 'BOOTSTRAP_SCRIPT=\${' "$entrypoint"
! grep -q 'BACKEND_DIR=\${' "$entrypoint"
! grep -q 'FRONTEND_DIR=\${' "$entrypoint"

echo 'bootstrap-secrets tests passed'
