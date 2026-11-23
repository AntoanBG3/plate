#!/bin/bash
# Deploys the Mac's main to wizard, from home or away (away, ssh goes through the Cloudflare Tunnel; README
# "Deploying from anywhere"). Dumps the live database first, copies the checkout, builds with the commit as
# APP_VERSION and waits until plate.peychev.uk serves that commit.
#   tools/deploy.sh            deploy HEAD of main, which must be clean
#   tools/deploy.sh --dry-run  show what the copy would change on wizard and stop
set -euo pipefail
cd "$(dirname "$0")/.."

[ "$(git branch --show-current)" = main ] || { echo "Not on main." >&2; exit 1; }
[ -z "$(git status --porcelain)" ] || { echo "Uncommitted changes; commit or stash them first." >&2; exit 1; }
VERSION=$(git rev-parse --short HEAD)

# The P filters keep wizard's own state out of --delete; without them it removed keys/ on 2026-10-02.
copy() {
    rsync -a --delete "$@" --filter='P /db/' --filter='P /keys/' --filter='P /docker/.env' \
        --filter=':- .gitignore' --exclude .git ./ wizard:docker/plate/
}

if [ "${1:-}" = --dry-run ]; then
    copy --dry-run --itemize-changes
    exit 0
fi

# No remote side effects until the exact checkout has passed the complete test suite.
bash tools/check.sh
[ "$(git branch --show-current)" = main ] && [ -z "$(git status --porcelain)" ] \
    && [ "$(git rev-parse --short HEAD)" = "$VERSION" ] \
    || { echo "The checkout changed during checks; run the deployment again." >&2; exit 1; }

DUMP="plate-pre-$VERSION-$(date +%Y%m%d-%H%M).dump"
echo "Dumping the database to ~/$DUMP on wizard"
ssh wizard "docker exec plate-db sh -c 'pg_dump -Fc -U \"\$POSTGRES_USER\" \"\$POSTGRES_DB\"' > ~/$DUMP \
    && docker exec -i plate-db pg_restore --list < ~/$DUMP > /dev/null"

echo "Copying $VERSION"
copy

echo "Building and starting $VERSION"
ssh wizard "cd ~/docker/plate && APP_VERSION=$VERSION docker compose -f docker/docker-compose.yml up -d --build \
    && docker builder prune -f > /dev/null"

# Migrations run at startup, so the old page can answer for a while after compose returns.
for _ in $(seq 60); do
    if curl -fsS --max-time 10 https://plate.peychev.uk/account/login 2>/dev/null | grep -q "data-version=\"$VERSION\""; then
        echo "plate.peychev.uk is serving $VERSION. Pre-deploy dump: ~/$DUMP on wizard."
        # Older pre-deploy dumps move to ~/predeploy-dumps-archive (P13); the deploy has worked, so a failure here
        # is only reported.
        ssh wizard "bash ~/docker/plate/tools/prune-predeploy-dumps.sh --apply" \
            || echo "Moving old pre-deploy dumps failed; run tools/prune-predeploy-dumps.sh on wizard to see why." >&2
        exit 0
    fi
    sleep 5
done
echo "plate.peychev.uk isn't serving $VERSION after 5 minutes; check: ssh wizard docker logs --tail 50 plate-app" >&2
exit 1
