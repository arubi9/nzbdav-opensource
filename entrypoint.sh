#!/bin/sh
set -eu
# Never inherit an operator-controlled PATH: this script runs as root while
# preparing /config and invoking the bootstrap security boundary.
PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
export PATH
SHUTDOWN_GRACE_SECONDS=${SHUTDOWN_GRACE_SECONDS:-10}

# Fixed image paths are intentional.  Production must not allow an operator
# supplied root script or source directory to become an arbitrary code hook.
BOOTSTRAP_SCRIPT=/bootstrap-secrets.sh
BACKEND_DIR=/app/backend
FRONTEND_DIR=/app/frontend

BACKEND_PID=
FRONTEND_PID=
MAINTENANCE_PID=

process_running() {
    PROCESS_PID=$1
    # BusyBox ps does not consistently implement `ps -o stat= -p PID`.
    # kill(0) proves that the process still exists, while /proc stat lets us
    # distinguish a live child from a zombie without depending on ps output.
    kill -0 "$PROCESS_PID" 2>/dev/null || return 1
    [ -r "/proc/$PROCESS_PID/stat" ] || return 1
    PROCESS_STATE=$(awk '{ line=$0; sub(/^.*\\) /, "", line); print substr(line, 1, 1) }' \
        "/proc/$PROCESS_PID/stat" 2>/dev/null || true)
    case "$PROCESS_STATE" in
        ''|Z*) return 1 ;;
        *) return 0 ;;
    esac
}

# /proc/self/mountinfo octal-escapes spaces, tabs, newlines, and backslashes
# in field 5. Decode the kernel field in awk and compare complete paths. The
# candidate travels through the environment, not awk -v, so backslashes are
# data and no decoded path is ever evaluated as shell code.
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

mountinfo_path_is_mountpoint() {
    [ -r /proc/self/mountinfo ] || return 1
    mountinfo_path_is_under "$1" 1
}

mountinfo_path_has_nested_mount() {
    [ -r /proc/self/mountinfo ] || return 1
    mountinfo_path_is_under "$1" 0
}

wait_for_process_exit() {
    WAIT_PID=$1
    WAIT_TICKS=$((SHUTDOWN_GRACE_SECONDS * 10))
    while [ "$WAIT_TICKS" -gt 0 ] && process_running "$WAIT_PID"; do
        sleep 0.1
        WAIT_TICKS=$((WAIT_TICKS - 1))
    done
}

request_stop() {
    REQUEST_PID=$1
    REQUEST_SIGNAL=${2:-TERM}
    # Do not gate signalling on ps(1): a minimal Alpine image may only
    # provide kill, and skipping TERM/KILL would make the later wait unsafe.
    if kill -0 "$REQUEST_PID" 2>/dev/null; then
        kill "-$REQUEST_SIGNAL" "$REQUEST_PID" 2>/dev/null || kill "$REQUEST_PID" 2>/dev/null || true
    fi
}

finish_stop() {
    FINISH_PID=$1
    wait_for_process_exit "$FINISH_PID"
    # kill -0 is intentionally used for the final decision. process_running
    # also filters zombies and depends on ps(1), while kill -0 ensures a
    # still-live child is killed before the reap can block.
    if kill -0 "$FINISH_PID" 2>/dev/null; then
        kill -KILL "$FINISH_PID" 2>/dev/null || true
    fi
    wait "$FINISH_PID" 2>/dev/null || true
}

stop_process() {
    STOP_PID=$1
    STOP_SIGNAL=${2:-TERM}
    request_stop "$STOP_PID" "$STOP_SIGNAL"
    finish_stop "$STOP_PID"
}

stop_started_processes() {
    if [ -n "${BACKEND_PID:-}" ]; then
        stop_process "$BACKEND_PID" TERM
        BACKEND_PID=
    fi
    if [ -n "${FRONTEND_PID:-}" ]; then
        stop_process "$FRONTEND_PID" TERM
        FRONTEND_PID=
    fi
}

stop_all_processes() {
    if [ -n "${MAINTENANCE_PID:-}" ]; then
        stop_process "$MAINTENANCE_PID" TERM
        MAINTENANCE_PID=
    fi
    stop_started_processes
}

# The EXIT trap is installed before all startup helpers have been evaluated.
# An input preflight can fail before secure_database_files() is declared, so
# cleanup must not replace that useful failure with "not found".
secure_database_files_if_available() {
    if command -v secure_database_files >/dev/null 2>&1; then
        secure_database_files || true
    fi
    return 0
}

cleanup_on_exit() {
    CLEANUP_EXIT_STATUS=$?
    stop_all_processes || true
    secure_database_files_if_available
    # Make the original exit status explicit: cleanup is best effort only.
    exit "$CLEANUP_EXIT_STATUS"
}

wait_either() {
    WAIT_PID_ONE=$1
    WAIT_PID_TWO=$2
    while true; do
        if ! process_running "$WAIT_PID_ONE"; then
            if wait "$WAIT_PID_ONE"; then WAIT_STATUS=0; else WAIT_STATUS=$?; fi
            EXITED_PID=$WAIT_PID_ONE
            REMAINING_PID=$WAIT_PID_TWO
            return "$WAIT_STATUS"
        fi
        if ! process_running "$WAIT_PID_TWO"; then
            if wait "$WAIT_PID_TWO"; then WAIT_STATUS=0; else WAIT_STATUS=$?; fi
            EXITED_PID=$WAIT_PID_TWO
            REMAINING_PID=$WAIT_PID_ONE
            return "$WAIT_STATUS"
        fi
        sleep 0.1
    done
}

# Bounded and proxy-free loopback health check for the backend supervisor gate.
backend_is_healthy() {
    curl -q -s -o /dev/null -w "%{http_code}" \
        --noproxy '*' \
        --connect-timeout "$BACKEND_HEALTH_CONNECT_TIMEOUT_SECONDS" \
        --max-time "$BACKEND_HEALTH_MAX_TIME_SECONDS" \
        -- "$BACKEND_URL/health"
}
# Validate health retry controls are bounded before any health-loop child
# cleanup can be required. This rejects unbounded or malformed attacker input.
validate_health_retry_settings() {
    case "$MAX_BACKEND_HEALTH_RETRIES" in
        ''|*[!0-9]*)
            echo "MAX_BACKEND_HEALTH_RETRIES must be a bounded positive integer." >&2
            return 1
            ;;
    esac
    [ "$MAX_BACKEND_HEALTH_RETRIES" -ge 1 ] || {
        echo "MAX_BACKEND_HEALTH_RETRIES must be greater than 0." >&2
        return 1
    }
    [ "$MAX_BACKEND_HEALTH_RETRIES" -le "$MAX_BACKEND_HEALTH_RETRIES_MAX" ] || {
        echo "MAX_BACKEND_HEALTH_RETRIES must be at most $MAX_BACKEND_HEALTH_RETRIES_MAX." >&2
        return 1
    }

    case "$MAX_BACKEND_HEALTH_RETRY_DELAY" in
        ''|*[!0-9.]*|*.*.*|.*.|.)
            echo "MAX_BACKEND_HEALTH_RETRY_DELAY must be a bounded nonnegative numeric value." >&2
            return 1
            ;;
    esac
    if ! awk -v max_delay="$MAX_BACKEND_HEALTH_RETRY_DELAY_MAX" -v delay="$MAX_BACKEND_HEALTH_RETRY_DELAY" '
        BEGIN {
            if (delay !~ /^[0-9]+(\.[0-9]+)?$/) exit 1
            if (delay < 0) exit 1
            if (delay > max_delay) exit 1
        }
    '; then
        echo "MAX_BACKEND_HEALTH_RETRY_DELAY must be a bounded nonnegative numeric value." >&2
        return 1
    fi
    return 0
}

validate_runtime_timeouts() {
    case "$SHUTDOWN_GRACE_SECONDS" in
        ''|*[!0-9]*)
            echo "SHUTDOWN_GRACE_SECONDS must be a decimal number." >&2
            return 1
            ;;
    esac
    case "$BACKEND_HEALTH_CONNECT_TIMEOUT_SECONDS" in
        ''|*[!0-9]*)
            echo "BACKEND_HEALTH_CONNECT_TIMEOUT_SECONDS must be a decimal number." >&2
            return 1
            ;;
    esac
    case "$BACKEND_HEALTH_MAX_TIME_SECONDS" in
        ''|*[!0-9]*)
            echo "BACKEND_HEALTH_MAX_TIME_SECONDS must be a decimal number." >&2
            return 1
            ;;
    esac

    [ "$SHUTDOWN_GRACE_SECONDS" -ge 1 ] || {
        echo "SHUTDOWN_GRACE_SECONDS must be at least 1." >&2
        return 1
    }
    [ "$BACKEND_HEALTH_CONNECT_TIMEOUT_SECONDS" -ge 1 ] || {
        echo "BACKEND_HEALTH_CONNECT_TIMEOUT_SECONDS must be at least 1." >&2
        return 1
    }
    [ "$BACKEND_HEALTH_MAX_TIME_SECONDS" -ge 1 ] || {
        echo "BACKEND_HEALTH_MAX_TIME_SECONDS must be at least 1." >&2
        return 1
    }

    [ "$SHUTDOWN_GRACE_SECONDS" -le "$SHUTDOWN_GRACE_SECONDS_MAX" ] || {
        echo "SHUTDOWN_GRACE_SECONDS must not exceed $SHUTDOWN_GRACE_SECONDS_MAX." >&2
        return 1
    }
    [ "$BACKEND_HEALTH_CONNECT_TIMEOUT_SECONDS" -le "$BACKEND_HEALTH_CONNECT_TIMEOUT_SECONDS_MAX" ] || {
        echo "BACKEND_HEALTH_CONNECT_TIMEOUT_SECONDS must not exceed $BACKEND_HEALTH_CONNECT_TIMEOUT_SECONDS_MAX." >&2
        return 1
    }
    [ "$BACKEND_HEALTH_MAX_TIME_SECONDS" -le "$BACKEND_HEALTH_MAX_TIME_SECONDS_MAX" ] || {
        echo "BACKEND_HEALTH_MAX_TIME_SECONDS must not exceed $BACKEND_HEALTH_MAX_TIME_SECONDS_MAX." >&2
        return 1
    }

    [ "$BACKEND_HEALTH_CONNECT_TIMEOUT_SECONDS" -le "$BACKEND_HEALTH_MAX_TIME_SECONDS" ] || {
        echo "BACKEND_HEALTH_CONNECT_TIMEOUT_SECONDS must not exceed BACKEND_HEALTH_MAX_TIME_SECONDS." >&2
        return 1
    }
    return 0
}

validate_jellyfin_public_port() {
    case "$JELLYFIN_PUBLIC_PORT" in
        ''|*[!0-9]*)
            echo "JELLYFIN_PUBLIC_PORT must be a decimal TCP port." >&2
            return 1
            ;;
    esac
    [ "$JELLYFIN_PUBLIC_PORT" -ge 1 ] && [ "$JELLYFIN_PUBLIC_PORT" -le 65535 ] || {
        echo "JELLYFIN_PUBLIC_PORT must be between 1 and 65535." >&2
        return 1
    }
}

validate_full_stack_setting() {
    NZBDAV_FULL_STACK=${NZBDAV_FULL_STACK:-false}
    case "$NZBDAV_FULL_STACK" in
        [Tt][Rr][Uu][Ee]) NZBDAV_FULL_STACK=true ;;
        [Ff][Aa][Ll][Ss][Ee]) NZBDAV_FULL_STACK=false ;;
        *)
            echo "NZBDAV_FULL_STACK must be a boolean value." >&2
            return 1
            ;;
    esac
    export NZBDAV_FULL_STACK
}

# Maintenance is a child so TERM/INT can be forwarded to the real migration
# process. Always wait for it; a committed transaction may have happened just
# before the signal, and its durable stage must remain for resume.
run_maintenance_child() {
    MAINTENANCE_CHILD=1 "$@" &
    MAINTENANCE_PID=$!
    if wait "$MAINTENANCE_PID"; then
        MAINTENANCE_STATUS=0
    else
        MAINTENANCE_STATUS=$?
    fi
    MAINTENANCE_PID=
    return "$MAINTENANCE_STATUS"
}

terminate() {
    TERMINATE_SIGNAL=$1
    echo "Caught termination signal. Shutting down..."
    # Signal every child first, so the original TERM/INT is not delayed by a
    # slow sibling. Then reap each one with the bounded grace period.
    if [ -n "${MAINTENANCE_PID:-}" ]; then request_stop "$MAINTENANCE_PID" "$TERMINATE_SIGNAL"; fi
    if [ -n "${BACKEND_PID:-}" ]; then request_stop "$BACKEND_PID" "$TERMINATE_SIGNAL"; fi
    if [ -n "${FRONTEND_PID:-}" ]; then request_stop "$FRONTEND_PID" "$TERMINATE_SIGNAL"; fi
    if [ -n "${MAINTENANCE_PID:-}" ]; then finish_stop "$MAINTENANCE_PID"; MAINTENANCE_PID=; fi
    if [ -n "${BACKEND_PID:-}" ]; then finish_stop "$BACKEND_PID"; BACKEND_PID=; fi
    if [ -n "${FRONTEND_PID:-}" ]; then finish_stop "$FRONTEND_PID"; FRONTEND_PID=; fi
    secure_database_files_if_available
    case "$TERMINATE_SIGNAL" in
        INT) exit 130 ;;
        TERM) exit 143 ;;
        *) exit 128 ;;
    esac
}
trap 'terminate TERM' TERM
trap 'terminate INT' INT
trap 'cleanup_on_exit' EXIT

PUID=${PUID:-1000}
PGID=${PGID:-1000}
FRONTEND_PUID=${FRONTEND_PUID:-1001}
case "$PUID" in ''|*[!0-9]*) echo "PUID must be numeric." >&2; exit 1 ;; esac
case "$PGID" in ''|*[!0-9]*) echo "PGID must be numeric." >&2; exit 1 ;; esac
case "$FRONTEND_PUID" in ''|*[!0-9]*) echo "FRONTEND_PUID must be numeric." >&2; exit 1 ;; esac
[ "$PUID" -ne 0 ] || { echo "PUID must not be root." >&2; exit 1; }
[ "$PGID" -ne 0 ] || { echo "PGID must not be root." >&2; exit 1; }
[ "$FRONTEND_PUID" -ne 0 ] || { echo "FRONTEND_PUID must not be root." >&2; exit 1; }
[ "$FRONTEND_PUID" -ne "$PUID" ] || { echo "FRONTEND_PUID must differ from PUID." >&2; exit 1; }

# Shared media/download volumes are initialized by NZBDAV itself, before any
# backend child is dropped to su-exec. Markers make the safe ownership scan a
# transition operation rather than an unconditional recursive chown.
initialize_shared_volume() {
    SHARED_VOLUME=$1
    shift
    SHARED_MARKER="$SHARED_VOLUME/.nzbdav-ownership"
    [ ! -L "${SHARED_VOLUME%/*}" ] || { echo "Unsafe volume parent: ${SHARED_VOLUME%/*}" >&2; return 1; }
    [ ! -L "$SHARED_VOLUME" ] || { echo "Unsafe volume symlink: $SHARED_VOLUME" >&2; return 1; }
    mkdir -p -- "$SHARED_VOLUME"
    [ -d "$SHARED_VOLUME" ] && [ ! -L "$SHARED_VOLUME" ] || return 1
    # The volume itself must be mounted, while a nested bind/subpath mount is
    # never safe to chown. Device numbers alone cannot detect same-filesystem
    # binds, and mountinfo field 5 is escaped, so use the exact decoder above.
    mountinfo_path_is_mountpoint "$SHARED_VOLUME" || return 1
    mountinfo_path_has_nested_mount "$SHARED_VOLUME" && return 1
    find -P "$SHARED_VOLUME" -xdev -exec /bin/sh -eu -c '
        root=$1; shift
        for item do
            [ ! -L "$item" ] && { [ -d "$item" ] || [ -f "$item" ]; } || exit 1
            if [ -f "$item" ]; then
                [ "$(stat -c "%h" "$item" 2>/dev/null)" = "1" ] || exit 1
            fi
            [ "$(stat -c "%d" "$item" 2>/dev/null)" = "$(stat -c "%d" "$root" 2>/dev/null)" ] || exit 1
        done
    ' sh "$SHARED_VOLUME" {} +
    SHARED_CHILDREN_READY=1
    for SHARED_CHILD in "$@"; do
        case "$SHARED_CHILD" in
            ''|.|..|*[!A-Za-z0-9._-]*)
                echo "Unsafe shared-volume child: $SHARED_CHILD" >&2
                return 1
                ;;
        esac
        SHARED_CHILD_PATH="$SHARED_VOLUME/$SHARED_CHILD"
        [ ! -L "$SHARED_CHILD_PATH" ] || return 1
        mkdir -p -- "$SHARED_CHILD_PATH"
        [ -d "$SHARED_CHILD_PATH" ] && [ ! -L "$SHARED_CHILD_PATH" ] || return 1
        [ "$(stat -c '%u:%g:%a' "$SHARED_CHILD_PATH" 2>/dev/null)" = "$PUID:$PGID:775" ] || SHARED_CHILDREN_READY=0
    done
    if [ -f "$SHARED_MARKER" ] && [ ! -L "$SHARED_MARKER" ] \
        && [ "$(stat -c '%u:%a:%h' "$SHARED_MARKER" 2>/dev/null)" = "0:600:1" ] \
        && [ "$(cat "$SHARED_MARKER" 2>/dev/null)" = "nzbdav-volume-v1:$PUID:$PGID" ] \
        && [ "$SHARED_CHILDREN_READY" = 1 ]; then
        return 0
    fi
    rm -f -- "$SHARED_MARKER"
    # Revalidate after creating categories and before changing ownership.
    find -P "$SHARED_VOLUME" -xdev -exec /bin/sh -eu -c '
        root=$1; shift
        for item do
            [ ! -L "$item" ] && { [ -d "$item" ] || [ -f "$item" ]; } || exit 1
            if [ -f "$item" ]; then
                [ "$(stat -c "%h" "$item" 2>/dev/null)" = "1" ] || exit 1
            fi
            [ "$(stat -c "%d" "$item" 2>/dev/null)" = "$(stat -c "%d" "$root" 2>/dev/null)" ] || exit 1
        done
    ' sh "$SHARED_VOLUME" {} +
    find -P "$SHARED_VOLUME" -xdev -exec chown -h "$PUID:$PGID" '{}' +
    for SHARED_CHILD in "$@"; do
        chmod 775 "$SHARED_VOLUME/$SHARED_CHILD" || return 1
    done
    printf 'nzbdav-volume-v1:%s:%s\n' "$PUID" "$PGID" > "$SHARED_MARKER"
    chown 0:0 "$SHARED_MARKER"
    chmod 600 "$SHARED_MARKER"
}

initialize_full_stack_shared_volumes() {
    [ "$NZBDAV_FULL_STACK" = true ] || return 0
    initialize_shared_volume /media/nzbdav || return 1
    # Compose mounts one completed-downloads volume at its parent in every
    # writer. Its direct category directories are ordinary children, not
    # separately mounted volumes.
    initialize_shared_volume /data/completed-downloads movies tv || return 1
}

validate_full_stack_setting || exit 1
initialize_full_stack_shared_volumes || exit 1

if [ -z "${CONFIG_PATH:-}" ]; then
    export CONFIG_PATH=/config/data
elif [ "$CONFIG_PATH" = /config ]; then
    # Keep the legacy environment spelling from making /config writable.
    export CONFIG_PATH=/config/data
fi
[ "$CONFIG_PATH" = /config/data ] || {
    echo "CONFIG_PATH must be /config/data; /config is root-only." >&2
    exit 1
}
# Compose expands an unset optional override to an empty value. Treat both
# unset and blank as the normal persisted-key path; only a nonblank value is
# handed to the root-only bootstrap secret process.
clear_blank_master_key_override() {
    if [ "${NZBDAV_MASTER_KEY+x}" = x ] && [ -z "$NZBDAV_MASTER_KEY" ]; then
        unset NZBDAV_MASTER_KEY
    fi
}
clear_blank_master_key_override
. "$BOOTSTRAP_SCRIPT"

# Create or reuse group based on PGID.
if getent group "$PGID" >/dev/null; then
    GROUP_NAME=$(getent group "$PGID" | cut -d: -f1)
else
    addgroup -g "$PGID" appgroup
    GROUP_NAME=appgroup
fi

# Create or reuse the backend account.
if getent passwd "$PUID" >/dev/null; then
    USER_NAME=$(getent passwd "$PUID" | cut -d: -f1)
else
    adduser -D -H -u "$PUID" -G "$GROUP_NAME" appuser
    USER_NAME=appuser
fi

# The frontend has a deliberately different UID.  It shares only the group
# needed to bind/listen; it does not own the config database or secret files.
if getent passwd "$FRONTEND_PUID" >/dev/null; then
    FRONTEND_USER=$(getent passwd "$FRONTEND_PUID" | cut -d: -f1)
else
    adduser -D -H -u "$FRONTEND_PUID" -G "$GROUP_NAME" frontenduser
    FRONTEND_USER=frontenduser
fi

export BACKEND_URL=${BACKEND_URL:-http://localhost:8080}
# The health probe is a shell/PID1 security boundary. It must never be an
# operator-controlled curl option, proxy target, or config-file URL. The
# combined image backend listens only on this loopback origin.
case "$BACKEND_URL" in
    http://localhost:8080|http://127.0.0.1:8080|http://\[::1\]:8080) ;;
    *) echo "BACKEND_URL must be the loopback HTTP origin on port 8080." >&2; exit 1 ;;
esac
PORT=${PORT:-3000}
case "$PORT" in
    ''|*[!0-9]*) echo "PORT must be a decimal TCP port." >&2; exit 1 ;;
esac
JELLYFIN_PUBLIC_PORT=${JELLYFIN_PUBLIC_PORT:-${JELLYFIN_PORT:-8096}}
[ "$PORT" -ge 1 ] && [ "$PORT" -le 65535 ] || { echo "PORT must be between 1 and 65535." >&2; exit 1; }
validate_jellyfin_public_port || exit 1
NODE_ENV=${NODE_ENV:-production}
NZBDAV_VERSION=${NZBDAV_VERSION:-unknown}
DISABLE_FRONTEND_AUTH=${DISABLE_FRONTEND_AUTH:-false}
TZ=${TZ:-UTC}
# Keep legacy unset behavior and avoid unbounded loop exits in startup/teardown.
PUBLIC_ORIGIN=${PUBLIC_ORIGIN:-}
JELLYFIN_PUBLIC_URL=${JELLYFIN_PUBLIC_URL:-}
BIND_ADDRESS=${BIND_ADDRESS:-127.0.0.1}
# BIND_ADDRESS controls host publication/cookie policy, not the interface
# used by the frontend process inside this container.
FRONTEND_LISTEN_ADDRESS=${FRONTEND_LISTEN_ADDRESS:-0.0.0.0}
NZBDAV_INSECURE_DEV_COOKIES=${NZBDAV_INSECURE_DEV_COOKIES:-false}
case "$NZBDAV_INSECURE_DEV_COOKIES" in
    true|false) ;;
    *) echo "NZBDAV_INSECURE_DEV_COOKIES must be true or false." >&2; exit 1 ;;
esac
validate_bind_address() {
    case "$BIND_ADDRESS" in
        ""|*[!0-9A-Fa-f:.[\]-]*|*" "*|*'['*|*']'*) echo "BIND_ADDRESS must be a valid IP address or localhost." >&2; return 1 ;;
        localhost|127.0.0.1|::1) return 0 ;;
        0.0.0.0|::) return 0 ;;
    esac
    case "$BIND_ADDRESS" in
        *.*)
            awk -v address="$BIND_ADDRESS" 'BEGIN {
                count=split(address, parts, ".");
                if (count != 4) exit 1;
                for (i=1; i<=4; i++) if (parts[i] !~ /^[0-9]+$/ || parts[i] < 0 || parts[i] > 255) exit 1;
            }' || { echo "BIND_ADDRESS must be a valid IP address or localhost." >&2; return 1; }
            ;;
        *:*)
            awk -v address="$BIND_ADDRESS" 'BEGIN {
                if (address !~ /^[0-9A-Fa-f:]+$/) exit 1;
                compressed = index(address, "::") > 0;
                if (compressed && index(substr(address, index(address, "::") + 2), "::") > 0) exit 1;
                count=split(address, parts, ":"); nonempty=0;
                for (i=1; i<=count; i++) if (parts[i] != "") {
                    if (parts[i] !~ /^[0-9A-Fa-f]{1,4}$/) exit 1; nonempty++;
                }
                if ((!compressed && nonempty != 8) || (compressed && nonempty >= 8)) exit 1;
            }' || { echo "BIND_ADDRESS must be a valid IP address or localhost." >&2; return 1; }
            ;;
        *) echo "BIND_ADDRESS must be a valid IP address or localhost." >&2; return 1 ;;
    esac
}
validate_bind_address || exit 1
# HTTPS is authoritative: an insecure explicit override cannot weaken it.
case "$PUBLIC_ORIGIN" in
    https://*) SECURE_COOKIES=true ;;
    *) SECURE_COOKIES=${SECURE_COOKIES:-false} ;;
esac
case "$SECURE_COOKIES" in true|false) ;; *) echo "SECURE_COOKIES must be true or false." >&2; exit 1 ;; esac
case "$BIND_ADDRESS" in
    localhost|127.0.0.1|::1) ;;
    *) if [ "$SECURE_COOKIES" = false ] && [ "$NZBDAV_INSECURE_DEV_COOKIES" != true ]; then
           echo "Non-loopback HTTP requires NZBDAV_INSECURE_DEV_COOKIES=true acknowledgement or Secure cookies." >&2
           exit 1
       fi ;;
esac
export SECURE_COOKIES
MAX_BACKEND_HEALTH_RETRIES=${MAX_BACKEND_HEALTH_RETRIES:-30}
MAX_BACKEND_HEALTH_RETRY_DELAY=${MAX_BACKEND_HEALTH_RETRY_DELAY:-1}
# Bounded retry controls prevent supervisor hangs from attacker-configured
# timing abuse while preserving practical startup flexibility.
MAX_BACKEND_HEALTH_RETRIES_MAX=300
MAX_BACKEND_HEALTH_RETRY_DELAY_MAX=30
BACKEND_HEALTH_CONNECT_TIMEOUT_SECONDS=${BACKEND_HEALTH_CONNECT_TIMEOUT_SECONDS:-1}
BACKEND_HEALTH_MAX_TIME_SECONDS=${BACKEND_HEALTH_MAX_TIME_SECONDS:-3}
SHUTDOWN_GRACE_SECONDS_MAX=120
BACKEND_HEALTH_CONNECT_TIMEOUT_SECONDS_MAX=30
BACKEND_HEALTH_MAX_TIME_SECONDS_MAX=60
validate_runtime_timeouts || exit 1
validate_health_retry_settings || exit 1

# SQLite sidecars are data, not optional convenience files: reject links and
# non-regular objects before handing the directory to the backend. Migration
# is run while the backend is stopped, and the invariant is re-established
# after each maintenance child because SQLite may create WAL/SHM files.
secure_database_files() {
    DATABASE_BASENAME=${DATABASE_BASENAME:-db.sqlite}
    case "$DATABASE_BASENAME" in
        *[!A-Za-z0-9._-]*)
            echo "Unsafe SQLite database path: ${DATABASE_BASENAME}" >&2
            return 1
            ;;
    esac
    for DATABASE_SUFFIX in "" "-wal" "-shm"; do
        DATABASE_PATH="$CONFIG_PATH/$DATABASE_BASENAME$DATABASE_SUFFIX"
        if [ -e "$DATABASE_PATH" ] || [ -L "$DATABASE_PATH" ]; then
            [ -f "$DATABASE_PATH" ] && [ ! -L "$DATABASE_PATH" ] || {
                echo "Unsafe SQLite database path: ${DATABASE_BASENAME}${DATABASE_SUFFIX}" >&2
                return 1
            }
            [ "$(stat -c '%h' "$DATABASE_PATH" 2>/dev/null)" = "1" ] || {
                echo "Unsafe SQLite database path: ${DATABASE_BASENAME}${DATABASE_SUFFIX}" >&2
                return 1
            }
            chown "$PUID:$PGID" "$DATABASE_PATH" || return 1
            chmod 600 "$DATABASE_PATH" || return 1
        fi
    done
}

# Validate the complete writable tree before changing any ownership. This is
# deliberately two-pass: a hostile later entry must not leave a partially
# re-owned tree. Links, devices, nested mounts, and hardlinked files fail.
data_tree_is_safe() {
    [ -d "$CONFIG_PATH" ] && [ ! -L "$CONFIG_PATH" ] || return 1
    [ "$(stat -c '%d' "$CONFIG_PATH" 2>/dev/null)" = "$(stat -c '%d' "$CONFIG_ROOT" 2>/dev/null)" ] || return 1
    # CONFIG_ROOT is the expected volume mount; CONFIG_PATH must remain an
    # ordinary child so a nested bind cannot escape the ownership boundary.
    mountinfo_path_is_mountpoint "$CONFIG_PATH" && return 1
    mountinfo_path_has_nested_mount "$CONFIG_PATH" && return 1
    find "$CONFIG_PATH" -exec /bin/sh -c '
        root=$1; shift
        for item do
            [ ! -L "$item" ] && { [ -d "$item" ] || [ -f "$item" ]; } || exit 1
            if [ -f "$item" ]; then
                [ "$(stat -c "%h" "$item" 2>/dev/null)" = "1" ] || exit 1
            fi
            [ "$(stat -c "%d" "$item" 2>/dev/null)" = "$(stat -c "%d" "$root" 2>/dev/null)" ] || exit 1
        done
    ' sh "$CONFIG_PATH" {} +
}

reown_data_tree() {
    DATA_MARKER="$CONFIG_PATH/.nzbdav-ownership"
    data_tree_is_safe || return 1
    if [ -f "$DATA_MARKER" ] && [ ! -L "$DATA_MARKER" ] \
        && [ "$(stat -c '%u:%a:%h' "$DATA_MARKER" 2>/dev/null)" = "0:600:1" ] \
        && [ "$(stat -c '%u:%a' "$CONFIG_PATH" 2>/dev/null)" = "$PUID:700" ] \
        && [ "$(cat "$DATA_MARKER" 2>/dev/null)" = "nzbdav-data-v1:$PUID:$PGID" ]; then
        return 0
    fi
    rm -f -- "$DATA_MARKER"
    data_tree_is_safe || return 1
    find "$CONFIG_PATH" -exec /bin/sh -c '
        uid=$1; gid=$2; shift 2
        for item do chown "$uid:$gid" "$item" || exit 1; done
    ' sh "$PUID" "$PGID" {} +
    printf 'nzbdav-data-v1:%s:%s\n' "$PUID" "$PGID" > "$DATA_MARKER"
    chown 0:0 "$DATA_MARKER"
    chmod 600 "$DATA_MARKER"
}

# The backend inherits only its own deployment environment. In particular,
# SESSION_KEY is frontend-only and must not be observable in /proc for any
# migration, maintenance, or runtime backend child. Setup URLs are retained
# for the runtime backend, but are unnecessary in maintenance children. The
# fixed image commands are ./NzbWebDAV --db-migration and
# ./NzbWebDAV --encryption-maintenance.
run_backend() {
    if [ "${MAINTENANCE_CHILD:-0}" = 1 ]; then
        unset SETUP_NZBDAV_URL SETUP_JELLYFIN_URL SETUP_SONARR_URL SETUP_RADARR_URL SETUP_PROWLARR_URL
    fi
    exec env \
        -u SESSION_KEY \
        -u PORT \
        -u NODE_ENV \
        -u DISABLE_FRONTEND_AUTH \
        -u SECURE_COOKIES \
        -u BACKEND_URL \
        -u NZBDAV_VERSION \
        -u FRONTEND_PUID \
        CONFIG_PATH="$CONFIG_PATH" \
        su-exec "$PUID:$PGID" ./NzbWebDAV "$@"
}

# Secret files remain root-owned for their entire lifetime.  The backend gets
# its master key through its inherited environment, never through file access.
[ -d "$CONFIG_ROOT" ] && [ ! -L "$CONFIG_ROOT" ] || exit 1
[ "$(stat -c '%u:%a' "$CONFIG_ROOT")" = 0:755 ] || exit 1
[ -d "$CONFIG_PATH" ] && [ ! -L "$CONFIG_PATH" ] || exit 1
reown_data_tree || exit 1
chmod 700 "$CONFIG_PATH"
[ "$(stat -c '%u:%a' "$CONFIG_ROOT")" = 0:755 ] || exit 1
[ "$(stat -c '%u:%a' "$CONFIG_PATH")" = "$PUID:700" ] || exit 1
[ "$(stat -c '%u:%a' "$BOOTSTRAP_SECRET_DIR")" = 0:700 ] || exit 1
for bootstrap_file in "$BOOTSTRAP_MASTER_FILE" "$BOOTSTRAP_API_FILE" "$BOOTSTRAP_SESSION_FILE"; do
    [ ! -L "$bootstrap_file" ] && [ -f "$bootstrap_file" ] || exit 1
    [ "$(stat -c '%u:%a:%h' "$bootstrap_file")" = 0:600:1 ] || exit 1
done
if [ "${BOOTSTRAP_MASTER_ROTATION_REQUIRED:-0}" = 1 ]; then
    [ ! -L "$BOOTSTRAP_MASTER_STAGED_FILE" ] && [ -f "$BOOTSTRAP_MASTER_STAGED_FILE" ] || exit 1
    [ "$(stat -c '%u:%a:%h' "$BOOTSTRAP_MASTER_STAGED_FILE")" = 0:600:1 ] || exit 1
fi

# Only regular SQLite files are handed to the backend. Never recursively
# chown CONFIG_PATH: that would change the ownership invariant of secrets.
secure_database_files || exit 1

cd "$BACKEND_DIR"
echo "Running database migration."
if run_maintenance_child run_backend --db-migration; then
    :
else
    MAINTENANCE_STATUS=$?
    stop_started_processes
    secure_database_files || true
    exit "$MAINTENANCE_STATUS"
fi
secure_database_files || exit 1

if [ "${BOOTSTRAP_MASTER_ROTATION_REQUIRED:-0}" = 1 ]; then
    # Both values are inherited by the real maintenance process. Promotion is
    # impossible until this child exits successfully; the stage is retained on
    # every failure and restart discovers it from its fixed filename.
    export NZBDAV_MASTER_KEY="$BOOTSTRAP_STAGED_MASTER_KEY"
    export NZBDAV_MASTER_KEY_OLD="$BOOTSTRAP_OLD_MASTER_KEY"
    echo "Running encryption maintenance."
    if run_maintenance_child run_backend --encryption-maintenance; then
        :
    else
        MAINTENANCE_STATUS=$?
        stop_started_processes
        secure_database_files || true
        exit "$MAINTENANCE_STATUS"
    fi
    secure_database_files || exit 1
    if ! bootstrap_promote_master_key; then
        secure_database_files || true
        exit 1
    fi
    echo "Encryption maintenance and master-key promotion complete."
    unset NZBDAV_MASTER_KEY_OLD
    unset BOOTSTRAP_MASTER_ROTATION_REQUIRED BOOTSTRAP_MASTER_STAGED_FILE BOOTSTRAP_OLD_MASTER_KEY BOOTSTRAP_STAGED_MASTER_KEY
fi
secure_database_files || exit 1

echo "Done with database maintenance."

run_backend &
BACKEND_PID=$!
echo "Waiting for backend to start."
i=0
while true; do
    if ! validate_health_retry_settings; then
        echo "Invalid backend health retry configuration while waiting for backend health; stopping started processes." >&2
        stop_started_processes
        secure_database_files || true
        exit 1
    fi
    if [ "$(backend_is_healthy)" = "200" ]; then
        echo "Backend is healthy."
        break
    fi
    i=$((i + 1))
    if [ "$i" -ge "$MAX_BACKEND_HEALTH_RETRIES" ]; then
        echo "Backend failed health checks; stopping started processes." >&2
        # Do not use an unbounded wait here: a failed backend may ignore TERM.
        # Stop every process that has been started through the same bounded
        # TERM-grace/KILL/reap path used by signal shutdown.
        stop_started_processes
        secure_database_files || true
        exit 1
    fi
    sleep "$MAX_BACKEND_HEALTH_RETRY_DELAY"
done

# The frontend receives an explicit, auditable allowlist. In particular it
# cannot read either master key/stage, CONFIG_PATH, or implementation secrets.
unset NZBDAV_MASTER_KEY NZBDAV_MASTER_KEY_OLD
unset BOOTSTRAP_SECRET_DIR BOOTSTRAP_MASTER_FILE BOOTSTRAP_API_FILE BOOTSTRAP_SESSION_FILE
unset BOOTSTRAP_MASTER_ROTATION_REQUIRED BOOTSTRAP_MASTER_STAGED_FILE BOOTSTRAP_OLD_MASTER_KEY BOOTSTRAP_STAGED_MASTER_KEY
cd "$FRONTEND_DIR"
# Frontend session state is private to its UID and bound to the stable
# SESSION_KEY by a one-way directory identifier. Docker restart preserves the
# writable container layer, while Compose recreation starts without this /run
# directory; a SESSION_KEY rotation selects a new registry and invalidates the
# old opaque cookies. The key itself is never sent to the frontend as a path.
FRONTEND_SESSION_STORE_KEY_ID=$(printf '%s' "$SESSION_KEY" | sha256sum | awk '{print $1}')
[ "$(printf '%s' "$FRONTEND_SESSION_STORE_KEY_ID" | wc -c)" = 64 ] || {
    echo "Unable to prepare frontend session storage." >&2
    exit 1
}
case "$FRONTEND_SESSION_STORE_KEY_ID" in
    *[!0-9a-f]*) echo "Unable to prepare frontend session storage." >&2; exit 1 ;;
esac
FRONTEND_SESSION_STORE_DIR="/run/nzbdav-frontend-sessions-$FRONTEND_SESSION_STORE_KEY_ID"
if [ -e "$FRONTEND_SESSION_STORE_DIR" ] || [ -L "$FRONTEND_SESSION_STORE_DIR" ]; then
    # This directory was created before the frontend account starts. Never
    # follow a frontend-controlled link or repair a changed owner/mode as root.
    [ -d "$FRONTEND_SESSION_STORE_DIR" ] && [ ! -L "$FRONTEND_SESSION_STORE_DIR" ] || exit 1
    [ "$(stat -c '%u:%g:%a' "$FRONTEND_SESSION_STORE_DIR")" = "$FRONTEND_PUID:$PGID:700" ] || exit 1
else
    mkdir -- "$FRONTEND_SESSION_STORE_DIR"
    chown "$FRONTEND_PUID:$PGID" "$FRONTEND_SESSION_STORE_DIR"
    chmod 700 "$FRONTEND_SESSION_STORE_DIR"
fi
[ "$(stat -c '%u:%g:%a' "$FRONTEND_SESSION_STORE_DIR")" = "$FRONTEND_PUID:$PGID:700" ] || exit 1
unset FRONTEND_SESSION_STORE_KEY_ID
export FRONTEND_SESSION_STORE_DIR

# Secret values are fed over stdin rather than appearing in env(1)'s argv.
# The child still starts from env -i, so no operator or backend-only variable
# leaks into the frontend process.
{
    printf '%s\n' "$FRONTEND_BACKEND_API_KEY"
    printf '%s\n' "$SESSION_KEY"
} | su-exec "$FRONTEND_PUID:$PGID" env -i \
    PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin \
    BACKEND_URL="$BACKEND_URL" \
    NODE_ENV="$NODE_ENV" \
    PORT="$PORT" \
    NZBDAV_VERSION="$NZBDAV_VERSION" \
    NZBDAV_FULL_STACK="$NZBDAV_FULL_STACK" \
    JELLYFIN_PUBLIC_PORT="$JELLYFIN_PUBLIC_PORT" \
    JELLYFIN_PUBLIC_URL="$JELLYFIN_PUBLIC_URL" \
    PUBLIC_ORIGIN="$PUBLIC_ORIGIN" \
    DISABLE_FRONTEND_AUTH="$DISABLE_FRONTEND_AUTH" \
    SECURE_COOKIES="$SECURE_COOKIES" \
    BIND_ADDRESS="$BIND_ADDRESS" \
    FRONTEND_LISTEN_ADDRESS="$FRONTEND_LISTEN_ADDRESS" \
    NZBDAV_INSECURE_DEV_COOKIES="$NZBDAV_INSECURE_DEV_COOKIES" \
    TZ="$TZ" \
    FRONTEND_SESSION_STORE_DIR="$FRONTEND_SESSION_STORE_DIR" \
    /bin/sh -c '
        IFS= read -r FRONTEND_BACKEND_API_KEY || exit 1
        IFS= read -r SESSION_KEY || exit 1
        export FRONTEND_BACKEND_API_KEY SESSION_KEY
        exec node dist-node/server.js
    ' &
FRONTEND_PID=$!

if wait_either "$BACKEND_PID" "$FRONTEND_PID"; then
    EXIT_CODE=0
else
    EXIT_CODE=$?
fi
if [ "$EXITED_PID" -eq "$FRONTEND_PID" ]; then
    echo "The web-frontend has exited. Shutting down the web-backend..."
else
    echo "The web-backend has exited. Shutting down the web-frontend..."
fi
# The survivor gets a graceful signal and is reaped before PID 1 exits.
# This prevents an orphaned server from outliving the supervisor.
stop_process "$REMAINING_PID" TERM
exit "$EXIT_CODE"
