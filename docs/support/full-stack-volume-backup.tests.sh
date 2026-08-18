#!/usr/bin/env bash
# Regression test for the exactly-eight-volume backup contract.
set -Eeuo pipefail

ROOT=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd -P)
HELPER="$ROOT/docs/support/full-stack-volume-backup.sh"
fail() { echo "backup test: $*" >&2; exit 1; }
assert_path() { [[ -e "$1" || -L "$1" ]] || fail "missing $1"; }

# Native MSYS/Git Bash is not the production validator environment. Detect it
# before creating any fixture or invoking the helper: native tar/awk operations
# can otherwise hang on regular files. Linux and WSL run the complete matrix.
native_windows_shell=0
validator_platform=$(uname -s 2>/dev/null || printf '%s' "${OSTYPE:-}")
case "$validator_platform" in
  MINGW*|MSYS*|CYGWIN*) native_windows_shell=1 ;;
esac
case "${OSTYPE:-}" in
  msys*|mingw*|cygwin*) native_windows_shell=1 ;;
esac

# The production guard must reject a deterministic non-Linux kernel before
# touching Docker, regardless of the host running this regression.
platform_fixture=$(mktemp -d)
mkdir -p "$platform_fixture/bin"
cat >"$platform_fixture/bin/uname" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' Darwin
EOF
cat >"$platform_fixture/bin/docker" <<'EOF'
#!/usr/bin/env bash
: >"${FAKE_DOCKER_CALLED:?}"
exit 99
EOF
chmod +x "$platform_fixture/bin/uname" "$platform_fixture/bin/docker"
if FAKE_DOCKER_CALLED="$platform_fixture/docker-called" \
    PATH="$platform_fixture/bin:$PATH" "$HELPER" backup "$platform_fixture/backup" \
    >"$platform_fixture/output" 2>&1; then
  fail 'fake Darwin host was accepted by the Linux-only guard'
fi
grep -Fq 'requires a Linux server or WSL' "$platform_fixture/output" || \
  fail 'non-Linux rejection did not explain the Linux/WSL requirement'
[[ ! -e "$platform_fixture/docker-called" ]] || \
  fail 'non-Linux rejection touched Docker before failing'
rm -rf -- "$platform_fixture"

if [[ "$native_windows_shell" -eq 1 ]]; then
  echo 'production Linux validator: SKIP (MSYS awk)'
  echo 'Docker named-volume round-trip: SKIP (native Windows; use WSL/Linux)'
  exit 0
fi

# Local GNU tar fixture: the completed archive has one direct tv/ tree and one
# direct movies/ tree, never a nested category directory.
fixture=$(mktemp -d)
trap 'rm -rf -- "$fixture"' EXIT
mkdir -p "$fixture/source/tv/episode/season" "$fixture/source/movies/movie"
printf 'tv fixture\n' >"$fixture/source/tv/episode/season/episode.mkv"
printf 'movie fixture\n' >"$fixture/source/movies/movie/movie.mkv"
ln -s season/episode.mkv "$fixture/source/tv/episode/current" 2>/dev/null || true
ln -s movie.mkv "$fixture/source/movies/movie/current" 2>/dev/null || true
tar --create --file "$fixture/completed_downloads.tar" --directory "$fixture/source" \
  --numeric-owner ./tv ./movies
mkdir "$fixture/target"
tar --extract --file "$fixture/completed_downloads.tar" --directory "$fixture/target" \
  --numeric-owner
assert_path "$fixture/target/tv/episode/season/episode.mkv"
assert_path "$fixture/target/movies/movie/movie.mkv"
[[ ! -e "$fixture/target/tv/tv" && ! -e "$fixture/target/movies/movies" ]] || \
  fail 'category directory was nested by fixture extraction'
if [[ -L "$fixture/source/tv/episode/current" ]]; then
  [[ "$(readlink "$fixture/target/tv/episode/current")" == season/episode.mkv ]] || fail 'TV symlink changed'
fi
if [[ -L "$fixture/source/movies/movie/current" ]]; then
  [[ "$(readlink "$fixture/target/movies/movie/current")" == movie.mkv ]] || fail 'movie symlink changed'
fi
echo 'local GNU tar completed-downloads round-trip: PASS'
rm -rf -- "$fixture"

# Exercise the production validator directly, without Docker. The helper's
# internal validate mode passes these exact bytes through sh -c, just as the
# container path does. Every invocation is bounded so malformed tar records
# cannot hang this regression test.
validator_fixture=$(mktemp -d)
validator_cleanup() { rm -rf -- "$validator_fixture"; }
trap validator_cleanup EXIT
mkdir -p "$validator_fixture/completed/tv/episode" \
  "$validator_fixture/completed/movies/movie" "$validator_fixture/ordinary"
printf 'episode\n' >"$validator_fixture/completed/tv/episode/file.mkv"
printf 'movie\n' >"$validator_fixture/completed/movies/movie/file.mkv"
printf 'ordinary\n' >"$validator_fixture/ordinary/config.ini"
tar --create --file "$validator_fixture/valid-completed.tar" \
  --directory "$validator_fixture/completed" --numeric-owner ./tv ./movies
tar --create --file "$validator_fixture/valid-ordinary.tar" \
  --directory "$validator_fixture/ordinary" --numeric-owner .

run_validator() {
  local label=$1 key=$2 archive=$3
  timeout 20s "$HELPER" validate "$key" "$archive" >/dev/null 2>&1 || \
    fail "$label validator unexpectedly failed or timed out"
}
expect_validator_failure() {
  local label=$1 key=$2 archive=$3
  if timeout 20s "$HELPER" validate "$key" "$archive" >/dev/null 2>&1; then
    fail "$label validator unexpectedly accepted archive"
  fi
}
run_validator 'valid completed' completed_downloads "$validator_fixture/valid-completed.tar"
run_validator 'valid ordinary' nzbdav_config "$validator_fixture/valid-ordinary.tar"

mkdir -p "$validator_fixture/foreign/tv" "$validator_fixture/foreign/movies" \
  "$validator_fixture/foreign/foreign"
printf bad >"$validator_fixture/foreign/foreign/file"
tar --create --file "$validator_fixture/foreign.tar" \
  --directory "$validator_fixture/foreign" --numeric-owner ./tv ./movies ./foreign
expect_validator_failure foreign completed_downloads "$validator_fixture/foreign.tar"

mkdir -p "$validator_fixture/traversal/tv" "$validator_fixture/traversal/movies"
ln -s ../outside "$validator_fixture/traversal/tv/traversal"
tar --create --file "$validator_fixture/traversal.tar" \
  --directory "$validator_fixture/traversal" --numeric-owner ./tv ./movies
expect_validator_failure traversal completed_downloads "$validator_fixture/traversal.tar"

mkdir -p "$validator_fixture/absolute/tv" "$validator_fixture/absolute/movies"
ln -s /etc/passwd "$validator_fixture/absolute/tv/absolute"
tar --create --file "$validator_fixture/absolute.tar" \
  --directory "$validator_fixture/absolute" --numeric-owner ./tv ./movies
expect_validator_failure absolute-symlink completed_downloads "$validator_fixture/absolute.tar"

mkdir -p "$validator_fixture/fifo/tv" "$validator_fixture/fifo/movies"
mkfifo "$validator_fixture/fifo/tv/fifo"
tar --create --file "$validator_fixture/fifo.tar" \
  --directory "$validator_fixture/fifo" --numeric-owner ./tv ./movies
expect_validator_failure FIFO completed_downloads "$validator_fixture/fifo.tar"

mkdir -p "$validator_fixture/arrow/tv" "$validator_fixture/arrow/movies"
printf bad >"$validator_fixture/arrow/tv/name -> literal"
tar --create --file "$validator_fixture/arrow.tar" \
  --directory "$validator_fixture/arrow" --numeric-owner ./tv ./movies
expect_validator_failure arrow completed_downloads "$validator_fixture/arrow.tar"

mkdir -p "$validator_fixture/control/tv" "$validator_fixture/control/movies"
control_name=$(printf 'line\nbreak')
printf bad >"$validator_fixture/control/tv/$control_name"
tar --create --file "$validator_fixture/control.tar" \
  --directory "$validator_fixture/control" --numeric-owner ./tv ./movies
expect_validator_failure control completed_downloads "$validator_fixture/control.tar"

cp -- "$validator_fixture/valid-completed.tar" "$validator_fixture/duplicate.tar"
tar --append --file "$validator_fixture/duplicate.tar" \
  --directory "$validator_fixture/completed" --numeric-owner ./tv/episode/file.mkv
expect_validator_failure duplicate completed_downloads "$validator_fixture/duplicate.tar"

mkdir -p "$validator_fixture/empty-category/tv"
tar --create --file "$validator_fixture/empty-category.tar" \
  --directory "$validator_fixture/empty-category" --numeric-owner ./tv
expect_validator_failure empty-category completed_downloads "$validator_fixture/empty-category.tar"
echo 'real-validator matrix: PASS'

if ! command -v docker >/dev/null 2>&1 || ! docker info >/dev/null 2>&1; then
  if [[ "${CI:-}" == true || "${CI:-}" == 1 ]]; then
    echo 'Docker unavailable in CI; named-volume round-trip cannot be skipped.' >&2
    exit 1
  fi
  echo 'Docker unavailable; named-volume round-trip: SKIP'
  exit 0
fi

backup_image='debian:bookworm-slim@sha256:abd67ffcfa541b485a3dff59865ab629aa048a6c613e639d36e7456b0b229241'
if ! docker image inspect "$backup_image" >/dev/null 2>&1 && ! docker pull "$backup_image" >/dev/null; then
  if [[ "${CI:-}" == true || "${CI:-}" == 1 ]]; then
    echo 'Docker backup image unavailable in CI; named-volume round-trip cannot be skipped.' >&2
    exit 1
  fi
  echo 'Docker backup image unavailable; named-volume round-trip: SKIP'
  exit 0
fi

sandbox=$(mktemp -d)
project="backup-test-$RANDOM"
backup="$sandbox/backup"
mkdir -p "$sandbox/bin"
containers=()
volumes=()
foreign_volume=''
cleanup() {
  set +e
  [[ -n "$foreign_volume" ]] && docker volume rm "$foreign_volume" >/dev/null 2>&1
  ((${#containers[@]})) && docker rm -f "${containers[@]}" >/dev/null 2>&1
  ((${#volumes[@]})) && docker volume rm "${volumes[@]}" >/dev/null 2>&1
  rm -rf -- "$sandbox"
}
trap cleanup EXIT

real_docker=$(command -v docker)
cat >"$sandbox/bin/docker" <<'EOF'
#!/usr/bin/env bash
set -Eeuo pipefail
if [[ ${1:-} == compose ]]; then
  service="${@: -1}"
  case "$service" in
    nzbdav) printf '%s\n' '__NZBDAV_ID__' ;;
    jellyfin) printf '%s\n' '__JELLYFIN_ID__' ;;
    sonarr) printf '%s\n' '__SONARR_ID__' ;;
    radarr) printf '%s\n' '__RADARR_ID__' ;;
    prowlarr) printf '%s\n' '__PROWLARR_ID__' ;;
    *) exit 1 ;;
  esac
  exit 0
fi
exec '__REAL_DOCKER__' "$@"
EOF
create_container() {
  local service=$1 id
  id=$(docker create --label "com.docker.compose.project=$project" \
    --label "com.docker.compose.service=$service" "$backup_image" sleep 3600)
  containers+=("$id")
  printf '%s' "$id"
}
nzbdav_id=$(create_container nzbdav)
jellyfin_id=$(create_container jellyfin)
sonarr_id=$(create_container sonarr)
radarr_id=$(create_container radarr)
prowlarr_id=$(create_container prowlarr)
sed -i \
  -e "s|__REAL_DOCKER__|$real_docker|" \
  -e "s|__NZBDAV_ID__|$nzbdav_id|" \
  -e "s|__JELLYFIN_ID__|$jellyfin_id|" \
  -e "s|__SONARR_ID__|$sonarr_id|" \
  -e "s|__RADARR_ID__|$radarr_id|" \
  -e "s|__PROWLARR_ID__|$prowlarr_id|" "$sandbox/bin/docker"
chmod +x "$sandbox/bin/docker"

volume_create() {
  local key=$1 name="${project}_${key}"
  docker volume create --label "com.docker.compose.project=$project" \
    --label "com.docker.compose.volume=$key" "$name" >/dev/null
  volumes+=("$name")
}
for key in nzbdav_config nzbdav_media completed_downloads jellyfin_config jellyfin_cache sonarr_config radarr_config prowlarr_config; do
  volume_create "$key"
done
[[ "$(docker volume ls -q --filter "label=com.docker.compose.project=$project" | wc -l)" -eq 8 ]] || fail 'fixture did not create exactly 8 physical volumes'

test_volume() {
  local volume=$1 command=$2
  docker run --rm --user 0:0 -v "$volume:/data" "$backup_image" sh -eu -c "$command"
}
test_volume "${project}_completed_downloads" \
  'mkdir -p /data/tv/episode/season /data/movies/movie; printf "episode\n" >/data/tv/episode/season/episode.mkv; ln -s season/episode.mkv /data/tv/episode/current; printf "movie\n" >/data/movies/movie/movie.mkv; ln -s movie.mkv /data/movies/movie/current; chown 123:456 /data/tv/episode/season/episode.mkv; chmod 0640 /data/tv/episode/season/episode.mkv; chmod 0750 /data/tv/episode'
PATH="$sandbox/bin:$PATH" "$HELPER" backup "$backup"
[[ "$(find "$backup" -maxdepth 1 -type f -name '*.tar' | wc -l)" -eq 8 ]] || fail 'backup did not create exactly 8 archives'
[[ -f "$backup/completed_downloads.tar" ]] || fail 'completed archive is missing'
cp -- "$backup/completed_downloads.tar" "$sandbox/good-completed_downloads.tar"

# Exercise the helper's real validator/extract path with malformed records too.
# Each rejected archive must leave the target sentinel in place, proving that
# validation still happens before the destructive clear.
make_completed_archive() {
  local command=$1
  docker run --rm --user 0:0 \
    --mount "type=volume,src=${project}_completed_downloads,dst=/source" \
    --mount "type=bind,src=$backup,dst=/backup" \
    "$backup_image" sh -eu -c "$command"
}
assert_completed_rejected() {
  local label=$1 command=$2
  make_completed_archive "$command"
  test_volume "${project}_completed_downloads" 'touch /data/tv-sentinel'
  if PATH="$sandbox/bin:$PATH" "$HELPER" restore "$backup" >/dev/null 2>&1; then
    fail "$label archive was accepted"
  fi
  test_volume "${project}_completed_downloads" 'test -e /data/tv-sentinel'
  cp -- "$sandbox/good-completed_downloads.tar" "$backup/completed_downloads.tar"
}

# A literal arrow in an ordinary filename must not be interpreted as tar's
# symlink delimiter. Escaped control characters are rejected for the same
# reason: the validator is intentionally line-oriented.
assert_completed_rejected 'ambiguous-arrow' \
  'rm -rf /source/tv /source/movies; mkdir -p /source/tv /source/movies; printf bad >"/source/tv/name -> literal"; tar --create --file /backup/completed_downloads.tar --directory /source --numeric-owner ./tv ./movies'
assert_completed_rejected 'escaped-control' \
  'rm -rf /source/tv /source/movies; mkdir -p /source/tv /source/movies; name=$(printf "line\\nbreak"); printf bad >"/source/tv/$name"; tar --create --file /backup/completed_downloads.tar --directory /source --numeric-owner ./tv ./movies'
assert_completed_rejected 'absolute-symlink' \
  'rm -rf /source/tv /source/movies; mkdir -p /source/tv /source/movies; ln -s /etc/passwd /source/tv/absolute; tar --create --file /backup/completed_downloads.tar --directory /source --numeric-owner ./tv ./movies'
assert_completed_rejected 'traversing-symlink' \
  'rm -rf /source/tv /source/movies; mkdir -p /source/tv /source/movies; ln -s ../outside /source/tv/traversal; tar --create --file /backup/completed_downloads.tar --directory /source --numeric-owner ./tv ./movies'
assert_completed_rejected 'FIFO' \
  'rm -rf /source/tv /source/movies; mkdir -p /source/tv /source/movies; mkfifo /source/tv/fifo; tar --create --file /backup/completed_downloads.tar --directory /source --numeric-owner ./tv ./movies'

# Every archive member must be validated before either clear or extraction.
test_volume "${project}_completed_downloads" 'touch /data/tv-sentinel'
mkdir -p "$sandbox/unsafe/tv" "$sandbox/unsafe/movies" "$sandbox/unsafe/foreign"
printf bad >"$sandbox/unsafe/foreign/file"
tar --create --file "$backup/completed_downloads.tar" --directory "$sandbox/unsafe" ./tv ./movies ./foreign
if PATH="$sandbox/bin:$PATH" "$HELPER" restore "$backup" >/dev/null 2>&1; then
  fail 'foreign completed archive was accepted'
fi
test_volume "${project}_completed_downloads" 'test -e /data/tv-sentinel'
rm -rf -- "$sandbox/unsafe"
cp -- "$sandbox/good-completed_downloads.tar" "$backup/completed_downloads.tar"

# An unexpected archive is rejected rather than being ignored by a glob.
touch "$backup/unexpected.tar"
if PATH="$sandbox/bin:$PATH" "$HELPER" restore "$backup" >/dev/null 2>&1; then
  fail 'unexpected archive was accepted'
fi
rm -f -- "$backup/unexpected.tar"

# A matching volume from another Compose project must not be selected.
docker volume rm "${project}_nzbdav_media" >/dev/null
foreign_volume="foreign-$RANDOM"
docker volume create --label com.docker.compose.project=foreign-project \
  --label com.docker.compose.volume=nzbdav_media "$foreign_volume" >/dev/null
if PATH="$sandbox/bin:$PATH" "$HELPER" restore "$backup" >/dev/null 2>&1; then
  fail 'foreign project volume was accepted'
fi
docker volume rm "$foreign_volume" >/dev/null
foreign_volume=''
docker volume create --label "com.docker.compose.project=$project" \
  --label "com.docker.compose.volume=nzbdav_media" "${project}_nzbdav_media" >/dev/null

# Destroy and recreate all eight named volumes, then restore the complete set.
docker volume rm "${volumes[@]}" >/dev/null
volumes=()
for key in nzbdav_config nzbdav_media completed_downloads jellyfin_config jellyfin_cache sonarr_config radarr_config prowlarr_config; do
  volume_create "$key"
done
PATH="$sandbox/bin:$PATH" "$HELPER" restore "$backup" >/dev/null

test_volume "${project}_completed_downloads" \
  'test -f /data/tv/episode/season/episode.mkv; test -f /data/movies/movie/movie.mkv; test ! -e /data/tv/tv; test ! -e /data/movies/movies; test "$(readlink /data/tv/episode/current)" = season/episode.mkv; test "$(readlink /data/movies/movie/current)" = movie.mkv; test "$(stat -c "%u:%g:%a" /data/tv/episode/season/episode.mkv)" = 123:456:640'
echo 'Docker named-volume completed-downloads round-trip: PASS'
