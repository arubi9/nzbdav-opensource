#!/bin/bash
# Proves the L2 tier survives a process restart with its metadata intact.
#
# This is the failure an S3 gateway over a filesystem would hide: the body
# comes back, the hit is counted, the yEnc header is gone, and the read
# silently falls back to NNTP. L1 must be emptied first, otherwise it serves
# the range and L2 is never consulted.
set -u
cd /opt/nzbdav-opensource
COMPOSE="docker compose -f docker-compose.full-stack.yml"
N=nzbdav-full-stack-nzbdav-1
OFFSET_START=45000000000
OFFSET_END=45104857599

echo "== L2 state on the NAS before =="
find /l2mount -type f ! -name '*.meta' 2>/dev/null | wc -l

$COMPOSE stop nzbdav >/dev/null 2>&1
echo "== emptying L1 =="
docker run --rm -v nzbdav-full-stack_nzbdav_config:/config alpine \
    find /config/data/stream-cache -type f -delete
docker run --rm -v nzbdav-full-stack_nzbdav_config:/config alpine \
    sh -c 'echo "L1 files remaining: $(find /config/data/stream-cache -type f | wc -l)"'

$COMPOSE up -d --no-deps nzbdav >/dev/null 2>&1
for _ in $(seq 1 40); do
    case "$(docker ps --filter name=$N --format '{{.Status}}')" in
        *healthy*) break ;;
    esac
    sleep 10
done
echo "== container: $(docker ps --filter name=$N --format '{{.Status}}') =="

K=$(docker exec $N cat /config/bootstrap-secrets/frontend-backend-api-key)
U=$(docker exec nzbdav-full-stack-jellyfin-1 sh -c 'cat /media/nzbdav/movies/*/*.strm')

echo "== re-reading the exact range that was cached before the restart =="
docker exec nzbdav-full-stack-radarr-1 curl -s -o /dev/null \
    -H "Range: bytes=$OFFSET_START-$OFFSET_END" \
    -w 'HTTP %{http_code}  %{size_download} bytes  %{speed_download} B/s\n' "$U"

sleep 15
echo "== L2 counters =="
docker exec $N wget -qO- --header="x-api-key: $K" http://localhost:8080/metrics \
    | grep -E 'l2_cache_(hits|misses|writes|write_failures|writes_dropped)_total' \
    | grep -vE 'HELP|TYPE'

echo "== yEnc header integrity (0 means every sidecar parsed) =="
docker logs --tail 400 $N 2>&1 | grep -icE 'malformed|pre-fix|falling back to NNTP'
