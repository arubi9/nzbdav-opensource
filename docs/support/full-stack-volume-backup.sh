#!/usr/bin/env bash
# Bounded backup/restore for the exactly eight physical volumes in the fresh stack.
# Run from the repository root, with the Compose project stopped.
set -Eeuo pipefail

# Official Docker Hub multi-platform manifest for debian:bookworm-slim:
# https://hub.docker.com/_/debian
readonly BACKUP_IMAGE='debian:bookworm-slim@sha256:abd67ffcfa541b485a3dff59865ab629aa048a6c613e639d36e7456b0b229241'

usage() {
  cat >&2 <<'EOF'
Usage: docs/support/full-stack-volume-backup.sh backup|restore [backup-directory]

The Compose project must already be stopped. The helper resolves exactly eight
physical named volumes by exact Compose labels: completed_downloads is one
volume and one archive containing direct tv/ and movies/ trees.
EOF
  exit 2
}

ACTION=${1:-}
BACKUP_DIR=${2:-full-stack-backup}
[[ "$ACTION" == backup || "$ACTION" == restore || "$ACTION" == validate ]] || usage

SERVICES=(nzbdav jellyfin sonarr radarr prowlarr)
VOLUME_KEYS=(
  nzbdav_config
  nzbdav_media
  completed_downloads
  jellyfin_config
  jellyfin_cache
  sonarr_config
  radarr_config
  prowlarr_config
)

# Docker volume backup/restore is a Linux-server contract. WSL reports Linux
# and is therefore accepted; every other kernel is rejected before any Docker
# command or archive/volume mutation, including internal validate mode.
require_linux_host() {
  local kernel
  kernel=$(uname -s 2>/dev/null || true)
  [[ "$kernel" == Linux ]] && return 0
  echo 'Unsupported platform: backup/restore requires a Linux server or WSL; run this helper from WSL/Linux.' >&2
  exit 1
}
require_linux_host

# This validator runs in the pinned root tar image before any target is cleared.
# It accepts regular files, directories, and non-traversing relative symlinks;
# rejects special files, duplicate members, traversal, set-id modes, malformed
# numeric ownership, and paths outside the archive's direct category roots.
# GNU tar's escaped verbose listing is intentionally parsed rather than replaced
# with a custom archive parser. Escaped names and ambiguous ` -> ` records are
# rejected closed so a name cannot be mistaken for a symlink record.
read -r -d '' VALIDATE_ARCHIVE_SCRIPT <<'EOF' || true
set -eu
key=$1
archive=$2
tmpdir=$(mktemp -d /tmp/full-stack-archive.XXXXXX)
trap 'rm -rf -- "$tmpdir"' EXIT
members=$tmpdir/archive-members
normalized=$tmpdir/archive-normalized
seen=$tmpdir/archive-seen
tar --list --verbose --numeric-owner --full-time --quoting-style=escape --file "$archive" >"$members"
# GNU tar pads the owner/size columns. Normalize the first three fields while
# retaining the complete (possibly spaced) member name after the timestamp.
awk '{ name=$6; for (i=7; i<=NF; i++) name=name " " $i; print $1 "\t" $2 "\t" $3 "\t" name }' "$members" >"$normalized"
: >"$seen"
seen_tv=0
seen_movies=0
seen_root=0
tab=$(printf '\tX')
tab=${tab%X}
while IFS="$tab" read -r mode owner size name; do
  [ -n "$name" ] || { echo 'Archive member has no name.' >&2; exit 1; }
  # quoting-style=escape renders newlines and other control characters as
  # backslash escapes. Reject escapes (and literal backslashes) fail closed;
  # otherwise line-oriented awk/read parsing could change the member name.
  case "$name" in
    *\\*) echo "Archive member contains an escaped control or backslash: $name" >&2; exit 1;;
  esac
  printf '%s\n' "$mode" | grep -Eq '^[dl-][rwx-]{9}$' || {
    echo "Unsafe archive mode/type: $name" >&2; exit 1;
  }
  printf '%s\n' "$owner" | grep -Eq '^[0-9]+/[0-9]+$' || {
    echo "Invalid archive uid/gid: $name" >&2; exit 1;
  }
  printf '%s\n' "$size" | grep -Eq '^[0-9]+$' || {
    echo "Invalid archive size: $name" >&2; exit 1;
  }
  case "$mode" in *s*|*S*) echo "Set-id archive mode is not allowed: $name" >&2; exit 1;; esac
  case "$name" in
    */) name=${name%/} ;;
  esac
  case "$name" in
    *' -> '* )
      # A non-symlink name containing the delimiter is ambiguous in tar's
      # verbose format. A symlink with another delimiter in its path/target is
      # ambiguous too, so reject it after extracting the first delimiter.
      case "$mode" in
        l*) ;;
        *) echo "Ambiguous archive member name: $name" >&2; exit 1;;
      esac
      [ "${mode#?}" = "rwxrwxrwx" ] || { echo "Malformed symlink member: $name" >&2; exit 1; }
      target=${name#*' -> '}
      name=${name%%' -> '*}
      case "$target" in
        *' -> '*) echo "Ambiguous symlink member: $name" >&2; exit 1;;
      esac
      [ -n "$target" ] || { echo "Empty symlink target: $name" >&2; exit 1; }
      case "$target" in
        /*) echo "Absolute symlink target: $name" >&2; exit 1;;
      esac
      case "/$target/" in
        */../*|*/./*|*//*) echo "Traversing symlink target: $name" >&2; exit 1;;
      esac
      ;;
  esac
  grep -Fqx -- "$name" "$seen" && { echo "Duplicate archive member: $name" >&2; exit 1; }
  printf '%s\n' "$name" >>"$seen"
  case "$name" in
    .|./)
      [ "$key" != completed_downloads ] || { echo 'Completed archive has an invalid root.' >&2; exit 1; }
      [ "${mode#?}" != "rwxrwxrwx" ] || { echo 'Archive root is a symlink.' >&2; exit 1; }
      seen_root=1 ;;
    ./tv)
      [ "$key" = completed_downloads ] || { echo "Unexpected category root: $name" >&2; exit 1; }
      [ "${mode#?}" != "rwxrwxrwx" ] || { echo 'TV root is a symlink.' >&2; exit 1; }
      seen_tv=1 ;;
    ./movies)
      [ "$key" = completed_downloads ] || { echo "Unexpected category root: $name" >&2; exit 1; }
      [ "${mode#?}" != "rwxrwxrwx" ] || { echo 'Movies root is a symlink.' >&2; exit 1; }
      seen_movies=1 ;;
    ./*)
      relative=${name#./}
      case "/$relative/" in */../*|*/./*|*//*) echo "Unsafe archive path: $name" >&2; exit 1;; esac
      if [ "$key" = completed_downloads ]; then
        case "$name" in
          ./tv/*) ;;
          ./movies/*) ;;
          *) echo "Completed archive member is outside tv/movies: $name" >&2; exit 1;;
        esac
        case "$relative" in tv|tv/*) case "$relative" in tv/tv|tv/tv/*) echo "Nested TV category root: $name" >&2; exit 1;; esac;; esac
        case "$relative" in movies|movies/*) case "$relative" in movies/movies|movies/movies/*) echo "Nested movies category root: $name" >&2; exit 1;; esac;; esac
      fi ;;
    *) echo "Unsafe archive path: $name" >&2; exit 1;;
  esac
done <"$normalized"
if [ "$key" = completed_downloads ]; then
  [ "$seen_tv" -eq 1 ] && [ "$seen_movies" -eq 1 ] || {
    echo 'Completed archive must contain direct ./tv and ./movies roots.' >&2; exit 1;
  }
else
  [ "$seen_root" -eq 1 ] || { echo 'Archive is missing its root member.' >&2; exit 1; }
fi
EOF

# Internal test/support mode: run the exact validator bytes locally without Docker.
if [[ "$ACTION" == validate ]]; then
  [[ $# -eq 3 ]] || usage
  key=$2
  archive=$3
  valid_key=0
  for expected_key in "${VOLUME_KEYS[@]}"; do [[ "$key" == "$expected_key" ]] && valid_key=1; done
  [[ "$valid_key" -eq 1 ]] || usage
  exec sh -eu -c "$VALIDATE_ARCHIVE_SCRIPT" sh "$key" "$archive"
fi

[[ -f docker-compose.full-stack.yml ]] || {
  echo 'Run this helper from the repository root.' >&2
  exit 1
}

mkdir -p -- "$BACKUP_DIR"
BACKUP_DIR=$(cd -- "$BACKUP_DIR" && pwd -P)

mapfile -t NZBDAV_CONTAINERS < <(docker compose -f docker-compose.full-stack.yml ps -aq nzbdav)
[[ ${#NZBDAV_CONTAINERS[@]} -eq 1 && -n ${NZBDAV_CONTAINERS[0]} ]] || {
  echo 'Could not find exactly one Compose nzbdav container; start the stack once before backing up.' >&2
  exit 1
}

PROJECT=$(docker inspect --format '{{ index .Config.Labels "com.docker.compose.project" }}' "${NZBDAV_CONTAINERS[0]}")
[[ "$PROJECT" =~ ^[a-zA-Z0-9][a-zA-Z0-9_.-]*$ ]] || {
  echo 'The discovered Compose project label is invalid.' >&2
  exit 1
}
for service in "${SERVICES[@]}"; do
  mapfile -t service_containers < <(docker compose -f docker-compose.full-stack.yml ps -aq "$service")
  [[ ${#service_containers[@]} -eq 1 && -n ${service_containers[0]} ]] || {
    echo "Expected exactly one stopped container for service $service in project $PROJECT." >&2
    exit 1
  }
done

# Refuse a live backup or restore. Stopping with Compose is deliberately an
# operator action so a caller cannot accidentally quiesce an unrelated stack.
mapfile -t ALL_CONTAINERS < <(docker ps -aq --filter "label=com.docker.compose.project=$PROJECT")
for container in "${ALL_CONTAINERS[@]}"; do
  [[ "$(docker inspect --format '{{.State.Running}}' "$container")" == false ]] || {
    echo "Compose project $PROJECT is running; stop it before $ACTION." >&2
    exit 1
  }
done

assert_project_volume() {
  local volume=$1
  [[ "$volume" == "$PROJECT"_* ]] || {
    echo "Volume $volume does not have the Compose project prefix $PROJECT_." >&2
    exit 1
  }
}

VOLUMES=()
for key in "${VOLUME_KEYS[@]}"; do
  mapfile -t matches < <(docker volume ls -q \
    --filter "label=com.docker.compose.project=$PROJECT" \
    --filter "label=com.docker.compose.volume=$key")
  [[ ${#matches[@]} -eq 1 && -n ${matches[0]} ]] || {
    echo "Expected exactly one physical volume for Compose volume $key in project $PROJECT." >&2
    exit 1
  }
  assert_project_volume "${matches[0]}"
  VOLUMES+=("${matches[0]}")
done

# A stale split-layout volume or any other project volume is a hard error. This
# prevents a restore from silently selecting one of several similarly named
# physical volumes.
mapfile -t PROJECT_VOLUMES < <(docker volume ls -q --filter "label=com.docker.compose.project=$PROJECT" | sort)
[[ ${#PROJECT_VOLUMES[@]} -eq 8 ]] || {
  echo "Expected exactly eight physical volumes in project $PROJECT; found ${#PROJECT_VOLUMES[@]}." >&2
  exit 1
}
for volume in "${PROJECT_VOLUMES[@]}"; do
  assert_project_volume "$volume"
  found=0
  for expected in "${VOLUMES[@]}"; do [[ "$volume" == "$expected" ]] && found=1; done
  [[ "$found" -eq 1 ]] || {
    echo "Unexpected project volume $volume; refusing cross-volume ambiguity." >&2
    exit 1
  }
done

# Require a complete, unambiguous archive set. Symlinked archive files are not
# accepted because the operator must know exactly which files are restored.
assert_archive_set() {
  local require_complete=$1 path name expected found
  local -a archives=()
  while IFS= read -r path; do
    [[ -f "$path" && ! -L "$path" ]] || {
      echo "Backup archive is not a regular file: $path" >&2
      exit 1
    }
    archives+=("$(basename -- "$path")")
  done < <(find "$BACKUP_DIR" -maxdepth 1 -type f -name '*.tar' -print)
  for path in "$BACKUP_DIR"/*.tar; do
    [[ -e "$path" || -L "$path" ]] || continue
    [[ -f "$path" && ! -L "$path" ]] || {
      echo "Backup archive is not a regular file: $path" >&2
      exit 1
    }
  done
  for name in "${archives[@]}"; do
    expected=0
    for key in "${VOLUME_KEYS[@]}"; do [[ "$name" == "$key.tar" ]] && expected=1; done
    [[ "$expected" -eq 1 ]] || {
      echo "Unexpected backup archive $name; refusing cross-archive ambiguity." >&2
      exit 1
    }
  done
  if [[ "$require_complete" == 1 ]]; then
    [[ ${#archives[@]} -eq 8 ]] || {
      echo "Expected exactly eight backup archives; found ${#archives[@]}." >&2
      exit 1
    }
    for key in "${VOLUME_KEYS[@]}"; do
      found=0
      for name in "${archives[@]}"; do [[ "$name" == "$key.tar" ]] && found=1; done
      [[ "$found" -eq 1 ]] || { echo "Missing backup archive $key.tar." >&2; exit 1; }
      [[ -s "$BACKUP_DIR/$key.tar" ]] || { echo "Empty backup archive $key.tar." >&2; exit 1; }
    done
  fi
}


if [[ "$ACTION" == backup ]]; then
  assert_archive_set 0
else
  assert_archive_set 1
fi

for index in "${!VOLUME_KEYS[@]}"; do
  key=${VOLUME_KEYS[$index]}
  volume=${VOLUMES[$index]}
  archive="$BACKUP_DIR/$key.tar"
  if [[ "$ACTION" == backup ]]; then
    echo "Backing up $key ($volume)"
    if [[ "$key" == completed_downloads ]]; then
      docker run --rm --user 0:0 \
        --mount "type=volume,src=$volume,dst=/source,readonly" \
        --mount "type=bind,src=$BACKUP_DIR,dst=/backup" \
        "$BACKUP_IMAGE" tar --create --file "/backup/$key.tar" --directory /source \
          --numeric-owner --xattrs --acls ./tv ./movies
    else
      docker run --rm --user 0:0 \
        --mount "type=volume,src=$volume,dst=/source,readonly" \
        --mount "type=bind,src=$BACKUP_DIR,dst=/backup" \
        "$BACKUP_IMAGE" tar --create --file "/backup/$key.tar" --directory /source \
          --numeric-owner --xattrs --acls --one-file-system .
    fi
    [[ -s "$archive" ]] || { echo "Empty backup: $archive" >&2; exit 1; }
    docker run --rm --user 0:0 \
      --mount "type=bind,src=$BACKUP_DIR,dst=/backup,readonly" \
      "$BACKUP_IMAGE" sh -eu -c "$VALIDATE_ARCHIVE_SCRIPT" sh "$key" "/backup/$key.tar"
  else
    [[ -s "$archive" ]] || { echo "Missing backup: $archive" >&2; exit 1; }
    echo "Restoring $key ($volume)"
    docker run --rm --user 0:0 \
      --mount "type=volume,src=$volume,dst=/target" \
      --mount "type=bind,src=$BACKUP_DIR,dst=/backup,readonly" \
      "$BACKUP_IMAGE" sh -eu -c "$VALIDATE_ARCHIVE_SCRIPT
       find /target -mindepth 1 -maxdepth 1 -exec rm -rf -- {} +
       tar --extract --file "/backup/\$1.tar" --directory /target \
         --same-owner --numeric-owner --xattrs --acls --no-overwrite-dir" sh "$key" "/backup/$key.tar"
  fi
done

if [[ "$ACTION" == backup ]]; then
  assert_archive_set 1
fi
echo "$ACTION completed for Compose project $PROJECT (exactly eight physical volumes and archives)."
