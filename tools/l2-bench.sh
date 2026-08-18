#!/bin/bash
# L2 segment-store benchmark.
#
# Models how NZBDAV actually uses the tier: whole-segment reads and writes of
# one yEnc part each, issued concurrently by independent streams. Block size
# defaults to the part size observed on this library (716800 bytes), because
# throughput at 716 KB and at 4 KB are different questions and only the former
# predicts playback behaviour.
#
# Usage: l2-bench.sh <directory> [segments-per-worker]
set -u

DIR="${1:?usage: l2-bench.sh <directory> [segments-per-worker]}"
PER_WORKER="${2:-40}"
SEGMENT_BYTES=716800
CONCURRENCY_LEVELS="1 4 8 16 32"
REPEATS=3

BENCH_DIR="$DIR/.l2-bench"
mkdir -p "$BENCH_DIR" || exit 1
trap 'rm -rf "$BENCH_DIR"' EXIT

drop_caches() {
    # Page cache would otherwise turn the read test into a RAM benchmark.
    sync
    [ -w /proc/sys/vm/drop_caches ] && echo 3 > /proc/sys/vm/drop_caches 2>/dev/null
}

# MB/s from bytes and a nanosecond span, without bc.
rate() {
    awk -v b="$1" -v ns="$2" 'BEGIN{ if (ns<=0) print "n/a"; else printf "%.1f", (b/1048576)/(ns/1000000000) }'
}

echo "target:      $DIR"
echo "segment:     $SEGMENT_BYTES bytes"
echo "per worker:  $PER_WORKER segments"
echo

printf '%-6s %-12s %-12s %-12s %-10s\n' "conc" "write MB/s" "read MB/s" "read p95 ms" "stability"
printf '%s\n' "----------------------------------------------------------"

for CONC in $CONCURRENCY_LEVELS; do
    W_RATES=""
    R_RATES=""

    for _ in $(seq 1 $REPEATS); do
        rm -rf "${BENCH_DIR:?}"/* 2>/dev/null

        # ---- write phase ----
        drop_caches
        START=$(date +%s%N)
        for w in $(seq 1 "$CONC"); do
            (
                mkdir -p "$BENCH_DIR/w$w"
                for s in $(seq 1 "$PER_WORKER"); do
                    dd if=/dev/zero of="$BENCH_DIR/w$w/seg-$s" \
                       bs=$SEGMENT_BYTES count=1 conv=fsync 2>/dev/null
                done
            ) &
        done
        wait
        END=$(date +%s%N)
        TOTAL=$((SEGMENT_BYTES * PER_WORKER * CONC))
        W_RATES="$W_RATES $(rate $TOTAL $((END - START)))"

        # ---- read phase ----
        drop_caches
        START=$(date +%s%N)
        for w in $(seq 1 "$CONC"); do
            (
                for s in $(seq 1 "$PER_WORKER"); do
                    dd if="$BENCH_DIR/w$w/seg-$s" of=/dev/null \
                       bs=$SEGMENT_BYTES count=1 2>/dev/null
                done
            ) &
        done
        wait
        END=$(date +%s%N)
        R_RATES="$R_RATES $(rate $TOTAL $((END - START)))"
    done

    # ---- single-segment latency, the number that governs seek responsiveness ----
    drop_caches
    LAT=$(for s in $(seq 1 20); do
        S=$(date +%s%N)
        dd if="$BENCH_DIR/w1/seg-$s" of=/dev/null bs=$SEGMENT_BYTES count=1 2>/dev/null
        E=$(date +%s%N)
        echo $(( (E - S) / 1000000 ))
    done | sort -n | awk '{a[NR]=$1} END{ print a[int(NR*0.95)] }')

    W_AVG=$(echo "$W_RATES" | awk '{s=0; for(i=1;i<=NF;i++) s+=$i; printf "%.1f", s/NF}')
    R_AVG=$(echo "$R_RATES" | awk '{s=0; for(i=1;i<=NF;i++) s+=$i; printf "%.1f", s/NF}')
    # Spread across repeats: a wide spread means the tier is not dependable
    # under sustained load, which matters more than a good peak number.
    SPREAD=$(echo "$R_RATES" | awk '{mn=$1; mx=$1; for(i=1;i<=NF;i++){if($i<mn)mn=$i; if($i>mx)mx=$i}
                                     if (mx>0) printf "+/-%.0f%%", ((mx-mn)/mx)*100; else printf "n/a"}')

    printf '%-6s %-12s %-12s %-12s %-10s\n' "$CONC" "$W_AVG" "$R_AVG" "${LAT:-n/a}" "$SPREAD"
done

echo
echo "reference: 20 concurrent 4K remux streams need ~145 MB/s aggregate."
