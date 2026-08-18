#!/usr/bin/env bash
# Build only the pinned final root image and prove Docker can execute its
# copied root supervisor. This catches CRLF/shebang regressions before E2E.
set -Eeuo pipefail

ROOT=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
IMAGE="nzbdav-root-image-startup-test:${RANDOM:-0}-$$"
IMAGE_ID=
SESSION_CONTAINER=
SESSION_VOLUME=
SESSION_TMP=
cleanup() {
  local status=$?
  [ -z "$SESSION_CONTAINER" ] || docker rm -f "$SESSION_CONTAINER" >/dev/null 2>&1 || true
  [ -z "$SESSION_VOLUME" ] || docker volume rm -f "$SESSION_VOLUME" >/dev/null 2>&1 || true
  [ -z "$SESSION_TMP" ] || rm -rf "$SESSION_TMP" || true
  [ -z "$IMAGE_ID" ] || docker image rm -f "$IMAGE_ID" >/dev/null 2>&1 || true
  return "$status"
}
trap cleanup EXIT
fail() { echo "root image startup test: $*" >&2; exit 1; }

docker build --pull --no-cache --target frontend-build --file "$ROOT/Dockerfile" "$ROOT" >/dev/null
# Build the actual final image after the frontend stage has proven the source
# build. --no-cache forces Docker COPY and the byte guard every time.
docker build --pull --no-cache --file "$ROOT/Dockerfile" --tag "$IMAGE" "$ROOT"
IMAGE_ID=$(docker image inspect --format '{{.Id}}' "$IMAGE")

# Regression for the real root supervisor rather than a direct node restart.
# The browser receives its cookies through the public onboarding flow; only
# names/attributes and registry metadata are emitted, never cookie values.
SESSION_CONTAINER="nzbdav-root-session-${RANDOM:-0}-$$"
SESSION_VOLUME="nzbdav-root-session-volume-${RANDOM:-0}-$$"
SESSION_TMP=$(mktemp -d)
docker volume create "$SESSION_VOLUME" >/dev/null
docker run --detach --name "$SESSION_CONTAINER" \
  --volume "$SESSION_VOLUME:/config" \
  --publish "127.0.0.1::3000" \
  --env NZBDAV_FULL_STACK=false \
  --env PUID=1000 --env PGID=1000 --env FRONTEND_PUID=1001 \
  --env BIND_ADDRESS=127.0.0.1 --env FRONTEND_LISTEN_ADDRESS=0.0.0.0 \
  --env SECURE_COOKIES=false \
  "$IMAGE" >/dev/null

SESSION_PORT=$(docker port "$SESSION_CONTAINER" 3000/tcp | sed -n 's/^127\.0\.0\.1://p')
[ -n "$SESSION_PORT" ] || fail "could not resolve root-image frontend port"
wait_for_onboarding() {
  local status=000 attempt
  for attempt in $(seq 1 90); do
    status=$(curl --noproxy '*' --silent --show-error --max-time 2 \
      --cookie "$SESSION_TMP/cookies" --cookie-jar "$SESSION_TMP/cookies" \
      --dump-header "$SESSION_TMP/onboarding.headers" --output "$SESSION_TMP/onboarding.body" \
      --write-out '%{http_code}' "http://127.0.0.1:$SESSION_PORT/onboarding" || true)
    [ "$status" = 200 ] && return 0
    sleep 1
  done
  fail "root image onboarding did not become ready (last HTTP $status)"
}
wait_for_onboarding
SESSION_CSRF=$(awk 'BEGIN { IGNORECASE=1 } tolower($1) == "x-csrf-token:" { gsub("\r", "", $2); print $2; exit }' "$SESSION_TMP/onboarding.headers")
[ -n "$SESSION_CSRF" ] || fail "root image onboarding omitted CSRF token"
REGISTER_STATUS=$(curl --noproxy '*' --silent --show-error --max-time 20 \
  --cookie "$SESSION_TMP/cookies" --cookie-jar "$SESSION_TMP/cookies" \
  --dump-header "$SESSION_TMP/register.headers" --output "$SESSION_TMP/register.body" \
  --write-out '%{http_code}' --request POST "http://127.0.0.1:$SESSION_PORT/onboarding" \
  --header "Origin: http://127.0.0.1:$SESSION_PORT" \
  --header 'Content-Type: application/x-www-form-urlencoded' \
  --data-urlencode 'action=legacy-register' \
  --data-urlencode "csrfToken=$SESSION_CSRF" \
  --data-urlencode 'username=root-session-restart-test' \
  --data-urlencode 'password=root-session-restart-test-password' \
  --data-urlencode 'confirm=root-session-restart-test-password')
[ "$REGISTER_STATUS" = 302 ] || fail "root image browser registration returned HTTP $REGISTER_STATUS"
grep -Eqi '^Set-Cookie: __session=[^;]+; Max-Age=604800; Path=/; HttpOnly; SameSite=Strict[[:space:]]*$' "$SESSION_TMP/register.headers" || fail "root image did not issue the required __session cookie attributes"

session_registry_metadata() {
  docker exec "$SESSION_CONTAINER" sh -ec '
    test "$(find /run -mindepth 1 -maxdepth 1 -type d -name "nzbdav-frontend-sessions-*" | wc -l)" = 1
    d=$(find /run -mindepth 1 -maxdepth 1 -type d -name "nzbdav-frontend-sessions-*")
    test -d "$d" && test ! -L "$d"
    test "$(stat -c "%u:%g:%a" "$d")" = "1001:1000:700"
    test "$(find "$d" -mindepth 1 -maxdepth 1 -type f | wc -l)" = 1
    test -f "$d/sessions.json" && test ! -L "$d/sessions.json"
    test "$(stat -c "%u:%g:%a" "$d/sessions.json")" = "1001:1000:600"
    printf "directory-inode=%s owner=%s entries=%s file-inode=%s\n" \
      "$(stat -c "%i" "$d")" "$(stat -c "%u:%g:%a" "$d")" \
      "$(find "$d" -mindepth 1 -maxdepth 1 -type f | wc -l)" "$(stat -c "%i" "$d/sessions.json")"
  '
}
SESSION_BEFORE=$(session_registry_metadata) || fail "root image session registry metadata was unsafe before restart"
printf 'root session registry before restart: %s\n' "$SESSION_BEFORE"

BEFORE_STATUS=$(curl --noproxy '*' --silent --show-error --max-time 5 \
  --cookie "$SESSION_TMP/cookies" --output "$SESSION_TMP/before-status.body" \
  --write-out '%{http_code}' "http://127.0.0.1:$SESSION_PORT/api/is-onboarding")
[ "$BEFORE_STATUS" = 200 ] || fail "root image issued session was not authenticated before restart (HTTP $BEFORE_STATUS)"
docker restart -t 20 "$SESSION_CONTAINER" >/dev/null

# Docker Desktop can transiently replace a direct docker-run host forwarder
# during restart. The full Compose E2E separately proves the published path;
# here use the same browser-issued cookie jar against the restarted frontend
# listener to isolate root-supervisor/session persistence from that host shim.
docker cp "$SESSION_TMP/cookies" "$SESSION_CONTAINER:/tmp/root-image-browser-cookies"
AFTER_STATUS=000
for SESSION_ATTEMPT in $(seq 1 90); do
  AFTER_STATUS=$(docker exec "$SESSION_CONTAINER" sh -ec \
    'curl -sS --max-time 2 --cookie /tmp/root-image-browser-cookies -o /dev/null -w "%{http_code}" http://127.0.0.1:3000/api/is-onboarding' \
    2>/dev/null || true)
  case "$AFTER_STATUS" in 200|401) break ;; esac
  sleep 1
done
SESSION_AFTER=$(session_registry_metadata) || fail "root image session registry metadata was unsafe after restart"
printf 'root session registry after restart: %s\n' "$SESSION_AFTER"
[ "$AFTER_STATUS" = 200 ] || fail "real root-image restart lost browser authentication (HTTP $AFTER_STATUS)"
printf 'root final-image session restart contract: PASS\n'

entrypoint=$(docker image inspect --format '{{json .Config.Cmd}}' "$IMAGE")
[ "$entrypoint" = '["/entrypoint.sh"]' ] || fail "unexpected final image command: $entrypoint"
probe=$(MSYS_NO_PATHCONV=1 docker run --rm --entrypoint /bin/sh "$IMAGE" -ec '
  test -x /bin/sh
  test -f /entrypoint.sh && test -x /entrypoint.sh
  test "$(head -c 10 /entrypoint.sh | od -An -tx1 | tr -d "[:space:]")" = 23212f62696e2f73680a
  ! grep -q "$(printf "\r")" /entrypoint.sh
  /bin/sh -n /entrypoint.sh
  printf "root-entrypoint=lf-sh\n"
' 2>&1) || fail "final image root entrypoint probe failed: $probe"
[ "$probe" = 'root-entrypoint=lf-sh' ] || fail "unexpected probe output: $probe"
printf 'root final-image startup contract: PASS image=%s id=%s\n' "$IMAGE" "$IMAGE_ID"
