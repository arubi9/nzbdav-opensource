#!/usr/bin/env bash
# Usage: export NZBDAV_STREAM_API_KEY=yourkey
#        then run: bash stress-test.sh
# Local-to-server stress test harness.
# Spawns N concurrent 50MB Range requests at random mid-file offsets
# across 5 different video IDs to minimize cache collusion.
#
# Usage: ./stress-test.sh <N> [timeout_seconds]
# Requires: curl in PATH, SSH keypair to Server B (15.204.245.157).

set -u

: "${NZBDAV_STREAM_API_KEY:?ERROR: NZBDAV_STREAM_API_KEY must be set. Usage: export NZBDAV_STREAM_API_KEY=yourkey}"

N=${1:-10}
MAXTIME=${2:-90}
HOST=15.204.245.157
DOMAIN=stream.flixmango.site

# 5 video ids with known large file sizes (all > 35 GB)
declare -a VIDEOS=(
    "a9928823-2b2d-497c-bd04-27c0ae0a6116:64944467902"  # Super Mario Bros
    "86bf7369-48f1-42e4-a7e7-97777e859108:58706865936"  # Greenland
    "4c9291e8-a383-409d-9860-31cd72a9d320:39716728901"
    "a1287311-a916-4def-b430-d243c77440e8:36782488431"
    "2a8b18ab-29d9-418f-b136-71edda6d3db2:36478249559"  # Matrix
)

# 50 MB range size - realistic single-stream burst
CHUNK=$((50 * 1024 * 1024))

TMPDIR=$(mktemp -d)
trap "rm -rf '$TMPDIR'" EXIT

# Capture server baseline
snap_metrics() {
    ssh -o ConnectTimeout=8 -o BatchMode=yes debian@$HOST "curl -s http://localhost:8080/metrics | grep -E '^(nzbdav_(streams_active|l2_cache_(hits|misses|writes|write_failures|writes_dropped|queue_depth|last_write_unixtime)_total|l2_cache_queue_depth|l2_cache_last_write_unixtime|cache_(hits|misses|evictions)_total|nntp_connections_(live|active|idle)|nntp_providers_healthy)|process_cpu_seconds_total|process_resident_memory_bytes)'" 2>/dev/null
}

echo "=== stress-test: N=$N concurrent, 50 MB each, max-time=${MAXTIME}s ==="
echo "--- pre-test server state ---"
snap_metrics > "$TMPDIR/metrics.pre"
grep -E 'l2_cache_(writes_total|writes_dropped|queue_depth|hits|misses)|streams_active|cache_hit' "$TMPDIR/metrics.pre" | head

echo "--- kicking off $N streams ---"
t0=$(date +%s.%N)

for i in $(seq 1 $N); do
    # Round-robin video
    idx=$((i % ${#VIDEOS[@]}))
    entry=${VIDEOS[$idx]}
    vid=${entry%:*}
    size=${entry#*:}

    # Random offset inside file (avoiding last CHUNK bytes)
    max_start=$((size - CHUNK - 1))
    start=$((RANDOM * RANDOM % max_start))
    end=$((start + CHUNK - 1))

    curl -sk --max-time "$MAXTIME" \
        --resolve "${DOMAIN}:443:${HOST}" \
        -H "Range: bytes=${start}-${end}" \
        -o /dev/null \
        -w "stream=%{response_code} bytes=%{size_download} time=%{time_total} ttfb=%{time_starttransfer} speed=%{speed_download}\n" \
        "https://${DOMAIN}/api/stream/${vid}?apikey=${NZBDAV_STREAM_API_KEY}" \
        > "$TMPDIR/stream-${i}.out" 2>&1 &
done

wait
t1=$(date +%s.%N)
elapsed=$(awk -v t0="$t0" -v t1="$t1" 'BEGIN {printf "%.2f", t1-t0}')

echo "--- all streams done in ${elapsed}s ---"

# Tally results
ok=0
fail=0
total_bytes=0
max_ttfb=0
min_ttfb=999
sum_ttfb=0
for f in "$TMPDIR"/stream-*.out; do
    line=$(cat "$f")
    code=$(echo "$line" | sed -n 's/.*stream=\([0-9]*\).*/\1/p')
    bytes=$(echo "$line" | sed -n 's/.*bytes=\([0-9]*\).*/\1/p')
    ttfb=$(echo "$line" | sed -n 's/.*ttfb=\([0-9.]*\).*/\1/p')
    if [[ "$code" == "206" && -n "$bytes" && "$bytes" -gt 0 ]]; then
        ok=$((ok+1))
        total_bytes=$((total_bytes + bytes))
        sum_ttfb=$(awk -v s="$sum_ttfb" -v t="$ttfb" 'BEGIN {print s+t}')
        max_ttfb=$(awk -v m="$max_ttfb" -v t="$ttfb" 'BEGIN {print (t>m)?t:m}')
        min_ttfb=$(awk -v m="$min_ttfb" -v t="$ttfb" 'BEGIN {print (t<m)?t:m}')
    else
        fail=$((fail+1))
    fi
done

total_mb=$(awk -v b="$total_bytes" 'BEGIN {printf "%.1f", b/1048576}')
agg_mbps=$(awk -v b="$total_bytes" -v e="$elapsed" 'BEGIN {printf "%.0f", b*8/e/1000000}')
avg_ttfb=$(awk -v s="$sum_ttfb" -v n="$ok" 'BEGIN {printf "%.3f", (n>0)?s/n:0}')

echo
echo "=== results for N=$N ==="
echo "ok:       $ok/$N"
echo "failed:   $fail"
echo "wall:     ${elapsed}s"
echo "bytes:    ${total_mb} MB"
echo "agg:      ${agg_mbps} Mbps"
echo "ttfb:     min=${min_ttfb}s  avg=${avg_ttfb}s  max=${max_ttfb}s"

echo
echo "--- post-test server delta ---"
snap_metrics > "$TMPDIR/metrics.post"
paste "$TMPDIR/metrics.pre" "$TMPDIR/metrics.post" | awk '
/^nzbdav_l2_cache_writes_total|^nzbdav_l2_cache_writes_dropped|^nzbdav_cache_hits_total|^nzbdav_cache_misses_total|^nzbdav_l2_cache_hits_total|^nzbdav_l2_cache_misses_total|^nzbdav_nntp_connections_active/ {
    # cols: metric value (pre) metric value (post)
    if (NF>=4) {
        delta = $4 - $2
        if (delta > 0) printf "  %s: %d -> %d (+%d)\n", $1, $2, $4, delta
    }
}
/^nzbdav_l2_cache_queue_depth/ {
    if (NF>=4) printf "  %s: %d -> %d\n", $1, $2, $4
}
/^nzbdav_streams_active/ {
    if (NF>=4) printf "  %s: %d -> %d\n", $1, $2, $4
}
'

echo
echo '=== done ==='
