#!/bin/sh
set -eu
# This gate deliberately uses CAP_SYS_ADMIN rather than --privileged so the
# test exercises an actual nested bind mount while retaining a narrow grant.
export MSYS_NO_PATHCONV=1

ROOT="${NZBDAV_NESTED_BIND_GATE_ROOT:-/nzbdav-bind}"
IMAGE="${NZBDAV_ALPINE_TEST_IMAGE:-mcr.microsoft.com/dotnet/sdk:10.0-alpine}"
SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
SOURCE_ROOT=$(dirname -- "$SCRIPT_DIR")

exec docker run --rm \
  --platform linux/amd64 \
  --cap-drop=ALL \
  --cap-add=SYS_ADMIN \
  --security-opt=no-new-privileges:true \
  --tmpfs "$ROOT:rw,exec,nosuid,nodev,mode=700" \
  -e "NZBDAV_NESTED_BIND_GATE_ROOT=$ROOT" \
  -e "NZBDAV_NESTED_BIND_TEST_ROOT=$ROOT/library" \
  -e "TMPDIR=$ROOT" \
  -e HOME=/tmp \
  -e DOTNET_CLI_HOME=/tmp \
  -e DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
  -v "$SOURCE_ROOT:/src:ro" \
  -w /tmp \
  "$IMAGE" \
  sh -ec '
    ROOT="$NZBDAV_NESTED_BIND_GATE_ROOT"
    rm -rf /tmp/nzbdav-src
    mkdir /tmp/nzbdav-src
    cp -R /src/. /tmp/nzbdav-src/
    mkdir -p "$NZBDAV_NESTED_BIND_TEST_ROOT/nested" "$ROOT/bind-source"
    mount --bind "$ROOT/bind-source" "$NZBDAV_NESTED_BIND_TEST_ROOT/nested" 2>/dev/null || {
      echo "nested bind mount could not be created" >&2
      exit 77
    }
    trap "umount \"$NZBDAV_NESTED_BIND_TEST_ROOT/nested\"" EXIT INT TERM
    cd /tmp/nzbdav-src
    if [ -n "${NZBDAV_TEST_FILTER:-}" ]; then
      dotnet test jellyfin-plugin.Tests/jellyfin-plugin.Tests.csproj --configuration Release --filter "$NZBDAV_TEST_FILTER"
    else
      dotnet test jellyfin-plugin.Tests/jellyfin-plugin.Tests.csproj --configuration Release
    fi
  '
