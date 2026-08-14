#!/usr/bin/env bash
# Samples the bulk-import pipeline once a minute into a CSV, so an import run
# produces a benchmark instead of an impression. Run on the stack host:
#   nohup bash tools/bulk-import-monitor.sh /tmp/bulk-import.csv &
# Columns: every stage of grab -> ingest -> strm -> jellyfin, plus failure
# counters, so a stall shows up as one column flatlining while others grow.
set -u

OUT=${1:-/tmp/bulk-import.csv}
RADARR_KEY=${RADARR_KEY:-$(docker exec nzbdav-full-stack-radarr-1 sed -n 's|.*<ApiKey>\([^<]*\)</ApiKey>.*|\1|p' /config/config.xml)}
SONARR_KEY=${SONARR_KEY:-$(docker exec nzbdav-full-stack-sonarr-1 sed -n 's|.*<ApiKey>\([^<]*\)</ApiKey>.*|\1|p' /config/config.xml)}
JELLYFIN_KEY=${JELLYFIN_KEY:-}

metric() { # metric <prometheus-name>  -> value or 0
  docker exec nzbdav-full-stack-nzbdav-1 sh -c \
    "wget -qO- http://localhost:8080/metrics 2>/dev/null" |
    awk -v m="$1" '$1 == m { print $2; found=1 } END { if (!found) print 0 }'
}

echo "epoch,radarr_movies,radarr_with_file,radarr_queue,sonarr_series,sonarr_queue,nzbdav_queue_processing,dav_items,strm_files,jellyfin_movies,jellyfin_episodes,l2_mb,nntp_live,l2_write_failures,yenc_hits,yenc_misses" >> "$OUT"

while true; do
  radarr() { docker exec nzbdav-full-stack-radarr-1 curl -sf -H "X-Api-Key: $RADARR_KEY" "http://localhost:7878/api/v3/$1"; }
  sonarr() { docker exec nzbdav-full-stack-sonarr-1 curl -sf -H "X-Api-Key: $SONARR_KEY" "http://localhost:8989/api/v3/$1"; }

  movies=$(radarr movie | grep -c '"tmdbId"')
  with_file=$(radarr movie | grep -c '"hasFile": *true')
  rqueue=$(radarr "queue?pageSize=1" | grep -o '"totalRecords": *[0-9]*' | grep -o '[0-9]*$')
  series=$(sonarr series | grep -c '"tvdbId"')
  squeue=$(sonarr "queue?pageSize=1" | grep -o '"totalRecords": *[0-9]*' | grep -o '[0-9]*$')

  qdepth=$(metric nzbdav_queue_processing)
  dav=$(docker exec nzbdav-full-stack-nzbdav-1 sh -c \
    "wget -qO- http://localhost:8080/health 2>/dev/null" |
    grep -o '"database_items": *[0-9]*' | grep -o '[0-9]*$')
  strm=$(docker exec nzbdav-full-stack-jellyfin-1 sh -c 'find /media/nzbdav -name "*.strm" 2>/dev/null | wc -l')

  jf_movies=0; jf_episodes=0
  if [ -n "$JELLYFIN_KEY" ]; then
    counts=$(docker exec nzbdav-full-stack-radarr-1 curl -sf "http://jellyfin:8096/Items/Counts?api_key=$JELLYFIN_KEY")
    jf_movies=$(echo "$counts" | grep -o '"MovieCount": *[0-9]*' | grep -o '[0-9]*$')
    jf_episodes=$(echo "$counts" | grep -o '"EpisodeCount": *[0-9]*' | grep -o '[0-9]*$')
  fi

  l2_mb=$(du -sm /mnt/nas-l2 2>/dev/null | cut -f1)
  live=$(metric nzbdav_nntp_connections_live)
  l2fail=$(metric nzbdav_l2_cache_write_failures_total)
  yhit=$(metric nzbdav_yenc_fast_path_hits_total)
  ymiss=$(metric nzbdav_yenc_fast_path_misses_total)

  echo "$(date +%s),${movies:-0},${with_file:-0},${rqueue:-0},${series:-0},${squeue:-0},${qdepth:-0},${dav:-0},${strm:-0},${jf_movies:-0},${jf_episodes:-0},${l2_mb:-0},${live:-0},${l2fail:-0},${yhit:-0},${ymiss:-0}" >> "$OUT"
  sleep 60
done
