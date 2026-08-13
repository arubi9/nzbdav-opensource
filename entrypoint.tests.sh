#!/bin/sh
# Focused supervisor tests. Run on Alpine (or another Linux image) with ps.
set -eu

ROOT=$(CDPATH= cd -- "$(dirname "$0")" && pwd)
export ROOT
SCRIPT=$ROOT/entrypoint.sh
export SCRIPT
TMP=${TMPDIR:-/tmp}/nzbdav-entrypoint-tests.$$
export TMP
ACTUAL_MOUNT_TARGETS=
mkdir -p "$TMP"
cleanup_actual_mounts() {
    for target in $ACTUAL_MOUNT_TARGETS; do
        umount "$target" >/dev/null 2>&1 || true
    done
    ACTUAL_MOUNT_TARGETS=
}
trap 'cleanup_actual_mounts; rm -rf "$TMP"' EXIT HUP INT TERM
if [ "$(uname -s)" != Linux ] || ! command -v ps >/dev/null 2>&1; then
    echo 'entrypoint supervisor test skipped: Linux ps is unavailable'
    exit 0
fi
REAL_PYTHON=$(command -v python3) || {
    echo 'entrypoint supervisor test skipped: Python 3 is unavailable'
    exit 0
}
export REAL_PYTHON

# Load only the supervisor helpers; sourcing the production entrypoint would
# intentionally perform its image startup work. Extract named function
# definitions rather than depending on source-file
# order or line numbers. This keeps production startup statements out of the
# test shell while retaining helpers that are defined later in entrypoint.sh.
ENTRYPOINT_TEST_FUNCTIONS='process_running wait_for_process_exit request_stop finish_stop stop_process stop_started_processes stop_all_processes secure_database_files_if_available cleanup_on_exit wait_either backend_is_healthy terminate validate_health_retry_settings validate_runtime_timeouts validate_jellyfin_public_port run_maintenance_child clear_blank_master_key_override secure_database_files data_tree_is_safe initialize_full_stack_shared_volumes initialize_shared_volume mountinfo_path_is_under mountinfo_path_is_mountpoint mountinfo_path_has_nested_mount'
export ENTRYPOINT_TEST_FUNCTIONS
awk '
    BEGIN {
        split(ENVIRON["ENTRYPOINT_TEST_FUNCTIONS"], names)
        for (name in names) required[names[name]] = 1
        capturing = 0
        depth = 0
    }
    # The selected production functions use balanced shell syntax. Count both
    # shell blocks and nested command-language blocks, and never evaluate them.
    function brace_delta(line, braces, opens, closes) {
        braces = line
        opens = braces
        gsub(/[^{}]/, "", opens)
        gsub(/}/, "", opens)
        closes = braces
        gsub(/[^{}]/, "", closes)
        gsub(/\{/, "", closes)
        return length(opens) - length(closes)
    }
    {
        if (!capturing) {
            candidate = $0
            sub(/^[[:space:]]*/, "", candidate)
            if (candidate ~ /^[A-Za-z_][A-Za-z0-9_]*[[:space:]]*\(\)[[:space:]]*\{[[:space:]]*$/) {
                name = candidate
                sub(/[[:space:]]*\(\).*/, "", name)
                if (name in required) {
                    capturing = 1
                    depth = brace_delta($0)
                    print
                    next
                }
            }
        } else {
            print
            depth += brace_delta($0)
            if (depth == 0) capturing = 0
        }
    }
    END {
        if (capturing) exit 1
    }
' "$SCRIPT" >"$TMP/helpers.sh"
for required_helper in $ENTRYPOINT_TEST_FUNCTIONS; do
    if ! grep -Fq "$required_helper() {" "$TMP/helpers.sh"; then
        echo "entrypoint supervisor test failed: production helper $required_helper was not extracted" >&2
        exit 1
    fi
done
cat >>"$TMP/helpers.sh" <<'EOF'
# Keep the extracted signal and cleanup behavior without sourcing production main.
trap 'terminate TERM' TERM
trap 'terminate INT' INT
trap 'cleanup_on_exit' EXIT
SHUTDOWN_GRACE_SECONDS=1
CONFIG_PATH=${CONFIG_PATH:-$TMP}
BACKEND_PID=
FRONTEND_PID=

fake_backend() {
    trap '' TERM
    while :; do
        sleep 1
    done
}

run_health_retries() {
    retries=0
    while true; do
        if [ "$(backend_is_healthy)" = "200" ]; then
            return 0
        fi
        retries=$((retries + 1))
        if [ "$retries" -ge "$MAX_BACKEND_HEALTH_RETRIES" ]; then
            return 1
        fi
        sleep "$MAX_BACKEND_HEALTH_RETRY_DELAY"
    done
}

# Ensure helper extraction includes cleanup-on-exit behavior for lingering children.
cleanup_exit_test() {
    (
        . "$TMP/helpers.sh"
        fake_backend >/dev/null 2>&1 &
        BACKEND_PID=$!
        printf '%s\n' "$BACKEND_PID" > "$TMP/cleanup-exit.pid"
    )
    cat "$TMP/cleanup-exit.pid"
}
EOF

cat >"$TMP/run-tests.sh" <<'EOF'
#!/bin/sh
set -eu

. "$TMP/helpers.sh"

fail() {
    echo "entrypoint supervisor test failed: $1" >&2
    exit 1
}

MAX_BACKEND_HEALTH_RETRIES_MAX=300
MAX_BACKEND_HEALTH_RETRY_DELAY_MAX=30
MAX_BACKEND_HEALTH_RETRIES=0
if validate_health_retry_settings; then
    fail "MAX_BACKEND_HEALTH_RETRIES=0 should fail"
fi
MAX_BACKEND_HEALTH_RETRIES=abc
if validate_health_retry_settings; then
    fail "non-numeric MAX_BACKEND_HEALTH_RETRIES should fail"
fi
MAX_BACKEND_HEALTH_RETRIES=301
if validate_health_retry_settings; then
    fail "unbounded MAX_BACKEND_HEALTH_RETRIES should fail"
fi
MAX_BACKEND_HEALTH_RETRIES=1
MAX_BACKEND_HEALTH_RETRY_DELAY=-1
if validate_health_retry_settings; then
    fail "negative MAX_BACKEND_HEALTH_RETRY_DELAY should fail"
fi
MAX_BACKEND_HEALTH_RETRY_DELAY=abc
if validate_health_retry_settings; then
    fail "non-numeric MAX_BACKEND_HEALTH_RETRY_DELAY should fail"
fi
MAX_BACKEND_HEALTH_RETRY_DELAY=31
if validate_health_retry_settings; then
    fail "unbounded MAX_BACKEND_HEALTH_RETRY_DELAY should fail"
fi
MAX_BACKEND_HEALTH_RETRY_DELAY=0

SHUTDOWN_GRACE_SECONDS_MAX=120
BACKEND_HEALTH_CONNECT_TIMEOUT_SECONDS_MAX=30
BACKEND_HEALTH_MAX_TIME_SECONDS_MAX=60

SHUTDOWN_GRACE_SECONDS=1
BACKEND_HEALTH_CONNECT_TIMEOUT_SECONDS=1
BACKEND_HEALTH_MAX_TIME_SECONDS=3
SHUTDOWN_GRACE_SECONDS=0
if validate_runtime_timeouts; then
    fail "SHUTDOWN_GRACE_SECONDS=0 should fail"
fi
SHUTDOWN_GRACE_SECONDS=1
BACKEND_HEALTH_CONNECT_TIMEOUT_SECONDS=1
BACKEND_HEALTH_MAX_TIME_SECONDS=3
SHUTDOWN_GRACE_SECONDS=121
if validate_runtime_timeouts; then
    fail "oversized SHUTDOWN_GRACE_SECONDS should fail"
fi
SHUTDOWN_GRACE_SECONDS=1
BACKEND_HEALTH_CONNECT_TIMEOUT_SECONDS=1
BACKEND_HEALTH_MAX_TIME_SECONDS=3
BACKEND_HEALTH_CONNECT_TIMEOUT_SECONDS=abc
if validate_runtime_timeouts; then
    fail "non-numeric BACKEND_HEALTH_CONNECT_TIMEOUT_SECONDS should fail"
fi
SHUTDOWN_GRACE_SECONDS=1
BACKEND_HEALTH_CONNECT_TIMEOUT_SECONDS=1
BACKEND_HEALTH_MAX_TIME_SECONDS=3
BACKEND_HEALTH_CONNECT_TIMEOUT_SECONDS=31
if validate_runtime_timeouts; then
    fail "oversized BACKEND_HEALTH_CONNECT_TIMEOUT_SECONDS should fail"
fi
SHUTDOWN_GRACE_SECONDS=1
BACKEND_HEALTH_CONNECT_TIMEOUT_SECONDS=1
BACKEND_HEALTH_MAX_TIME_SECONDS=3
BACKEND_HEALTH_MAX_TIME_SECONDS=abc
if validate_runtime_timeouts; then
    fail "non-numeric BACKEND_HEALTH_MAX_TIME_SECONDS should fail"
fi
SHUTDOWN_GRACE_SECONDS=1
BACKEND_HEALTH_CONNECT_TIMEOUT_SECONDS=1
BACKEND_HEALTH_MAX_TIME_SECONDS=3
BACKEND_HEALTH_MAX_TIME_SECONDS=61
if validate_runtime_timeouts; then
    fail "oversized BACKEND_HEALTH_MAX_TIME_SECONDS should fail"
fi
SHUTDOWN_GRACE_SECONDS=1
BACKEND_HEALTH_CONNECT_TIMEOUT_SECONDS=1
BACKEND_HEALTH_MAX_TIME_SECONDS=3
BACKEND_HEALTH_MAX_TIME_SECONDS=1
BACKEND_HEALTH_CONNECT_TIMEOUT_SECONDS=2
if validate_runtime_timeouts; then
    fail "BACKEND_HEALTH_MAX_TIME_SECONDS must be at least connect timeout"
fi
SHUTDOWN_GRACE_SECONDS=1
BACKEND_HEALTH_CONNECT_TIMEOUT_SECONDS=1
BACKEND_HEALTH_MAX_TIME_SECONDS=3
SHUTDOWN_GRACE_SECONDS=10
BACKEND_HEALTH_CONNECT_TIMEOUT_SECONDS=1
BACKEND_HEALTH_MAX_TIME_SECONDS=3
if ! validate_runtime_timeouts; then
    fail "default timing limits should pass"
fi

JELLYFIN_PUBLIC_PORT=0
if validate_jellyfin_public_port; then
    fail "JELLYFIN_PUBLIC_PORT=0 should fail"
fi
JELLYFIN_PUBLIC_PORT=abc
if validate_jellyfin_public_port; then
    fail "non-numeric JELLYFIN_PUBLIC_PORT should fail"
fi
JELLYFIN_PUBLIC_PORT=65536
if validate_jellyfin_public_port; then
    fail "JELLYFIN_PUBLIC_PORT above 65535 should fail"
fi
JELLYFIN_PUBLIC_PORT=18096
if ! validate_jellyfin_public_port; then
    fail "valid JELLYFIN_PUBLIC_PORT should pass"
fi
unset JELLYFIN_PUBLIC_PORT
JELLYFIN_PORT=18096
JELLYFIN_PUBLIC_PORT=${JELLYFIN_PUBLIC_PORT:-${JELLYFIN_PORT:-8096}}
[ "$JELLYFIN_PUBLIC_PORT" -eq 18096 ] || fail "JELLYFIN_PUBLIC_PORT should default to JELLYFIN_PORT"
unset JELLYFIN_PORT
unset JELLYFIN_PUBLIC_PORT

# Source and final image startup both depend on an LF-terminated /bin/sh
# shebang. Keep this byte-level assertion next to the supervisor contract so
# a Windows checkout cannot make Docker report a misleading "not found".
"$REAL_PYTHON" - "$SCRIPT" <<'PY'
import sys
from pathlib import Path

entrypoint = Path(sys.argv[1]).read_bytes()
if b"\x0d" in entrypoint or not entrypoint.startswith(b"#!/bin/sh\n"):
    raise SystemExit("root entrypoint must use LF-only #!/bin/sh")
PY

# The frontend's restart-persistent registry is keyed by a one-way
# SESSION_KEY identifier. A pre-existing path is fail-closed: a frontend user
# cannot substitute a link or obtain a root ownership repair on restart.
grep -qF 'FRONTEND_SESSION_STORE_KEY_ID=$(printf' "$SCRIPT"
grep -qF 'sha256sum' "$SCRIPT"
grep -qF 'FRONTEND_SESSION_STORE_DIR="/run/nzbdav-frontend-sessions-$FRONTEND_SESSION_STORE_KEY_ID"' "$SCRIPT"
grep -qF '[ -d "$FRONTEND_SESSION_STORE_DIR" ] && [ ! -L "$FRONTEND_SESSION_STORE_DIR" ] || exit 1' "$SCRIPT"
grep -qF "stat -c" "$SCRIPT"
grep -qF 'FRONTEND_SESSION_STORE_DIR="$FRONTEND_SESSION_STORE_DIR"' "$SCRIPT"
if grep -qF 'rm -rf -- "$FRONTEND_SESSION_STORE_DIR"' "$SCRIPT"; then
    fail "frontend session registry must survive a same-container restart"
fi

# A synthetic runtime launch with unset browser metadata variables reaches the
# frontend launch path under `set -u` without unbound-variable failures.
ALPINE_RUNTIME_DIR=$TMP/alpine-runtime
mkdir -p "$ALPINE_RUNTIME_DIR/bin"
cat >"$ALPINE_RUNTIME_DIR/launch.sh" <<'LAUNCH_EOF'
#!/bin/sh
set -eu
JELLYFIN_PUBLIC_PORT=${JELLYFIN_PUBLIC_PORT:-${JELLYFIN_PORT:-8096}}
PUBLIC_ORIGIN=${PUBLIC_ORIGIN:-}
JELLYFIN_PUBLIC_URL=${JELLYFIN_PUBLIC_URL:-}
{
    printf '%s\n' "frontend-api-key"
    printf '%s\n' "frontend-session"
} | su-exec frontenduser env -i \
    BACKEND_URL="http://127.0.0.1:8080" \
    PORT="3000" \
    NZBDAV_VERSION="unknown" \
    NZBDAV_FULL_STACK="false" \
    JELLYFIN_PUBLIC_PORT="$JELLYFIN_PUBLIC_PORT" \
    JELLYFIN_PUBLIC_URL="$JELLYFIN_PUBLIC_URL" \
    PUBLIC_ORIGIN="$PUBLIC_ORIGIN" \
    /bin/sh -c '
        IFS= read -r FRONTEND_BACKEND_API_KEY
        IFS= read -r SESSION_KEY
        [ -n "$FRONTEND_BACKEND_API_KEY" ]
        [ -n "$SESSION_KEY" ]
        [ -z "$PUBLIC_ORIGIN" ]
        [ -z "$JELLYFIN_PUBLIC_URL" ]
        [ "$JELLYFIN_PUBLIC_PORT" -eq 18096 ]
        exit 0
    '
LAUNCH_EOF
cat >"$ALPINE_RUNTIME_DIR/bin/su-exec" <<'SUEXEC_EOF'
#!/bin/sh
shift
exec "$@"
SUEXEC_EOF
chmod +x "$ALPINE_RUNTIME_DIR/launch.sh" "$ALPINE_RUNTIME_DIR/bin/su-exec"
if ! env -i \
    PATH="$ALPINE_RUNTIME_DIR/bin:$PATH" \
    JELLYFIN_PORT=18096 \
    BACKEND_URL="http://127.0.0.1:8080" \
    PORT="3000" \
    NZBDAV_VERSION="unknown" \
    NZBDAV_FULL_STACK="false" \
    sh "$ALPINE_RUNTIME_DIR/launch.sh" >"$TMP/alpine-runtime.stdout" 2>"$TMP/alpine-runtime.stderr"; then
    fail "standalone unset public metadata launch should pass under set -u"
fi
if [ -s "$TMP/alpine-runtime.stderr" ]; then
    fail "standalone launch test should print no stderr"
fi


# Compose's optional key interpolation is blank when no override is set. Use
# the actual entrypoint helper to prove blank is removed before bootstrap,
# while a nonblank value remains available to that root-only process.
NZBDAV_MASTER_KEY=
clear_blank_master_key_override
[ "${NZBDAV_MASTER_KEY+x}" != x ] || fail "blank master-key override was not cleared"
NZBDAV_MASTER_KEY=nonblank-test-value
clear_blank_master_key_override
[ "$NZBDAV_MASTER_KEY" = nonblank-test-value ] || fail "nonblank master-key override was cleared"
unset NZBDAV_MASTER_KEY

# Exercise the actual root secret process after the actual entrypoint guard:
# blank input must select/generated the persisted path rather than persist an
# empty key or request a rotation.
blank_bootstrap="$TMP/blank-bootstrap"
mkdir -p "$blank_bootstrap"
(
    NZBDAV_MASTER_KEY=
    CONFIG_PATH="$blank_bootstrap"
    clear_blank_master_key_override
    . "$ROOT/bootstrap-secrets.sh"
    test -s "$blank_bootstrap/bootstrap-secrets/nzbdav-master-key"
    test -n "$(cat "$blank_bootstrap/bootstrap-secrets/nzbdav-master-key")"
)

# health-failure cleanup path: fake backend ignores TERM, so completion proves
# the KILL fallback and bounded reap rather than a graceful exit masking
# an unbounded wait.
# Runtime creates an empty emergency-journal lock on the first startup. The
# next startup must accept this ordinary zero-byte file while validating the
# backend-owned tree; GNU stat calls it a "regular empty file", not a
# "regular file". This is the exact pre-maintenance restart boundary.
EMPTY_LOCK_RESTART_ROOT=$TMP/empty-lock-restart
EMPTY_LOCK_RESTART_DATA=$EMPTY_LOCK_RESTART_ROOT/data
mkdir -p "$EMPTY_LOCK_RESTART_DATA/.setup-emergency"
: >"$EMPTY_LOCK_RESTART_DATA/.setup-emergency/.lock"
(
    CONFIG_ROOT=$EMPTY_LOCK_RESTART_ROOT
    CONFIG_PATH=$EMPTY_LOCK_RESTART_DATA
    if ! data_tree_is_safe; then
        fail "data_tree_is_safe rejected the runtime empty emergency-journal lock on restart"
    fi
)
(
    fake_backend
) &
FAKE_BACKEND_PID=$!
BACKEND_PID=$FAKE_BACKEND_PID
stop_started_processes
[ -z "$BACKEND_PID" ] || fail "BACKEND_PID was not cleared"
! kill -0 "$FAKE_BACKEND_PID" 2>/dev/null || fail "backend was not killed"

# FRONTEND_PID should use the same bounded stop path.
(
    fake_backend
) &
FAKE_FRONTEND_PID=$!
FRONTEND_PID=$FAKE_FRONTEND_PID
stop_started_processes
[ -z "$FRONTEND_PID" ] || fail "FRONTEND_PID was not cleared"
! kill -0 "$FAKE_FRONTEND_PID" 2>/dev/null || fail "frontend was not killed"

# Sourced helper must perform deterministic cleanup on EXIT.
TRAPPED_PID=$(cleanup_exit_test)
! kill -0 "$TRAPPED_PID" 2>/dev/null || fail "cleanup-on-exit trap did not kill lingering backend"

# secure_database_files should fix ownership/modes for db paths and configured sidecars.
if [ "$(id -u)" -eq 0 ]; then
    TEST_SECURE_DB_DIR=$TMP/secure-database-files
    mkdir -p "$TEST_SECURE_DB_DIR"
    for suffix in "" -wal -shm; do
        printf '%s' "db$suffix" > "$TEST_SECURE_DB_DIR/db.sqlite$suffix"
        chown 0:0 "$TEST_SECURE_DB_DIR/db.sqlite$suffix"
        chmod 644 "$TEST_SECURE_DB_DIR/db.sqlite$suffix"
    done
    (
        . "$TMP/helpers.sh"
        CONFIG_PATH="$TEST_SECURE_DB_DIR"
        DATABASE_BASENAME=db.sqlite
        PUID=1001
        PGID=1001
        secure_database_files || fail "secure_database_files failed on default SQLite artifacts"
        for suffix in "" -wal -shm; do
            path="$TEST_SECURE_DB_DIR/db.sqlite${suffix}"
            [ "$(stat -c '%a' "$path")" = 600 ] || fail "sqlite artifact ${path} not mode 600"
            [ "$(stat -c '%u:%g' "$path")" = "1001:1001" ] || fail "sqlite artifact ${path} not owned by backend user"
        done

        DATABASE_BASENAME=custom.sqlite
        printf '%s' "custom" > "$TEST_SECURE_DB_DIR/custom.sqlite"
        printf '%s' "custom-wal" > "$TEST_SECURE_DB_DIR/custom.sqlite-wal"
        printf '%s' "custom-shm" > "$TEST_SECURE_DB_DIR/custom.sqlite-shm"
        chown 0:0 "$TEST_SECURE_DB_DIR/custom.sqlite" "$TEST_SECURE_DB_DIR/custom.sqlite-wal" "$TEST_SECURE_DB_DIR/custom.sqlite-shm"
        chmod 644 "$TEST_SECURE_DB_DIR/custom.sqlite" "$TEST_SECURE_DB_DIR/custom.sqlite-wal" "$TEST_SECURE_DB_DIR/custom.sqlite-shm"
        secure_database_files || fail "secure_database_files failed on configured basename sidecars"
        for suffix in "" -wal -shm; do
            path="$TEST_SECURE_DB_DIR/custom.sqlite${suffix}"
            [ "$(stat -c '%a' "$path")" = 600 ] || fail "configured basename artifact ${path} not mode 600"
        done

        DATABASE_BASENAME=db.sqlite
        ln -sf "$TEST_SECURE_DB_DIR/db.sqlite" "$TEST_SECURE_DB_DIR/db.sqlite-wal"
        set +e
        secure_database_files
        symlink_status=$?
        set -e
        [ "$symlink_status" -ne 0 ] || fail "secure_database_files should fail on symlinked sidecar"
    )
    rm -rf "$TEST_SECURE_DB_DIR"
fi

# run_maintenance_child must preserve non-zero exit status for direct callers.
(
    . "$TMP/helpers.sh"
    maintenance_status=0
    if run_maintenance_child /bin/sh -c 'exit 23'; then
        maintenance_status=0
    else
        maintenance_status=$?
    fi
    [ "$maintenance_status" -eq 23 ] || fail "run_maintenance_child failed to preserve custom status"
)

# wait_either regression: backend process exits first.
(
    exit 17
) &
WAIT_BACKEND_PID=$!
(
    sleep 2
) &
WAIT_FRONTEND_PID=$!
WAIT_EITHER_STATUS=0
wait_either "$WAIT_BACKEND_PID" "$WAIT_FRONTEND_PID" || WAIT_EITHER_STATUS=$?
[ "$WAIT_EITHER_STATUS" -eq 17 ] || fail "unexpected wait_either status when backend exits first"
[ "$EXITED_PID" -eq "$WAIT_BACKEND_PID" ] || fail "wrong exited pid when backend exits first"
[ "$REMAINING_PID" -eq "$WAIT_FRONTEND_PID" ] || fail "wrong remaining pid when backend exits first"
stop_process "$REMAINING_PID" TERM

# Signal a real supervisor, isolated in its own process group, that is running
# two real stubborn children. The production terminate() function must forward
# the signal, deterministically reap both children, and return conventional
# 130/143.
cat >"$TMP/real-supervisor.sh" <<'SUPERVISOR_EOF'
#!/bin/sh
set -eu
. "$TMP/helpers.sh"
SHUTDOWN_GRACE_SECONDS=1
stubborn_child() {
    trap '' INT TERM
    while :; do sleep 1; done
}
(stubborn_child) &
BACKEND_PID=$!
(stubborn_child) &
FRONTEND_PID=$!
printf '%s %s\n' "$BACKEND_PID" "$FRONTEND_PID" >"$TMP/supervisor-pids"
while :; do sleep 1; done
SUPERVISOR_EOF
chmod +x "$TMP/real-supervisor.sh"
run_real_supervisor_signal_case() {
    signal=$1
    expected=$2
    rm -f "$TMP/supervisor-pids"
    set +e
    status=$(
        "$REAL_PYTHON" - "$TMP/real-supervisor.sh" "$signal" "$TMP/supervisor-pids" <<'PY'
import os
import signal
import subprocess
import sys
import time

runner, signal_name, pid_file = sys.argv[1:]
process = subprocess.Popen(
    [runner],
    start_new_session=True,
    stdout=subprocess.DEVNULL,
    stderr=subprocess.DEVNULL,
)
for _ in range(200):
    if os.path.exists(pid_file):
        break
    time.sleep(0.01)
else:
    os.killpg(process.pid, signal.SIGKILL)
    process.wait()
    raise SystemExit("real supervisor did not publish child pids")
with open(pid_file, encoding="utf-8") as stream:
    child_pids = [int(value) for value in stream.read().split()]
if os.getpgid(process.pid) != process.pid:
    raise SystemExit("supervisor did not start its own process group")
os.kill(process.pid, getattr(signal, "SIG" + signal_name))
try:
    result = process.wait(timeout=15)
except subprocess.TimeoutExpired:
    os.killpg(process.pid, signal.SIGKILL)
    process.wait()
    raise SystemExit("real supervisor did not exit after deterministic cleanup")
for child_pid in child_pids:
    try:
        os.kill(child_pid, 0)
    except ProcessLookupError:
        continue
    raise SystemExit(f"supervisor child {child_pid} survived cleanup")
print(result)
PY
    )
    python_status=$?
    set -e
    [ "$python_status" -eq 0 ] || fail "real supervisor $signal test failed"
    [ "$status" -eq "$expected" ] || fail "real supervisor $signal returned $status, expected $expected"
}
run_real_supervisor_signal_case INT 130
run_real_supervisor_signal_case TERM 143

# wait_either regression: frontend process exits first.
(
    sleep 2
) &
WAIT_BACKEND_PID=$!
(
    exit 19
) &
WAIT_FRONTEND_PID=$!
WAIT_EITHER_STATUS=0
wait_either "$WAIT_BACKEND_PID" "$WAIT_FRONTEND_PID" || WAIT_EITHER_STATUS=$?
[ "$WAIT_EITHER_STATUS" -eq 19 ] || fail "unexpected wait_either status when frontend exits first"
[ "$EXITED_PID" -eq "$WAIT_FRONTEND_PID" ] || fail "wrong exited pid when frontend exits first"
[ "$REMAINING_PID" -eq "$WAIT_BACKEND_PID" ] || fail "wrong remaining pid when frontend exits first"
stop_process "$REMAINING_PID" TERM

# Backend health probe must ignore proxy settings and be bounded by explicit timeouts.
ORIGINAL_PATH=$PATH
FAKE_CURL_BIN=$TMP/fake-bin
FAKE_CURL_LOG=$TMP/fake-curl.log
mkdir -p "$FAKE_CURL_BIN"
cat >"$FAKE_CURL_BIN/curl" <<'CURL_EOF'
#!/bin/sh
: >"${FAKE_CURL_LOG:-/dev/stdout}"
has_noproxy=0
has_connect_timeout=0
has_max_time=0
proxy_env=0

if [ -n "${http_proxy:-}" ] || [ -n "${HTTP_PROXY:-}" ] || [ -n "${ALL_PROXY:-}" ]; then
    proxy_env=1
fi

while [ "$#" -gt 0 ]; do
    case "$1" in
        --noproxy)
            has_noproxy=1
            shift
            [ "$#" -gt 0 ] && shift
            continue
            ;;
        --noproxy=*)
            has_noproxy=1
            shift
            continue
            ;;
        --connect-timeout)
            has_connect_timeout=1
            shift
            [ "$#" -gt 0 ] && shift
            continue
            ;;
        --connect-timeout=*)
            has_connect_timeout=1
            shift
            continue
            ;;
        --max-time)
            has_max_time=1
            shift
            [ "$#" -gt 0 ] && shift
            continue
            ;;
        --max-time=*)
            has_max_time=1
            shift
            continue
            ;;
        --)
            break
            ;;
        *)
            shift
            ;;
    esac
done

if [ "$has_noproxy" -ne 1 ] && [ "$proxy_env" -eq 1 ]; then
    [ "$has_connect_timeout" -eq 1 ] && [ "$has_max_time" -eq 1 ] || sleep 5
    printf '200'
    exit 0
fi

[ "$has_connect_timeout" -eq 1 ] && [ "$has_max_time" -eq 1 ] || sleep 5
printf '000'
exit 0
CURL_EOF
chmod +x "$FAKE_CURL_BIN/curl"
export http_proxy="http://127.0.0.1:45678"
export HTTP_PROXY="http://127.0.0.1:45678"
export ALL_PROXY="http://127.0.0.1:45678"
export NO_PROXY=
export BACKEND_URL="http://127.0.0.1:65431"
export BACKEND_HEALTH_CONNECT_TIMEOUT_SECONDS=1
export BACKEND_HEALTH_MAX_TIME_SECONDS=1
export MAX_BACKEND_HEALTH_RETRIES=1
export MAX_BACKEND_HEALTH_RETRY_DELAY=0
PATH="$FAKE_CURL_BIN:$PATH"

probe_start=$(date +%s)
if [ "$(backend_is_healthy)" != "000" ]; then
    fail "backend probe should not use proxy settings when NO_PROXY is empty"
fi
probe_seconds=$(( $(date +%s) - probe_start ))
[ "$probe_seconds" -lt 2 ] || fail "backend probe was not bounded by curl timeouts"
PATH=$ORIGINAL_PATH

# Accepted-but-unresponsive real listener regression: real curl, per-probe max-time,
# bounded retries/delay, and bounded stop/reap cleanup.
start_hanging_listener() {
    PORT=$1
    if command -v node >/dev/null 2>&1; then
        ( BACKEND_LISTENER_PORT=$PORT node -e '
            const net = require("net");
            const port = parseInt(process.env.BACKEND_LISTENER_PORT, 10);
            const server = net.createServer((socket) => {
                socket.on("data", () => {});
            });
            process.on("SIGTERM", () => {});
            server.listen(port, "127.0.0.1");
        ' ) &
    elif command -v nc >/dev/null 2>&1; then
        ( trap "" TERM; while :; do nc -l -s 127.0.0.1 -p "$PORT" < /dev/null > /dev/null; done ) &
    else
        return 1
    fi
}

if ! start_hanging_listener 38171; then
    fail "hostile listener launch failed"
fi
HANGING_BACKEND_PID=$!
BACKEND_PID=$HANGING_BACKEND_PID
export BACKEND_URL="http://127.0.0.1:38171"
export MAX_BACKEND_HEALTH_RETRIES=2
export MAX_BACKEND_HEALTH_RETRY_DELAY=0
export BACKEND_HEALTH_CONNECT_TIMEOUT_SECONDS=1
export BACKEND_HEALTH_MAX_TIME_SECONDS=1

probe_start=$(date +%s)
if [ "$(backend_is_healthy)" != "000" ]; then
    fail "real hanging listener should not answer health checks"
fi
probe_seconds=$(( $(date +%s) - probe_start ))
[ "$probe_seconds" -lt 2 ] || fail "real backend probe was not bounded by curl timeouts"

retry_start=$(date +%s)
if run_health_retries; then
    fail "backend health should fail against hanging listener"
fi
retry_seconds=$(( $(date +%s) - retry_start ))
[ "$retry_seconds" -lt 4 ] || fail "backend health retries exceeded bounded timeout"
stop_started_processes
[ -z "$BACKEND_PID" ] || fail "backend pid was not cleared after stop"
! kill -0 "$HANGING_BACKEND_PID" 2>/dev/null || fail "hanging backend was not reaped"

start_http_listener() {
    PORT=$1
    MODE=$2
    HITS_FILE=$3
    START_HTTP_LISTENER_PID=
    : >"$HITS_FILE"
    if command -v node >/dev/null 2>&1; then
        ( node -e '
            const http = require("http");
            const fs = require("fs");
            const port = Number(process.argv[1]);
            const mode = process.argv[2];
            const hitsFile = process.argv[3];
            const server = http.createServer((req, res) => {
                fs.appendFileSync(hitsFile, "1\n");
                if (mode === "hang") {
                    return;
                }
                res.writeHead(200, {"content-type":"text/plain"});
                res.end("ok\n");
            });
            server.listen(port, "127.0.0.1");
        ' "$PORT" "$MODE" "$HITS_FILE" ) &
        START_HTTP_LISTENER_PID=$!
    else
        return 1
    fi
}

start_http_listener_pid_kill() {
    LISTENER_PID=$1
    if [ -n "$LISTENER_PID" ]; then
        kill "$LISTENER_PID" 2>/dev/null || true
        wait "$LISTENER_PID" 2>/dev/null || true
    fi
}

if ! command -v node >/dev/null 2>&1; then
    fail "node is required for real-curl curlrc regression tests"
fi

CURLRC_BACKEND_PORT=38173
CURLRC_FAKE_200_PORT=38174
CURLRC_FAKE_NEXT_PORT=38175
CURLRC_HITS_200=$TMP/curlrc-hit-200
CURLRC_HITS_NEXT=$TMP/curlrc-hit-next
start_http_listener "$CURLRC_FAKE_200_PORT" 200 "$CURLRC_HITS_200"
FAKE_200_PID=$START_HTTP_LISTENER_PID
start_http_listener "$CURLRC_FAKE_NEXT_PORT" hang "$CURLRC_HITS_NEXT"
FAKE_NEXT_PID=$START_HTTP_LISTENER_PID
CURLRC_HOME=$TMP/curlrc-home
mkdir -p "$CURLRC_HOME"
cat >"$CURLRC_HOME/.curlrc" <<CURLRC_EOF
connect-to = "127.0.0.1:${CURLRC_BACKEND_PORT}:127.0.0.1:${CURLRC_FAKE_200_PORT}"
url = "http://127.0.0.1:${CURLRC_FAKE_NEXT_PORT}/health"
next
max-time = 10
CURLRC_EOF
(
    export CURL_HOME=$CURLRC_HOME
    export BACKEND_URL="http://127.0.0.1:${CURLRC_BACKEND_PORT}"
    export BACKEND_HEALTH_CONNECT_TIMEOUT_SECONDS=1
    export BACKEND_HEALTH_MAX_TIME_SECONDS=1
    probe_start=$(date +%s)
    set +e
    PROBE_RESULT=$(backend_is_healthy)
    set -e
    probe_seconds=$(( $(date +%s) - probe_start ))
    printf '%s\n' "${PROBE_RESULT}" > "$TMP/curlrc-probe-result"
    printf '%s\n' "${probe_seconds}" > "$TMP/curlrc-probe-seconds"
)
read -r PROBE_RESULT < "$TMP/curlrc-probe-result"
probe_seconds=$(cat "$TMP/curlrc-probe-seconds")
[ "$PROBE_RESULT" = "000" ] || fail "backend health should ignore hostile .curlrc settings"
[ "$probe_seconds" -lt 3 ] || fail "hostile curlrc url/next options exceeded max-time"
[ ! -s "$CURLRC_HITS_200" ] || fail "connect-to mapping was honored despite -q"
[ ! -s "$CURLRC_HITS_NEXT" ] || fail "curlrc url/next extra transfer was honored despite -q"
start_http_listener_pid_kill "$FAKE_200_PID"
start_http_listener_pid_kill "$FAKE_NEXT_PID"

echo 'entrypoint tests: running shared-volume mount-policy regression'

export PUID=1000
export PGID=1000
mkdir -p /media/nzbdav /data/completed-downloads
export NZBDAV_FULL_STACK=false
if ! initialize_full_stack_shared_volumes; then
    fail "full-stack disabled should skip shared volume mount checks"
fi
export NZBDAV_FULL_STACK=true
if initialize_full_stack_shared_volumes; then
    fail "full-stack enabled should fail on unmounted fixed shared volumes"
fi

# Arr mounts one volume at the parent and expects ordinary category children.
# Exercise that exact kernel mount layout, including the ownership marker's
# no-churn restart path and hostile child objects.
SHARED_VOLUME_TEST=$TMP/shared-volume
mkdir -p "$SHARED_VOLUME_TEST"
mount -t tmpfs -o mode=0755 tmpfs "$SHARED_VOLUME_TEST" || fail "could not mount shared-volume test tmpfs"
ACTUAL_MOUNT_TARGETS="$SHARED_VOLUME_TEST"
if ! initialize_shared_volume "$SHARED_VOLUME_TEST" movies tv; then
    fail "mounted shared-volume parent with category children should initialize"
fi
[ -d "$SHARED_VOLUME_TEST/movies" ] && [ -d "$SHARED_VOLUME_TEST/tv" ] || fail "category children were not created"
[ "$(stat -c '%u:%g:%a' "$SHARED_VOLUME_TEST/movies")" = "1000:1000:775" ] || fail "movies ownership/mode is not runtime-compatible"
[ "$(stat -c '%u:%g:%a' "$SHARED_VOLUME_TEST/tv")" = "1000:1000:775" ] || fail "tv ownership/mode is not runtime-compatible"
[ "$(stat -c '%u:%g:%a:%h' "$SHARED_VOLUME_TEST/.nzbdav-ownership")" = "0:0:600:1" ] || fail "shared-volume marker is not root-owned 0600"
SHARED_MARKER_STATE=$(stat -c '%i:%u:%g:%a:%Y:%Z' "$SHARED_VOLUME_TEST/.nzbdav-ownership")
if ! initialize_shared_volume "$SHARED_VOLUME_TEST" movies tv; then
    fail "shared-volume restart should accept valid marker"
fi
[ "$(stat -c '%i:%u:%g:%a:%Y:%Z' "$SHARED_VOLUME_TEST/.nzbdav-ownership")" = "$SHARED_MARKER_STATE" ] || fail "shared-volume restart churned valid marker"
umount "$SHARED_VOLUME_TEST" || fail "could not unmount shared-volume test tmpfs"
ACTUAL_MOUNT_TARGETS=

for SHARED_CHILD_CASE in symlink non-directory nested-mount; do
    mkdir -p "$SHARED_VOLUME_TEST"
    mount -t tmpfs -o mode=0755 tmpfs "$SHARED_VOLUME_TEST" || fail "could not mount $SHARED_CHILD_CASE test tmpfs"
    ACTUAL_MOUNT_TARGETS="$SHARED_VOLUME_TEST"
    rm -rf "$SHARED_VOLUME_TEST/movies" "$SHARED_VOLUME_TEST/tv"
    case "$SHARED_CHILD_CASE" in
        symlink) ln -s /tmp "$SHARED_VOLUME_TEST/movies" ;;
        non-directory) : > "$SHARED_VOLUME_TEST/movies" ;;
        nested-mount)
            mkdir "$SHARED_VOLUME_TEST/movies"
            mount -t tmpfs tmpfs "$SHARED_VOLUME_TEST/movies" || fail "could not mount nested-volume test tmpfs"
            ACTUAL_MOUNT_TARGETS="$SHARED_VOLUME_TEST/movies $SHARED_VOLUME_TEST"
            ;;
    esac
    if initialize_shared_volume "$SHARED_VOLUME_TEST" movies tv >"$TMP/shared-volume-$SHARED_CHILD_CASE.stdout" 2>"$TMP/shared-volume-$SHARED_CHILD_CASE.stderr"; then
        fail "shared-volume $SHARED_CHILD_CASE child should fail"
    fi
    if [ "$SHARED_CHILD_CASE" = non-directory ] && ! grep -Fq 'File exists' "$TMP/shared-volume-$SHARED_CHILD_CASE.stderr"; then
        fail "shared-volume non-directory child did not reject mkdir collision"
    fi
    umount "$SHARED_VOLUME_TEST/movies" >/dev/null 2>&1 || true
    umount "$SHARED_VOLUME_TEST" || fail "could not unmount $SHARED_CHILD_CASE test tmpfs"
    ACTUAL_MOUNT_TARGETS=
    rm -rf "$SHARED_VOLUME_TEST"
done

# The production EXIT trap is active before secure_database_files is parsed;
# an early preflight error must retain its diagnostic without a second failure.
set +e
EARLY_PREFLIGHT_OUTPUT=$(PUID=not-a-number PGID=1000 FRONTEND_PUID=1001 sh "$SCRIPT" 2>&1)
EARLY_PREFLIGHT_STATUS=$?
set -e
[ "$EARLY_PREFLIGHT_STATUS" -eq 1 ] || fail "early preflight failure changed exit status"
printf '%s\n' "$EARLY_PREFLIGHT_OUTPUT" | grep -Fq 'PUID must be numeric.' || fail "early preflight cause was not emitted"
if printf '%s\n' "$EARLY_PREFLIGHT_OUTPUT" | grep -Fq 'not found'; then
    fail "early preflight failure invoked an undefined cleanup helper"
fi

EOF
chmod +x "$TMP/run-tests.sh"

if command -v timeout >/dev/null 2>&1; then
    timeout 30 sh "$TMP/run-tests.sh"
else
    echo 'entrypoint supervisor test skipped: timeout is unavailable'
fi

# Run the production entrypoint itself as PID 1 in a real PID namespace. The
# small fake backend/frontend only provide HTTP listeners; all supervisor,
# signal, user, env-allowlist, and root bootstrap behavior comes from the
# checked-in entrypoint.sh. This catches a test that merely sources helpers.
run_actual_pid1_signal_case() {
    signal_name=$1
    expected_status=$2
    maintenance_status=${3:-0}
    actual_root="$TMP/actual-pid1-$signal_name"
    mkdir -p "$actual_root/backend" "$actual_root/frontend/dist-node" "$actual_root/config/data"
    cp "$SCRIPT" "$actual_root/entrypoint.sh"
    cp "$ROOT/bootstrap-secrets.sh" "$actual_root/bootstrap-secrets.sh"
    chmod 0755 "$actual_root/entrypoint.sh" "$actual_root/bootstrap-secrets.sh"
    cat >"$actual_root/backend/NzbWebDAV" <<'BACKEND_EOF'
#!/bin/sh
case "${1:-}" in
    --db-migration|--encryption-maintenance) exit "${ENTRYPOINT_TEST_MAINTENANCE_STATUS:-0}" ;;
esac
exec python3 -c '
import os
from http.server import BaseHTTPRequestHandler, HTTPServer
class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path == "/identity":
            groups = ",".join(str(group) for group in os.getgroups())
            body = ("%s:%s:%s\n" % (os.getuid(), os.getgid(), groups)).encode()
        else:
            body = b"ok\n"
        self.send_response(200)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)
    def log_message(self, *_):
        pass
HTTPServer(("127.0.0.1", 8080), Handler).serve_forever()
'
BACKEND_EOF
    cat >"$actual_root/frontend/dist-node/server.js" <<'FRONTEND_EOF'
const http = require("http");
const server = http.createServer((request, response) => {
    if (request.url === "/identity") {
        return response.end(`${process.getuid()}:${process.getgid()}:${process.getgroups().join(",")}\n`);
    }
    response.end("ok\n");
});
server.listen(Number(process.env.PORT || 3000), "127.0.0.1");
FRONTEND_EOF
    chmod 0755 "$actual_root/backend/NzbWebDAV" "$actual_root/frontend/dist-node/server.js"

    # The fixed production paths are mounted only for this test and are
    # unmounted by the outer trap before its temporary tree is removed.
    mkdir -p /app/backend /app/frontend /config
    : >/entrypoint.sh
    : >/bootstrap-secrets.sh
    mount_actual() {
        if mount --bind "$1" "$2"; then
            ACTUAL_MOUNT_TARGETS="$2 $ACTUAL_MOUNT_TARGETS"
            return 0
        fi
        cleanup_actual_mounts
        return 1
    }
    if ! mount_actual "$actual_root/backend" /app/backend \
        || ! mount_actual "$actual_root/frontend" /app/frontend \
        || ! mount_actual "$actual_root/config" /config \
        || ! mount_actual "$actual_root/entrypoint.sh" /entrypoint.sh \
        || ! mount_actual "$actual_root/bootstrap-secrets.sh" /bootstrap-secrets.sh; then
        if [ "${CI:-false}" = true ]; then
            echo 'production PID1 test could not establish its bind mounts' >&2
            exit 1
        fi
        echo 'entrypoint PID1 signal test skipped: bind mounts are unavailable'
        return 0
    fi

    # Alpine's existing nobody account is UID 65534/GID 65534. Configure
    # a different numeric PGID to prove su-exec does not reuse that
    # account's passwd primary group or supplementary groups.
    env -i \
        PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin \
        PUID=65534 PGID=1000 FRONTEND_PUID=1001 \
        CONFIG_PATH=/config/data BACKEND_URL=http://127.0.0.1:8080 \
        NZBDAV_FULL_STACK=false PORT=3000 \
        MAX_BACKEND_HEALTH_RETRIES=30 MAX_BACKEND_HEALTH_RETRY_DELAY=0 \
        BACKEND_HEALTH_CONNECT_TIMEOUT_SECONDS=1 BACKEND_HEALTH_MAX_TIME_SECONDS=2 \
        SHUTDOWN_GRACE_SECONDS=1 \
        ENTRYPOINT_TEST_MAINTENANCE_STATUS="$maintenance_status" \
        "$REAL_PYTHON" - "$signal_name" "$expected_status" "$actual_root/pid1.log" <<'PY'
import os
import signal
import subprocess
import sys
import time
from urllib.request import urlopen

signal_name, expected, log_path = sys.argv[1:]
expected = int(expected)
environment = os.environ.copy()
expected_backend_identity = f"{environment['PUID']}:{environment['PGID']}:{environment['PGID']}"
expected_frontend_identity = f"{environment['FRONTEND_PUID']}:{environment['PGID']}:{environment['PGID']}"

def read_identity(url):
    try:
        with urlopen(url, timeout=1) as response:
            return response.read().decode("ascii").strip()
    except Exception:
        return None
with open(log_path, "w", encoding="utf-8") as log:
    process = subprocess.Popen(
        ["unshare", "--fork", "--pid", "--mount-proc", "/bin/sh", "-c", "exec /entrypoint.sh"],
        env=environment,
        stdout=log,
        stderr=subprocess.STDOUT,
        start_new_session=True,
    )
    namespace_pid = None
    for _ in range(300):
        try:
            children = open(f"/proc/{process.pid}/task/{process.pid}/children", encoding="ascii").read().split()
        except (FileNotFoundError, ProcessLookupError):
            children = []
        if children:
            namespace_pid = int(children[0])
        backend_ready = namespace_pid is not None and "Backend is healthy." in open(log_path, encoding="utf-8").read()
        backend_identity = read_identity("http://127.0.0.1:8080/identity") if backend_ready else None
        frontend_identity = read_identity("http://127.0.0.1:3000/identity") if backend_ready else None
        if backend_identity == expected_backend_identity and frontend_identity == expected_frontend_identity:
            break
        time.sleep(0.01)
    if signal_name.startswith("MAINTENANCE"):
        try:
            result = process.wait(timeout=20)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait()
            raise SystemExit("production migration status test did not exit")
        if result != expected:
            raise SystemExit(f"production migration returned {result}, expected {expected}")
        raise SystemExit(0)
    if namespace_pid is None:
        process.kill()
        process.wait()
        raise SystemExit("production entrypoint did not create a PID namespace init")
    if backend_identity != expected_backend_identity or frontend_identity != expected_frontend_identity:
        process.kill()
        process.wait()
        raise SystemExit(
            f"numeric UID:GID regression failed: backend={backend_identity!r}, "
            f"frontend={frontend_identity!r}, expected backend={expected_backend_identity!r}, "
            f"expected frontend={expected_frontend_identity!r}"
        )
    os.kill(namespace_pid, getattr(signal, "SIG" + signal_name))
    try:
        result = process.wait(timeout=20)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait()
        raise SystemExit("production PID1 did not exit after signal")
    if result != expected:
        raise SystemExit(f"production PID1 returned {result}, expected {expected}")
PY
    cleanup_actual_mounts
}

if [ "$(id -u)" -eq 0 ] && command -v unshare >/dev/null 2>&1 && command -v su-exec >/dev/null 2>&1 && command -v mount >/dev/null 2>&1; then
    existing_uid_gid=$(getent passwd 65534 | cut -d: -f4)
    [ -n "$existing_uid_gid" ] && [ "$existing_uid_gid" != 1000 ] || {
        echo 'numeric UID:GID regression requires Alpine nobody UID 65534 with a different passwd GID' >&2
        exit 1
    }
    run_actual_pid1_signal_case INT 130
    run_actual_pid1_signal_case TERM 143
    # Exact child statuses must survive set -e, cleanup, and the EXIT trap;
    # this catches the forbidden `if ! command; status=$?` regression.
    run_actual_pid1_signal_case MAINTENANCE 7 7
    run_actual_pid1_signal_case MAINTENANCE-23 23 23
else
    if [ "${CI:-false}" = true ]; then
        echo 'entrypoint PID1 signal test dependencies are missing in CI' >&2
        exit 1
    fi
    echo 'entrypoint PID1 signal test skipped: root, unshare, mount, and su-exec are required'
fi

grep -qF 'backend_is_healthy' "$SCRIPT"
grep -qF -- "--noproxy '*'" "$SCRIPT"
grep -qF 'curl -q' "$SCRIPT"
grep -qF -- '--max-time' "$SCRIPT"
grep -qF -- '--connect-timeout' "$SCRIPT"
grep -qF -- 'MAX_BACKEND_HEALTH_RETRIES' "$SCRIPT"
grep -qF -- 'MAX_BACKEND_HEALTH_RETRY_DELAY' "$SCRIPT"
grep -qF -- 'BACKEND_HEALTH_CONNECT_TIMEOUT_SECONDS' "$SCRIPT"
grep -qF -- 'BACKEND_HEALTH_MAX_TIME_SECONDS' "$SCRIPT"
grep -qF 'validate_runtime_timeouts' "$SCRIPT"
grep -qF 'validate_jellyfin_public_port' "$SCRIPT"
grep -qF 'initialize_full_stack_shared_volumes' "$SCRIPT"
grep -qF 'su-exec "$PUID:$PGID"' "$SCRIPT"
grep -qF 'su-exec "$FRONTEND_PUID:$PGID"' "$SCRIPT"
grep -qF 'JELLYFIN_PUBLIC_PORT="$JELLYFIN_PUBLIC_PORT"' "$SCRIPT"
grep -qF 'PUBLIC_ORIGIN="$PUBLIC_ORIGIN"' "$SCRIPT"
grep -qF 'JELLYFIN_PUBLIC_URL="$JELLYFIN_PUBLIC_URL"' "$SCRIPT"
! grep -q 'if ! run_maintenance_child' "$SCRIPT"
grep -qF 'MAINTENANCE_STATUS=$?' "$SCRIPT"
! grep -q 'kill "$BACKEND_PID"' "$SCRIPT"
! grep -q 'wait "$BACKEND_PID"' "$SCRIPT"
echo 'entrypoint supervisor tests passed'
