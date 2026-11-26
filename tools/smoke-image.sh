#!/bin/bash
# Starts a built app image against an empty Postgres 18 under the restrictions it has on wizard (read-only root,
# user 1000, no capabilities, 256 MB) and checks that it migrates, seeds and serves. CI runs it after building the
# image; it also works on the Mac with Docker Desktop:
#   docker build --platform linux/amd64 -f docker/Dockerfile --build-arg APP_VERSION=abc1234 -t plate:smoke .
#   tools/smoke-image.sh plate:smoke abc1234
# The image publishes for linux-x64, so on an Apple Silicon Mac it has to be built for amd64 (slow, emulated).
set -euo pipefail
cd "$(dirname "$0")/.."

[ $# -eq 2 ] || { echo "Usage: $0 <image> <expected version>" >&2; exit 2; }
image=$1
version=$2
name="plate-smoke-$$"
password="smoke-$$-$RANDOM"

# The app sits behind cloudflared on wizard: requests arrive from a known proxy address with X-Forwarded-Proto: https,
# and Production refuses its secure-only cookies on plain HTTP. A curl container at a fixed address plays cloudflared.
subnet_prefix=10.213.77
proxy_ip=$subnet_prefix.10
curl_image=curlimages/curl:8.11.1

cleanup() {
    docker rm -f "$name-app" "$name-db" "$name-proxy" > /dev/null 2>&1 || true
    docker network rm "$name" > /dev/null 2>&1 || true
}
fail() {
    echo "$1" >&2
    echo "--- app log (last 80 lines)" >&2
    docker logs --tail 80 "$name-app" >&2 2>&1 || true
    exit 1
}
trap cleanup EXIT

# Prints the HTTP status of a GET through the proxy (000 when nothing answered) and the body's size, and writes the
# body to /tmp/body in the proxy container.
fetch() {
    docker exec "$name-proxy" curl -sS --compressed --max-time 10 -o /tmp/body -w '%{http_code} %{size_download}' \
        -H 'Host: plate.peychev.uk' -H 'X-Forwarded-Proto: https' -H 'CF-Connecting-IP: 203.0.113.7' \
        "http://app:8080$1" 2> /dev/null || echo "000 0"
}

docker network create --subnet "$subnet_prefix.0/24" "$name" > /dev/null
# Same settings as docker-compose.yml.
docker run -d --name "$name-db" --network "$name" --network-alias db \
    -e POSTGRES_DB=plate -e POSTGRES_USER=plate -e POSTGRES_PASSWORD="$password" \
    --memory 256m postgres:18 postgres -c shared_buffers=32MB -c max_connections=15 -c jit=off > /dev/null
docker run -d --name "$name-proxy" --network "$name" --ip "$proxy_ip" --entrypoint sleep "$curl_image" infinity > /dev/null
for _ in $(seq 60); do
    docker exec "$name-db" pg_isready -U plate -d plate > /dev/null 2>&1 && break
    sleep 1
done
docker exec "$name-db" pg_isready -U plate -d plate > /dev/null || fail "Postgres didn't start."

# /keys is a bind mount on wizard; a tmpfs owned by the same user stands in for it.
docker run -d --name "$name-app" --network "$name" --network-alias app \
    --user 1000:1000 --read-only --tmpfs /tmp:size=16m --tmpfs /keys:size=1m,uid=1000,gid=1000 \
    --cap-drop ALL --security-opt no-new-privileges:true --pids-limit 200 --memory 256m --cpus 1.0 \
    -e ConnectionStrings__Default="Host=db;Database=plate;Username=plate;Password=$password;Maximum Pool Size=8" \
    -e DataProtection__KeysPath=/keys \
    -e ReverseProxy__KnownProxies="$proxy_ip" \
    -e AllowedHosts="plate.peychev.uk;localhost" \
    -e Passkeys__ServerDomain=plate.peychev.uk \
    "$image" > /dev/null

# Startup applies every migration and loads the seeds into the empty database before the first request is served.
# Once anything answers, it has to be a 200: a 500 won't turn into one by waiting.
# Bounded by the clock rather than a count of tries, so an app that accepts connections and never answers still fails
# with its log well inside the job's timeout.
status=000
deadline=$((SECONDS + 240))
while [ "$SECONDS" -lt "$deadline" ]; do
    [ "$(docker inspect -f '{{.State.Running}}' "$name-app")" = true ] || fail "The app container exited during startup."
    read -r status _ <<< "$(fetch /account/login)"
    [ "$status" = 000 ] || break
    sleep 2
done
[ "$status" != 000 ] || fail "The app didn't answer /account/login within 4 minutes."
[ "$status" = 200 ] || fail "/account/login answered $status."
docker exec "$name-proxy" grep -q "data-version=\"$version\"" /tmp/body \
    || fail "/account/login doesn't carry data-version=\"$version\"."
echo "Serving $version at /account/login."

# Every file the service worker precaches has to be in the image, or the worker fails to install on phones. A file
# that's in the build's static asset manifest but missing on disk comes back as an empty 200, hence the size.
offline=$(sed -n 's/^const OFFLINE = "\(.*\)";/\1/p' src/Plate.Web/wwwroot/sw.js)
paths=()
while read -r path; do
    paths+=("$path")
done < <(sed -n '/^const PRECACHE = \[/,/^\];/p' src/Plate.Web/wwwroot/sw.js | grep -o '"/[^"]*"' | tr -d '"')
if [ -z "$offline" ] || [ "${#paths[@]}" -le 10 ]; then fail "Couldn't read OFFLINE and PRECACHE from sw.js."; fi
missing=0
for path in /sw.js "$offline" "${paths[@]}"; do
    read -r status size <<< "$(fetch "$path")"
    if [ "$status" != 200 ] || [ "$size" = 0 ]; then
        echo "Not served: $path ($status, $size bytes)" >&2
        missing=$((missing + 1))
    fi
done
[ "$missing" -eq 0 ] || fail "$missing precached files aren't served by the image."
echo "Serves sw.js and all $((${#paths[@]} + 1)) precached files."

# Errors logged during startup (a failed seed, a fixup that threw) don't stop the app, so look for them. One is
# expected on an empty database and never on wizard's: EF reads __EFMigrationsHistory before it has created it.
errors=$(docker logs "$name-app" 2>&1 | awk '
    function flush() { if (block != "" && block !~ /FROM "__EFMigrationsHistory"/) printf "%s", block; block = "" }
    /^[a-z]+: / { flush(); if (/^(fail|crit): /) block = $0 "\n"; next }
    block != "" { block = block $0 "\n" }
    END { flush() }')
if [ -n "$errors" ]; then
    printf '%s' "$errors" >&2
    fail "The app logged errors during startup."
fi
echo "Smoke test passed."
