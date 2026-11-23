#!/bin/bash
# Usage: bash tools/perf/setup.sh 730 (isolated LOCAL stack; requires Docker, .NET10, Python3, openssl)
# Refuses existing audit containers. No production connection or credential is used.
set -euo pipefail
cd "$(dirname "$0")/../.."
days="${1:-730}"
for name in plate-perf-db plate-perf-app; do
    if docker container inspect "$name" >/dev/null 2>&1; then
        echo "Audit container $name already exists. Reuse it, or explicitly remove it first." >&2
        exit 1
    fi
done
image=mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra@sha256:00e0ad6a7ef8c0c1391b87f05c7ac757a15740455688f2bfcd146a3f4b987efd
dotnet publish src/Plate.Web -c Release -r linux-arm64 --self-contained false -p:UseAppHost=false -o /private/tmp/plate-perf-publish
docker pull "$image"
perf_db_path="$(mktemp -d /private/tmp/plate-perf-db.XXXXXX)"
docker run -d --name plate-perf-db --memory 256m -p 127.0.0.1:55432:5432 \
    -e POSTGRES_USER=gym -e POSTGRES_DB=gym_perf -e POSTGRES_PASSWORD=perf-local-only \
    -v "$perf_db_path:/var/lib/postgresql" postgres:18 \
    -c shared_buffers=32MB -c max_connections=15 -c jit=off -c shared_preload_libraries=pg_stat_statements
for ((attempt=0;attempt<60;attempt++)); do
    docker exec plate-perf-db pg_isready -U gym -d gym_perf >/dev/null 2>&1 && break
    sleep 1
done
docker exec plate-perf-db pg_isready -U gym -d gym_perf
mkdir -p /private/tmp/plate-perf-tls /private/tmp/plate-perf-keys /private/tmp/plate-perf-dotnet
# Disposable synthetic keys only. Never use these permissions or credentials for production.
chmod 777 /private/tmp/plate-perf-keys
openssl req -x509 -newkey rsa:2048 -nodes -keyout /private/tmp/plate-perf-tls/key.pem \
    -out /private/tmp/plate-perf-tls/cert.pem -days 2 -subj /CN=localhost \
    -addext 'subjectAltName=DNS:localhost,IP:127.0.0.1'
openssl pkcs12 -export -out /private/tmp/plate-perf-tls/cert.pfx \
    -inkey /private/tmp/plate-perf-tls/key.pem -in /private/tmp/plate-perf-tls/cert.pem -passout pass:perf-local-only
docker run --rm -i --entrypoint dotnet --memory 256m --cpus 1 --network container:plate-perf-db \
    -v /private/tmp/plate-perf-publish:/app:ro -v /private/tmp/plate-perf-keys:/keys \
    -e ASPNETCORE_ENVIRONMENT=Production -e DataProtection__KeysPath=/keys \
    -e 'ConnectionStrings__Default=Host=127.0.0.1;Database=gym_perf;Username=gym;Password=perf-local-only;Maximum Pool Size=8' \
    -w /app "$image" Plate.Web.dll create-user --email demo@example.test --name Synthetic <<'EOF'
demo-password-123
EOF
python3 tools/perf/generate.py --days "$days" > /private/tmp/plate-perf-seed.sql
docker exec -i plate-perf-db psql -U gym -d gym_perf -v ON_ERROR_STOP=1 < /private/tmp/plate-perf-seed.sql
# Fresh statistics, as production has: without them the planner scans every food for a search the trigram index
# answers in a fifth of the time, and autovacuum may only analyze later, in the middle of a measurement.
docker exec plate-perf-db psql -U gym -d gym_perf -v ON_ERROR_STOP=1 -c 'ANALYZE'
# The database must have no CPU cap, like production's. A capped one made five concurrent searches ten times slower
# than one in the 2026-11-05 audit (docs/perf/2026-11-23-c5-slowdown.md).
if [ "$(docker inspect plate-perf-db --format '{{.HostConfig.NanoCpus}}')" != 0 ]; then
    echo "plate-perf-db has a CPU cap; production's database has none, so timings would mislead. Recreate it without one." >&2
    exit 1
fi
bash tools/perf/serve.sh
