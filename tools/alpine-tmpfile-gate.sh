#!/bin/sh
set -eu
# Keep Docker's in-container absolute paths intact under MSYS/Git Bash.
export MSYS_NO_PATHCONV=1

# Fresh-Linux security gate: the test process is an unprivileged UID 1000,
# has every Linux capability dropped, and writes only to a real tmpfs.
ROOT="${NZBDAV_O_TMPFILE_TEST_ROOT:-/nzbdav-tmp}"
IMAGE="${NZBDAV_ALPINE_TEST_IMAGE:-mcr.microsoft.com/dotnet/sdk:10.0-alpine}"
SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
SOURCE_ROOT=$(dirname -- "$SCRIPT_DIR")

exec docker run --rm \
  --platform linux/amd64 \
  --user 1000:1000 \
  --cap-drop=ALL \
  --security-opt=no-new-privileges:true \
  --tmpfs "$ROOT:rw,exec,nosuid,nodev,uid=1000,gid=1000,mode=700" \
  -e "NZBDAV_O_TMPFILE_TEST_ROOT=$ROOT" \
  -e "TMPDIR=$ROOT" \
  -e HOME=/tmp \
  -e DOTNET_CLI_HOME=/tmp \
  -e DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
  -v "$SOURCE_ROOT:/src:ro" \
  -w /tmp \
  "$IMAGE" \
  sh -ec 'rm -rf nzbdav-src; mkdir nzbdav-src; cp -R /src/. nzbdav-src/; cd nzbdav-src; if [ -n "${NZBDAV_TEST_FILTER:-}" ]; then dotnet test jellyfin-plugin.Tests/jellyfin-plugin.Tests.csproj --configuration Release --filter "$NZBDAV_TEST_FILTER"; else dotnet test jellyfin-plugin.Tests/jellyfin-plugin.Tests.csproj --configuration Release; fi'
