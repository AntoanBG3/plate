#!/bin/bash
# Restore drill, run on wizard. Restores a GymTracker dump into a throwaway postgres:18 container with no
# network and compares row counts per table with the live database. Touches nothing live.
#   tools/restore-drill.sh           dump the live database now, the way the nightly backup does
#   tools/restore-drill.sh nightly   use the latest nightly dump from /mnt/storage/backups/latest
set -euo pipefail
DUMP=$(mktemp /tmp/gymtracker-drill.XXXXXX.dump)
trap 'docker rm -f gymtracker-restore-drill >/dev/null 2>&1; rm -f "$DUMP"' EXIT

if [ "${1:-}" = nightly ]; then
    # /mnt/storage is root-only; read it through a container like ~/bin/export-latest-backup does.
    docker run --rm -v /mnt/storage:/s:ro alpine cat /s/backups/latest/gymtracker-db.dump > "$DUMP"
    echo "nightly dump from $(docker run --rm -v /mnt/storage:/s:ro alpine readlink /s/backups/latest)"
else
    docker exec gymtracker-db sh -c 'pg_dump -Fc -U "$POSTGRES_USER" "$POSTGRES_DB"' > "$DUMP"
fi
echo "dump size: $(du -h "$DUMP" | cut -f1)"

docker run -d --name gymtracker-restore-drill --network none \
    -e POSTGRES_USER=gym -e POSTGRES_DB=gym -e POSTGRES_PASSWORD=drill postgres:18 >/dev/null
for _ in $(seq 30); do docker exec gymtracker-restore-drill pg_isready -U gym -d gym -q && break; sleep 1; done
sleep 2 # the image restarts the server once after initdb
docker exec -i gymtracker-restore-drill pg_restore -U gym -d gym --no-owner --exit-on-error < "$DUMP"

COUNT_SQL="select string_agg(t || '=' || n, ' ' order by t) from (select table_name t, (xpath('/row/c/text()', query_to_xml(format('select count(*) c from %I', table_name), false, true, '')))[1]::text n from information_schema.tables where table_schema = 'public' and table_type = 'BASE TABLE') x"
live=$(docker exec gymtracker-db sh -c "psql -U \"\$POSTGRES_USER\" -d \"\$POSTGRES_DB\" -tAc \"$COUNT_SQL\"")
restored=$(docker exec gymtracker-restore-drill psql -U gym -d gym -tAc "$COUNT_SQL")
echo "live:     $live"
echo "restored: $restored"
if [ "$live" = "$restored" ]; then
    echo "Restore OK: every table matches."
elif [ "${1:-}" = nightly ]; then
    echo "Restore OK. Counts differ from live, which is expected for anything logged since the nightly dump."
else
    echo "MISMATCH between the live database and the restored dump." >&2
    exit 1
fi
