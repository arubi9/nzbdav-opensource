#!/bin/sh

# This file is sourced by entrypoint.sh.  Secret files deliberately live in a
# separate, private directory so that a restart can reuse them without putting
# secret values in the container environment supplied by the operator.
CONFIG_PATH=${CONFIG_PATH:-/config/data}
# /config is a root-only control plane.  The writable database state lives in
# its child; normalize the old default so an operator cannot accidentally make
# the control-plane directory the backend's data directory.
[ "$CONFIG_PATH" = /config ] && CONFIG_PATH=/config/data
case "$CONFIG_PATH" in
    */data) CONFIG_ROOT=${CONFIG_PATH%/data} ;;
    *) CONFIG_ROOT=$CONFIG_PATH ;;
esac
[ -n "$CONFIG_ROOT" ] || exit 1
export CONFIG_PATH CONFIG_ROOT

# Capture operator requests before any persisted value is loaded.  In
# particular, a restart must not accidentally turn the value exported by a
# previous bootstrap into a new override.
REQUESTED_MASTER_SET=${NZBDAV_MASTER_KEY+x}
REQUESTED_MASTER_VALUE=${NZBDAV_MASTER_KEY-}
REQUESTED_API_SET=${FRONTEND_BACKEND_API_KEY+x}
REQUESTED_API_VALUE=${FRONTEND_BACKEND_API_KEY-}
REQUESTED_SESSION_SET=${SESSION_KEY+x}
REQUESTED_SESSION_VALUE=${SESSION_KEY-}

umask 077

# Secrets are later framed as one value per stdin line. Reject framing
# characters before persistence or export; command substitution cannot safely
# preserve NUL, so persisted bytes are checked separately below.
BOOTSTRAP_CR=$(printf '\r')
BOOTSTRAP_LF='
'
secret_value_has_framing_control() {
    case "$1" in
        *"$BOOTSTRAP_CR"*|*"$BOOTSTRAP_LF"*) return 0 ;;
        *) return 1 ;;
    esac
}

bootstrap_fail() {
    echo "Unable to prepare bootstrap secrets." >&2
    return 1
}

# Establish the secret directory while its parent is root-owned and
# non-writable.  Once this invariant holds, an app user cannot substitute a
# path between validation and open; no recursive chown or check-then-open in an
# app-writable directory is used for secrets.
prepare_secret_directory() {
    # Do this before any application account is started.  Never recursively
    # chown either directory: the parent is the security boundary and the
    # child is the only directory the backend may write.
    if [ -e "$CONFIG_ROOT" ] || [ -L "$CONFIG_ROOT" ]; then
        [ -d "$CONFIG_ROOT" ] && [ ! -L "$CONFIG_ROOT" ] || return 1
    else
        mkdir -p "$CONFIG_ROOT" || return 1
    fi
    chown root:root "$CONFIG_ROOT" || return 1
    # The backend needs to traverse to /config/data, but nobody except root
    # may write this control-plane directory.
    chmod 755 "$CONFIG_ROOT" || return 1
    [ "$(stat -c '%u:%a' "$CONFIG_ROOT")" = 0:755 ] || return 1

    if [ "$CONFIG_PATH" != "$CONFIG_ROOT" ]; then
        if [ -e "$CONFIG_PATH" ] || [ -L "$CONFIG_PATH" ]; then
            [ -d "$CONFIG_PATH" ] && [ ! -L "$CONFIG_PATH" ] || return 1
        else
            mkdir "$CONFIG_PATH" || return 1
        fi
        chown root:root "$CONFIG_PATH" || return 1
        chmod 700 "$CONFIG_PATH" || return 1
        [ "$(stat -c '%u:%a' "$CONFIG_PATH")" = 0:700 ] || return 1
    fi

    BOOTSTRAP_SECRET_DIR="$CONFIG_ROOT/bootstrap-secrets"
    if [ -e "$BOOTSTRAP_SECRET_DIR" ] || [ -L "$BOOTSTRAP_SECRET_DIR" ]; then
        [ -d "$BOOTSTRAP_SECRET_DIR" ] && [ ! -L "$BOOTSTRAP_SECRET_DIR" ] || return 1
    else
        mkdir "$BOOTSTRAP_SECRET_DIR" || return 1
    fi
    chown root:root "$BOOTSTRAP_SECRET_DIR" || return 1
    chmod 700 "$BOOTSTRAP_SECRET_DIR" || return 1
    [ "$(stat -c '%u:%a' "$BOOTSTRAP_SECRET_DIR")" = 0:700 ] || return 1
    BOOTSTRAP_MASTER_FILE="$BOOTSTRAP_SECRET_DIR/nzbdav-master-key"
    BOOTSTRAP_API_FILE="$BOOTSTRAP_SECRET_DIR/frontend-backend-api-key"
    BOOTSTRAP_SESSION_FILE="$BOOTSTRAP_SECRET_DIR/session-key"
    export BOOTSTRAP_SECRET_DIR BOOTSTRAP_MASTER_FILE BOOTSTRAP_API_FILE BOOTSTRAP_SESSION_FILE
}

secret_directory_is_safe() {
    [ -d "$BOOTSTRAP_SECRET_DIR" ] && [ ! -L "$BOOTSTRAP_SECRET_DIR" ] || return 1
    [ "$(stat -c '%u:%a' "$BOOTSTRAP_SECRET_DIR" 2>/dev/null)" = 0:700 ]
    [ "$(stat -c '%u:%a' "$CONFIG_ROOT" 2>/dev/null)" = 0:755 ]
}

# /proc/self/mountinfo stores spaces, tabs, newlines, and backslashes as
# octal escapes in field 5. Decode that field in awk and compare the complete
# path, rather than using a prefix or an unescaped shell string. TARGET is
# passed through the environment so backslashes are never interpreted as awk
# command-line escapes; no decoded value is evaluated as shell code.
mountinfo_path_is_under() {
    LC_ALL=C MOUNTINFO_TARGET=$1 MOUNTINFO_INCLUDE_ROOT=${2:-1} awk '
        function decode_mount_path(value, result, i, character, octal) {
            result = ""
            for (i = 1; i <= length(value); i++) {
                character = substr(value, i, 1)
                if (character == "\\" && i + 3 <= length(value)) {
                    octal = substr(value, i + 1, 3)
                    if (octal ~ /^[0-7][0-7][0-7]$/) {
                        result = result sprintf("%c", (substr(octal, 1, 1) * 64) + (substr(octal, 2, 1) * 8) + substr(octal, 3, 1))
                        i += 3
                        continue
                    }
                }
                result = result character
            }
            return result
        }
        BEGIN {
            target = ENVIRON["MOUNTINFO_TARGET"]
            include_root = ENVIRON["MOUNTINFO_INCLUDE_ROOT"]
            found = 0
        }
        {
            mount_path = decode_mount_path($5)
            is_root = mount_path == target
            is_child = target == "/" ? (substr(mount_path, 1, 1) == "/" && mount_path != "/") : index(mount_path, target "/") == 1
            if ((include_root == 1 && is_root) || is_child) found = 1
        }
        END { exit(found ? 0 : 1) }
    ' /proc/self/mountinfo
}

path_is_mountpoint() {
    [ -r /proc/self/mountinfo ] || return 1
    mountinfo_path_is_under "$1" 1
}

path_has_nested_mount() {
    [ -r /proc/self/mountinfo ] || return 1
    mountinfo_path_is_under "$1" 0
}

path_is_safe_data_object() {
    DATA_OBJECT=$1
    [ ! -L "$DATA_OBJECT" ] && { [ -f "$DATA_OBJECT" ] || [ -d "$DATA_OBJECT" ]; } || return 1
    if [ -f "$DATA_OBJECT" ]; then
        [ "$(stat -c '%F:%h' "$DATA_OBJECT" 2>/dev/null)" = "regular file:1" ] || return 1
    fi
    [ "$(stat -c '%d' "$DATA_OBJECT" 2>/dev/null)" = "$(stat -c '%d' "$CONFIG_ROOT" 2>/dev/null)" ] || return 1
    # The volume mount at CONFIG_ROOT is expected; every child mount is not.
    if [ "$DATA_OBJECT" != "$CONFIG_ROOT" ] && path_is_mountpoint "$DATA_OBJECT"; then
        return 1
    fi
    # A bind/subpath mount can share the device number with its parent. Check
    # mountinfo independently of stat so it cannot enter a migration or move.
    path_has_nested_mount "$DATA_OBJECT" && return 1
    return 0
}

# Validate a complete legacy directory without following links or silently
# skipping a nested mount. BusyBox find does not follow links by default; each
# visited item is checked before any rename can occur.
validate_legacy_tree() {
    LEGACY_TREE=$1
    path_is_safe_data_object "$LEGACY_TREE" || return 1
    path_has_nested_mount "$LEGACY_TREE" && return 1
    if [ -d "$LEGACY_TREE" ]; then
        find "$LEGACY_TREE" -exec /bin/sh -c '
            for item do
                [ ! -L "$item" ] && { [ -d "$item" ] || [ -f "$item" ]; } || exit 1
                if [ -f "$item" ]; then
                    [ "$(stat -c "%F:%h" "$item" 2>/dev/null)" = "regular file:1" ] || exit 1
                fi
                [ "$(stat -c "%d" "$item" 2>/dev/null)" = "$(stat -c "%d" "$CONFIG_ROOT" 2>/dev/null)" ] || exit 1
            done
        ' sh {} + || return 1
    fi
}

# Move a legacy SQLite database only while the root-owned boundary is held.
# rename(2) does not follow the final component; the lstat-style checks below
# additionally reject links, non-regular objects, hardlinks, and cross-device
# moves.  The post-rename check closes the useful shell-level substitution
# race without ever chowning an object that failed validation.
migrate_legacy_database() {
    [ "$CONFIG_PATH" != "$CONFIG_ROOT" ] || return 0
    # Preflight every sidecar first so one hostile WAL/SHM/journal cannot leave
    # a partially migrated database behind. CONFIG_PATH was checked by the
    # caller before this function, including its device and mount topology.
    for LEGACY_NAME in db.sqlite db.sqlite-wal db.sqlite-shm db.sqlite-journal; do
        LEGACY_PATH="$CONFIG_ROOT/$LEGACY_NAME"
        DATA_PATH="$CONFIG_PATH/$LEGACY_NAME"
        if [ ! -e "$LEGACY_PATH" ] && [ ! -L "$LEGACY_PATH" ]; then
            continue
        fi
        path_is_safe_data_object "$LEGACY_PATH" || return 1
        [ "$(stat -c '%d' "$LEGACY_PATH" 2>/dev/null)" = "$(stat -c '%d' "$CONFIG_PATH" 2>/dev/null)" ] || return 1
        # Refuse replacement of any destination, including a symlink.
        [ ! -e "$DATA_PATH" ] && [ ! -L "$DATA_PATH" ] || return 1
    done
    for LEGACY_NAME in db.sqlite db.sqlite-wal db.sqlite-shm db.sqlite-journal; do
        LEGACY_PATH="$CONFIG_ROOT/$LEGACY_NAME"
        DATA_PATH="$CONFIG_PATH/$LEGACY_NAME"
        [ -e "$LEGACY_PATH" ] || [ -L "$LEGACY_PATH" ] || continue
        if ! mv "$LEGACY_PATH" "$DATA_PATH"; then
            return 1
        fi
        if [ ! -L "$DATA_PATH" ] && [ -f "$DATA_PATH" ] &&
            [ "$(stat -c '%F:%h' "$DATA_PATH" 2>/dev/null)" = "regular file:1" ]; then
            continue
        fi
        # A race can only have moved an unsafe final component here. Remove a
        # link or regular file by name (never recurse/follow); leave a device
        # or directory untouched and fail closed.
        [ -L "$DATA_PATH" ] || [ -f "$DATA_PATH" ] && rm -f "$DATA_PATH" || true
        return 1
    done
    sync || return 1
}

# These are all backend-owned paths that historically lived directly under
# /config. Keep the control plane root-only while moving them into /config/data
# so upgrades preserve blobs, data-protection keys, snapshots, and cache state.
migrate_legacy_backend_state() {
    [ "$CONFIG_PATH" != "$CONFIG_ROOT" ] || return 0
    # Reject the writable data root and every descendant mount before any
    # legacy rename. Bind/subpath mounts can have the same device number.
    path_is_safe_data_object "$CONFIG_PATH" || return 1
    path_has_nested_mount "$CONFIG_PATH" && return 1
    # Validate *all* source and destination names, and traverse each source,
    # before the first rename. This ordering is important: BusyBox mv falls
    # back to copy-and-delete across devices, so a late failure must not leave
    # an earlier legacy object moved (or deleted).
    for LEGACY_NAME in blobs data-protection content-index.snapshot.json content-index.snapshot.json.tmp content-index.snapshot.backup.json stream-cache; do
        LEGACY_PATH="$CONFIG_ROOT/$LEGACY_NAME"
        DATA_PATH="$CONFIG_PATH/$LEGACY_NAME"
        if [ -e "$LEGACY_PATH" ] || [ -L "$LEGACY_PATH" ]; then
            validate_legacy_tree "$LEGACY_PATH" || return 1
            [ ! -e "$DATA_PATH" ] && [ ! -L "$DATA_PATH" ] || return 1
        fi
    done
    migrate_legacy_database || return 1
    for LEGACY_NAME in blobs data-protection content-index.snapshot.json content-index.snapshot.json.tmp content-index.snapshot.backup.json stream-cache; do
        LEGACY_PATH="$CONFIG_ROOT/$LEGACY_NAME"
        DATA_PATH="$CONFIG_PATH/$LEGACY_NAME"
        if [ ! -e "$LEGACY_PATH" ] && [ ! -L "$LEGACY_PATH" ]; then
            continue
        fi
        validate_legacy_tree "$LEGACY_PATH" || return 1
        [ ! -e "$DATA_PATH" ] && [ ! -L "$DATA_PATH" ] || return 1
    done
    for LEGACY_NAME in blobs data-protection content-index.snapshot.json content-index.snapshot.json.tmp content-index.snapshot.backup.json stream-cache; do
        LEGACY_PATH="$CONFIG_ROOT/$LEGACY_NAME"
        DATA_PATH="$CONFIG_PATH/$LEGACY_NAME"
        [ -e "$LEGACY_PATH" ] || [ -L "$LEGACY_PATH" ] || continue
        mv "$LEGACY_PATH" "$DATA_PATH" || return 1
        validate_legacy_tree "$DATA_PATH" || return 1
    done
    sync || return 1
}

file_mode_is_private() {
    mode=$(stat -c '%a' "$1" 2>/dev/null) || return 1
    [ "$mode" = 600 ] || return 1
    # A secret owned by someone other than the private directory owner is
    # suspicious (and would not be readable by the eventual app user).
    file_owner=$(stat -c '%u:%g' "$1" 2>/dev/null) || return 1
    directory_owner=$(stat -c '%u:%g' "$BOOTSTRAP_SECRET_DIR" 2>/dev/null) || return 1
    [ "$(stat -c '%h' "$1" 2>/dev/null)" = 1 ] || return 1
    [ "$file_owner" = "$directory_owner" ] && [ "$directory_owner" = 0:0 ] && secret_directory_is_safe
}

# Read an existing file only after checking both its type and its mode.  In
# particular, -f alone follows symlinks on several implementations of test.
read_private_secret() {
    secret_file=$1
    secret_directory_is_safe || return 1
    [ ! -L "$secret_file" ] && [ -f "$secret_file" ] || return 1
    file_mode_is_private "$secret_file" || return 1
    secret_value=$(cat "$secret_file") || return 1
    [ -n "$secret_value" ] || return 1
    secret_value_has_framing_control "$secret_value" && return 1
    secret_file_size=$(wc -c <"$secret_file") || return 1
    secret_value_size=$(printf '%s' "$secret_value" | wc -c) || return 1
    # Command substitution removes trailing newlines; reject them rather than
    # silently accepting a file whose bytes differ from the secret value.
    [ "$secret_file_size" -eq "$secret_value_size" ] || return 1
    # Shell variables cannot represent NUL. Comparing the raw file with a
    # NUL-stripped byte count rejects an otherwise silently truncated secret.
    secret_without_nul_size=$(tr -d '\000' <"$secret_file" | wc -c) || return 1
    [ "$secret_file_size" -eq "$secret_without_nul_size" ] || return 1
    BOOTSTRAP_READ_SECRET=$secret_value
}

# The master key is the canonical base64 representation of exactly 32 bytes.
# Checking the alphabet and length before invoking base64 makes permissive
# base64 implementations reject malformed input too.
validate_master_key() {
    master_value=$1
    [ -n "$master_value" ] || return 1
    [ "$(printf '%s' "$master_value" | wc -c)" -eq 44 ] || return 1
    printf '%s' "$master_value" | grep -Eq '^[A-Za-z0-9+/]{43}=$' || return 1

    decoded_file=$(mktemp "$BOOTSTRAP_SECRET_DIR/.master-decode.XXXXXX") || return 1
    chmod 600 "$decoded_file" || { rm -f "$decoded_file"; return 1; }
    if ! printf '%s' "$master_value" | base64 -d >"$decoded_file"; then
        rm -f "$decoded_file"
        return 1
    fi
    decoded_size=$(wc -c <"$decoded_file") || {
        rm -f "$decoded_file"
        return 1
    }
    [ "$decoded_size" -eq 32 ] || {
        rm -f "$decoded_file"
        return 1
    }
    canonical_file=$(mktemp "$BOOTSTRAP_SECRET_DIR/.master-canonical.XXXXXX") || {
        rm -f "$decoded_file"
        return 1
    }
    chmod 600 "$canonical_file" || {
        rm -f "$decoded_file" "$canonical_file"
        return 1
    }
    if ! base64 <"$decoded_file" >"$canonical_file"; then
        rm -f "$decoded_file" "$canonical_file"
        return 1
    fi
    canonical_value=$(tr -d '\r\n' <"$canonical_file") || {
        rm -f "$decoded_file" "$canonical_file"
        return 1
    }
    rm -f "$decoded_file" "$canonical_file" || return 1
    [ "$canonical_value" = "$master_value" ]
}

# Generate using a file so a failed RNG cannot be hidden by a successful final
# command in a pipeline.  The resulting value has at least 256 bits of entropy.
generate_secret() {
    random_file=$(mktemp "$BOOTSTRAP_SECRET_DIR/.random.XXXXXX") || return 1
    chmod 600 "$random_file" || { rm -f "$random_file"; return 1; }
    if ! head -c 32 /dev/urandom >"$random_file"; then
        rm -f "$random_file"
        return 1
    fi
    random_size=$(wc -c <"$random_file") || {
        rm -f "$random_file"
        return 1
    }
    [ "$random_size" -eq 32 ] || {
        rm -f "$random_file"
        return 1
    }
    encoded_file=$(mktemp "$BOOTSTRAP_SECRET_DIR/.encoded.XXXXXX") || {
        rm -f "$random_file"
        return 1
    }
    chmod 600 "$encoded_file" || {
        rm -f "$random_file" "$encoded_file"
        return 1
    }
    if ! base64 <"$random_file" >"$encoded_file"; then
        rm -f "$random_file" "$encoded_file"
        return 1
    fi
    generated_secret=$(tr -d '\r\n' <"$encoded_file") || {
        rm -f "$random_file" "$encoded_file"
        return 1
    }
    rm -f "$random_file" "$encoded_file" || return 1
    [ -n "$generated_secret" ] || return 1
    BOOTSTRAP_GENERATED_SECRET=$generated_secret
}

# BusyBox sync -d performs fdatasync on a named file; the plain sync call
# additionally fsyncs the file and containing directory so the rename itself
# is durable too. Do not turn any call into best effort: callers must fail
# before maintenance or promotion if the image cannot provide this guarantee.
durable_secret_path() {
    DURABLE_PATH=$1
    [ "${BOOTSTRAP_TEST_FAIL_FSYNC:-0}" != 1 ] || return 1
    [ "${BOOTSTRAP_TEST_FAIL_FILE_FSYNC:-0}" != 1 ] || return 1
    [ ! -L "$DURABLE_PATH" ] && [ -f "$DURABLE_PATH" ] || return 1
    [ "$(stat -c '%F:%h:%u:%a' "$DURABLE_PATH" 2>/dev/null)" = "regular file:1:0:600" ] || return 1
    sync -d "$DURABLE_PATH" || return 1
    sync "$DURABLE_PATH" || return 1
    DURABLE_DIRECTORY=$(dirname "$DURABLE_PATH")
    [ "${BOOTSTRAP_TEST_FAIL_DIR_FSYNC:-0}" != 1 ] || return 1
    sync "$DURABLE_DIRECTORY" || return 1
    return 0
}

# Write a value to an unpredictable file in the destination directory, then
# rename it.  The old destination is not touched until all checks on the new
# file have passed. Durability is proved again after rename.
atomic_write_secret() {
    destination=$1
    value=$2
    secret_directory_is_safe || return 1
    [ ! -L "$destination" ] || return 1

    temporary_file=$(mktemp "$BOOTSTRAP_SECRET_DIR/.secret.XXXXXX") || return 1
    [ "${BOOTSTRAP_TEST_FAIL_WRITE:-0}" != 1 ] || {
        rm -f "$temporary_file"
        return 1
    }
    if ! (umask 077 && printf '%s' "$value" >"$temporary_file"); then
        rm -f "$temporary_file"
        return 1
    fi
    chmod 600 "$temporary_file" || {
        rm -f "$temporary_file"
        return 1
    }
    chown root:root "$temporary_file" || {
        rm -f "$temporary_file"
        return 1
    }
    [ "$(stat -c '%u:%a' "$temporary_file")" = 0:600 ] || {
        rm -f "$temporary_file"
        return 1
    }
    [ -f "$temporary_file" ] && [ ! -L "$temporary_file" ] || {
        rm -f "$temporary_file"
        return 1
    }
    [ "$(stat -c '%F:%h:%u:%a' "$temporary_file" 2>/dev/null)" = "regular file:1:0:600" ] || {
        rm -f "$temporary_file"
        return 1
    }
    temporary_size=$(wc -c <"$temporary_file") || {
        rm -f "$temporary_file"
        return 1
    }
    expected_size=$(printf '%s' "$value" | wc -c) || {
        rm -f "$temporary_file"
        return 1
    }
    [ "$temporary_size" -gt 0 ] && [ "$temporary_size" -eq "$expected_size" ] || {
        rm -f "$temporary_file"
        return 1
    }
    written_value=$(cat "$temporary_file") || {
        rm -f "$temporary_file"
        return 1
    }
    [ "$written_value" = "$value" ] || {
        rm -f "$temporary_file"
        return 1
    }
    [ ! -L "$destination" ] || {
        rm -f "$temporary_file"
        return 1
    }
    secret_directory_is_safe || {
        rm -f "$temporary_file"
        return 1
    }
    # Flush and validate while the old destination is still untouched. This
    # is the file-fsync and directory-fsync phase before the atomic rename.
    durable_secret_path "$temporary_file" || {
        rm -f "$temporary_file"
        return 1
    }
    [ "${BOOTSTRAP_TEST_FAIL_RENAME:-0}" != 1 ] || {
        rm -f "$temporary_file"
        return 1
    }
    if ! mv -f -- "$temporary_file" "$destination"; then
        rm -f "$temporary_file"
        return 1
    fi
    durable_secret_path "$destination" || return 1
    return 0
}

# This is the second phase of master-key rotation. It is intentionally not
# called by this sourced file: entrypoint calls it only after migration.
# Until the canonical replacement is durable, the pending file is untouched.
bootstrap_promote_master_key() {
    [ "${BOOTSTRAP_MASTER_ROTATION_REQUIRED:-0}" = 1 ] || return 0
    staged_file=${BOOTSTRAP_MASTER_STAGED_FILE:-}
    [ -n "$staged_file" ] && [ ! -L "$staged_file" ] && [ -f "$staged_file" ] || return 1
    file_mode_is_private "$staged_file" || return 1
    read_private_secret "$staged_file" || return 1
    staged_value=$BOOTSTRAP_READ_SECRET
    validate_master_key "$staged_value" || return 1
    [ ! -L "$BOOTSTRAP_MASTER_FILE" ] || return 1
    [ -e "$BOOTSTRAP_MASTER_FILE" ] || return 1
    file_mode_is_private "$BOOTSTRAP_MASTER_FILE" || return 1
    read_private_secret "$BOOTSTRAP_MASTER_FILE" || return 1
    old_master_value=$BOOTSTRAP_READ_SECRET
    validate_master_key "$old_master_value" || return 1

    # Keep a durable rollback copy until both the canonical replacement and
    # pending-stage unlink have completed. Without this transaction boundary,
    # an unlink failure reports startup failure after replacing the only old
    # canonical key, contradicting the restart contract in the deployment
    # guide. The rollback file is private and exists only during promotion.
    rollback_file="$BOOTSTRAP_SECRET_DIR/.master-key.rollback"
    [ ! -e "$rollback_file" ] && [ ! -L "$rollback_file" ] || rm -f -- "$rollback_file" || return 1
    atomic_write_secret "$rollback_file" "$old_master_value" || return 1

    # Never rename the only pending copy. Write the staged value to a fresh
    # canonical temporary file, fsync that file and its directory, rename it,
    # and fsync the canonical file and directory again. If any operation after
    # the rename fails, restore the old durable canonical value and retain the
    # pending candidate for restart.
    if ! atomic_write_secret "$BOOTSTRAP_MASTER_FILE" "$staged_value"; then
        # A failure before rename leaves the old file untouched. A failure
        # after rename is rolled back while the durable copy still exists.
        atomic_write_secret "$BOOTSTRAP_MASTER_FILE" "$old_master_value" || true
        rm -f -- "$rollback_file" || true
        return 1
    fi

    if [ "${BOOTSTRAP_TEST_FAIL_UNLINK:-0}" = 1 ] || ! rm -f -- "$staged_file"; then
        atomic_write_secret "$BOOTSTRAP_MASTER_FILE" "$old_master_value" || return 1
        rm -f -- "$rollback_file" || true
        return 1
    fi
    if [ "${BOOTSTRAP_TEST_FAIL_DIR_FSYNC:-0}" = 1 ] || ! sync "$(dirname "$staged_file")"; then
        atomic_write_secret "$BOOTSTRAP_MASTER_FILE" "$old_master_value" || return 1
        if [ ! -e "$staged_file" ] && [ ! -L "$staged_file" ]; then
            (umask 077 && printf '%s' "$staged_value" >"$staged_file") || true
            chmod 600 "$staged_file" 2>/dev/null || true
            chown root:root "$staged_file" 2>/dev/null || true
        fi
        rm -f -- "$rollback_file" || true
        return 1
    fi
    rm -f -- "$rollback_file" || return 1
    return 0
}

if [ "$REQUESTED_MASTER_SET" = x ]; then
    secret_value_has_framing_control "$REQUESTED_MASTER_VALUE" && exit 1
fi
if [ "$REQUESTED_API_SET" = x ]; then
    secret_value_has_framing_control "$REQUESTED_API_VALUE" && exit 1
fi
if [ "$REQUESTED_SESSION_SET" = x ]; then
    secret_value_has_framing_control "$REQUESTED_SESSION_VALUE" && exit 1
fi

prepare_secret_directory || exit 1
migrate_legacy_backend_state || exit 1

# A pending stage is durable rotation state. It is deliberately retained on
# every failure because maintenance may already have committed database rows.
# Successful promotion removes it only after canonical durability is proven.

# The stage filename is stable so it remains discoverable after a process
# crash. A valid stage is never overwritten by a different request.
BOOTSTRAP_MASTER_STAGED_FILE="$BOOTSTRAP_SECRET_DIR/nzbdav-master-key.pending"
BS_MASTER_STAGE_EXISTS=0
if [ -e "$BOOTSTRAP_MASTER_STAGED_FILE" ] || [ -L "$BOOTSTRAP_MASTER_STAGED_FILE" ]; then
    BS_MASTER_STAGE_EXISTS=1
fi
if [ "$REQUESTED_MASTER_SET" = x ]; then
    [ -n "$REQUESTED_MASTER_VALUE" ] || exit 1
    validate_master_key "$REQUESTED_MASTER_VALUE" || exit 1
fi
if [ "$BS_MASTER_STAGE_EXISTS" -eq 1 ]; then
    # Restart path: the durable staged value, not a new override, is the
    # source of truth. Both keys are exported for idempotent maintenance.
    [ ! -L "$BOOTSTRAP_MASTER_STAGED_FILE" ] && [ -f "$BOOTSTRAP_MASTER_STAGED_FILE" ] || exit 1
    file_mode_is_private "$BOOTSTRAP_MASTER_STAGED_FILE" || exit 1
    read_private_secret "$BOOTSTRAP_MASTER_STAGED_FILE" || exit 1
    BS_STAGED_MASTER_KEY=$BOOTSTRAP_READ_SECRET
    validate_master_key "$BS_STAGED_MASTER_KEY" || exit 1
    if [ "$REQUESTED_MASTER_SET" = x ] && [ "$REQUESTED_MASTER_VALUE" != "$BS_STAGED_MASTER_KEY" ]; then
        echo "A master-key rotation is already in progress; resume it with the staged key." >&2
        exit 1
    fi
    [ -e "$BOOTSTRAP_MASTER_FILE" ] && [ ! -L "$BOOTSTRAP_MASTER_FILE" ] || exit 1
    read_private_secret "$BOOTSTRAP_MASTER_FILE" || exit 1
    BS_OLD_MASTER_KEY=$BOOTSTRAP_READ_SECRET
    validate_master_key "$BS_OLD_MASTER_KEY" || exit 1
    BOOTSTRAP_MASTER_ROTATION_REQUIRED=1
    BOOTSTRAP_OLD_MASTER_KEY=$BS_OLD_MASTER_KEY
    BOOTSTRAP_STAGED_MASTER_KEY=$BS_STAGED_MASTER_KEY
    export BOOTSTRAP_MASTER_STAGED_FILE BOOTSTRAP_MASTER_ROTATION_REQUIRED
    export BOOTSTRAP_OLD_MASTER_KEY BOOTSTRAP_STAGED_MASTER_KEY
    NZBDAV_MASTER_KEY=$BS_STAGED_MASTER_KEY
elif [ "$REQUESTED_MASTER_SET" = x ]; then
    if [ -e "$BOOTSTRAP_MASTER_FILE" ] || [ -L "$BOOTSTRAP_MASTER_FILE" ]; then
        read_private_secret "$BOOTSTRAP_MASTER_FILE" || exit 1
        BS_OLD_MASTER_KEY=$BOOTSTRAP_READ_SECRET
        validate_master_key "$BS_OLD_MASTER_KEY" || exit 1
        if [ "$BS_OLD_MASTER_KEY" != "$REQUESTED_MASTER_VALUE" ]; then
            BOOTSTRAP_MASTER_ROTATION_REQUIRED=1
            export BOOTSTRAP_MASTER_STAGED_FILE BOOTSTRAP_MASTER_ROTATION_REQUIRED
            atomic_write_secret "$BOOTSTRAP_MASTER_STAGED_FILE" "$REQUESTED_MASTER_VALUE" || exit 1
            BOOTSTRAP_OLD_MASTER_KEY=$BS_OLD_MASTER_KEY
            BOOTSTRAP_STAGED_MASTER_KEY=$REQUESTED_MASTER_VALUE
            export BOOTSTRAP_OLD_MASTER_KEY BOOTSTRAP_STAGED_MASTER_KEY
            NZBDAV_MASTER_KEY=$REQUESTED_MASTER_VALUE
        else
            # Same-value overrides preserve inode, mtime, and bytes.
            BOOTSTRAP_MASTER_ROTATION_REQUIRED=0
            export BOOTSTRAP_MASTER_ROTATION_REQUIRED
            NZBDAV_MASTER_KEY=$BS_OLD_MASTER_KEY
        fi
    else
        atomic_write_secret "$BOOTSTRAP_MASTER_FILE" "$REQUESTED_MASTER_VALUE" || exit 1
        BOOTSTRAP_MASTER_ROTATION_REQUIRED=0
        export BOOTSTRAP_MASTER_ROTATION_REQUIRED
        NZBDAV_MASTER_KEY=$REQUESTED_MASTER_VALUE
    fi
else
    if [ -e "$BOOTSTRAP_MASTER_FILE" ] || [ -L "$BOOTSTRAP_MASTER_FILE" ]; then
        read_private_secret "$BOOTSTRAP_MASTER_FILE" || exit 1
        NZBDAV_MASTER_KEY=$BOOTSTRAP_READ_SECRET
        validate_master_key "$NZBDAV_MASTER_KEY" || exit 1
    else
        generate_secret || exit 1
        NZBDAV_MASTER_KEY=$BOOTSTRAP_GENERATED_SECRET
        validate_master_key "$NZBDAV_MASTER_KEY" || exit 1
        atomic_write_secret "$BOOTSTRAP_MASTER_FILE" "$NZBDAV_MASTER_KEY" || exit 1
    fi
    BOOTSTRAP_MASTER_ROTATION_REQUIRED=0
    export BOOTSTRAP_MASTER_ROTATION_REQUIRED
fi
export NZBDAV_MASTER_KEY


persist_non_master_secret() {
    BS_SECRET_FILE=$1
    BS_REQUESTED_SET=$2
    BS_REQUESTED_VALUE=$3
    BS_OUTPUT_NAME=$4
    if [ "$BS_REQUESTED_SET" = x ]; then
        [ -n "$BS_REQUESTED_VALUE" ] || return 1
        BS_SECRET_VALUE=$BS_REQUESTED_VALUE
        BS_SECRET_LENGTH=$(printf '%s' "$BS_SECRET_VALUE" | wc -c) || return 1
        [ "$BS_SECRET_LENGTH" -ge 16 ] || return 1
        if [ -e "$BS_SECRET_FILE" ] || [ -L "$BS_SECRET_FILE" ]; then
            read_private_secret "$BS_SECRET_FILE" || return 1
            # Same-value overrides are intentionally a no-op. This preserves
            # inode, mtime, and content while still exporting the request.
            if [ "$BOOTSTRAP_READ_SECRET" != "$BS_SECRET_VALUE" ]; then
                atomic_write_secret "$BS_SECRET_FILE" "$BS_SECRET_VALUE" || return 1
            fi
        else
            atomic_write_secret "$BS_SECRET_FILE" "$BS_SECRET_VALUE" || return 1
        fi
    elif [ -e "$BS_SECRET_FILE" ] || [ -L "$BS_SECRET_FILE" ]; then
        read_private_secret "$BS_SECRET_FILE" || return 1
        BS_SECRET_VALUE=$BOOTSTRAP_READ_SECRET
    else
        generate_secret || return 1
        BS_SECRET_VALUE=$BOOTSTRAP_GENERATED_SECRET
        atomic_write_secret "$BS_SECRET_FILE" "$BS_SECRET_VALUE" || return 1
    fi
    [ -n "$BS_SECRET_VALUE" ] || return 1
    if [ "$BS_OUTPUT_NAME" = FRONTEND_BACKEND_API_KEY ]; then
        FRONTEND_BACKEND_API_KEY=$BS_SECRET_VALUE
        export FRONTEND_BACKEND_API_KEY
    else
        SESSION_KEY=$BS_SECRET_VALUE
        export SESSION_KEY
    fi
}

persist_non_master_secret "$BOOTSTRAP_API_FILE" "$REQUESTED_API_SET" "$REQUESTED_API_VALUE" FRONTEND_BACKEND_API_KEY || exit 1
persist_non_master_secret "$BOOTSTRAP_SESSION_FILE" "$REQUESTED_SESSION_SET" "$REQUESTED_SESSION_VALUE" SESSION_KEY || exit 1

# Do not leave implementation details in the caller's environment when this
# script is used standalone.  entrypoint intentionally consumes these values
# before unsetting them for the frontend.
unset BOOTSTRAP_READ_SECRET BOOTSTRAP_GENERATED_SECRET secret_value
