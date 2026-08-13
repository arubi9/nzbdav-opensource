#!/usr/bin/env bash
# Regression for cleanup during static and real-smoke allocations.
set -Eeuo pipefail

ROOT=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
SMOKE="$ROOT/tools/full-stack-deployment-smoke.sh"
SUPERVISOR="$ROOT/tools/full-stack-deployment-supervisor.py"
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT
REAL_PYTHON=$(command -v python3) || {
    echo 'full-stack deployment signal regression requires Python 3' >&2
    exit 1
}
FAKE_BIN="$TMP/bin"
mkdir "$FAKE_BIN"

# The smoke contract only needs successful command prerequisites here. Keeping
# these fake interpreters in the test makes it runnable in minimal Alpine
# images that have bash but no Docker daemon or Python installation.
cat >"$FAKE_BIN/docker" <<'EOF'
#!/usr/bin/env bash
if [[ "${FAKE_DOCKER_FAIL_PULL:-0}" == 1 && "${1:-}" == pull ]]; then
    exit 1
fi
exit 0
EOF
cat >"$FAKE_BIN/python3" <<'EOF'
#!/usr/bin/env bash
exit 0
EOF
cat >"$FAKE_BIN/mktemp" <<'EOF'
#!/usr/bin/env bash
set -Eeuo pipefail
count_file="$FAKE_TMP/count"
count=0
if [ -f "$count_file" ]; then
    read -r count <"$count_file"
fi
count=$((count + 1))
path="$FAKE_TMP/artifact-$count"
: >"$path"
# An in-flight allocation owns its artifact until command substitution returns.
trap 'rm -f -- "$path"; exit 130' INT
trap 'rm -f -- "$path"; exit 143' TERM
printf '%s\n' "$path" >>"$FAKE_TMP/mktemp.log"
printf '%s\n' "$path"
printf '%s\n' "$count" >"$count_file"
if [ "${BLOCK_AT:-0}" -eq "$count" ]; then
    while [ ! -e "$FAKE_TMP/release" ]; do
        sleep 0.05
done
fi
if [ "${FAIL_AT:-0}" -eq "$count" ]; then
    exit 1
fi
EOF
chmod +x "$FAKE_BIN/docker" "$FAKE_BIN/python3" "$FAKE_BIN/mktemp"

# Exercise the same process parser against /proc-shaped fixtures. A backend
# without the independently running frontend must never be a false green.
write_process_fixture() {
    local root=$1 pid=$2 command=$3 uid=$4 gid=$5 cap=$6
    mkdir -p "$root/$pid"
    printf '%s\0' "$command" >"$root/$pid/cmdline"
    # Field 22 is starttime. The parser also validates the PID prefix, making
    # this fixture a faithful enough /proc stat identity for PID-reuse tests.
    printf '%s (fixture) S 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 %s\n' "$pid" "$pid" >"$root/$pid/stat"
    printf 'Name:\tfixture\nUid:\t%s\t%s\t%s\t%s\nGid:\t%s\t%s\t%s\t%s\nGroups:\t%s\nCapEff:\t%s\n' \
        "$uid" "$uid" "$uid" "$uid" "$gid" "$gid" "$gid" "$gid" "$gid" "$cap" >"$root/$pid/status"
}
write_process_supervisor_fixture() {
    write_process_fixture "$1" 1 '/entrypoint.sh' 0 0 00000000000000eb
}
run_process_fixture_case() {
    local name=$1 frontend=$2 uid=$3 cap=$4 root="$TMP/process-$1"
    mkdir -p "$root"
    write_process_supervisor_fixture "$root"
    write_process_fixture "$root" 101 '/app/backend/NzbWebDAV' 1000 1000 0000000000000000
    if [ "$frontend" = present ]; then
        write_process_fixture "$root" 202 'node dist-node/server.js' "$uid" 1000 "$cap"
    fi
    if env PUID=1000 PGID=1000 FRONTEND_PUID=1001 PROCESS_STATUS_ROOT="$root" \
        "$SMOKE" --process-contract >"$root/output" 2>&1; then
        echo "process contract unexpectedly passed: $name" >&2
        exit 1
    fi
}
run_process_fixture_case frontend-absent absent 1001 0000000000000000
run_process_fixture_case frontend-wrong-uid present 1002 0000000000000000
run_process_fixture_case frontend-wrong-capeff present 1001 00000000000000000001

# PID 1 polls its two children with this exact short-lived helper. It is part
# of the root supervisor, not an extra long-lived application daemon.
run_process_fixture_with_supervisor_poll() {
    local root="$TMP/process-supervisor-poll"
    mkdir -p "$root"
    write_process_supervisor_fixture "$root"
    write_process_fixture "$root" 101 '/app/backend/NzbWebDAV' 1000 1000 0000000000000000
    write_process_fixture "$root" 202 'node dist-node/server.js' 1001 1000 0000000000000000
    write_process_fixture "$root" 303 'sleep 0.1' 0 0 00000000000000eb
    if ! env PUID=1000 PGID=1000 FRONTEND_PUID=1001 PROCESS_STATUS_ROOT="$root"         "$SMOKE" --process-contract >"$root/output" 2>&1; then
        echo 'process contract rejected PID 1 bounded sleep helper' >&2
        cat "$root/output" >&2
        exit 1
    fi
}
run_process_fixture_with_supervisor_poll

# docker-publish runs this suite as root in the pinned Alpine 3.22 image with
# /config on tmpfs. Exercise the exact remote ash probe there: a denied
# redirection must be handled successfully, while a writable config root must
# fail and neither case may leave its marker behind.
run_alpine_root_write_denial_regression() (
    if [ ! -f /etc/alpine-release ]; then
        echo 'root-write denial regression: SKIP (requires pinned Alpine harness)'
        return 0
    fi
    [ "$(id -u)" -eq 0 ] || {
        echo 'root-write denial regression requires the pinned Alpine root harness' >&2
        return 1
    }
    command -v su-exec >/dev/null || {
        echo 'root-write denial regression requires su-exec in the pinned Alpine harness' >&2
        return 1
    }

    local original_owner original_mode marker denied_log writable_log
    original_owner=$(stat -c '%u:%g' /config)
    original_mode=$(stat -c '%a' /config)
    marker=/config/.deployment-smoke-root-write
    denied_log="$TMP/root-write-denied.log"
    writable_log="$TMP/root-write-writable.log"
    restore_config() {
        rm -f -- "$marker"
        chown "$original_owner" /config
        chmod "$original_mode" /config
    }
    trap restore_config EXIT

    rm -f -- "$marker"
    chown 0:0 /config
    chmod 0755 /config
    if ! su-exec 1000:1000 bash "$SMOKE" --root-write-probe >"$denied_log" 2>&1; then
        echo 'denied root-write redirection was not handled as success' >&2
        cat "$denied_log" >&2
        return 1
    fi
    grep -Fq 'Permission denied' "$denied_log" || {
        echo 'denied root-write redirection did not retain its diagnostic' >&2
        cat "$denied_log" >&2
        return 1
    }
    [ ! -e "$marker" ] || {
        echo 'denied root-write redirection leaked its marker' >&2
        return 1
    }

    chmod 0777 /config
    if su-exec 1000:1000 bash "$SMOKE" --root-write-probe >"$writable_log" 2>&1; then
        echo 'writable config root unexpectedly passed the denial probe' >&2
        return 1
    fi
    [ ! -e "$marker" ] || {
        echo 'writable root-write denial probe leaked its marker' >&2
        return 1
    }
)
run_alpine_root_write_denial_regression

# Reproduce the exact media sentinel failure against a shared Alpine fixture.
# The helper's command must defer cat/stat expansion to its child shell; the
# pinned Alpine gate also catches a wrong reader mount because device/inode
# identity cannot match across separate directories.
run_alpine_cross_container_sentinel_regression() (
    if [ ! -f /etc/alpine-release ]; then
        echo 'cross-container sentinel regression: SKIP (requires pinned Alpine harness)'
        return 0
    fi

    local shared wrong marker wrong_log
    shared="$TMP/media-shared"
    wrong="$TMP/media-wrong"
    marker=.deployment-smoke-sentinel
    wrong_log="$TMP/media-wrong.log"
    mkdir -p "$shared" "$wrong"
    "$SMOKE" --cross-container-sentinel "$shared" "$shared" "$marker"
    [ ! -e "$shared/$marker" ] || {
        echo 'cross-container sentinel cleanup leaked its marker' >&2
        return 1
    }
    if "$SMOKE" --cross-container-sentinel "$shared" "$wrong" "$marker" >"$wrong_log" 2>&1; then
        echo 'cross-container sentinel accepted a wrong reader mount' >&2
        return 1
    fi
    grep -Fq "cat: $wrong/$marker: No such file or directory" "$wrong_log" || {
        echo 'wrong-mount sentinel regression did not retain the exact missing-sentinel diagnostic' >&2
        cat "$wrong_log" >&2
        return 1
    }
    [ ! -e "$shared/$marker" ] || {
        echo 'wrong-mount sentinel failure leaked its marker' >&2
        return 1
    }
)
run_alpine_cross_container_sentinel_regression

# /config/data is NZBDAV-only, so this probe models its real lifecycle:
# backend-principal write followed by a fresh backend-principal read after the
# restart boundary. It rejects the historical wrong-path/missing-sentinel
# regression and verifies cleanup on both success and failure.
run_alpine_config_data_restart_sentinel_regression() (
    if [ ! -f /etc/alpine-release ]; then
        echo 'config-data restart sentinel regression: SKIP (requires pinned Alpine harness)'
        return 0
    fi

    local shared wrong marker wrong_log
    shared="$TMP/config-data-shared"
    wrong="$TMP/config-data-wrong"
    marker=.deployment-smoke-sentinel
    wrong_log="$TMP/config-data-wrong.log"
    mkdir -p "$shared" "$wrong"
    "$SMOKE" --persistent-sentinel "$shared" "$shared" "$marker"
    [ ! -e "$shared/$marker" ] || {
        echo 'config-data restart sentinel cleanup leaked its marker' >&2
        return 1
    }
    if "$SMOKE" --persistent-sentinel "$shared" "$wrong" "$marker" >"$wrong_log" 2>&1; then
        echo 'config-data restart sentinel accepted a wrong reader path' >&2
        return 1
    fi
    # Alpine's BusyBox cat spelling differs from GNU cat's "can't open", but
    # this still pins the exact wrong reader path and missing-sentinel errno.
    grep -Fq "cat: $wrong/$marker: No such file or directory" "$wrong_log" || {
        echo 'wrong-path config-data sentinel did not retain the exact missing-sentinel diagnostic' >&2
        cat "$wrong_log" >&2
        return 1
    }
    [ ! -e "$shared/$marker" ] || {
        echo 'wrong-path config-data sentinel failure leaked its marker' >&2
        return 1
    }
)
run_alpine_config_data_restart_sentinel_regression

# The entrypoint's persisted plugin is an immutable four-file artifact: two
# payload files plus a byte-identical manifest proof and fixed marker. Run the
# smoke verifier itself against fixtures so its real-container contract cannot
# silently regress to a cardinality-only check. This is intentionally limited
# to the pinned Alpine root harness used by the workflow.
run_alpine_plugin_artifact_contract_regression() (
    if [ ! -f /etc/alpine-release ]; then
        echo 'plugin artifact contract regression: SKIP (requires pinned Alpine harness)'
        return 0
    fi
    [ "$(id -u)" -eq 0 ] || {
        echo 'plugin artifact contract regression requires the pinned Alpine root harness' >&2
        return 1
    }

    local fixture source plugin manifest owner
    fixture="$TMP/plugin-artifact-contract"
    source="$fixture/source"
    plugin="$fixture/plugin"
    manifest="$fixture/source.manifest.sha256"
    owner=1000:1000

    make_fixture() {
        rm -rf -- "$fixture"
        mkdir -p "$source" "$plugin"
        printf 'dll-payload' >"$source/Jellyfin.Plugin.Nzbdav.dll"
        printf '{"version":"1.1.0.0"}' >"$source/meta.json"
        (cd "$source" && sha256sum Jellyfin.Plugin.Nzbdav.dll meta.json | sort) >"$manifest"
        cp "$source/Jellyfin.Plugin.Nzbdav.dll" "$source/meta.json" "$plugin/"
        cp "$manifest" "$plugin/.nzbdav-artifact.manifest.sha256"
        printf 'nzbdav-plugin-artifact-v1\n' >"$plugin/.nzbdav-artifact.marker"
        chown -R "$owner" "$source" "$plugin"
        chmod 0755 "$plugin"
        chmod 0644 "$source"/* "$manifest" "$plugin"/* "$plugin"/.[!.]*
    }

    assert_contract_passes() {
        env PUID=1000 PGID=1000 NZBDAV_PLUGIN_SOURCE_DIR="$source" \
            NZBDAV_PLUGIN_SOURCE_MANIFEST="$manifest" NZBDAV_PLUGIN_DIR="$plugin" \
            "$SMOKE" --plugin-artifact-contract >/dev/null
    }
    assert_contract_fails() {
        if assert_contract_passes; then
            echo "plugin artifact contract unexpectedly passed: $1" >&2
            return 1
        fi
    }

    make_fixture
    assert_contract_passes || {
        echo 'exact four-file plugin artifact was rejected' >&2
        return 1
    }

    make_fixture
    rm -f -- "$plugin/.nzbdav-artifact.marker"
    assert_contract_fails missing-marker

    make_fixture
    printf extra >"$plugin/unexpected"
    chown "$owner" "$plugin/unexpected"
    chmod 0644 "$plugin/unexpected"
    assert_contract_fails extra-file

    make_fixture
    printf '0%.0s' $(seq 1 64) >"$plugin/.nzbdav-artifact.manifest.sha256"
    printf ' Jellyfin.Plugin.Nzbdav.dll\n' >>"$plugin/.nzbdav-artifact.manifest.sha256"
    cat "$manifest" | tail -n 1 >>"$plugin/.nzbdav-artifact.manifest.sha256"
    chown "$owner" "$plugin/.nzbdav-artifact.manifest.sha256"
    chmod 0644 "$plugin/.nzbdav-artifact.manifest.sha256"
    assert_contract_fails tampered-manifest

    make_fixture
    printf 'tampered-marker\n' >"$plugin/.nzbdav-artifact.marker"
    chown "$owner" "$plugin/.nzbdav-artifact.marker"
    chmod 0644 "$plugin/.nzbdav-artifact.marker"
    assert_contract_fails tampered-marker
)
run_alpine_plugin_artifact_contract_regression

valid_process_fixture="$TMP/process-valid"
mkdir -p "$valid_process_fixture"
write_process_supervisor_fixture "$valid_process_fixture"
write_process_fixture "$valid_process_fixture" 101 '/app/backend/NzbWebDAV' 1000 1000 0000000000000000
write_process_fixture "$valid_process_fixture" 202 'node dist-node/server.js' 1001 1000 0000000000000000
env PUID=1000 PGID=1000 FRONTEND_PUID=1001 PROCESS_STATUS_ROOT="$valid_process_fixture" \
    "$SMOKE" --process-contract >/dev/null

# This is the exact snapshot/read race that occurs after a supervised restart:
# a numeric PID exists during glob enumeration then exits before its files are
# read. It is deliberately deterministic for the pinned-Alpine CI harness.
racy_process_fixture="$TMP/process-racy-exit"
cp -R "$valid_process_fixture" "$racy_process_fixture"
write_process_fixture "$racy_process_fixture" 303 '/transient-exits-before-read' 1000 1000 0000000000000000
env PUID=1000 PGID=1000 FRONTEND_PUID=1001 PROCESS_STATUS_ROOT="$racy_process_fixture" \
    PROCESS_CONTRACT_TEST_DELETE_PID=303 "$SMOKE" --process-contract >/dev/null

# Do not turn the race exemption into a blanket ignored read failure: a stable
# malformed status and a stable unreadable cmdline must both remain failures.
malformed_process_fixture="$TMP/process-malformed"
cp -R "$valid_process_fixture" "$malformed_process_fixture"
printf 'Name:\tbroken\n' >"$malformed_process_fixture/101/status"
if env PUID=1000 PGID=1000 FRONTEND_PUID=1001 PROCESS_STATUS_ROOT="$malformed_process_fixture" \
    "$SMOKE" --process-contract >"$malformed_process_fixture/output" 2>&1; then
    echo 'process contract accepted a malformed stable status entry' >&2
    exit 1
fi
grep -Fq 'malformed' "$malformed_process_fixture/output"

unreadable_process_fixture="$TMP/process-unreadable"
cp -R "$valid_process_fixture" "$unreadable_process_fixture"
rm -f -- "$unreadable_process_fixture/202/cmdline"
mkdir "$unreadable_process_fixture/202/cmdline"
if env PUID=1000 PGID=1000 FRONTEND_PUID=1001 PROCESS_STATUS_ROOT="$unreadable_process_fixture" \
    "$SMOKE" --process-contract >"$unreadable_process_fixture/output" 2>&1; then
    echo 'process contract accepted an unreadable stable cmdline entry' >&2
    exit 1
fi
grep -Fq 'cannot read' "$unreadable_process_fixture/output"

# Execute the real smoke script through its real-mode service discovery. The
# fake Docker command only supplies deterministic command results; the test
# therefore catches a stale SERVICES list when Compose reports the actual five
# services, rather than merely searching the source for the word radarr.
SERVICE_FAKE_BIN="$TMP/service-bin"
mkdir "$SERVICE_FAKE_BIN"
cat >"$SERVICE_FAKE_BIN/docker" <<'EOF'
#!/usr/bin/env bash
set -Eeuo pipefail
printf '%s\n' "$*" >>"${SERVICE_FAKE_LOG:?}"
if [[ "${1:-}" == compose ]]; then
    has_arg() {
        local wanted=$1 value
        shift
        for value in "$@"; do
            [[ "$value" == "$wanted" ]] && return 0
        done
        return 1
    }
    if has_arg --services "$@"; then
        printf '%s\n' nzbdav jellyfin sonarr radarr prowlarr
    elif has_arg port "$@"; then
        printf '127.0.0.1:32123\n'
    elif has_arg exec "$@"; then
        # Stop after the exact-service assertion; reaching this proves that
        # the five-service check itself passed.
        exit 1
    elif has_arg config "$@"; then
        printf '{}\n'
    fi
fi
EOF
cat >"$SERVICE_FAKE_BIN/python3" <<'EOF'
#!/usr/bin/env bash
exit 0
EOF
chmod +x "$SERVICE_FAKE_BIN/docker" "$SERVICE_FAKE_BIN/python3"

run_actual_five_service_regression() {
    local log="$TMP/service-smoke.log" status
    : >"$TMP/service-docker.log"
    set +e
    env PATH="$SERVICE_FAKE_BIN:$PATH" SERVICE_FAKE_LOG="$TMP/service-docker.log" \
        FAKE_TMP="$TMP/service-smoke-tmp" "$SMOKE" --real >"$log" 2>&1
    status=$?
    set -e
    # The intentionally failing exec is after real service-list validation.
    [ "$status" -ne 0 ] || {
        echo 'service-list smoke unexpectedly succeeded' >&2
        exit 1
    }
    ! grep -Fq 'compose did not define exactly five services' "$log" || {
        echo "smoke rejected Compose's actual five-service list" >&2
        cat "$log" >&2
        exit 1
    }
    grep -Fq 'nzbdav jellyfin sonarr radarr prowlarr' "$TMP/service-docker.log" || {
        echo 'smoke did not execute all five services through Compose' >&2
        cat "$TMP/service-docker.log" >&2
        exit 1
    }
}

mkdir "$TMP/service-smoke-tmp"
run_actual_five_service_regression

# Static deployment assertions cover the one shared completed-downloads
# volume mounted identically in every writer, plus the category-root contract.
for service in nzbdav sonarr radarr; do
    category="(\"$service\", \"completed_downloads\", \"/data/completed-downloads\")"
    grep -Fq "$category" "$SMOKE" || {
        echo "missing exact shared completed-downloads mount contract: $category" >&2
        exit 1
    }
done
for category in tv movies; do
    root="/data/completed-downloads/$category"
    grep -Fq "$root" "$SMOKE" || {
        echo "missing runtime category root contract: $root" >&2
        exit 1
    }
    ! grep -Fq "$root/$category" "$SMOKE" || {
        echo "nested duplicate category root contract found: $root/$category" >&2
        exit 1
    }
done
! grep -Eq '(sonarr|radarr)_completed_downloads' "$SMOKE" || {
    echo 'stale split completed-downloads source remains in smoke contract' >&2
    exit 1
}
[ "$(grep -Fc 'probe_cross_container_sentinel' "$SMOKE")" -ge 5 ] || {
    echo 'cross-container sentinel probes are missing' >&2
    exit 1
}

assert_no_artifacts() {
    local dir=$1
    [ "$(find "$dir" -maxdepth 1 -name 'artifact-*' -print -quit)" = '' ] || {
        echo "temporary artifacts remain in $dir" >&2
        find "$dir" -maxdepth 1 -name 'artifact-*' -print >&2
        exit 1
    }
}

run_failure_case() {
    local name=$1 expected_calls=$2 fail_at=$3 docker_fail=${4:-0}
    local dir="$TMP/$name"
    mkdir "$dir"
    : >"$dir/mktemp.log"
    printf '0\n' >"$dir/count"
    if env PATH="$FAKE_BIN:$PATH" FAKE_TMP="$dir" FAIL_AT="$fail_at" \
        FAKE_DOCKER_FAIL_PULL="$docker_fail" BLOCK_AT=0 "$SMOKE" "$5" >"$dir/smoke.log" 2>&1; then
        echo "$name: smoke unexpectedly succeeded" >&2
        exit 1
    fi
    test -f "$dir/smoke.log"
    test -s "$dir/mktemp.log"
    [ "$(wc -l <"$dir/mktemp.log")" -eq "$expected_calls" ] || {
        echo "$name: allocation log is incomplete" >&2
        cat "$dir/smoke.log" >&2
        exit 1
    }
    [ "$(cat "$dir/count")" -eq "$expected_calls" ] || {
        echo "$name: did not reach expected allocation ($expected_calls)" >&2
        cat "$dir/smoke.log" >&2
        exit 1
    }
    assert_no_artifacts "$dir"
}

# The second static allocation creates an artifact and fails. The first one
# must already be globally tracked by the EXIT trap.
run_failure_case static-second-allocation 2 2 0 --static

# A failed real override allocation must remove both static files and its own
# artifact, even though command substitution returned a non-zero status.
run_failure_case real-override-allocation 3 3 0 --real

# A failure after a successful override allocation must remove all three.
run_failure_case real-after-override 3 0 1 --real

run_signal_case() {
    local name=$1 signal=$2 expected_status=$3
    local dir="$TMP/$name" status python_status
    mkdir "$dir"
    : >"$dir/mktemp.log"
    printf '0\n' >"$dir/count"
    set +e
    status=$(
        "$REAL_PYTHON" - "$SMOKE" "$FAKE_BIN" "$dir" "$signal" <<'PY'
import os
import signal
import subprocess
import sys
import time

smoke, fake_bin, directory, signal_name = sys.argv[1:]
environment = os.environ.copy()
environment.update(
    PATH=f"{fake_bin}{os.pathsep}{environment['PATH']}",
    FAKE_TMP=directory,
    FAIL_AT="0",
    FAKE_DOCKER_FAIL_PULL="0",
    BLOCK_AT="2",
)
with open(os.path.join(directory, "smoke.log"), "w", encoding="utf-8") as log:
    process = subprocess.Popen(
        [smoke, "--static"],
        env=environment,
        stdout=log,
        stderr=subprocess.STDOUT,
        start_new_session=True,
    )
    for _ in range(200):
        try:
            with open(os.path.join(directory, "count"), encoding="utf-8") as count_file:
                count = int(count_file.read().strip())
        except (FileNotFoundError, ValueError):
            count = 0
        if count == 2:
            break
        time.sleep(0.01)
    else:
        os.killpg(process.pid, signal.SIGTERM)
        process.wait()
        raise SystemExit("second static allocation was not reached")

    os.killpg(process.pid, getattr(signal, f"SIG{signal_name}"))
    print(process.wait())
PY
    )
    python_status=$?
    set -e
    if [ "$python_status" -ne 0 ]; then
        echo "$name: signal subprocess failed" >&2
        cat "$dir/smoke.log" >&2
        exit 1
    fi
    [ "$status" -eq "$expected_status" ] || {
        echo "$name: expected exit $expected_status, got $status" >&2
        cat "$dir/smoke.log" >&2
        exit 1
    }
    test -f "$dir/smoke.log"
    test -s "$dir/mktemp.log"
    [ "$(wc -l <"$dir/mktemp.log")" -eq 2 ] || {
        echo "$name: allocation log is incomplete" >&2
        cat "$dir/smoke.log" >&2
        exit 1
    }
    assert_no_artifacts "$dir"
}

run_signal_case static-int INT 130
run_signal_case static-term TERM 143

# Run the same process-session supervisor used by the publish workflow. The
# fake Compose `up` starts a child that blocks, so this checks signal delivery
# through the real smoke shell and its entire session rather than merely
# inspecting source text. The second case deliberately ignores TERM and proves
# that the post-leader sweep kills every live member after cleanup grace.
BLOCKING_BIN="$TMP/blocking-bin"
mkdir "$BLOCKING_BIN"
cat >"$BLOCKING_BIN/docker" <<'EOF'
#!/usr/bin/env bash
set -Eeuo pipefail
log=${BLOCKING_DOCKER_LOG:?}
printf '%s\n' "$*" >>"$log"
has_arg() {
    local wanted=$1 value
    shift
    for value in "$@"; do
        [[ "$value" == "$wanted" ]] && return 0
    done
    return 1
}
if [[ "${1:-}" == compose ]]; then
    if has_arg config "$@"; then
        printf '{}\n'
    elif has_arg up "$@"; then
        : >"$BLOCKING_DOCKER_UP"
        (
            child_term() {
                printf '%s\n' term >"$BLOCKING_DOCKER_CHILD_TERM"
                if [[ "${BLOCKING_IGNORE_TERM:-0}" == 1 ]]; then
                    trap - TERM
                    while :; do sleep 1; done
                fi
                exit 143
            }
            printf '%s\n' "$BASHPID" >"$BLOCKING_DOCKER_CHILD_PID"
            trap child_term TERM
            while :; do sleep 1; done
        ) &
        child=$!
        printf '%s\n' "$BASHPID" >"$BLOCKING_DOCKER_PARENT_PID"
        docker_term() {
            # Let the smoke shell observe its interrupted Compose child and
            # enter its cleanup trap; the stubborn grandchild is what the
            # supervisor's exact-session KILL must reap after grace.
            printf '%s\n' term >"$BLOCKING_DOCKER_PARENT_TERM"
            exit 143
        }
        trap docker_term TERM
        wait "$child"
    elif has_arg port "$@"; then
        printf '127.0.0.1:32123\n'
    elif has_arg down "$@"; then
        : >"$BLOCKING_DOCKER_DOWN"
    elif has_arg image "$@"; then
        :
    fi
fi
EOF
cat >"$BLOCKING_BIN/python3" <<'EOF'
#!/usr/bin/env bash
exit 0
EOF
chmod +x "$BLOCKING_BIN/docker" "$BLOCKING_BIN/python3"

run_timeout_wrapper_case() {
    local name=$1 ignore_term=$2
    local dir="$TMP/$name" status python_status
    mkdir "$dir"
    : >"$dir/mktemp.log"
    printf '0\n' >"$dir/count"
    : >"$dir/docker.log"
    set +e
    status=$(
        "$REAL_PYTHON" - "$SMOKE" "$SUPERVISOR" "$BLOCKING_BIN" "$FAKE_BIN" "$dir" "$ignore_term" <<'PY'
import os
import subprocess
import sys
import time

smoke, supervisor, blocking_bin, fake_bin, directory, ignore_term = sys.argv[1:]
environment = os.environ.copy()
environment.update(
    PATH=os.pathsep.join((blocking_bin, fake_bin, environment["PATH"])),
    BLOCKING_DOCKER_LOG=os.path.join(directory, "docker.log"),
    BLOCKING_DOCKER_UP=os.path.join(directory, "up"),
    BLOCKING_DOCKER_PARENT_PID=os.path.join(directory, "parent.pid"),
    BLOCKING_DOCKER_CHILD_PID=os.path.join(directory, "child.pid"),
    BLOCKING_DOCKER_CHILD_TERM=os.path.join(directory, "child.term"),
    BLOCKING_DOCKER_PARENT_TERM=os.path.join(directory, "parent.term"),
    BLOCKING_DOCKER_DOWN=os.path.join(directory, "down"),
    BLOCKING_IGNORE_TERM=ignore_term,
    FAKE_TMP=directory,
    FAIL_AT="0",
    FAKE_DOCKER_FAIL_PULL="0",
    BLOCK_AT="0",
)
with open(os.path.join(directory, "smoke.log"), "w", encoding="utf-8") as log:
    # These options intentionally mirror the workflow's supervisor
    # invocation; only the durations are shortened for this bounded
    # regression.
    process = subprocess.Popen(
        [
            sys.executable, supervisor, "--deadline", "2s", "--grace", "0.2s", "--",
            "bash", smoke, "--real",
        ],
        env=environment,
        stdout=log,
        stderr=subprocess.STDOUT,
        start_new_session=True,
    )
    deadline = time.monotonic() + 5
    while not os.path.exists(os.path.join(directory, "up")):
        if process.poll() is not None:
            raise SystemExit("process-session supervisor ended before blocking Compose up")
        if time.monotonic() >= deadline:
            process.kill()
            process.wait()
            raise SystemExit("blocking Compose up was not reached")
        time.sleep(0.005)
    child_deadline = time.monotonic() + 1
    while not os.path.exists(os.path.join(directory, "child.pid")):
        if time.monotonic() >= child_deadline:
            process.kill()
            process.wait()
            raise SystemExit("blocking Docker child did not publish its PID")
        time.sleep(0.005)
    print(process.wait())
PY
    )
    python_status=$?
    set -e
    if [ "$python_status" -ne 0 ]; then
        echo "$name: process-session supervisor subprocess failed" >&2
        cat "$dir/smoke.log" >&2
        exit 1
    fi
    # The supervisor reports 124 for its own deadline even when the smoke
    # TERM trap returns 143. Either way, a deadline must never be green.
    [ "$status" -eq 124 ] || {
        echo "$name: expected timeout status 124, got $status" >&2
        cat "$dir/smoke.log" >&2
        exit 1
    }
    test -s "$dir/child.term"
    test -s "$dir/parent.term"
    test -f "$dir/down"
    grep -Fq -- 'down --volumes --remove-orphans' "$dir/docker.log" || {
        echo "$name: cleanup did not invoke Compose volume/orphan removal" >&2
        cat "$dir/docker.log" >&2
        exit 1
    }
    assert_no_artifacts "$dir"
    "$REAL_PYTHON" - "$dir/parent.pid" "$dir/child.pid" <<'PY'
import os
import sys
import time


def state(pid):
    try:
        with open(f"/proc/{pid}/stat", encoding="ascii") as stream:
            stat = stream.read()
    except (FileNotFoundError, ProcessLookupError):
        return None
    closing = stat.rfind(")")
    return stat[closing + 2 :].split()[0] if closing >= 0 else None


for pid_path in sys.argv[1:]:
    with open(pid_path, encoding="utf-8") as pid_file:
        pid = int(pid_file.read().strip())
    for _ in range(200):
        process_state = state(pid)
        # A zombie has finished executing and cannot be killed again; the
        # regression is specifically a live S-state Docker descendant.
        if process_state is None or process_state == "Z":
            break
        time.sleep(0.005)
    else:
        raise SystemExit(f"live blocking Docker process remains: {pid}, state {process_state}")
PY
}

# TERM must reach the blocking child before the grace period expires; the
# smoke trap then has time to remove its Compose volumes and temp artifact.
run_timeout_wrapper_case timeout-term-reaches-child 0
# A stubborn child must still be killed by the supervisor's post-leader sweep.
run_timeout_wrapper_case timeout-kill-after-grace 1

# A command that returns success can still leave a child behind.  Normal
# completion must perform the same exact-session sweep without changing the
# successful smoke status.
run_normal_success_with_escaped_child_case() {
    local dir="$TMP/normal-success-with-escaped-child" status python_status
    mkdir "$dir"
    cat >"$dir/escaped-child.sh" <<'EOF'
#!/usr/bin/env bash
set -Eeuo pipefail
(
    trap '' TERM INT
    printf '%s\n' "$BASHPID" >"$ESCAPED_CHILD_PID"
    while :; do sleep 1; done
) &
exit 0
EOF
    chmod +x "$dir/escaped-child.sh"
    set +e
    env ESCAPED_CHILD_PID="$dir/child.pid" "$REAL_PYTHON" "$SUPERVISOR" \
        --deadline 5s --grace 0.2s -- bash "$dir/escaped-child.sh"
    status=$?
    python_status=$status
    set -e
    [ "$python_status" -eq 0 ] || {
        echo 'normal-success supervisor subprocess failed' >&2
        exit 1
    }
    [ "$status" -eq 0 ] || {
        echo "normal-success supervisor changed status to $status" >&2
        exit 1
    }
    "$REAL_PYTHON" - "$dir/child.pid" <<'PY'
import os
import sys
import time

with open(sys.argv[1], encoding="utf-8") as pid_file:
    pid = int(pid_file.read().strip())
for _ in range(200):
    try:
        with open(f"/proc/{pid}/stat", encoding="ascii") as stream:
            stat = stream.read()
        closing = stat.rfind(")")
        process_state = stat[closing + 2 :].split()[0]
    except (FileNotFoundError, ProcessLookupError):
        break
    if process_state == "Z":
        break
    time.sleep(0.005)
else:
    raise SystemExit(f"live escaped child remains after normal success: {pid}, state {process_state}")
PY
}
run_normal_success_with_escaped_child_case

echo 'full-stack deployment allocation/signal/timeout cleanup regression: PASS'
