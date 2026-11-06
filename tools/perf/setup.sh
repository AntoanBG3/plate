#!/bin/bash
# Usage: bash tools/perf/setup.sh 730 (isolated LOCAL stack; requires Docker, .NET10, Python3, openssl)
# Refuses existing audit containers. No production connection or credential is used.
set -euo pipefail
cd "$(dirname "$0")/../.."
days="${1:-730}"
for name in repa-perf-db repa-perf-app; do
    if docker container inspect "$name" >/dev/null 2>&1; then
        echo "Audit container $name already exists. Reuse it, or explicitly remove it first." >&2
        exit 1
    fi
done
image=mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra@sha256:00e0ad6a7ef8c0c1391b87f05c7ac757a15740455688f2bfcd146a3f4b987efd
dotnet publish src/GymTracker.Web -c Release -r linux-arm64 --self-contained false -p:UseAppHost=false -o /private/tmp/repa-perf-publish
docker pull "$image"
docker run -d --name repa-perf-db --memory 256m -p 127.0.0.1:55432:5432 \
    -e POSTGRES_USER=gym -e POSTGRES_DB=gym_perf -e POSTGRES_PASSWORD=perf-local-only \
    -v /private/tmp/repa-perf-db:/var/lib/postgresql postgres:18 \
    -c shared_buffers=32MB -c max_connections=15 -c jit=off -c shared_preload_libraries=pg_stat_statements
for ((attempt=0;attempt<60;attempt++)); do
    docker exec repa-perf-db pg_isready -U gym -d gym_perf >/dev/null 2>&1 && break
    sleep 1
done
docker exec repa-perf-db pg_isready -U gym -d gym_perf
mkdir -p /private/tmp/repa-perf-tls /private/tmp/repa-perf-keys /private/tmp/repa-perf-dotnet
# Disposable synthetic keys only. Never use these permissions or credentials for production.
chmod 777 /private/tmp/repa-perf-keys
openssl req -x509 -newkey rsa:2048 -nodes -keyout /private/tmp/repa-perf-tls/key.pem \
    -out /private/tmp/repa-perf-tls/cert.pem -days 2 -subj /CN=localhost \
    -addext 'subjectAltName=DNS:localhost,IP:127.0.0.1'
openssl pkcs12 -export -out /private/tmp/repa-perf-tls/cert.pfx \
    -inkey /private/tmp/repa-perf-tls/key.pem -in /private/tmp/repa-perf-tls/cert.pem -passout pass:perf-local-only
docker run --rm -i --entrypoint dotnet --memory 256m --cpus 1 --network container:repa-perf-db \
    -v /private/tmp/repa-perf-publish:/app:ro -v /private/tmp/repa-perf-keys:/keys \
    -e ASPNETCORE_ENVIRONMENT=Production -e DataProtection__KeysPath=/keys \
    -e 'ConnectionStrings__Default=Host=127.0.0.1;Database=gym_perf;Username=gym;Password=perf-local-only;Maximum Pool Size=8' \
    -w /app "$image" GymTracker.Web.dll create-user --email demo@example.test --name Synthetic <<'EOF'
demo-password-123
EOF
python3 tools/perf/generate.py --days "$days" > /private/tmp/repa-perf-seed.sql
docker exec -i repa-perf-db psql -U gym -d gym_perf -v ON_ERROR_STOP=1 < /private/tmp/repa-perf-seed.sql
bash tools/perf/serve.sh
