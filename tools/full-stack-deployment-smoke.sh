#!/usr/bin/env bash
# Full-stack deployment smoke test.
#
# Fast contract mode (no daemon or containers are needed beyond `compose config`):
#   tools/full-stack-deployment-smoke.sh --static
# Real mode builds and starts exactly the five services, then uses one random
# Compose project. It never prunes or removes resources outside that project:
#   tools/full-stack-deployment-smoke.sh
# It publishes loopback ports by default. Set BIND_ADDRESS explicitly for
# deliberate LAN/VPN exposure, and use HTTPS/Secure cookies for non-loopback
# browser access. Set FULL_STACK_GPU_SMOKE=1 to apply the NVIDIA override and
# require an NVIDIA device in Jellyfin.
set -Eeuo pipefail

# These are initialized before any command substitution or temporary-file
# allocation. The traps therefore also cover failures during the first
# allocation, and cleanup never dereferences an unset variable.
override=
TEMP_FILES=()
COMPOSE=()
SMOKE_IMAGES=()
CONFIG_DATA_SENTINEL_MARKER=
cleanup() {
    local status=${1:-$?}
    # A failed post-restart assertion must not leave a probe file behind while
    # Compose is still available. `down --volumes` remains the final fallback.
    if [ -n "$CONFIG_DATA_SENTINEL_MARKER" ] && [ "${#COMPOSE[@]}" -gt 0 ]; then
        remove_persistent_sentinel nzbdav /config/data "$CONFIG_DATA_SENTINEL_MARKER" >/dev/null 2>&1 || true
    fi
    CONFIG_DATA_SENTINEL_MARKER=
    if [ "${#COMPOSE[@]}" -gt 0 ]; then
        "${COMPOSE[@]}" down --volumes --remove-orphans >/dev/null 2>&1 || true
    fi
    if [ "${#SMOKE_IMAGES[@]}" -gt 0 ]; then
        docker image rm -f "${SMOKE_IMAGES[@]}" >/dev/null 2>&1 || true
    fi
    if [ "${#TEMP_FILES[@]}" -gt 0 ]; then
        rm -f -- "${TEMP_FILES[@]}" >/dev/null 2>&1 || true
    fi
    TEMP_FILES=()
    COMPOSE=()
    SMOKE_IMAGES=()
    CONFIG_DATA_SENTINEL_MARKER=
    override=
    return "$status"
}
on_signal() {
    cleanup 0
    if [ "$1" = INT ]; then exit 130; else exit 143; fi
}
trap cleanup EXIT
trap 'on_signal INT' INT
trap 'on_signal TERM' TERM

ROOT=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
cd "$ROOT"
BASE="$ROOT/docker-compose.full-stack.yml"
GPU="$ROOT/docker-compose.nvidia.yml"
SERVICES=(nzbdav jellyfin sonarr radarr prowlarr)
PYTHON=()

usage() {
    sed -n '2,9p' "$0"
}

fail() { echo "full-stack smoke: $*" >&2; exit 1; }
need() { command -v "$1" >/dev/null 2>&1 || fail "missing command: $1"; }
select_python() {
    if command -v python3 >/dev/null 2>&1 && python3 -c 'pass' >/dev/null 2>&1; then
        PYTHON=(python3)
    elif command -v py >/dev/null 2>&1 && py -3 -c 'pass' >/dev/null 2>&1; then
        PYTHON=(py -3)
    else
        fail "missing a usable Python 3 interpreter"
    fi
}

check_normalized() {
    local base_json=$1
    local gpu_json=$2
    local expected_public_port=$3
    local expected_public_url=$4
    local expected_jellyfin_published=${5:-8096}
    "${PYTHON[@]}" - "$base_json" "$gpu_json" "$expected_public_port" "$expected_public_url" "$expected_jellyfin_published" <<'PY'
import json
import re
import sys

base = json.load(open(sys.argv[1], encoding="utf-8"))
gpu = json.load(open(sys.argv[2], encoding="utf-8"))
expected_public_port = sys.argv[3]
expected_public_url = sys.argv[4]
expected_jellyfin_published = int(sys.argv[5])
services = {"nzbdav", "jellyfin", "sonarr", "radarr", "prowlarr"}
assert set(base["services"]) == services, base["services"]
assert len(base["networks"]) == 1
assert set(base["volumes"]) == {
    "nzbdav_config", "nzbdav_media", "completed_downloads",
    "jellyfin_config", "jellyfin_cache", "sonarr_config",
    "radarr_config", "prowlarr_config",
}
assert len(base["volumes"]) == 8
network = next(iter(base["networks"].values()))
assert network["driver"] == "bridge"
assert network.get("internal") is not True
for name, service in base["services"].items():
    assert "gpus" not in service
    assert "privileged" not in service
    assert all("/var/run/docker.sock" not in str(value) for value in service.values())
    if name == "nzbdav":
        assert service.get("cap_drop") == ["ALL"]
        assert set(service.get("cap_add", [])) == {"CHOWN", "DAC_OVERRIDE", "FOWNER", "SETUID", "SETGID", "KILL"}
    elif name == "jellyfin":
        assert service.get("cap_drop") == ["ALL"]
        assert set(service.get("cap_add", [])) == {"CHOWN", "DAC_OVERRIDE", "SETUID", "SETGID"}
    else:
        assert service.get("cap_drop") == ["NET_RAW"]

expected = {
    "sonarr": "linuxserver/sonarr:4.0.19.2979-ls321@sha256:373159ba768e23a3a1c497d9f2b936addf8fd5b1fdce7dd6a14080ac928bfda0",
    "radarr": "linuxserver/radarr:6.3.0.10514-ls313@sha256:a45b5ab0f850f39edb4cc9c95bbd967b52ddc3d4574a4dfb45561177db6c88f4",
    "prowlarr": "linuxserver/prowlarr:2.5.2.5491-ls156@sha256:1295cff29d10b486c0d8324d1559a552140a5932bf8b3d87e398654414f63f92",
}
for name, image in expected.items():
    assert base["services"][name]["image"] == image
assert base["services"]["nzbdav"]["build"]["dockerfile"] == "Dockerfile"
assert base["services"]["jellyfin"]["build"]["dockerfile"] == "jellyfin-stack/Dockerfile"

def mounts(name):
    return base["services"][name].get("volumes", [])
def source_users(source):
    return {name for name in services if any(m.get("source") == source for m in mounts(name))}
assert source_users("nzbdav_media") == {"nzbdav", "jellyfin"}
assert source_users("completed_downloads") == {"nzbdav", "sonarr", "radarr"}
expected_mounts = {
    ("nzbdav", "nzbdav_media", "/media/nzbdav"),
    ("jellyfin", "nzbdav_media", "/media/nzbdav"),
    # ArrSetup configures these exact category roots inside one shared volume.
    ("nzbdav", "completed_downloads", "/data/completed-downloads"),
    ("sonarr", "completed_downloads", "/data/completed-downloads"),
    ("radarr", "completed_downloads", "/data/completed-downloads"),
}
actual_mounts = {
    (name, mount.get("source"), mount.get("target"))
    for name in services
    for mount in mounts(name)
    if mount.get("source") in {"nzbdav_media", "completed_downloads"}
}
assert actual_mounts == expected_mounts, actual_mounts
assert not any(m.get("target") in {"/media/nzbdav", "/data/completed-downloads", "/data/completed-downloads/tv", "/data/completed-downloads/movies"} for m in mounts("prowlarr"))
assert not any(m.get("target") == "/media/nzbdav" and m.get("read_only") for m in mounts("jellyfin"))
for app in ("sonarr", "radarr", "prowlarr"):
    mount = next(m for m in mounts("nzbdav") if m.get("target") == f"/bootstrap/{app}")
    assert mount.get("read_only") is True

env = base["services"]["nzbdav"]["environment"]
assert env["CONFIG_PATH"] == "/config/data"
assert env["FRONTEND_PUID"] == "1001"
# Compose always supplies this optional input as an empty value when the host
# has not configured it; entrypoint clears that blank before the secret
# bootstrap process so it cannot become a persisted override.
assert env["NZBDAV_MASTER_KEY"] == ""
for key, value in {
    "BACKEND_URL": "http://localhost:8080",
    "SETUP_NZBDAV_URL": "http://nzbdav:8080",
    "SETUP_JELLYFIN_URL": "http://jellyfin:8096",
    "SETUP_SONARR_URL": "http://sonarr:8989",
    "SETUP_RADARR_URL": "http://radarr:7878",
    "SETUP_PROWLARR_URL": "http://prowlarr:9696",
}.items():
    assert env[key] == value
assert env["JELLYFIN_PUBLIC_PORT"] == expected_public_port
assert env["JELLYFIN_PUBLIC_URL"] == expected_public_url
assert set(gpu["services"]) == services
assert gpu["services"]["jellyfin"]["gpus"] == [{"count": -1}]
assert gpu["services"]["jellyfin"]["environment"]["NVIDIA_DRIVER_CAPABILITIES"] == "compute,video,utility"
assert all("gpus" not in gpu["services"][name] for name in services if name != "jellyfin")
for name, target, published in (
    ("nzbdav", 3000, 3000),
    ("jellyfin", 8096, expected_jellyfin_published),
    ("sonarr", 8989, 8989),
    ("radarr", 7878, 7878),
    ("prowlarr", 9696, 9696),
):
    ports = base["services"][name]["ports"]
    assert any(p["target"] == target and int(p["published"]) == published for p in ports), (name, ports)

dockerfile = open("jellyfin-stack/Dockerfile", encoding="utf-8").read()
assert re.search(r"FROM jellyfin/jellyfin:10\.11\.8@sha256:[0-9a-f]{64}", dockerfile)
assert re.search(r"FROM mcr\.microsoft\.com/dotnet/sdk:9\.0\.308-alpine3\.22@sha256:[0-9a-f]{64}", dockerfile)
assert re.search(r"FROM --platform=\$BUILDPLATFORM mcr\.microsoft\.com/dotnet/sdk:10\.0\.302-alpine3\.23@sha256:[0-9a-f]{64}", open("Dockerfile", encoding="utf-8").read())
assert "aspnet:10.0.10-alpine3.23@sha256:" in open("Dockerfile", encoding="utf-8").read()
assert "10.11.*" not in dockerfile
assert 'truncate -s "$((size - 1))" /opt/nzbdav-plugin/meta.json' in dockerfile
assert "manifest.sha256" in dockerfile and "--locked-mode" in dockerfile
root_dockerfile = open("Dockerfile", encoding="utf-8").read()
assert "linux-musl-x64" in root_dockerfile and "linux-musl-arm64" in root_dockerfile
assert "--locked-mode" in root_dockerfile and "--no-restore" in root_dockerfile
assert "RUN npm test -- --run" in root_dockerfile and "npm audit --omit=dev" in root_dockerfile
assert "RUN npm run test:server-image-startup" in root_dockerfile
standalone_dockerfile = open("frontend/Dockerfile", encoding="utf-8").read()
assert "RUN npm test -- --run" in standalone_dockerfile
assert "RUN npm run test:server-startup" in standalone_dockerfile
assert "RUN npm run test:server-image-startup" in standalone_dockerfile
assert "npm audit --omit=dev" in standalone_dockerfile
assert "--audit-level" not in root_dockerfile and "--audit-level" not in standalone_dockerfile
compose_file_text = open("docker-compose.full-stack.yml", encoding="utf-8").read()
assert "BIND_ADDRESS:-127.0.0.1" in compose_file_text
assert 'http://127.0.0.1:3000/' in compose_file_text
assert 'http://127.0.0.1:8080/health' in compose_file_text
assert "BIND_ADDRESS=127.0.0.1" in open("docs/full-stack-deployment.md", encoding="utf-8").read()
assert "--header 'Cookie:'" in open("tools/full-stack-deployment-smoke.sh", encoding="utf-8").read()
entrypoint = open("jellyfin-stack/entrypoint.sh", encoding="utf-8").read()
assert "setpriv --reuid" in entrypoint and "--keep-groups" in entrypoint and "find -P" in entrypoint and "mv -T" in entrypoint
assert "sha256sum -c" in entrypoint and ".nzbdav-ownership" in entrypoint
assert "plugin manifest mismatch" in entrypoint
assert "plugin_install_fingerprint" in open("tools/full-stack-deployment-smoke.sh", encoding="utf-8").read()
plugin_permissions = '''chown 0:0 "$plugins_dir" "$plugin_dir"
chmod 0755 "$plugins_dir" "$plugin_dir"
chown "$puid:$pgid" "$plugins_dir" "$plugin_dir"'''
assert entrypoint.count(plugin_permissions) == 1
print("full-stack normalized deployment contract: PASS")
PY
}

compose_config_json() {
    local output=$1
    local compose_file=$2
    local compose_overlay=$3
    shift 3
    if [ -n "$compose_overlay" ]; then
        if [ $# -eq 0 ]; then
            docker compose -f "$compose_file" -f "$compose_overlay" config --format json >"$output"
        else
            env "$@" docker compose -f "$compose_file" -f "$compose_overlay" config --format json >"$output"
        fi
    else
        if [ $# -eq 0 ]; then
            docker compose -f "$compose_file" config --format json >"$output"
        else
            env "$@" docker compose -f "$compose_file" config --format json >"$output"
        fi
    fi
}

static_contract() {
    need docker
    select_python
    local base_file gpu_file
    local -a env_pair
    base_file=
    gpu_file=
    if ! base_file=$(mktemp); then
        [ -z "$base_file" ] || TEMP_FILES+=("$base_file")
        return 1
    fi
    TEMP_FILES+=("$base_file")
    if ! gpu_file=$(mktemp); then
        [ -z "$gpu_file" ] || TEMP_FILES+=("$gpu_file")
        return 1
    fi
    TEMP_FILES+=("$gpu_file")

    EXPECTED_JELLYFIN_PUBLIC_PORT=8096
    EXPECTED_JELLYFIN_PUBLIC_URL=""
    EXPECTED_JELLYFIN_PUBLISHED=8096
    env_pair=(JELLYFIN_PUBLIC_PORT=8096 NZBDAV_MASTER_KEY=)
    compose_config_json "$base_file" "$BASE" "" "${env_pair[@]}"
    compose_config_json "$gpu_file" "$BASE" "$GPU" "${env_pair[@]}"
    check_normalized "$base_file" "$gpu_file" "$EXPECTED_JELLYFIN_PUBLIC_PORT" "$EXPECTED_JELLYFIN_PUBLIC_URL" "$EXPECTED_JELLYFIN_PUBLISHED"

    EXPECTED_JELLYFIN_PUBLIC_PORT=18096
    EXPECTED_JELLYFIN_PUBLIC_URL=""
    EXPECTED_JELLYFIN_PUBLISHED=18096
    env_pair=(JELLYFIN_PORT=18096 NZBDAV_MASTER_KEY=)
    compose_config_json "$base_file" "$BASE" "" "${env_pair[@]}"
    compose_config_json "$gpu_file" "$BASE" "$GPU" "${env_pair[@]}"
    check_normalized "$base_file" "$gpu_file" "$EXPECTED_JELLYFIN_PUBLIC_PORT" "$EXPECTED_JELLYFIN_PUBLIC_URL" "$EXPECTED_JELLYFIN_PUBLISHED"

    EXPECTED_JELLYFIN_PUBLIC_PORT=19096
    EXPECTED_JELLYFIN_PUBLIC_URL="http://media.example:19096"
    EXPECTED_JELLYFIN_PUBLISHED=18096
    env_pair=(JELLYFIN_PORT=18096 JELLYFIN_PUBLIC_PORT=19096 JELLYFIN_PUBLIC_URL=http://media.example:19096 NZBDAV_MASTER_KEY=)
    compose_config_json "$base_file" "$BASE" "" "${env_pair[@]}"
    compose_config_json "$gpu_file" "$BASE" "$GPU" "${env_pair[@]}"
    check_normalized "$base_file" "$gpu_file" "$EXPECTED_JELLYFIN_PUBLIC_PORT" "$EXPECTED_JELLYFIN_PUBLIC_URL" "$EXPECTED_JELLYFIN_PUBLISHED"

}

compose_exec() {
    # The fixture service is an Alpine regression hook; production always
    # routes through the explicitly selected Compose project.
    if [ "$1" = fixture ]; then
        sh -c "$2"
    else
        "${COMPOSE[@]}" exec -T "$1" sh -c "$2"
    fi
}

runtime_prefix() {
    case "$1" in
        jellyfin) printf '%s' 'setpriv --reuid="${PUID:-1000}" --regid="${PGID:-1000}" --keep-groups' ;;
        nzbdav) printf '%s' 'su-exec "${PUID:-1000}:${PGID:-1000}"' ;;
        frontend) printf '%s' 'su-exec "${FRONTEND_PUID:-1001}:${PGID:-1000}"' ;;
        fixture) printf '%s' '' ;;
        *) printf '%s' 'user=$(getent passwd "${PUID:-1000}" | cut -d: -f1); s6-setuidgid "$user"' ;;
    esac
}

probe_write() {
    local service=$1 path=$2 marker=$3 prefix target command
    prefix=$(runtime_prefix "$service")
    target="$path/$marker"
    # Keep the write assertion meaningful: set -eu stops at a failed test or
    # write, while the trap removes only a marker that this probe created.
    command="$prefix sh -eu -c 'trap \"rm -f -- $target\" EXIT; test \"\$(id -u)\" -ne 0; test -w \"$path\"; printf smoke > \"$target\"; test \"\$(cat \"$target\")\" = smoke'"
    compose_exec "$service" "$command" >/dev/null
}

probe_egress() {
    local service=$1 prefix command
    prefix=$(runtime_prefix "$service")
    command="$prefix sh -eu -c 'getent hosts example.com >/dev/null; curl --fail --silent --show-error --max-time 10 https://example.com >/dev/null'"
    compose_exec "$service" "$command" >/dev/null
}

# Write and later verify a file through the backend principal. This is kept
# separate from the shared-media probe: /config/data belongs only to NZBDAV,
# so its meaningful boundary is that the exact backend-owned inode survives a
# NZBDAV restart at the documented CONFIG_PATH, not cross-container access.
persistent_sentinel_write() {
    local service=$1 path=$2 marker=$3 payload=$4 prefix command principal_check
    prefix=$(runtime_prefix "$service")
    # The local fixture has no demoted application principal. Production must
    # always prove the backend's actual non-root identity.
    principal_check='test "$(id -u)" -ne 0'
    [ "$service" = fixture ] && principal_check=:
    command="$prefix sh -eu -c 'path=\$1; marker=\$2; payload=\$3; target=\"\$path/\$marker\"; $principal_check; test ! -e \"\$target\"; test -w \"\$path\"; printf \"%s\" \"\$payload\" > \"\$target\"; test \"\$(cat \"\$target\")\" = \"\$payload\"; stat -c \"%d|%i|%u|%g|%a|%n\" \"\$target\"' sh '$path' '$marker' '$payload'"
    compose_exec "$service" "$command"
}

persistent_sentinel_verify() {
    local service=$1 path=$2 marker=$3 payload=$4 expected_metadata=$5 prefix command actual_metadata principal_check
    prefix=$(runtime_prefix "$service")
    # Every command substitution is escaped into the demoted remote shell.
    # Otherwise Compose's exec shell would evaluate cat before the writer runs.
    principal_check='test "$(id -u)" -ne 0'
    [ "$service" = fixture ] && principal_check=:
    command="$prefix sh -eu -c 'path=\$1; marker=\$2; payload=\$3; target=\"\$path/\$marker\"; $principal_check; test \"\$(cat \"\$target\")\" = \"\$payload\"; stat -c \"%d|%i|%u|%g|%a|%n\" \"\$target\"' sh '$path' '$marker' '$payload'"
    actual_metadata=$(compose_exec "$service" "$command")
    [ "$actual_metadata" = "$expected_metadata" ]
}

remove_persistent_sentinel() {
    local service=$1 path=$2 marker=$3 prefix command
    prefix=$(runtime_prefix "$service")
    command="$prefix sh -eu -c 'path=\$1; marker=\$2; target=\"\$path/\$marker\"; test -f \"\$target\"; rm -f -- \"\$target\"; test ! -e \"\$target\"' sh '$path' '$marker'"
    compose_exec "$service" "$command"
}

# Fixture hook for the pinned-Alpine regression. Production splits this around
# a restart so that the metadata proves named-volume persistence.
probe_persistent_sentinel() {
    local writer=$1 writer_path=$2 reader=$3 reader_path=$4 marker=$5 payload=deployment-smoke-config-data-v1 metadata
    metadata=$(persistent_sentinel_write "$writer" "$writer_path" "$marker" "$payload")
    if ! persistent_sentinel_verify "$reader" "$reader_path" "$marker" "$payload" "$metadata"; then
        remove_persistent_sentinel "$writer" "$writer_path" "$marker" >/dev/null 2>&1 || true
        return 1
    fi
    remove_persistent_sentinel "$writer" "$writer_path" "$marker"
}

# Prove the named volume is the same filesystem from both containers, not just
# writable at two independently-created paths. The writer creates exact bytes;
# the reader must see those bytes at its equivalent path and the same device,
# inode, and (when mountinfo exposes it) mount root/source. Keep command
# substitutions inside the demoted child shell: expanding cat in Compose's
# root exec shell would check before the child has written the sentinel.
probe_cross_container_sentinel() {
    local writer=$1 writer_path=$2 reader=$3 reader_path=$4 marker=$5
    local writer_prefix reader_prefix writer_command reader_command metadata_command cleanup_command
    local writer_identity reader_identity writer_stat reader_stat writer_mount reader_mount
    local writer_device writer_inode writer_observed_path reader_device reader_inode reader_observed_path
    writer_prefix=$(runtime_prefix "$writer")
    reader_prefix=$(runtime_prefix "$reader")
    writer_command="$writer_prefix sh -eu -c 'printf cross-container > \"$writer_path/$marker\"; test \"\$(cat \"$writer_path/$marker\")\" = cross-container'"
    reader_command="$reader_prefix sh -eu -c 'test \"\$(cat \"$reader_path/$marker\")\" = cross-container'"
    metadata_command() {
        local path=$1
        # $1/$2 deliberately defer to the remote shell. Do not use nested
        # double quotes here: that would run cat/stat in Compose's outer shell
        # before the demoted writer command has created the file.
        printf '%s' "sh -eu -c 'path=\$1; marker=\$2; target=\"\$path/\$marker\"; test -f \"\$target\"; printf \"%s|%s|%s\\n\" \"\$(stat -c \"%d\" \"\$target\")\" \"\$(stat -c \"%i\" \"\$target\")\" \"\$target\"; mount_line=\$(grep -F \" \$path \" /proc/self/mountinfo 2>/dev/null | head -n 1 || true); if test -n \"\$mount_line\"; then set -- \$mount_line; mount_root=\$4; while test \"\$1\" != -; do shift; done; mount_source=\$3; printf \"mount-source=%s|%s\\n\" \"\$mount_root\" \"\$mount_source\"; fi' sh '$path' '$marker'"
    }
    cleanup_command="$writer_prefix sh -eu -c 'rm -f -- \"$writer_path/$marker\"'"

    compose_exec "$writer" "$writer_command" >/dev/null
    if ! compose_exec "$reader" "$reader_command" >/dev/null; then
        compose_exec "$writer" "$cleanup_command" >/dev/null 2>&1 || true
        return 1
    fi
    if ! writer_identity=$(compose_exec "$writer" "$(metadata_command "$writer_path")"); then
        compose_exec "$writer" "$cleanup_command" >/dev/null 2>&1 || true
        return 1
    fi
    if ! reader_identity=$(compose_exec "$reader" "$(metadata_command "$reader_path")"); then
        compose_exec "$writer" "$cleanup_command" >/dev/null 2>&1 || true
        return 1
    fi

    writer_stat=${writer_identity%%$'\n'*}
    reader_stat=${reader_identity%%$'\n'*}
    writer_mount=
    reader_mount=
    [[ "$writer_identity" == *$'\n'* ]] && writer_mount=${writer_identity#*$'\n'}
    [[ "$reader_identity" == *$'\n'* ]] && reader_mount=${reader_identity#*$'\n'}
    IFS='|' read -r writer_device writer_inode writer_observed_path <<<"$writer_stat"
    IFS='|' read -r reader_device reader_inode reader_observed_path <<<"$reader_stat"
    if [[ "$writer_device" != "$reader_device" || "$writer_inode" != "$reader_inode" ||
          "$writer_observed_path" != "$writer_path/$marker" || "$reader_observed_path" != "$reader_path/$marker" ||
          ( -n "$writer_mount" && -n "$reader_mount" && "$writer_mount" != "$reader_mount" ) ]]; then
        compose_exec "$writer" "$cleanup_command" >/dev/null 2>&1 || true
        return 1
    fi
    compose_exec "$writer" "$cleanup_command" >/dev/null
}

root_write_denial_probe() {
    trap "rm -f -- /config/.deployment-smoke-root-write" EXIT
    test "$(id -u)" -ne 0
    test ! -w /config
    # Alpine ash exits before an `if` can handle a failed redirection when
    # errexit is set. Capture only this expected failure explicitly.
    set +e
    printf blocked > /config/.deployment-smoke-root-write
    write_status=$?
    set -e
    if [ "$write_status" -eq 0 ]; then
        exit 1
    fi
    test ! -e /config/.deployment-smoke-root-write
}

probe_not_writable() {
    local service=$1 prefix command
    prefix=$(runtime_prefix "$service")
    command="$prefix sh -eu -c '$(declare -f root_write_denial_probe); root_write_denial_probe'"
    # The frontend is a child of the NZBDAV container, not a Compose service.
    compose_exec nzbdav "$command" >/dev/null
}

probe_secret_boundary() {
    local uid_service=$1 prefix command
    prefix=$(runtime_prefix "$uid_service")
    command="$prefix sh -eu -c 'test \"\$(id -u)\" -ne 0; test ! -x /config/bootstrap-secrets; test ! -r /config/bootstrap-secrets/nzbdav-master-key; if cat /config/bootstrap-secrets/nzbdav-master-key >/dev/null 2>&1; then exit 1; fi; if mv /config/bootstrap-secrets /config/bootstrap-secrets.smoke-rename 2>/dev/null; then exit 1; fi'"
    # Both backend and frontend run inside the NZBDAV container.
    compose_exec nzbdav "$command" >/dev/null
}

assert_root_boundary() {
    compose_exec nzbdav 'sh -eu -c '\''test "$(stat -c "%u:%a" /config)" = 0:755; test "$(stat -c "%u:%a" /config/data)" = "${PUID:-1000}:700"; test "$(stat -c "%u:%a" /config/bootstrap-secrets)" = 0:700'\''' \
        || fail "NZBDAV config root/data/secret ownership contract failed"
    probe_not_writable nzbdav || fail "backend can write /config"
    probe_not_writable frontend || fail "frontend can write /config"
    if probe_write nzbdav /config root-write; then
        fail "write probe unexpectedly passed for non-writable /config"
    fi
    probe_secret_boundary nzbdav || fail "backend can traverse, read, or rename bootstrap secrets"
    probe_secret_boundary frontend || fail "frontend can traverse, read, or rename bootstrap secrets"
    probe_write nzbdav /config/data config-data || fail "backend cannot write /config/data"
}

process_contract_check() {
    cat <<'EOF'
set -eu
status_root=${PROCESS_STATUS_ROOT:-/proc}
backend_count=0
frontend_count=0
supervisor_count=0
backend_fingerprint=
frontend_fingerprint=
inspector_pid=$$

# /proc is inherently racy: a process can exit after the numeric directory is
# expanded but before one of its files is opened.  A failed read is benign
# only when the PID directory has gone away.  If it remains, retain cat/tr's
# diagnostic and fail rather than hiding an unreadable or malformed entry.
snapshot_file() {
    snapshot_pid_path=$1
    snapshot_name=$2
    # Do not wrap this cat in another command substitution: this helper is
    # itself called in one, and BusyBox/Cygwin shell variants can retain the
    # nested substitution pipe after cat has exited. Its stderr is preserved
    # for real failures; an exited PID is explicitly accepted below.
    if cat "$snapshot_pid_path/$snapshot_name"; then
        return 0
    fi
    snapshot_status=$?
    if [ ! -e "$snapshot_pid_path" ]; then
        return 2
    fi
    echo "process contract: cannot read $snapshot_pid_path/$snapshot_name while PID remains" >&2
    return "$snapshot_status"
}
snapshot_cmdline() {
    snapshot_pid_path=$1
    if tr '\000' ' ' <"$snapshot_pid_path/cmdline"; then
        return 0
    fi
    snapshot_status=$?
    if [ ! -e "$snapshot_pid_path" ]; then
        return 2
    fi
    echo "process contract: cannot read $snapshot_pid_path/cmdline while PID remains" >&2
    return "$snapshot_status"
}
stat_starttime() {
    expected_pid=$1
    awk -v expected_pid="$expected_pid" '
        $0 !~ ("^" expected_pid " \\(") { exit 1 }
        {
            stat = $0
            sub(/^.*\\) /, "", stat)
            count = split(stat, fields, " ")
            if (count < 20 || fields[20] !~ /^[0-9]+$/) exit 1
            print fields[20]
        }
    '
}
status_field() {
    expected_field=$1
    awk -v expected_field="$expected_field" '
        $1 == expected_field ":" { value = $2; matches += 1 }
        END { if (matches != 1 || value == "") exit 1; print value }
    '
}

# Snapshot numeric PID directory names before any parser helper is started.
# This prevents our own awk/tr children from becoming candidates.  The
# test-only deletion switch models the real exit-between-enumeration-and-read
# race against a writable fixture root; /proc itself cannot be written.
for pid_path in "$status_root"/[0-9]*; do
    [ -d "$pid_path" ] || continue
    pid=${pid_path##*/}
    case "$pid" in ''|*[!0-9]*) continue ;; esac
    if [ "${PROCESS_CONTRACT_TEST_DELETE_PID:-}" = "$pid" ]; then
        rm -rf -- "$pid_path"
    fi

    if stat_data=$(snapshot_file "$pid_path" stat); then :; else
        read_status=$?
        [ "$read_status" -eq 2 ] && continue
        exit "$read_status"
    fi
    if start=$(printf '%s\n' "$stat_data" | stat_starttime "$pid"); then :; else
        echo "process contract: malformed $pid_path/stat" >&2
        exit 1
    fi
    if status_data=$(snapshot_file "$pid_path" status); then :; else
        read_status=$?
        [ "$read_status" -eq 2 ] && continue
        exit "$read_status"
    fi
    if cmd=$(snapshot_cmdline "$pid_path"); then :; else
        read_status=$?
        [ "$read_status" -eq 2 ] && continue
        exit "$read_status"
    fi
    if uid=$(printf '%s\n' "$status_data" | status_field Uid) &&
       gid=$(printf '%s\n' "$status_data" | status_field Gid) &&
       groups=$(printf '%s\n' "$status_data" | status_field Groups) &&
       cap=$(printf '%s\n' "$status_data" | status_field CapEff); then :; else
        echo "process contract: malformed $pid_path/status" >&2
        exit 1
    fi

    # PID reuse is not an exit race.  Re-read stat and require its immutable
    # start time to match the snapshot before accepting this identity.
    if final_stat_data=$(snapshot_file "$pid_path" stat); then :; else
        read_status=$?
        [ "$read_status" -eq 2 ] && continue
        exit "$read_status"
    fi
    if final_start=$(printf '%s\n' "$final_stat_data" | stat_starttime "$pid"); then :; else
        echo "process contract: malformed $pid_path/stat" >&2
        exit 1
    fi
    [ "$start" = "$final_start" ] || {
        echo "process contract: PID $pid was reused during inspection" >&2
        exit 1
    }

    # The inspection shell contains these expressions in its -c argument;
    # it is a short-lived probe, not a container principal.
    [ "$pid" = "$inspector_pid" ] && continue
    case "$cmd" in
        sh\ -*\ -c*|/bin/sh\ -*\ -c*|/usr/bin/sh\ -*\ -c*|bash\ -*\ -c*) continue ;;
        # PID 1's fixed wait_either polling helper is transient by design. It
        # inherits the root supervisor's exact bootstrap identity; accept no
        # other sleep shape so a stray daemon cannot be hidden here.
        sleep\ 0.1|sleep\ 0.1\ )
            [ "$uid" = 0 ] && [ "$gid" = 0 ] && [ "$groups" = 0 ] && [ "$cap" = 00000000000000eb ] || exit 1
            continue
            ;;
    esac

    if [ "$pid" = 1 ] && printf '%s' "$cmd" | grep -Eq '(^|[[:space:]/])entrypoint\.sh([[:space:]]|$)'; then
        supervisor_count=$((supervisor_count + 1))
        [ "$uid" = 0 ] && [ "$gid" = 0 ] && [ "$groups" = 0 ] || exit 1
        # cap_add is the exact bootstrap set in docker-compose.full-stack.yml.
        [ "$cap" = 00000000000000eb ] || exit 1
    elif printf '%s' "$cmd" | grep -Eq '(^|[[:space:]/])NzbWebDAV([[:space:]]|$)'; then
        backend_count=$((backend_count + 1))
        [ "$uid" = "${PUID:-1000}" ] || exit 1
        [ "$gid" = "${PGID:-1000}" ] || exit 1
        [ "$groups" = "${PGID:-1000}" ] || exit 1
        case "$cap" in ''|*[!0]*) exit 1 ;; esac
        backend_fingerprint="$pid:$start:$uid:$gid:$cap"
    elif printf '%s' "$cmd" | grep -Eq '(^|[[:space:]/])node[[:space:]]+dist-node/server\.js([[:space:]]|$)'; then
        frontend_count=$((frontend_count + 1))
        [ "$uid" = "${FRONTEND_PUID:-1001}" ] || exit 1
        [ "$gid" = "${PGID:-1000}" ] || exit 1
        [ "$groups" = "${PGID:-1000}" ] || exit 1
        case "$cap" in ''|*[!0]*) exit 1 ;; esac
        frontend_fingerprint="$pid:$start:$uid:$gid:$cap"
    else
        # Every candidate in the pre-parser snapshot must be one of the three
        # long-lived principals.  This makes a stray daemon a hard failure.
        echo "process contract: unexpected stable process PID $pid: $cmd" >&2
        exit 1
    fi
done
[ "$supervisor_count" -eq 1 ]
[ "$backend_count" -eq 1 ]
[ "$frontend_count" -eq 1 ]
printf '%s %s\n' "$backend_fingerprint" "$frontend_fingerprint"
EOF
}

run_process_contract() {
    local status_root=${1:-${PROCESS_STATUS_ROOT:-/proc}}
    # The regression suite explicitly has Bash available (including its pinned
    # Alpine invocation); production still executes this same POSIX body via
    # Compose's in-container sh.
    PROCESS_STATUS_ROOT=$status_root PUID=${PUID:-1000} PGID=${PGID:-1000} \
        FRONTEND_PUID=${FRONTEND_PUID:-1001} bash -eu -c "$(process_contract_check)"
}

assert_application_processes() {
    local command
    command=$(process_contract_check)
    compose_exec nzbdav "$command" >/dev/null \
        || fail "NZBDAV backend/frontend process contract failed (one exact PID each, expected UID/GID, zero CapEff)"
}

process_fingerprint() {
    local service=$1 pattern=$2 check
    check=$(cat <<'EOF'
result=
for status in /proc/[0-9]*/status; do
    cmdline=${status%/status}/cmdline
    [ -r "$cmdline" ] || continue
    cmd=$(tr '\0' ' ' < "$cmdline")
    case "$cmd" in sh\ -*\ -c*|/bin/sh\ -*\ -c*|/usr/bin/sh\ -*\ -c*|bash\ -*\ -c*) continue ;; esac
    if printf '%s' "$cmd" | grep -Eq "__PATTERN__"; then
        pid=${status#/proc/}
        pid=${pid%/status}
        start=$(awk '{print $22}' "/proc/$pid/stat")
        uid=$(grep '^Uid:' "$status" | cut -f2)
        result="$pid:$start:$uid"
        break
    fi
done
test -n "$result"
printf '%s' "$result"
EOF
)
    check=${check//__PATTERN__/$pattern}
    check="set -eu
$check"
    compose_exec "$service" "$check"
}

assert_backend_api_contract() {
    local command
    command=$(cat <<'EOF'
set -eu
api_key=$(cat /config/bootstrap-secrets/frontend-backend-api-key)
# Backend/plugin traffic uses the API key header, never a browser session.
curl --fail --silent --show-error --header "X-Api-Key: $api_key" --header 'Cookie:' http://localhost:8080/health >/dev/null
curl --fail --silent --show-error --header "X-Api-Key: $api_key" --header 'Cookie:' 'http://localhost:8080/api?mode=version' >/dev/null
curl --fail --silent --show-error --header "X-Api-Key: $api_key" --header 'Cookie:' http://localhost:8080/api/manifest >/dev/null
EOF
)
    compose_exec nzbdav "$command" >/dev/null
}

assert_effective_users() {
    # s6 containers retain a root supervisor, so inspect /proc directly. The
    # pinned Jellyfin image intentionally has no ps utility.
    local service=$1 pattern=$2 check
    check=$(cat <<'EOF'
found=0
bad=0
for status in /proc/[0-9]*/status; do
    cmdline=${status%/status}/cmdline
    [ -r "$cmdline" ] || continue
    cmd=$(tr '\0' ' ' < "$cmdline")
    case "$cmd" in sh\ -c*|/bin/sh\ -c*|bash\ -c*) continue ;; esac
    if printf '%s' "$cmd" | grep -Eq "__PATTERN__"; then
        uid=$(grep '^Uid:' "$status" | cut -f2)
        found=1
        [ "$uid" -ne 0 ] || bad=1
    fi
done
test "$found" -eq 1 && test "$bad" -eq 0
EOF
)
    check=${check//__PATTERN__/$pattern}
    check="set -eu
$check"
    compose_exec "$service" "$check" \
        || fail "$service has no non-root effective application process matching $pattern"
}

assert_no_effective_caps() {
    local service=$1 pattern=$2 check
    check=$(cat <<'EOF'
found=0
for status in /proc/[0-9]*/status; do
    cmdline=${status%/status}/cmdline
    [ -r "$cmdline" ] || continue
    cmd=$(tr '\0' ' ' < "$cmdline")
    case "$cmd" in sh\ -c*|/bin/sh\ -c*|bash\ -c*) continue ;; esac
    if printf '%s' "$cmd" | grep -Eq "__PATTERN__"; then
        found=1
        cap=$(awk '/^CapEff:/ { print $2 }' "$status")
        case "$cap" in 0|0000000000000000) ;; *) exit 1 ;; esac
    fi
done
test "$found" -eq 1
EOF
)
    check=${check//__PATTERN__/$pattern}
    check="set -eu
$check"
    compose_exec "$service" "$check" \
        || fail "$service application retains effective capabilities"
}

plugin_hash_contract_command() {
    cat <<'EOF'
set -eu
source_dir=${NZBDAV_PLUGIN_SOURCE_DIR:-/opt/nzbdav-plugin}
source_manifest=${NZBDAV_PLUGIN_SOURCE_MANIFEST:-/opt/nzbdav-plugin.manifest.sha256}
plugin_dir=${NZBDAV_PLUGIN_DIR:-/config/plugins/Nzbdav}
expected_dll=Jellyfin.Plugin.Nzbdav.dll
expected_meta=meta.json
artifact_manifest=.nzbdav-artifact.manifest.sha256
artifact_marker=.nzbdav-artifact.marker
artifact_marker_value=nzbdav-plugin-artifact-v1

# The baked manifest is constrained to the two payload names before it is
# used. This is deliberately separate from the persisted artifact proof,
# which must be byte-for-byte identical to this baked manifest.
check_payload_manifest() {
    directory=$1
    manifest=$2
    test -d "$directory" && test ! -L "$directory"
    test -f "$manifest" && test ! -L "$manifest"
    test "$(stat -c '%F' "$manifest")" = 'regular file'
    test "$(wc -l < "$manifest")" -eq 2
    awk '
        NF != 2 || length($1) != 64 || $1 ~ /[^[:xdigit:]]/ { bad = 1 }
        $2 != "Jellyfin.Plugin.Nzbdav.dll" && $2 != "meta.json" { bad = 1 }
        $2 == "Jellyfin.Plugin.Nzbdav.dll" { dll += 1 }
        $2 == "meta.json" { meta += 1 }
        END { exit(bad || dll != 1 || meta != 1 ? 1 : 0) }
    ' "$manifest"
    for file in "$expected_dll" "$expected_meta"; do
        test -f "$directory/$file" && test ! -L "$directory/$file"
        test "$(stat -c '%F' "$directory/$file")" = 'regular file'
    done
    if (cd "$directory" && sha256sum -c "$manifest" >/dev/null); then
        return 0
    fi
    # Keep diagnostics to the two fixed payload names; never print payload
    # contents, URLs, or arbitrary manifest paths.
    for file in "$expected_dll" "$expected_meta"; do
        expected=$(awk -v name="$file" '$2 == name { print $1 }' "$manifest")
        actual=$(sha256sum "$directory/$file" 2>/dev/null | awk '{ print $1 }') || actual=
        if test "$actual" != "$expected"; then
            echo "Jellyfin NZBDAV manifest mismatch: $file" >&2
            return 1
        fi
    done
    echo "Jellyfin NZBDAV manifest validation failed" >&2
    return 1
}

check_source_plugin() {
    test "$(find -P "$source_dir" -mindepth 1 -maxdepth 1 | wc -l)" -eq 2
    test "$(find -P "$source_dir" -mindepth 1 -maxdepth 1 -type f | wc -l)" -eq 2
    check_payload_manifest "$source_dir" "$source_manifest"
}

check_persisted_artifact() {
    test -d "$plugin_dir" && test ! -L "$plugin_dir"
    test "$(find -P "$plugin_dir" -mindepth 1 -maxdepth 1 | wc -l)" -eq 4
    test "$(find -P "$plugin_dir" -mindepth 1 -maxdepth 1 -type f | wc -l)" -eq 4
    test "$(stat -c '%u:%g:%a' "$plugin_dir")" = "${PUID:-1000}:${PGID:-1000}:755"
    for file in "$expected_dll" "$expected_meta" "$artifact_manifest" "$artifact_marker"; do
        test -f "$plugin_dir/$file" && test ! -L "$plugin_dir/$file"
        test "$(stat -c '%F' "$plugin_dir/$file")" = 'regular file'
        test "$(stat -c '%u:%g:%a' "$plugin_dir/$file")" = "${PUID:-1000}:${PGID:-1000}:644"
    done
    test "$(cat "$plugin_dir/$artifact_marker")" = "$artifact_marker_value"
    cmp -s "$plugin_dir/$artifact_manifest" "$source_manifest"
    check_payload_manifest "$plugin_dir" "$plugin_dir/$artifact_manifest"
}

check_source_plugin
check_persisted_artifact
grep -Eq '"version"[[:space:]]*:[[:space:]]*"1\.1\.0\.0"' "$source_dir/$expected_meta"
grep -Eq '"version"[[:space:]]*:[[:space:]]*"1\.1\.0\.0"' "$plugin_dir/$expected_meta"
EOF
}

plugin_hash_contract() {
    local command
    command=$(plugin_hash_contract_command)
    compose_exec jellyfin "$command"
}

discover_port() {
    local service=$1 container_port=$2 published
    published=$("${COMPOSE[@]}" port "$service" "$container_port" | awk -F: 'NF { print $NF; exit }')
    [[ "$published" =~ ^[1-9][0-9]*$ ]] || fail "no published host port discovered for $service:$container_port"
    printf 'full-stack smoke: %s:%s -> host %s\n' "$service" "$container_port" "$published"
}

real_smoke() {
    need docker
    select_python
    static_contract
    # Port 0 asks Docker for an ephemeral host port, avoiding collisions with
    # an operator's LAN defaults (including Sonarr's commonly used 8989).
    # Other host publishes may be ephemeral, but production validates this
    # browser-facing value and rejects zero. Keep it explicit in real smoke.
    export BIND_ADDRESS=127.0.0.1
    export NZBDAV_PORT=0 JELLYFIN_PORT=0 JELLYFIN_PUBLIC_PORT=8096 SONARR_PORT=0 RADARR_PORT=0 PROWLARR_PORT=0
    project="nzbdav-smoke-$RANDOM-$$"
    override=
    COMPOSE=()
    SMOKE_IMAGES=()
    if ! override=$(mktemp); then
        [ -z "$override" ] || TEMP_FILES+=("$override")
        return 1
    fi
    TEMP_FILES+=("$override")
    SMOKE_IMAGES=(
        "$project:nzbdav"
        "$project:jellyfin"
        "$project:sonarr"
        "$project:radarr"
        "$project:prowlarr"
    )
    # The process-wide traps were installed before this first allocation;
    # all subsequent pulls, tags, and Compose operations are covered as well.
    cat >"$override" <<EOF
services:
  nzbdav:
    image: ${project}:nzbdav
  jellyfin:
    image: ${project}:jellyfin
  sonarr:
    image: ${project}:sonarr
  radarr:
    image: ${project}:radarr
  prowlarr:
    image: ${project}:prowlarr
EOF
    # The Arr services have no local build. Pull their pinned source images
    # once, then retag them into this smoke project's private image namespace.
    docker pull linuxserver/sonarr:4.0.19.2979-ls321@sha256:373159ba768e23a3a1c497d9f2b936addf8fd5b1fdce7dd6a14080ac928bfda0 >/dev/null
    docker tag linuxserver/sonarr:4.0.19.2979-ls321@sha256:373159ba768e23a3a1c497d9f2b936addf8fd5b1fdce7dd6a14080ac928bfda0 "$project:sonarr"
    docker pull linuxserver/radarr:6.3.0.10514-ls313@sha256:a45b5ab0f850f39edb4cc9c95bbd967b52ddc3d4574a4dfb45561177db6c88f4 >/dev/null
    docker tag linuxserver/radarr:6.3.0.10514-ls313@sha256:a45b5ab0f850f39edb4cc9c95bbd967b52ddc3d4574a4dfb45561177db6c88f4 "$project:radarr"
    docker pull linuxserver/prowlarr:2.5.2.5491-ls156@sha256:1295cff29d10b486c0d8324d1559a552140a5932bf8b3d87e398654414f63f92 >/dev/null
    docker tag linuxserver/prowlarr:2.5.2.5491-ls156@sha256:1295cff29d10b486c0d8324d1559a552140a5932bf8b3d87e398654414f63f92 "$project:prowlarr"
    COMPOSE=(docker compose -p "$project" -f "$BASE" -f "$override")
    if [[ "${FULL_STACK_GPU_SMOKE:-0}" == 1 ]]; then
        COMPOSE+=( -f "$GPU" )
    fi
    echo "full-stack smoke: project=$project"
    "${COMPOSE[@]}" up -d --build --wait "${SERVICES[@]}"
    discover_port nzbdav 3000
    discover_port jellyfin 8096
    discover_port sonarr 8989
    discover_port radarr 7878
    discover_port prowlarr 9696
    mapfile -t actual < <("${COMPOSE[@]}" config --services | sort)
    expected=$(printf '%s\n' "${SERVICES[@]}" | sort)
    [[ "$(printf '%s\n' "${actual[@]}")" == "$expected" ]] || fail "compose did not define exactly five services"

    # Compose exec starts a shell as the image's configured user (root for
    # Jellyfin's volume-initializing entrypoint), so inspect PID 1 itself.
    compose_exec jellyfin 'sh -eu -c '\''test "$(grep "^Uid:" /proc/1/status | cut -f2)" -ne 0'\''' \
        || fail "Jellyfin PID 1 is root"
    assert_effective_users jellyfin '/jellyfin/jellyfin'
    assert_effective_users sonarr 'Sonarr'
    assert_effective_users radarr 'Radarr'
    assert_effective_users prowlarr 'Prowlarr'
    # Inspect the independently running backend and frontend PIDs. The
    # frontend's production command is node dist-node/server.js; matching the
    # old npm wrapper (or only finding the backend) is a false green.
    assert_application_processes
    assert_no_effective_caps jellyfin '/jellyfin/jellyfin'
    before_nzbdav_process=$(process_fingerprint nzbdav 'NzbWebDAV')
    before_jellyfin_process=$(process_fingerprint jellyfin '/jellyfin/jellyfin')

    assert_root_boundary
    probe_write nzbdav /media/nzbdav media || fail "NZBDAV media volume is not writable"
    probe_write nzbdav /data/completed-downloads/tv sonarr-downloads || fail "NZBDAV Sonarr downloads volume is not writable"
    probe_write nzbdav /data/completed-downloads/movies radarr-downloads || fail "NZBDAV Radarr downloads volume is not writable"
    probe_write jellyfin /config jellyfin-config || fail "Jellyfin config volume is not writable"
    probe_write jellyfin /cache cache || fail "Jellyfin cache volume is not writable"
    probe_write jellyfin /media/nzbdav jellyfin-media || fail "Jellyfin media volume is not writable"
    probe_write sonarr /config sonarr-config || fail "Sonarr config volume is not writable"
    probe_write sonarr /data/completed-downloads/tv sonarr-downloads || fail "Sonarr downloads volume is not writable"
    probe_write radarr /config radarr-config || fail "Radarr config volume is not writable"
    probe_write radarr /data/completed-downloads/movies radarr-downloads || fail "Radarr downloads volume is not writable"
    probe_write prowlarr /config prowlarr-config || fail "Prowlarr config volume is not writable"
    probe_cross_container_sentinel nzbdav /data/completed-downloads/tv sonarr /data/completed-downloads/tv nzbdav-sonarr-forward || fail "Sonarr cannot read NZBDAV's category sentinel"
    probe_cross_container_sentinel sonarr /data/completed-downloads/tv nzbdav /data/completed-downloads/tv sonarr-nzbdav-reverse || fail "NZBDAV cannot read Sonarr's category sentinel"
    probe_cross_container_sentinel nzbdav /data/completed-downloads/movies radarr /data/completed-downloads/movies nzbdav-radarr-forward || fail "Radarr cannot read NZBDAV's category sentinel"
    probe_cross_container_sentinel radarr /data/completed-downloads/movies nzbdav /data/completed-downloads/movies radarr-nzbdav-reverse || fail "NZBDAV cannot read Radarr's category sentinel"
    compose_exec nzbdav 'sh -eu -c '\''test -f /config/data/db.sqlite; test ! -e /config/db.sqlite; test "$(stat -c "%u:%a" /config/bootstrap-secrets)" = 0:700'\''' \
        || fail "database or root-owned bootstrap secret location is incorrect"
    assert_backend_api_contract || fail "backend health/SAB/plugin APIs require a session cookie or are unavailable"

    compose_exec nzbdav 'sh -eu -c '\''su-exec "${PUID:-1000}:${PGID:-1000}" sh -eu -c "for app in sonarr radarr prowlarr; do test -r /bootstrap/\$app/config.xml; done"'\'''
    for egress_service in nzbdav sonarr radarr prowlarr; do
        probe_egress "$egress_service" || fail "$egress_service controlled egress probe failed"
    done
    compose_exec jellyfin 'sh -eu -c '\''command -v setpriv >/dev/null'\'''
    if [[ "${FULL_STACK_GPU_SMOKE:-0}" == 1 ]]; then
        compose_exec jellyfin 'sh -eu -c '\''test -e /dev/nvidiactl || test -e /dev/nvidia0'\''' \
            || fail "GPU smoke requested but no NVIDIA device is available"
    fi
    plugin_hash_contract
    plugin_install_fingerprint=$(compose_exec jellyfin "sh -eu -c 'stat -c \"%i:%s\" /config/plugins/Nzbdav/Jellyfin.Plugin.Nzbdav.dll /config/plugins/Nzbdav/meta.json /config/plugins/Nzbdav/.nzbdav-artifact.manifest.sha256 /config/plugins/Nzbdav/.nzbdav-artifact.marker'")

    probe_cross_container_sentinel jellyfin /media/nzbdav nzbdav /media/nzbdav .deployment-smoke-sentinel \
        || fail "NZBDAV cannot read Jellyfin's media sentinel on the shared /media/nzbdav volume"

    # /config/data is backend-owned (the frontend must not access it). Write
    # as NZBDAV's non-root principal now, then verify this exact inode and
    # bytes after restart using a new backend-principal exec process.
    config_sentinel_payload=deployment-smoke-config-data-v1
    config_sentinel_marker=.deployment-smoke-sentinel
    # Do not register cleanup until the exclusive create passed: if an
    # unexpected pre-existing marker stops the probe, it was not ours to erase.
    if ! config_sentinel_metadata=$(persistent_sentinel_write nzbdav /config/data "$config_sentinel_marker" "$config_sentinel_payload"); then
        fail "backend cannot write the /config/data restart sentinel"
    fi
    CONFIG_DATA_SENTINEL_MARKER=$config_sentinel_marker
    logs=$("${COMPOSE[@]}" logs --no-color jellyfin 2>&1)
    echo "$logs" | grep -Fq 'Loaded plugin: NZBDAV' \
        || fail "Jellyfin logs do not report the exact NZBDAV plugin identity"
    echo "$logs" | grep -Eq 'NZBDAV.*1\.1\.0\.0|1\.1\.0\.0.*NZBDAV' \
        || fail "Jellyfin logs do not report NZBDAV version 1.1.0.0"

    restart_since=$(date -u +%Y-%m-%dT%H:%M:%SZ)
    "${COMPOSE[@]}" restart jellyfin nzbdav
    "${COMPOSE[@]}" up -d --wait jellyfin nzbdav
    after_nzbdav_process=$(process_fingerprint nzbdav 'NzbWebDAV')
    after_jellyfin_process=$(process_fingerprint jellyfin '/jellyfin/jellyfin')
    [[ "$before_nzbdav_process" != "$after_nzbdav_process" ]] || fail "NZBDAV PID/start-time did not change after restart"
    [[ "$before_jellyfin_process" != "$after_jellyfin_process" ]] || fail "Jellyfin PID/start-time did not change after restart"
    assert_effective_users jellyfin '/jellyfin/jellyfin'
    assert_effective_users sonarr 'Sonarr'
    assert_effective_users radarr 'Radarr'
    assert_effective_users prowlarr 'Prowlarr'
    assert_application_processes
    assert_no_effective_caps jellyfin '/jellyfin/jellyfin'
    restart_logs=$("${COMPOSE[@]}" logs --since "$restart_since" --no-color jellyfin 2>&1)
    echo "$restart_logs" | grep -Fq 'Loaded plugin: NZBDAV' \
        || fail "post-restart logs do not report a fresh exact NZBDAV plugin identity"
    plugin_hash_contract
    after_plugin_install_fingerprint=$(compose_exec jellyfin "sh -eu -c 'stat -c \"%i:%s\" /config/plugins/Nzbdav/Jellyfin.Plugin.Nzbdav.dll /config/plugins/Nzbdav/meta.json /config/plugins/Nzbdav/.nzbdav-artifact.manifest.sha256 /config/plugins/Nzbdav/.nzbdav-artifact.marker'")
    [[ "$plugin_install_fingerprint" == "$after_plugin_install_fingerprint" ]] \
        || fail "Jellyfin restaged NZBDAV payload on restart"
    echo "$restart_logs" | grep -Eq 'NZBDAV.*1\.1\.0\.0|1\.1\.0\.0.*NZBDAV' \
        || fail "post-restart logs do not report NZBDAV version 1.1.0.0"
    assert_root_boundary
    compose_exec nzbdav 'sh -eu -c '\''test -f /config/data/db.sqlite; test ! -e /config/db.sqlite'\'''
    if ! persistent_sentinel_verify nzbdav /config/data "$CONFIG_DATA_SENTINEL_MARKER" "$config_sentinel_payload" "$config_sentinel_metadata"; then
        fail "NZBDAV /config/data restart sentinel did not preserve exact bytes and metadata"
    fi
    remove_persistent_sentinel nzbdav /config/data "$CONFIG_DATA_SENTINEL_MARKER" \
        || fail "NZBDAV /config/data restart sentinel cleanup failed"
    CONFIG_DATA_SENTINEL_MARKER=
    echo "full-stack real deployment smoke: PASS"
}

case "${1:---real}" in
    --static) static_contract ;;
    --process-contract) run_process_contract "${PROCESS_STATUS_ROOT:-/proc}" ;;
    --cross-container-sentinel) probe_cross_container_sentinel fixture "$2" fixture "$3" "$4" ;;
    --persistent-sentinel) probe_persistent_sentinel fixture "$2" fixture "$3" "$4" ;;
    --root-write-probe) sh -eu -c "$(declare -f root_write_denial_probe); root_write_denial_probe" ;;
    # Local fixture hook for the pinned-Alpine regression below. Production
    # calls the same command through compose_exec in plugin_hash_contract.
    --plugin-artifact-contract) sh -eu -c "$(plugin_hash_contract_command)" ;;
    --real) real_smoke ;;
    -h|--help) usage ;;
    *) usage >&2; exit 2 ;;
esac
