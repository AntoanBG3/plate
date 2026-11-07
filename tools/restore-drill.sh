#!/bin/bash
# Restore drill, run on wizard. Restores a GymTracker dump into a throwaway postgres:18 container with no
# network and compares table names, column definitions and row counts with the live database. Touches nothing live.
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

# Pass SQL as an argument rather than interpolating it into the container shell command.
live_query() {
    docker exec gymtracker-db sh -c 'psql -X -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB" -tAc "$1"' sh "$1"
}
restored_query() {
    docker exec gymtracker-restore-drill psql -X -v ON_ERROR_STOP=1 -U gym -d gym -tAc "$1"
}

TABLE_SQL="select string_agg(quote_ident(table_name), E'\\n' order by table_name) from information_schema.tables where table_schema = 'public' and table_type = 'BASE TABLE'"
live_tables=$(live_query "$TABLE_SQL")
restored_tables=$(restored_query "$TABLE_SQL")
if [ -z "$live_tables" ] || [ "$live_tables" != "$restored_tables" ]; then
    echo "MISMATCH between live and restored table names (or no live tables)." >&2
    exit 1
fi

# Column names/order/types/defaults/nullability and identity settings must match even for an older nightly dump.
COLUMN_SQL="select string_agg(row_to_json(c)::text, E'\\n' order by table_name, ordinal_position) from (select table_name, column_name, ordinal_position, data_type, udt_schema, udt_name, is_nullable, column_default, character_maximum_length, numeric_precision, numeric_scale, datetime_precision, is_identity, identity_generation from information_schema.columns where table_schema = 'public' and table_name in (select table_name from information_schema.tables where table_schema = 'public' and table_type = 'BASE TABLE')) c"
live_columns=$(live_query "$COLUMN_SQL")
restored_columns=$(restored_query "$COLUMN_SQL")
if [ -z "$live_columns" ] || [ "$live_columns" != "$restored_columns" ]; then
    echo "MISMATCH between live and restored column definitions (or no live columns)." >&2
    exit 1
fi

COUNT_SQL="select string_agg(t || '=' || n, ' ' order by t) from (select table_name t, (xpath('/row/c/text()', query_to_xml(format('select count(*) c from %I.%I', table_schema, table_name), false, true, '')))[1]::text n from information_schema.tables where table_schema = 'public' and table_type = 'BASE TABLE') x"
live=$(live_query "$COUNT_SQL")
restored=$(restored_query "$COUNT_SQL")
echo "live:     $live"
echo "restored: $restored"
if [ "$live" = "$restored" ]; then
    echo "Restore OK: table names, column definitions and row counts match."
elif [ "${1:-}" = nightly ]; then
    echo "Restore OK: table names and column definitions match. Counts differ from live; this may reflect changes since the nightly dump, not verified backup-time counts."
else
    echo "MISMATCH between the live database and the restored dump." >&2
    exit 1
fi
