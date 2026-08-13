#!/usr/bin/env bash
# Build the final Jellyfin image without cache, then prove Docker can invoke
# its configured entrypoint through the image's /bin/sh interpreter.
set -Eeuo pipefail

ROOT=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
IMAGE="nzbdav-jellyfin-image-startup-test:${RANDOM:-0}-$$"
IMAGE_ID=

cleanup() {
    local status=$?
    if [ -n "$IMAGE_ID" ]; then
        docker image rm -f "$IMAGE_ID" >/dev/null 2>&1 || true
    fi
    return "$status"
}
trap cleanup EXIT

fail() {
    echo "jellyfin image startup test: $*" >&2
    exit 1
}

# --pull verifies the pinned base is available; --no-cache prevents a prior
# layer from hiding a bad COPY/shebang/permission in the checked-out source.
docker build --pull --no-cache \
    --file "$ROOT/jellyfin-stack/Dockerfile" \
    --tag "$IMAGE" \
    "$ROOT"
IMAGE_ID=$(docker image inspect --format '{{.Id}}' "$IMAGE")

entrypoint=$(docker image inspect --format '{{json .Config.Entrypoint}}' "$IMAGE")
[ "$entrypoint" = '["/usr/local/bin/nzbdav-jellyfin-entrypoint"]' ] \
    || fail "unexpected final image entrypoint: $entrypoint"

# Override only long-running Jellyfin, not the image filesystem. This proves
# the copied target exists, is executable, starts with an LF-terminated shebang,
# and names an interpreter present in the final image. /config and /cache are
# the image-declared volumes; neither can mask /usr/local/bin.
set +e
probe=$(MSYS_NO_PATHCONV=1 docker run --rm \
    --tmpfs /config --tmpfs /cache \
    --entrypoint /bin/sh "$IMAGE" -ec '
        test -x /bin/sh
        test -f /usr/local/bin/nzbdav-jellyfin-entrypoint
        test -x /usr/local/bin/nzbdav-jellyfin-entrypoint
        printf "first-bytes=%s\n" "$(od -An -tx1 -N10 /usr/local/bin/nzbdav-jellyfin-entrypoint | tr -d "[:space:]")"
    ' 2>&1)
probe_status=$?
set -e
[ "$probe_status" -eq 0 ] || fail "final image lacks /bin/sh or an executable entrypoint (status $probe_status): $probe"
[ "$probe" = 'first-bytes=23212f62696e2f73680a' ] \
    || fail "entrypoint shebang is not LF-terminated: $probe"

# PUID=0 makes the configured entrypoint exit before it can initialize a
# volume or launch Jellyfin. Seeing this exact validation proves Docker used
# the executable's shebang rather than merely finding its pathname.
set +e
output=$(MSYS_NO_PATHCONV=1 docker run --rm \
    --tmpfs /config --tmpfs /cache \
    --env PUID=0 "$IMAGE" 2>&1)
status=$?
set -e
[ "$status" -ne 0 ] || fail 'configured entrypoint unexpectedly accepted root PUID'
printf '%s\n' "$output" | grep -Fqx 'nzbdav-jellyfin: PUID must be a non-root numeric uid' \
    || fail "configured entrypoint did not execute through /bin/sh (status $status): $output"

printf 'jellyfin final-image startup contract: PASS image=%s id=%s\n' "$IMAGE" "$IMAGE_ID"
