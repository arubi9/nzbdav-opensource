#!/usr/bin/env bash
# IMDB top 1000 TV series (Bayesian-weighted) → Sonarr bulk-add.
# Sister script to radarr-add-imdb-top1000.sh — same IMDB dataset,
# different titleType filter + different target API.
#
# Usage: bash sonarr-add-imdb-top1000-tv.sh
# Env vars (optional):
#   SONARR_URL   (default http://localhost:8989)
#   SONARR_KEY   (default from /config/config.xml via docker exec sonarr)
#   PROFILE_ID   (default 6 = "HD - 720p/1080p")
#   ROOT_PATH    (default /tv)
#   TOP_N        (default 1000)
#   MIN_VOTES    (default 10000 — lower than movies: fewer shows have huge vote counts)
#   MONITOR      (default "all" — monitor every episode)
#   SEARCH_AFTER (default false — trigger MissingEpisodeSearch after adding)

set -euo pipefail

SONARR_URL=${SONARR_URL:-http://localhost:8989}
SONARR_CONTAINER=${SONARR_CONTAINER:-sonarr}
SONARR_KEY=${SONARR_KEY:-$(docker exec "$SONARR_CONTAINER" sed -n 's|.*<ApiKey>\([^<]*\)</ApiKey>.*|\1|p' /config/config.xml 2>/dev/null)}
PROFILE_ID=${PROFILE_ID:-}
ROOT_PATH=${ROOT_PATH:-}
TOP_N=${TOP_N:-1000}
MIN_VOTES=${MIN_VOTES:-10000}
MONITOR=${MONITOR:-all}
SEARCH_AFTER=${SEARCH_AFTER:-false}

command -v jq      >/dev/null 2>&1 || { echo "jq required"; exit 1; }
command -v python3 >/dev/null 2>&1 || { echo "python3 required"; exit 1; }

[ -n "$SONARR_KEY" ] || {
  echo "could not read the Sonarr api key."
  echo "set SONARR_KEY=..., or SONARR_CONTAINER=<name> (this one is not '$SONARR_CONTAINER')."
  exit 1
}

api() { curl -fsS -H "X-Api-Key: $SONARR_KEY" "$SONARR_URL/api/v3/$1"; }

# Read defaults from the instance rather than hardcoding them: a wrong profile id or
# root path is accepted per-title and then fails every add, which reads as thousands
# of network errors instead of one bad setting.
if [ -z "$PROFILE_ID" ]; then
  PROFILE_ID=$(api qualityprofile | jq -r '.[0].id')
  [ -n "$PROFILE_ID" ] && [ "$PROFILE_ID" != null ] || { echo "no quality profiles exist in Sonarr"; exit 1; }
elif ! api qualityprofile | jq -e --argjson p "$PROFILE_ID" 'any(.id == $p)' >/dev/null; then
  echo "PROFILE_ID=$PROFILE_ID does not exist. available:"
  api qualityprofile | jq -r '.[] | "  \(.id)\t\(.name)"'
  exit 1
fi

if [ -z "$ROOT_PATH" ]; then
  ROOT_PATH=$(api rootfolder | jq -r '.[0].path')
  [ -n "$ROOT_PATH" ] && [ "$ROOT_PATH" != null ] || { echo "no root folders configured in Sonarr"; exit 1; }
elif ! api rootfolder | jq -e --arg r "$ROOT_PATH" 'any(.path == $r)' >/dev/null; then
  echo "ROOT_PATH=$ROOT_PATH is not a Sonarr root folder. available:"
  api rootfolder | jq -r '.[] | "  \(.path)"'
  exit 1
fi

echo "=== config ==="
echo "sonarr: $SONARR_URL  profile=$PROFILE_ID  root=$ROOT_PATH"
echo "top_n=$TOP_N  min_votes=$MIN_VOTES  monitor=$MONITOR  search_after=$SEARCH_AFTER"
echo

WORK=$(mktemp -d); trap "rm -rf '$WORK'" EXIT
cd "$WORK"

echo "=== downloading IMDB datasets ==="
for f in title.basics.tsv.gz title.ratings.tsv.gz; do
    echo "  $f"
    curl -sS -o "$f" "https://datasets.imdbws.com/$f"
done

echo
echo "=== ranking TV series by Bayesian weighted rating ==="
python3 - "$MIN_VOTES" "$TOP_N" <<'PY' > top_imdb_tv.tsv
import gzip, csv, math, sys

min_votes = int(sys.argv[1])
top_n     = int(sys.argv[2])

ratings = {}
with gzip.open('title.ratings.tsv.gz', 'rt') as f:
    for row in csv.DictReader(f, delimiter='\t'):
        ratings[row['tconst']] = (float(row['averageRating']), int(row['numVotes']))

all_avgs = [a for (a, _) in ratings.values()]
C = sum(all_avgs) / len(all_avgs)

shows = []
with gzip.open('title.basics.tsv.gz', 'rt') as f:
    for row in csv.DictReader(f, delimiter='\t'):
        # TV-only: include tvSeries + tvMiniSeries (exclude episode/special/etc.)
        if row['titleType'] not in ('tvSeries', 'tvMiniSeries'):
            continue
        if row['isAdult'] == '1':
            continue
        tconst = row['tconst']
        if tconst not in ratings:
            continue
        avg, votes = ratings[tconst]
        if votes < min_votes:
            continue
        shows.append((tconst, row['primaryTitle'], row['startYear'], avg, votes, row['titleType']))

m = sorted(v for (_, _, _, _, v, _) in shows)[len(shows) * 3 // 4]

def wr(row):
    _, _, _, r, v, _ = row
    return (v / (v + m)) * r + (m / (v + m)) * C

shows.sort(key=wr, reverse=True)

sys.stderr.write(f"pool: {len(shows)} TV titles with votes >= {min_votes}\n")
sys.stderr.write(f"global mean C={C:.3f}, bayesian prior m={m}\n")

for tconst, title, year, avg, votes, tt in shows[:top_n]:
    print(f"{tconst}\t{title}\t{year}\t{avg}\t{votes}\t{tt}")
PY
echo "  wrote $(wc -l < top_imdb_tv.tsv) entries"
head -5 top_imdb_tv.tsv | awk -F'\t' '{printf "  %s  %-45s %s  r=%s votes=%s  [%s]\n", $1, $2, $3, $4, $5, $6}'

echo
echo "=== dedupe against Sonarr library ==="
curl -s "$SONARR_URL/api/v3/series" -H "X-Api-Key: $SONARR_KEY" | \
    jq -r '.[] | [.imdbId, .tvdbId] | @tsv' > existing.tsv
awk -F'\t' '{if ($1 != "" && $1 != "null") print $1}' existing.tsv > existing_imdb.txt
awk -F'\t' 'NR==FNR {have[$1]=1; next} !have[$1]' existing_imdb.txt top_imdb_tv.tsv > to_add.tsv
TO_ADD=$(wc -l < to_add.tsv)
echo "  library has $(wc -l < existing.tsv) series"
echo "  new to add:    $TO_ADD"
echo

[ "$TO_ADD" -eq 0 ] && { echo "nothing to add"; exit 0; }

echo "=== adding to Sonarr (profile=$PROFILE_ID root=$ROOT_PATH monitor=$MONITOR) ==="
added=0; skipped=0; failed=0
while IFS=$'\t' read -r tconst title year avg votes tt; do
    # Sonarr lookup returns a full series resource when it finds a match
    lookup=$(curl -s "$SONARR_URL/api/v3/series/lookup?term=$tconst" -H "X-Api-Key: $SONARR_KEY")
    first=$(echo "$lookup" | jq -r '.[0] // empty')

    if [ -z "$first" ]; then
        echo "  SKIP $tconst \"$title\" — no Sonarr match"
        skipped=$((skipped+1))
        continue
    fi

    tvdb_id=$(echo "$first" | jq -r '.tvdbId // empty')
    lookup_title=$(echo "$first" | jq -r '.title // empty')
    lookup_slug=$(echo "$first" | jq -r '.titleSlug // empty')
    lookup_year=$(echo "$first" | jq -r '.year // 0')

    if [ -z "$tvdb_id" ] || [ "$tvdb_id" = "0" ]; then
        echo "  SKIP $tconst \"$title\" — no TVDB match"
        skipped=$((skipped+1))
        continue
    fi

    payload=$(jq -n \
        --arg title "$lookup_title" \
        --arg slug "$lookup_slug" \
        --argjson tvdb "$tvdb_id" \
        --argjson year "$lookup_year" \
        --argjson profile "$PROFILE_ID" \
        --arg root "$ROOT_PATH" \
        --arg monitor "$MONITOR" \
        '{
          title: $title,
          titleSlug: $slug,
          tvdbId: $tvdb,
          year: $year,
          qualityProfileId: $profile,
          rootFolderPath: $root,
          monitored: true,
          seasonFolder: true,
          seriesType: "standard",
          addOptions: {
            monitor: $monitor,
            searchForMissingEpisodes: false,
            searchForCutoffUnmetEpisodes: false
          }
        }')

    resp=$(curl -s -w "\n%{http_code}" -X POST "$SONARR_URL/api/v3/series" \
        -H "X-Api-Key: $SONARR_KEY" -H "Content-Type: application/json" -d "$payload")

    code=$(echo "$resp" | tail -1)
    body=$(echo "$resp" | head -n -1)

    if [ "$code" = "201" ]; then
        added=$((added+1))
        echo "  OK    $tconst tvdb=$tvdb_id \"$lookup_title\" ($lookup_year)"
    elif [ "$code" = "400" ] && echo "$body" | grep -qi 'already been added\|SeriesExists'; then
        echo "  DUPE  $tconst \"$lookup_title\""
        skipped=$((skipped+1))
    else
        echo "  FAIL  $tconst code=$code body=$(echo "$body" | head -c 200)"
        failed=$((failed+1))
    fi
    sleep 0.3
done < to_add.tsv

echo
echo "=== summary ==="
echo "added:   $added"
echo "skipped: $skipped"
echo "failed:  $failed"

if [ "$SEARCH_AFTER" = "true" ]; then
    echo
    echo "=== triggering MissingEpisodeSearch ==="
    curl -s -X POST "$SONARR_URL/api/v3/command" \
        -H "X-Api-Key: $SONARR_KEY" -H "Content-Type: application/json" \
        -d '{"name":"MissingEpisodeSearch"}'
    echo
fi
