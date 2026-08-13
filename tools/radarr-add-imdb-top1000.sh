#!/usr/bin/env bash
# Pull IMDB's official datasets, rank movies by Bayesian weighted
# rating (IMDB's own top-chart formula), pick top 1000, dedupe against
# Radarr's existing library, then bulk-add to Radarr under the
# Standard quality profile on /movies root.
#
# Dependencies: curl, python3, jq (install via apt if missing).
#
# Usage: bash radarr-add-imdb-top1000.sh
# Env vars (optional overrides):
#   RADARR_URL   (default http://localhost:7878)
#   RADARR_KEY   (default pulled from /config/config.xml via docker exec radarr)
#   PROFILE_ID   (default 8 = "Standard (1080p/4K WEB-DL)")
#   ROOT_PATH    (default /movies)
#   TOP_N        (default 1000)
#   MIN_VOTES    (default 25000 — filters noise titles)
#   SEARCH_AFTER (default false — set true to auto-trigger MissingMoviesSearch)

set -euo pipefail

RADARR_URL=${RADARR_URL:-http://localhost:7878}
# BusyBox grep in Radarr container doesn't support -P, use sed.
RADARR_KEY=${RADARR_KEY:-$(docker exec radarr sed -n 's|.*<ApiKey>\([^<]*\)</ApiKey>.*|\1|p' /config/config.xml)}
PROFILE_ID=${PROFILE_ID:-8}
ROOT_PATH=${ROOT_PATH:-/movies}
TOP_N=${TOP_N:-1000}
MIN_VOTES=${MIN_VOTES:-25000}
SEARCH_AFTER=${SEARCH_AFTER:-false}

echo "=== config ==="
echo "radarr: $RADARR_URL"
echo "profile: $PROFILE_ID (verify with: curl -sH 'X-Api-Key: ...' $RADARR_URL/api/v3/qualityprofile | jq)"
echo "root: $ROOT_PATH"
echo "top_n: $TOP_N  min_votes: $MIN_VOTES  search_after: $SEARCH_AFTER"
echo

# --- 1. deps ---
command -v jq      >/dev/null 2>&1 || { echo "jq required (sudo apt install jq)"; exit 1; }
command -v python3 >/dev/null 2>&1 || { echo "python3 required"; exit 1; }

WORK=$(mktemp -d); trap "rm -rf '$WORK'" EXIT
cd "$WORK"

# --- 2. download IMDB datasets ---
echo "=== downloading IMDB datasets (one-time, cached under $WORK) ==="
for f in title.basics.tsv.gz title.ratings.tsv.gz; do
    echo "  fetching $f ..."
    curl -sS -o "$f" "https://datasets.imdbws.com/$f"
done
echo "  done."

# --- 3. rank: Bayesian weighted rating (IMDB's chart formula) ---
echo
echo "=== ranking movies ==="
python3 - "$MIN_VOTES" "$TOP_N" <<'PY' > top_imdb_ids.tsv
import gzip, csv, math, sys, statistics

min_votes = int(sys.argv[1])
top_n     = int(sys.argv[2])

# Load ratings: tconst -> (averageRating, numVotes)
ratings = {}
with gzip.open('title.ratings.tsv.gz', 'rt') as f:
    for row in csv.DictReader(f, delimiter='\t'):
        tconst = row['tconst']
        avg = float(row['averageRating'])
        votes = int(row['numVotes'])
        ratings[tconst] = (avg, votes)

# Compute mean rating across all titles (used in Bayesian formula)
all_avgs = [a for (a, _) in ratings.values()]
C = sum(all_avgs) / len(all_avgs)   # global mean rating

# Filter to movies + votes threshold, join with basics
movies = []
with gzip.open('title.basics.tsv.gz', 'rt') as f:
    for row in csv.DictReader(f, delimiter='\t'):
        if row['titleType'] != 'movie':
            continue
        if row['isAdult'] == '1':
            continue
        tconst = row['tconst']
        if tconst not in ratings:
            continue
        avg, votes = ratings[tconst]
        if votes < min_votes:
            continue
        movies.append((tconst, row['primaryTitle'], row['startYear'], avg, votes))

# Bayesian weighted rating (same formula IMDB uses for their top chart):
#   WR = (v / (v + m)) * R + (m / (v + m)) * C
# m = minimum votes for inclusion; choose the 75th percentile of our pool
m = sorted(v for (_, _, _, _, v) in movies)[len(movies) * 3 // 4]

def wr(row):
    _, _, _, r, v = row
    return (v / (v + m)) * r + (m / (v + m)) * C

movies.sort(key=wr, reverse=True)

sys.stderr.write(f"pool size: {len(movies)} movies with votes >= {min_votes}\n")
sys.stderr.write(f"global mean rating C: {C:.3f}, m (bayesian prior): {m}\n")

for tconst, title, year, avg, votes in movies[:top_n]:
    print(f"{tconst}\t{title}\t{year}\t{avg}\t{votes}")
PY

echo "  wrote $(wc -l < top_imdb_ids.tsv) entries to top_imdb_ids.tsv"
head -5 top_imdb_ids.tsv | awk -F'\t' '{printf "  %s  %-50s %s  r=%s votes=%s\n", $1, $2, $3, $4, $5}'
echo

# --- 4. dedupe: fetch existing Radarr library ---
echo "=== dedupe against existing library ==="
curl -s "$RADARR_URL/api/v3/movie" -H "X-Api-Key: $RADARR_KEY" | \
    jq -r '.[] | [.imdbId, .tmdbId] | @tsv' > existing.tsv
echo "  existing library: $(wc -l < existing.tsv) entries"

# Build set of existing imdb IDs
awk -F'\t' '{if ($1 != "" && $1 != "null") print $1}' existing.tsv > existing_imdb.txt

# Filter top list to only those not already present
awk -F'\t' 'NR==FNR {have[$1]=1; next} !have[$1]' existing_imdb.txt top_imdb_ids.tsv > to_add.tsv
TO_ADD=$(wc -l < to_add.tsv)
echo "  new to add: $TO_ADD"
echo

if [ "$TO_ADD" -eq 0 ]; then
    echo "nothing to add, exiting"
    exit 0
fi

# --- 5. add to Radarr ---
echo "=== adding to Radarr (profile=$PROFILE_ID root=$ROOT_PATH) ==="
added=0; failed=0; skipped=0
while IFS=$'\t' read -r tconst title year avg votes; do
    # Lookup to resolve TMDB ID (Radarr add requires TMDB, not IMDB)
    lookup=$(curl -s "$RADARR_URL/api/v3/movie/lookup/imdb?imdbId=$tconst" -H "X-Api-Key: $RADARR_KEY")
    tmdb_id=$(echo "$lookup" | jq -r '.tmdbId // empty')
    lookup_title=$(echo "$lookup" | jq -r '.title // empty')
    lookup_year=$(echo "$lookup" | jq -r '.year // empty')
    lookup_slug=$(echo "$lookup" | jq -r '.titleSlug // empty')

    if [ -z "$tmdb_id" ] || [ "$tmdb_id" = "0" ]; then
        echo "  SKIP $tconst \"$title\" — no TMDB match"
        skipped=$((skipped+1))
        continue
    fi

    # POST add; searchForMovie=false to avoid indexer stampede — trigger
    # MissingMoviesSearch at end if SEARCH_AFTER=true.
    payload=$(jq -n \
        --arg title "$lookup_title" \
        --arg slug "$lookup_slug" \
        --argjson tmdb "$tmdb_id" \
        --argjson year "${lookup_year:-0}" \
        --argjson profile "$PROFILE_ID" \
        --arg root "$ROOT_PATH" \
        '{
          title: $title,
          titleSlug: $slug,
          tmdbId: $tmdb,
          year: $year,
          qualityProfileId: $profile,
          rootFolderPath: $root,
          monitored: true,
          minimumAvailability: "released",
          addOptions: { searchForMovie: false, monitor: "movieOnly" }
        }')

    resp=$(curl -s -w "\n%{http_code}" -X POST \
        "$RADARR_URL/api/v3/movie" \
        -H "X-Api-Key: $RADARR_KEY" \
        -H "Content-Type: application/json" \
        -d "$payload")

    code=$(echo "$resp" | tail -1)
    body=$(echo "$resp" | head -n -1)

    if [ "$code" = "201" ]; then
        added=$((added+1))
        echo "  OK    $tconst tmdb=$tmdb_id \"$lookup_title\" ($lookup_year)"
    elif [ "$code" = "400" ] && echo "$body" | grep -qi 'already been added'; then
        echo "  DUPE  $tconst \"$lookup_title\" (already present)"
        skipped=$((skipped+1))
    else
        echo "  FAIL  $tconst code=$code body=$(echo "$body" | head -c 200)"
        failed=$((failed+1))
    fi

    # Small pause to avoid TMDB rate limits via Radarr
    sleep 0.25
done < to_add.tsv

echo
echo "=== summary ==="
echo "added:   $added"
echo "skipped: $skipped"
echo "failed:  $failed"

# --- 6. optional search trigger ---
if [ "$SEARCH_AFTER" = "true" ]; then
    echo
    echo "=== triggering MissingMoviesSearch ==="
    curl -s -X POST "$RADARR_URL/api/v3/command" \
        -H "X-Api-Key: $RADARR_KEY" \
        -H "Content-Type: application/json" \
        -d '{"name":"MissingMoviesSearch"}'
    echo
fi
