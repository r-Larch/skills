#!/usr/bin/env bash
# Nightly pg_dump of the WorkState database (custom format, restorable with pg_restore). Keeps the
# newest $KEEP dumps in $DIR. Run by workstate-backup.timer; run it by hand before any upgrade.
set -euo pipefail

DIR=${WORKSTATE_BACKUP_DIR:-/var/backups/workstate}
KEEP=${WORKSTATE_BACKUP_KEEP:-14}

mkdir -p "$DIR"
chmod 700 "$DIR"
file="$DIR/workstate-$(date -u +%Y%m%dT%H%M%SZ).dump"

docker exec workstate-db-1 pg_dump -U workstate -d workstate -Fc > "$file.part"
mv "$file.part" "$file"
echo "backup $file $(stat -c %s "$file") bytes"

ls -1t "$DIR"/workstate-*.dump | tail -n +$((KEEP + 1)) | xargs -r rm --
