#!/bin/sh
# Docker creates named volumes as root. Initialize them here, then replace
# this root-only bootstrap with Jellyfin under the configured identity.
set -eu

fail() { echo "nzbdav-jellyfin: $*" >&2; exit 1; }

puid=${PUID:-1000}
pgid=${PGID:-1000}
case "$puid" in ''|*[!0-9]*) fail "PUID must be numeric" ;; esac
case "$pgid" in ''|*[!0-9]*) fail "PGID must be numeric" ;; esac
[ "$puid" -gt 0 ] || fail "PUID must be a non-root numeric uid"
[ "$pgid" -gt 0 ] || fail "PGID must be a non-root numeric gid"
[ "$(id -u)" -eq 0 ] || fail "the entrypoint must start as root to initialize volumes"

# Validate a volume without following links or crossing a nested mount. The
# mount itself is allowed (it is the named-volume boundary); child mounts are
# not, because chowning one would escape the intended writable volume.
volume_tree_is_safe() {
    volume=$1
    require_mount=${2:-1}
    [ -d "$volume" ] && [ ! -L "$volume" ] || return 1
    # A named volume is the exact mountpoint. Reject nested mounts before any
    # ownership transition; checking only children would reject every named
    # volume because mountinfo records the mount as /config, not /config/.
    if [ "$require_mount" -eq 1 ]; then
        awk -v target="$volume" '
            $5 == target { root = 1 }
            index($5, target "/") == 1 { nested = 1 }
            END { exit(root && !nested ? 0 : 1) }
        ' /proc/self/mountinfo || return 1
    else
        awk -v target="$volume" 'index($5, target "/") == 1 { nested = 1 }
            END { exit(nested ? 1 : 0) }' /proc/self/mountinfo || return 1
    fi
    find -P "$volume" -xdev -exec /bin/sh -eu -c '
        root=$1; shift
        for item do
            [ ! -L "$item" ] && { [ -d "$item" ] || [ -f "$item" ]; } || exit 1
            # Config files may legitimately be hardlinked by Jellyfin; the
            # no-link and same-filesystem checks still prevent path escapes.
            [ "$(stat -c "%d" "$item" 2>/dev/null)" = "$(stat -c "%d" "$root" 2>/dev/null)" ] || exit 1
        done
    ' sh "$volume" {} +
}

# A root-owned marker avoids a recursive ownership transition on every start.
# A changed uid/gid/version, missing marker, or malformed marker causes the
# complete safe scan exactly once before the new marker is installed.
initialize_volume() {
    volume=$1
    marker="$volume/.nzbdav-ownership"
    mkdir -p -- "$volume"
    volume_tree_is_safe "$volume" || fail "unsafe volume tree: $volume"
    if [ -f "$marker" ] && [ ! -L "$marker" ] \
        && [ "$(stat -c '%u:%a:%h' "$marker" 2>/dev/null)" = "0:600:1" ] \
        && [ "$(cat "$marker" 2>/dev/null)" = "nzbdav-volume-v1:$puid:$pgid" ]; then
        return 0
    fi
    rm -f -- "$marker"
    volume_tree_is_safe "$volume" || fail "unsafe volume tree during ownership transition: $volume"
    find -P "$volume" -xdev -exec chown -h "$puid:$pgid" '{}' +
    printf 'nzbdav-volume-v1:%s:%s\n' "$puid" "$pgid" > "$marker"
    chown 0:0 "$marker"
    chmod 600 "$marker"
}

# Jellyfin rejects a virtual-folder create when its target does not exist.
# Seed the two fixed managed category roots before dropping privileges.
# NZBDAV does not depend on Jellyfin or a separately initialized container
# for these shared-volume paths.
for path in /config /cache /media/nzbdav /media/nzbdav/movies /media/nzbdav/tv; do
    [ ! -L "$path" ] || fail "$path must not be a symlink"
    mkdir -p -- "$path"
done
initialize_volume /config
initialize_volume /cache
initialize_volume /media/nzbdav

# The ownership marker makes initialize_volume a one-time recursive scan, so a
# path created in the shared volume after that first run stays root-owned. The
# NZBDAV container starts first and seeds these same category roots, which left
# the plugin unable to write its library:
#   IOException: Failed to create '/media/nzbdav/movies/<release>' ... errno=13
# Both roots are fixed and shallow, so correcting them on every start is cheap
# and does not reintroduce a full-tree scan.
for path in /media/nzbdav/movies /media/nzbdav/tv; do
    [ ! -L "$path" ] || fail "$path must not be a symlink"
    chown "$puid:$pgid" -- "$path"
done

plugins_dir=/config/plugins
plugin_dir="$plugins_dir/Nzbdav"
[ ! -L "$plugins_dir" ] || fail "$plugins_dir must not be a symlink"
mkdir -p -- "$plugins_dir"
[ ! -L "$plugin_dir" ] || fail "$plugin_dir must not be a symlink"

SOURCE_MANIFEST=/opt/nzbdav-plugin.manifest.sha256
SOURCE_PLUGIN=/opt/nzbdav-plugin
expected_dll=Jellyfin.Plugin.Nzbdav.dll
expected_meta=meta.json
artifact_manifest=.nzbdav-artifact.manifest.sha256
artifact_marker=.nzbdav-artifact.marker
artifact_marker_value=nzbdav-plugin-artifact-v1
plugin_artifact_suffix=state1
install_prefix=.Nzbdav.install.
stale_prefix=.Nzbdav.stale.
current_install="${plugins_dir}/${install_prefix}${plugin_artifact_suffix}"
current_stale="${plugins_dir}/${stale_prefix}${plugin_artifact_suffix}"

# Validate both the baked manifest and the files it describes. A manifest that
# names anything other than the DLL and metadata is corrupt, not an instruction
# to copy arbitrary image content.
manifest_is_valid() {
    manifest=$1
    directory=$2
    [ -f "$manifest" ] && [ ! -L "$manifest" ] || return 1
    [ "$(wc -l < "$manifest")" -eq 2 ] || return 1
    awk '$2 != "Jellyfin.Plugin.Nzbdav.dll" && $2 != "meta.json" { bad=1 }
         END { exit(bad ? 1 : 0) }' "$manifest" || return 1
    [ "$(grep -c ' Jellyfin.Plugin.Nzbdav.dll$' "$manifest")" -eq 1 ] || return 1
    [ "$(grep -c ' meta.json$' "$manifest")" -eq 1 ] || return 1
    if (cd "$directory" && sha256sum -c "$manifest" >/dev/null); then
        return 0
    fi

    # sha256sum -c intentionally stays quiet on the success path. On failure,
    # identify only the fixed payload filename, never its contents or any
    # caller-provided path that could contain a secret.
    mismatch=
    while IFS=' ' read -r expected file remainder; do
        case "$file" in
            "$expected_dll"|"$expected_meta") ;;
            *) continue ;;
        esac
        actual=$(cd "$directory" && sha256sum "$file" 2>/dev/null | awk '{print $1}') || actual=
        if [ "$actual" != "$expected" ]; then
            mismatch=$file
            break
        fi
    done < "$manifest"
    if [ -n "$mismatch" ]; then
        echo "nzbdav-jellyfin: plugin manifest mismatch: $mismatch" >&2
    else
        echo "nzbdav-jellyfin: plugin manifest validation failed" >&2
    fi
    return 1
}

plugin_payload_matches_manifest() {
    directory=$1
    manifest=$2
    [ -d "$directory" ] && [ ! -L "$directory" ] || return 1
    [ -f "$directory/$expected_dll" ] && [ ! -L "$directory/$expected_dll" ] || return 1
    [ -f "$directory/$expected_meta" ] && [ ! -L "$directory/$expected_meta" ] || return 1
    [ "$(stat -c '%F' "$directory/$expected_dll" 2>/dev/null)" = "regular file" ] || return 1
    [ "$(stat -c '%F' "$directory/$expected_meta" 2>/dev/null)" = "regular file" ] || return 1
    manifest_is_valid "$manifest" "$directory"
}

# A canonical directory carries the operation proof with its payload. The
# proof is intentionally not the baked image manifest: after an image upgrade
# it remains the evidence for an interrupted operation from the old image.
plugin_tree_matches_manifest() {
    directory=$1
    manifest=$2
    [ -d "$directory" ] && [ ! -L "$directory" ] || return 1
    [ "$(stat -c '%u:%g' "$directory" 2>/dev/null)" = "$(plugin_artifact_owner)" ] || return 1
    [ "$(find -P "$directory" -mindepth 1 -maxdepth 1 | wc -l)" -eq 4 ] || return 1
    [ -f "$directory/$artifact_manifest" ] && [ ! -L "$directory/$artifact_manifest" ] || return 1
    [ -f "$directory/$artifact_marker" ] && [ ! -L "$directory/$artifact_marker" ] || return 1
    [ "$(stat -c '%F' "$directory/$artifact_manifest" 2>/dev/null)" = "regular file" ] || return 1
    [ "$(stat -c '%F' "$directory/$artifact_marker" 2>/dev/null)" = "regular file" ] || return 1
    for payload_file in "$expected_dll" "$expected_meta" "$artifact_manifest" "$artifact_marker"; do
        [ "$(stat -c '%u:%g' "$directory/$payload_file" 2>/dev/null)" = "$(plugin_artifact_owner)" ] || return 1
    done
    [ "$(cat "$directory/$artifact_marker" 2>/dev/null)" = "$artifact_marker_value" ] || return 1
    cmp -s "$directory/$artifact_manifest" "$manifest" || return 1
    plugin_payload_matches_manifest "$directory" "$manifest"
}

manifest_is_valid "$SOURCE_MANIFEST" "$SOURCE_PLUGIN" \
    || fail "baked NZBDAV plugin manifest or payload is corrupt"

plugin_artifact_root() {
    printf '%s' "$plugins_dir/$1"
}

plugin_artifact_owner() {
    printf '%s:%s' "$puid" "$pgid"
}

artifact_path_is_present() {
    candidate_path=$1
    [ -e "$candidate_path" ] || [ -L "$candidate_path" ]
}

is_owned_plugin_artifact_file() {
    artifact_file_path=$1
    [ -f "$artifact_file_path" ] && [ ! -L "$artifact_file_path" ] || return 1
    [ "$(stat -c '%F' "$artifact_file_path" 2>/dev/null)" = "regular file" ] || return 1
    [ "$(stat -c '%u:%g' "$artifact_file_path" 2>/dev/null)" = "$(plugin_artifact_owner)" ] || return 1
}

is_owned_plugin_artifact_dir() {
    artifact_dir=$1
    [ -d "$artifact_dir" ] && [ ! -L "$artifact_dir" ] || return 1
    [ "$(stat -c '%F' "$artifact_dir" 2>/dev/null)" = "directory" ] || return 1
    [ "$(stat -c '%u:%g' "$artifact_dir" 2>/dev/null)" = "$(plugin_artifact_owner)" ] || return 1
    [ "$(find -P "$artifact_dir" -mindepth 1 -maxdepth 1 | wc -l)" -eq 4 ] || return 1
    is_owned_plugin_artifact_file "$artifact_dir/$expected_dll" || return 1
    is_owned_plugin_artifact_file "$artifact_dir/$expected_meta" || return 1
    is_owned_plugin_artifact_file "$artifact_dir/$artifact_manifest" || return 1
    is_owned_plugin_artifact_file "$artifact_dir/$artifact_marker" || return 1
    [ "$(cat "$artifact_dir/$artifact_marker" 2>/dev/null)" = "$artifact_marker_value" ] || return 1
    volume_tree_is_safe "$artifact_dir" 0 || return 1
    manifest_is_valid "$artifact_dir/$artifact_manifest" "$artifact_dir"
}

# Only an artifact carrying its bounded local manifest and marker may be
# removed or moved. This also proves a canonical directory before it becomes
# the stale side of a replacement; foreign directories and symlinks fail shut.
is_replaceable_plugin_tree() {
    path=$1
    is_owned_plugin_artifact_dir "$path"
}

cleanup_plugin_artifacts() {
    # Reconciliation is deliberately bounded to the two fixed operation slots.
    # Do not glob arbitrary suffixes: a foreign artifact must remain untouched,
    # even if it happens to contain a plausible-looking payload.
    for artifact in "$current_install" "$current_stale"; do
        artifact_path_is_present "$artifact" || continue
        is_owned_plugin_artifact_dir "$artifact" || continue
        rm -rf -- "$artifact"
    done
}

recover_plugin_from_staged_artifacts() {
    stale_artifact="$(plugin_artifact_root "${stale_prefix}${plugin_artifact_suffix}")"
    install_artifact="$(plugin_artifact_root "${install_prefix}${plugin_artifact_suffix}")"

    # Inspect both fixed slots before accepting even a current canonical
    # directory. A foreign exact-name slot is a startup conflict, not cleanup.
    recovery_artifact=''
    for candidate in "$install_artifact" "$stale_artifact"; do
        if artifact_path_is_present "$candidate"; then
            is_owned_plugin_artifact_dir "$candidate" || return 1
            [ -n "$recovery_artifact" ] || recovery_artifact=$candidate
        fi
    done

    if plugin_tree_matches_manifest "$plugin_dir" "$SOURCE_MANIFEST"; then
        return 0
    fi

    if [ -n "$recovery_artifact" ]; then
        if artifact_path_is_present "$plugin_dir"; then
            # Leave an owned old canonical in place; restage_plugin will move
            # it to the stale slot after it has deterministically selected the
            # install slot above. Never replace an unproven pathname here.
            is_replaceable_plugin_tree "$plugin_dir" || return 1
            return 0
        fi
        mv -T -- "$recovery_artifact" "$plugin_dir"
    fi
}

# Stage only the two baked payload files plus the local operation proof.
# Rename the old canonical directory away and the complete replacement into
# place; an artifact is never trusted against the current baked hash alone.
restage_plugin() {
    temporary="$current_install"
    stale="$current_stale"
    for artifact in "$temporary" "$stale"; do
        if artifact_path_is_present "$artifact"; then
            is_owned_plugin_artifact_dir "$artifact" \
                || fail "refusing to overwrite non-owned plugin artifact: $artifact"
            rm -rf -- "$artifact"
        fi
    done

    mkdir -- "$temporary"
    cp -- "$SOURCE_PLUGIN/$expected_dll" "$SOURCE_PLUGIN/$expected_meta" "$temporary/"
    cp -- "$SOURCE_MANIFEST" "$temporary/$artifact_manifest"
    printf '%s\n' "$artifact_marker_value" > "$temporary/$artifact_marker"
    # The publish output is already 0644; chown is the only ownership
    # transition required before the unprivileged Jellyfin exec.
    chown -R "$puid:$pgid" "$temporary"
    if artifact_path_is_present "$plugin_dir"; then
        is_replaceable_plugin_tree "$plugin_dir" || fail "unsafe persisted plugin tree"
        mv -T -- "$plugin_dir" "$stale"
    fi
    mv -T -- "$temporary" "$plugin_dir"
    plugin_tree_matches_manifest "$plugin_dir" "$SOURCE_MANIFEST" \
        || fail "staged NZBDAV plugin does not match baked manifest"
}

if ! recover_plugin_from_staged_artifacts; then
    fail "failed to recover NZBDAV plugin from staged artifacts"
fi
cleanup_plugin_artifacts
if ! plugin_tree_matches_manifest "$plugin_dir" "$SOURCE_MANIFEST"; then
    restage_plugin
fi
cleanup_plugin_artifacts
# initialize_volume recursively assigns persisted content to the application
# identity. Take ownership of these directories back while CHOWN is available,
# chmod as their owner, then hand them to Jellyfin. This keeps the reduced
# capability set free of FOWNER.
chown 0:0 "$plugins_dir" "$plugin_dir"
chmod 0755 "$plugins_dir" "$plugin_dir"
chown "$puid:$pgid" "$plugins_dir" "$plugin_dir"

# Keep supplementary groups supplied by the NVIDIA runtime. --clear-groups
# would silently discard the injected video/render groups.
exec setpriv --reuid="$puid" --regid="$pgid" --keep-groups /jellyfin/jellyfin "$@"
