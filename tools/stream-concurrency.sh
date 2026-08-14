#!/bin/bash
# Concurrent-playback ceiling.
#
# Each worker is rate-limited to a real 4K remux bitrate rather than reading
# flat out, because the question is how many *streams* hold their rate, not
# how much aggregate bandwidth the box can produce. A stream that falls behind
# its bitrate is a stall for the viewer even if aggregate throughput looks
# healthy.
set -u
LEVELS="${1:-10 20 40 80}"
SECONDS_PER_RUN=30
RATE_MB=7                       # ~56 Mbps, a 4K remux
OFFSET=45000000000              # range known to be cached
N=nzbdav-full-stack-nzbdav-1

U=$(docker exec nzbdav-full-stack-jellyfin-1 sh -c 'cat /media/nzbdav/movies/*/*.strm')
BYTES=$((RATE_MB * 1024 * 1024 * SECONDS_PER_RUN))

printf '%-6s %-9s %-12s %-14s %-10s\n' "conc" "ok/total" "aggregate" "per-stream" "held rate"
printf '%s\n' "---------------------------------------------------------------"

for C in $LEVELS; do
    TMP=$(mktemp -d)
    for i in $(seq 1 "$C"); do
        # Stagger start offsets so workers do not all read identical segments.
        START=$((OFFSET + (i % 8) * 1048576))
        (
            docker exec nzbdav-full-stack-radarr-1 \
                curl -s -o /dev/null --limit-rate ${RATE_MB}M \
                -H "Range: bytes=$START-$((START + BYTES - 1))" \
                -w '%{size_download} %{speed_download}\n' "$U" > "$TMP/$i" 2>/dev/null
        ) &
    done
    wait

    OK=$(awk '$1 > 0 {n++} END{print n+0}' "$TMP"/* 2>/dev/null)
    AGG=$(awk '{s+=$2} END{printf "%.0f", s/1048576}' "$TMP"/* 2>/dev/null)
    PER=$(awk '{s+=$2; n++} END{if(n>0) printf "%.2f", (s/n)/1048576; else print 0}' "$TMP"/* 2>/dev/null)
    # A stream is "holding" if it sustained at least 95% of the target bitrate.
    HELD=$(awk -v r="$RATE_MB" '$2/1048576 >= r*0.95 {n++} END{print n+0}' "$TMP"/* 2>/dev/null)

    printf '%-6s %-9s %-12s %-14s %-10s\n' \
        "$C" "$OK/$C" "${AGG} MB/s" "${PER} MB/s" "$HELD/$C"
    rm -rf "$TMP"
done

echo
docker exec $N sh -c 'cat /proc/loadavg' 2>/dev/null
