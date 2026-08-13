#!/bin/sh
# Focused plugin restage crash-recovery tests for jellyfin-stack entrypoint.
set -eu

ROOT=$(CDPATH= cd -- "$(dirname "$0")" && pwd)
SCRIPT="$ROOT/entrypoint.sh"
TMP=${TMPDIR:-/tmp}/nzbdav-jellyfin-stack-entrypoint-tests.$$
mkdir -p "$TMP"
trap 'cleanup' EXIT HUP INT TERM

cleanup() {
    set +eu
    rm -rf "$TMP"
}

fail() {
    echo "nzbdav-jellyfin-stack: $*" >&2
    exit 1
}

if [ ! -x "$SCRIPT" ]; then
    fail "entrypoint script missing: $SCRIPT"
fi
[ "$(id -u)" -eq 0 ] || fail 'entrypoint staging tests require root'

for mount in /config /cache /media/nzbdav; do
    [ -d "$mount" ] && [ ! -L "$mount" ] || fail "required runtime mount is missing: $mount"
    awk -v target="$mount" '$5 == target { found = 1 }
        END { exit(found ? 0 : 1) }' /proc/self/mountinfo \
        || fail "required runtime mount is not mounted: $mount"
done

expected_dll=Jellyfin.Plugin.Nzbdav.dll
expected_meta=meta.json
plugin_install_path=/config/plugins/.Nzbdav.install.state1
plugin_stale_path=/config/plugins/.Nzbdav.stale.state1

prepare_source_plugin() {
    payload=$1
    mkdir -p /opt/nzbdav-plugin
    printf '%s' "$payload" > /opt/nzbdav-plugin/$expected_dll
    printf '{"payload":"%s"}\n' "$payload" > /opt/nzbdav-plugin/$expected_meta
    (cd /opt/nzbdav-plugin && sha256sum "$expected_dll" "$expected_meta" | sort) > /opt/nzbdav-plugin.manifest.sha256
}

reset_mount_contents() {
    mount=$1
    [ -d "$mount" ] && [ ! -L "$mount" ] || fail "unsafe runtime mount: $mount"
    # Delete only children. In particular, never pass a mounted directory to
    # rm: this preserves the tmpfs boundary while also removing dotfiles and
    # dangling symlinks without following them.
    find -P "$mount" -mindepth 1 -maxdepth 1 -exec rm -rf -- '{}' +
}

reset_runtime() {
    reset_mount_contents /config
    reset_mount_contents /cache
    reset_mount_contents /media/nzbdav
    rm -rf -- /opt/nzbdav-plugin /opt/nzbdav-plugin.manifest.sha256
    mkdir -p /opt
}

artifact_count() {
    find /config/plugins -maxdepth 1 -type d \( -name '.Nzbdav.install.*' -o -name '.Nzbdav.stale.*' \) | wc -l
}

assert_plugin_payload() {
    expected=$1
    actual=
    actual=$(cat /config/plugins/Nzbdav/$expected_dll)
    [ "$actual" = "$expected" ] || fail "Unexpected plugin payload: '$actual' != '$expected'"
}

leave_old_plugin_tree_stale() {
    [ -d /config/plugins/Nzbdav ] || fail 'expected installed old plugin tree'
    [ -f /config/plugins/Nzbdav/$expected_meta ] || fail 'old plugin metadata is missing'
    [ -f /config/plugins/Nzbdav/.nzbdav-artifact.manifest.sha256 ] \
        || fail 'old plugin operation manifest is missing'
    [ -f /config/plugins/Nzbdav/.nzbdav-artifact.marker ] \
        || fail 'old plugin operation marker is missing'
}

run_entrypoint() {
    crash_point=${1-}
    printf '0\n' > "$TMP/mv.state"
    if [ -n "$crash_point" ]; then
        CRASH_ON_MV=$crash_point MV_STATE_FILE="$TMP/mv.state" PATH="$TMP/bin:$PATH" "$SCRIPT" >/dev/null 2>&1
    else
        unset CRASH_ON_MV
        MV_STATE_FILE="$TMP/mv.state" PATH="$TMP/bin:$PATH" "$SCRIPT" >/dev/null 2>&1
    fi
}

assert_crash_at_point() {
    point=$1
    if run_entrypoint "$point"; then
        fail "expected crash at point $point"
    fi
}

assert_not_crash() {
    if ! run_entrypoint "${1-}"; then
        fail "unexpected startup failure at point ${1-}"
    fi
}

mkdir -p "$TMP/bin"
cat > "$TMP/bin/mv" <<'EOF'
#!/bin/sh
state_file=${MV_STATE_FILE:-/tmp/nzbdav-mv-state}
count=0
if [ -f "$state_file" ]; then
    count=$(cat "$state_file")
fi
count=$((count + 1))
printf '%s\n' "$count" > "$state_file"
/bin/mv "$@"
status=$?
if [ "$status" -ne 0 ]; then
    exit "$status"
fi
if [ -n "$CRASH_ON_MV" ] && [ "$count" -eq "$CRASH_ON_MV" ]; then
    exit 1
fi
exit 0
EOF
chmod +x "$TMP/bin/mv"
cat > "$TMP/bin/setpriv" <<'EOF'
#!/bin/sh
exit 0
EOF
chmod +x "$TMP/bin/setpriv"

# The reset helper must remove dotfiles and links without ever removing the
# mounted directory itself or following a link out of it.
mkdir -p /config/.nzbdav-test-dotdir
ln -s /tmp /config/.nzbdav-test-symlink
reset_mount_contents /config
[ ! -e /config/.nzbdav-test-dotdir ] || fail 'dotfile directory survived mounted reset'
[ ! -L /config/.nzbdav-test-symlink ] || fail 'symlink survived mounted reset'
[ -d /config ] || fail 'mounted config directory was removed'

# Initial successful install.
reset_runtime
prepare_source_plugin 'v1'
run_entrypoint
assert_plugin_payload v1

# Old->stale crash point (first mv) recovers from staged install artifact.
prepare_source_plugin 'v2'
leave_old_plugin_tree_stale
assert_crash_at_point 1
[ -d "$plugin_install_path" ] || fail 'missing install artifact after crash'
[ -d "$plugin_stale_path" ] || fail 'missing stale artifact after crash'
[ ! -d /config/plugins/Nzbdav ] || fail 'plugin directory should be absent during old->stale crash'
assert_not_crash
assert_plugin_payload v2
[ "$(artifact_count)" -eq 0 ] || fail 'staging artifacts not cleaned after recovery'

# Replacement install crash point (second mv) keeps old stale artifact and should clean up on restart.
prepare_source_plugin 'v3'
leave_old_plugin_tree_stale
assert_crash_at_point 2
[ -d "/config/plugins/Nzbdav" ] || fail 'plugin should be in place before cleanup crash point'
[ -d "$plugin_stale_path" ] || fail 'expected stale artifact after replacement crash point'
assert_not_crash
assert_plugin_payload v3
[ ! -d "$plugin_stale_path" ] || fail 'stale artifact not removed after recovery'

# An image upgrade must not validate an interrupted old operation against the
# new baked manifest. The old install/stale slots carry their own exact hash,
# owner, non-symlink payload, and operation marker; recovery then restages the
# newly baked payload deterministically.
reset_runtime
prepare_source_plugin 'v12'
assert_not_crash
prepare_source_plugin 'v13'
leave_old_plugin_tree_stale
assert_crash_at_point 1
[ -d "$plugin_install_path" ] || fail 'missing old install artifact before image upgrade'
[ -d "$plugin_stale_path" ] || fail 'missing old stale artifact before image upgrade'
prepare_source_plugin 'v14'
if cmp -s "$plugin_install_path/.nzbdav-artifact.manifest.sha256" /opt/nzbdav-plugin.manifest.sha256; then
    fail 'old install artifact was incorrectly validated against current baked manifest'
fi
assert_not_crash
assert_plugin_payload v14
[ "$(artifact_count)" -eq 0 ] || fail 'old artifacts survived upgraded restage'

# The old canonical side of a completed replacement is also recovered from
# its local proof before the current image is staged.
reset_runtime
prepare_source_plugin 'v15'
assert_not_crash
prepare_source_plugin 'v16'
leave_old_plugin_tree_stale
assert_crash_at_point 2
[ -d /config/plugins/Nzbdav ] || fail 'canonical replacement missing before image upgrade'
[ -d "$plugin_stale_path" ] || fail 'missing old canonical stale artifact before image upgrade'
prepare_source_plugin 'v17'
if cmp -s /config/plugins/Nzbdav/.nzbdav-artifact.manifest.sha256 /opt/nzbdav-plugin.manifest.sha256; then
    fail 'old canonical artifact unexpectedly matches current baked manifest'
fi
assert_not_crash
assert_plugin_payload v17
[ "$(artifact_count)" -eq 0 ] || fail 'old canonical artifact survived upgraded restage'

# Repeated crash injection remains bounded to fixed artifact names for both crash points.
reset_runtime
prepare_source_plugin 'v4'
assert_not_crash
prepare_source_plugin 'v5'
leave_old_plugin_tree_stale
assert_crash_at_point 1
first_artifacts=$(artifact_count)
[ "$first_artifacts" -gt 0 ] || fail 'expected at least one artifact during crash recovery test'
assert_not_crash
prepare_source_plugin 'v6'
leave_old_plugin_tree_stale
assert_crash_at_point 1
second_artifacts=$(artifact_count)
[ "$second_artifacts" -eq "$first_artifacts" ] || fail "artifact count changed under repeated crashes: $first_artifacts -> $second_artifacts"
assert_not_crash
assert_plugin_payload v6

reset_runtime
prepare_source_plugin 'v7'
assert_not_crash
prepare_source_plugin 'v8'
leave_old_plugin_tree_stale
assert_crash_at_point 2
first_artifacts=$(artifact_count)
[ "$first_artifacts" -gt 0 ] || fail 'expected at least one artifact during repeated crash test (replacement)'
assert_not_crash
prepare_source_plugin 'v9'
leave_old_plugin_tree_stale
assert_crash_at_point 2
second_artifacts=$(artifact_count)
[ "$second_artifacts" -eq "$first_artifacts" ] || fail "artifact count changed under repeated replacement crashes: $first_artifacts -> $second_artifacts"
assert_not_crash
assert_plugin_payload v9

# Foreign preservation: exact state1 names and symlinks are preserved and startup fails on conflict.
reset_runtime
prepare_source_plugin 'v10'
rm -rf -- "$plugin_install_path" "$plugin_stale_path"
mkdir -p "$plugin_install_path"
printf 'foreign-dll' > "$plugin_install_path/$expected_dll"
printf 'foreign-meta' > "$plugin_install_path/$expected_meta"
(cd "$plugin_install_path" && sha256sum "$expected_dll" "$expected_meta" | sort) > "$plugin_install_path/.nzbdav-artifact.manifest.sha256"
printf 'not-nzbdav-plugin-artifact-v1\n' > "$plugin_install_path/.nzbdav-artifact.marker"
chown -R 1000:1000 "$plugin_install_path"
if run_entrypoint; then
    fail 'expected startup conflict with foreign exact state1 artifact'
fi
[ -f "$plugin_install_path/$expected_dll" ] || fail 'foreign exact install payload was removed'
[ "$(cat "$plugin_install_path/.nzbdav-artifact.marker")" = 'not-nzbdav-plugin-artifact-v1' ] \
    || fail 'foreign exact install marker was changed'

rm -rf -- "$plugin_install_path" "$plugin_stale_path"
mkdir -p "$plugin_install_path"
printf 'foreign-manual' > "$plugin_install_path/$expected_meta"
ln -s /tmp "$plugin_stale_path"
if run_entrypoint; then
    fail 'expected startup conflict with foreign exact state1 symlink'
fi
[ -f "$plugin_install_path/$expected_meta" ] || fail 'foreign exact install artifact was removed'
[ -L "$plugin_stale_path" ] || fail 'foreign exact stale artifact was removed'

# Fresh-volume success is still expected when no staged artifacts exist.
reset_runtime
prepare_source_plugin 'v11'
assert_not_crash
assert_plugin_payload v11
[ "$(artifact_count)" -eq 0 ] || fail 'fresh-volume staging left artifacts behind'

echo 'entrypoint staging tests passed'
