#!/bin/bash
# Restore drill, run on wizard. Restores a Plate dump into a throwaway postgres:18 container on an internal-only
# network and compares table names, column definitions and row counts with the live database. Touches nothing live.
#   tools/restore-drill.sh                 dump the live database now, the way the nightly backup does
#   tools/restore-drill.sh nightly         use the latest nightly dump from /mnt/storage/backups/latest
#   tools/restore-drill.sh nightly --app   also start the app on the restored copy with the backed-up keys and sign in
set -euo pipefail
MODE=
APP=no
for arg in "$@"; do
    case "$arg" in
        nightly) MODE=nightly ;;
        --app) APP=yes ;;
        *) echo "Unknown argument: $arg" >&2; exit 2 ;;
    esac
done

DB=plate-restore-drill
APPC=plate-drill-app
NET=plate-drill-net
CURL_IMAGE=curlimages/curl:8.14.1
# The drill network's addresses; the curl container stands in for Cloudflare's proxy, so the app trusts its
# X-Forwarded-Proto and serves it as HTTPS (production cookies are Secure-only).
# Containers get addresses from the lower half; the proxy's fixed one is above it.
SUBNET=172.31.250.0/28
DYNAMIC=172.31.250.0/29
PROXY_IP=172.31.250.10
DUMP=$(mktemp /tmp/plate-drill.XXXXXX.dump)
KEYS=$(mktemp -d /tmp/plate-drill-keys.XXXXXX)
cleanup() {
    docker rm -f -v "$APPC" "$DB" >/dev/null 2>&1 || true
    docker network rm "$NET" >/dev/null 2>&1 || true
    rm -rf "$DUMP" "$KEYS"
}
trap cleanup EXIT

# /mnt/storage is root-only; read it through a container. It is mounted at its own path because `latest` is an
# absolute symlink (/mnt/storage/backups/<date>), which doesn't resolve under any other mount point.
storage() {
    docker run --rm -v /mnt/storage:/mnt/storage:ro "$@"
}

if [ "$MODE" = nightly ]; then
    storage alpine cat /mnt/storage/backups/latest/plate-db.dump > "$DUMP"
    echo "nightly dump from $(storage alpine readlink /mnt/storage/backups/latest)"
else
    docker exec plate-db sh -c 'pg_dump -Fc -U "$POSTGRES_USER" "$POSTGRES_DB"' > "$DUMP"
fi
echo "dump size: $(du -h "$DUMP" | cut -f1)"

# Internal: the drill containers reach each other and nothing else.
docker network create --internal --subnet "$SUBNET" --ip-range "$DYNAMIC" "$NET" >/dev/null
docker run -d --name "$DB" --network "$NET" \
    -e POSTGRES_USER=gym -e POSTGRES_DB=gym -e POSTGRES_PASSWORD=drill postgres:18 >/dev/null
for _ in $(seq 30); do docker exec "$DB" pg_isready -U gym -d gym -q && break; sleep 1; done
sleep 2 # the image restarts the server once after initdb
docker exec -i "$DB" pg_restore -U gym -d gym --no-owner --exit-on-error < "$DUMP"

# Pass SQL as an argument rather than interpolating it into the container shell command.
live_query() {
    docker exec plate-db sh -c 'psql -X -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB" -tAc "$1"' sh "$1"
}
restored_query() {
    docker exec "$DB" psql -X -v ON_ERROR_STOP=1 -U gym -d gym -tAc "$1"
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
elif [ "$MODE" = nightly ]; then
    echo "Restore OK: table names and column definitions match. Counts differ from live; this may reflect changes since the nightly dump, not verified backup-time counts."
else
    echo "MISMATCH between the live database and the restored dump." >&2
    exit 1
fi

[ "$APP" = yes ] || exit 0
if [ "$MODE" != nightly ]; then
    echo "--app checks the backed-up keys, so it needs the nightly mode." >&2
    exit 1
fi

# ---- App stage: the backed-up keys and the restored database bring the app back, and someone can sign in. ----
# The schema was compared above, before the app's startup migrations change the copy.

# The keys come from the same snapshot as the dump; the live keys/ would prove nothing about the backup.
storage -v "$KEYS:/out" alpine sh -c 'cp -a /mnt/storage/backups/latest/docker/plate/keys/. /out/ && chown -R 1000:1000 /out'
keys_before=$(cd "$KEYS" && ls)
[ -n "$keys_before" ] || { echo "MISMATCH: the snapshot holds no Data Protection keys." >&2; exit 1; }
if [ "$(cd "$KEYS" && sha256sum -- *)" = "$(cd ~/docker/plate/keys && sha256sum -- *)" ]; then
    echo "keys: the snapshot's key files are identical to the live ones"
else
    echo "keys: the snapshot's key files differ from the live ones (a key may have been added since the snapshot)"
fi

# The image already on wizard (no build on the SSD), with nothing from .env: no Telegram, no OpenRouter.
docker run -d --name "$APPC" --network "$NET" --user 1000:1000 --read-only --tmpfs /tmp:size=16m \
    --cap-drop ALL --security-opt no-new-privileges:true --memory 256m \
    -e "ConnectionStrings__Default=Host=$DB;Database=gym;Username=gym;Password=drill;Maximum Pool Size=8" \
    -e DataProtection__KeysPath=/keys -e "AllowedHosts=$APPC" -e "ReverseProxy__KnownProxies=$PROXY_IP" \
    -v "$KEYS:/keys" plate-app:latest >/dev/null

# A curl container on the drill network, the only way to reach the app.
drill_curl() {
    docker run --rm --network "$NET" --ip "$PROXY_IP" "$CURL_IMAGE" -sS --max-time 10 -H "X-Forwarded-Proto: https" "$@"
}
BASE="http://$APPC:8080"
up=no
for _ in $(seq 60); do
    answer=$(drill_curl -o /dev/null -w '%{http_code}' "$BASE/account/login" 2>&1 || true)
    if [ "$answer" = 200 ]; then up=yes; break; fi
    sleep 2
done
if [ "$up" != yes ]; then
    echo "MISMATCH: the app didn't start on the restored copy. Last answer: $answer. Its errors:" >&2
    docker logs "$APPC" 2>&1 | grep -E -A3 '^(fail|crit):' | grep -vE '^\s+at ' | tail -20 >&2 || true
    exit 1
fi
version=$(drill_curl "$BASE/account/login" | sed -n 's/.*data-version="\([^"]*\)".*/\1/p' | head -1)
echo "app: version ${version:-unknown} started on the restored copy"

# A throwaway account, made inside the drill only.
EMAIL=drill@example.invalid
PASSWORD="Drill-$(head -c 12 /dev/urandom | od -An -tx1 | tr -d ' \n')"
echo "$PASSWORD" | docker exec -i "$APPC" dotnet Plate.Web.dll create-user --email "$EMAIL" --name Drill >/dev/null

# Signs in with an antiforgery cookie and token, then opens Today with the auth cookie. Cookies are passed by hand:
# the app sets __Host- (Secure) cookies, which curl would not keep for a plain-http address.
# $1 is the antiforgery cookie ("name=value"), $2 the form token.
sign_in() {
    local headers auth location status
    headers=$(drill_curl -o /dev/null -D - -H "Cookie: $1" \
        --data-urlencode "email=$EMAIL" --data-urlencode "password=$PASSWORD" \
        --data-urlencode "__RequestVerificationToken=$2" --data-urlencode "returnUrl=/" "$BASE/account/login")
    location=$(printf '%s' "$headers" | tr -d '\r' | sed -n 's/^[Ll]ocation: //p' | head -1)
    auth=$(printf '%s' "$headers" | tr -d '\r' | sed -n 's/^[Ss]et-[Cc]ookie: \([^;]*gym\.auth=[^;]*\).*/\1/p' | head -1)
    if [ -z "$auth" ] || printf '%s' "$location" | grep -q '/account/login'; then
        return 1
    fi
    # Signed in: Today answers, or sends a new account to its setup, but never back to the sign-in page.
    status=$(drill_curl -o /dev/null -D - -H "Cookie: $auth" "$BASE/" | tr -d '\r')
    if printf '%s' "$status" | grep -qi '^location: .*/account/login'; then
        return 1
    fi
    printf '%s' "$status" | head -1 | grep -qE ' (200|302)'
}

# Antiforgery cookie and token from a login page's response headers and body.
af_cookie() { tr -d '\r' | sed -n 's/^[Ss]et-[Cc]ookie: \([^;]*gym\.af=[^;]*\).*/\1/p' | head -1; }
af_token() { sed -n 's/.*name="__RequestVerificationToken" type="hidden" value="\([^"]*\)".*/\1/p; s/.*name="__RequestVerificationToken" value="\([^"]*\)".*/\1/p' | head -1; }

page=$(drill_curl -D - "$BASE/account/login")
if ! sign_in "$(printf '%s' "$page" | af_cookie)" "$(printf '%s' "$page" | af_token)"; then
    echo "MISMATCH: signing in to the restored app failed." >&2
    exit 1
fi
echo "sign-in: a new account signs in on the restored app"

# The real test of the keys: an antiforgery cookie and token the live app made are accepted by the restored one,
# so the backed-up key ring decrypts what the live key ring encrypted (sign-in cookies use the same keys).
live_page=$(curl -sS --max-time 10 -D - https://plate.peychev.uk/account/login)
if ! sign_in "$(printf '%s' "$live_page" | af_cookie)" "$(printf '%s' "$live_page" | af_token)"; then
    echo "MISMATCH: the restored app can't read what the live app encrypted; the backed-up keys don't match." >&2
    exit 1
fi
echo "keys: the restored app accepts antiforgery data made by the live app"

keys_after=$(cd "$KEYS" && ls)
if [ "$keys_before" != "$keys_after" ]; then
    echo "MISMATCH: the app wrote a new key, so it couldn't use the backed-up ones." >&2
    exit 1
fi
users=$(docker exec "$DB" psql -X -U gym -d gym -tAc 'select count(*) from "AspNetUsers"')
echo "App OK: the nightly dump and keys bring the app back ($users accounts, one of them the drill's)."
