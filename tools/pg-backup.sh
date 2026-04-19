#!/bin/bash
#
# Postgres backup via pg_dump inside the nzbdav-postgres container.
# Keeps the last N backups (default 7). Run from a cron job on the
# host that runs the Postgres container.
#
# Usage:
#   ./pg-backup.sh                    # uses defaults
#   BACKUP_DIR=/mnt/backups KEEP=14 ./pg-backup.sh
#
# Crontab example (daily at 03:00):
#   0 3 * * * /opt/nzbdav/tools/pg-backup.sh >> /var/log/nzbdav-backup.log 2>&1

set -euo pipefail

CONTAINER="${PG_CONTAINER:-nzbdav-postgres}"
DB_USER="${PG_USER:-nzbdav}"
DB_NAME="${PG_DB:-nzbdav}"
BACKUP_DIR="${BACKUP_DIR:-/opt/nzbdav/backups}"
KEEP="${KEEP:-7}"

mkdir -p "$BACKUP_DIR"

TIMESTAMP=$(date +%Y%m%d-%H%M%S)
FILENAME="nzbdav-${TIMESTAMP}.sql.gz"
FILEPATH="${BACKUP_DIR}/${FILENAME}"

echo "[$(date)] Starting backup → ${FILEPATH}"

docker exec "$CONTAINER" pg_dump -U "$DB_USER" "$DB_NAME" | gzip > "$FILEPATH"

SIZE=$(du -h "$FILEPATH" | cut -f1)
echo "[$(date)] Backup complete: ${SIZE}"

# Prune old backups
BACKUPS=$(ls -1t "${BACKUP_DIR}"/nzbdav-*.sql.gz 2>/dev/null | tail -n +$((KEEP + 1)))
if [ -n "$BACKUPS" ]; then
    echo "$BACKUPS" | xargs rm -f
    echo "[$(date)] Pruned $(echo "$BACKUPS" | wc -l) old backup(s)"
fi
