#!/bin/bash
# Deploys the Mac's main to wizard, from home or away (away, ssh goes through the Cloudflare Tunnel; README
# "Deploying from anywhere"). Dumps the live database first, copies the checkout, builds with the commit as
# APP_VERSION and waits until plate.peychev.uk serves that commit.
#   tools/deploy.sh            deploy HEAD of main, which must be clean
#   tools/deploy.sh --dry-run  show what the copy would change on wizard and stop
#   tools/deploy.sh --skip-ci  deploy without asking GitHub how CI went (GitHub down, an urgent fix)
set -euo pipefail
cd "$(dirname "$0")/.."

dry_run=false
skip_ci=false
for arg in "$@"; do
    case "$arg" in
        --dry-run) dry_run=true ;;
        --skip-ci) skip_ci=true ;;
        *) echo "Usage: $0 [--dry-run] [--skip-ci]" >&2; exit 2 ;;
    esac
done

[ "$(git branch --show-current)" = main ] || { echo "Not on main." >&2; exit 1; }
[ -z "$(git status --porcelain)" ] || { echo "Uncommitted changes; commit or stash them first." >&2; exit 1; }
VERSION=$(git rev-parse --short HEAD)
SHA=$(git rev-parse HEAD)

# The P filters keep wizard's own state out of --delete; without them it removed keys/ on 2026-10-02. The repository's
# info/exclude (shared by every worktree) holds this Mac's own untracked files, which stay off wizard too.
EXCLUDE="$(git rev-parse --git-common-dir)/info/exclude"
copy() {
    rsync -a --delete "$@" --filter='P /db/' --filter='P /keys/' --filter='P /data/' --filter='P /docker/.env' \
        --filter=':- .gitignore' ${EXCLUDE:+--exclude-from="$EXCLUDE"} --exclude .git ./ wizard:docker/plate/
}

if [ "$dry_run" = true ]; then
    copy --dry-run --itemize-changes
    exit 0
fi

# GitHub's CI runs the same checks on Linux, where fonts and timing differ from the Mac: on 2026-11-23 it caught
# clipped buttons the local checks passed, after the commit was already deployed. A failed run on main blocks the
# deploy; a run still going is waited for once the local checks are done. A commit not pushed yet has no run, so the
# latest finished run on main has to have passed instead.
ci_run() {  # prints "<id> <status> <url> <conclusion>" for the newest CI run of the given commit on main, or nothing
    gh run list --workflow ci.yml --branch main --commit "$1" --limit 1 \
        --json databaseId,status,conclusion,url --jq '.[0] // empty | "\(.databaseId) \(.status) \(.url) \(.conclusion)"'
}
ci_wait=""
if [ "$skip_ci" = true ]; then
    echo "Skipping the CI check (--skip-ci)."
else
    command -v gh > /dev/null || { echo "gh isn't installed, so CI can't be checked; deploy with --skip-ci to go without." >&2; exit 1; }
    run=$(ci_run "$SHA") \
        || { echo "Couldn't read CI runs from GitHub (gh auth status?); deploy with --skip-ci to go without." >&2; exit 1; }
    if [ -n "$run" ]; then
        read -r run_id run_status run_url run_conclusion <<< "$run"
        if [ "$run_status" != completed ]; then
            ci_wait=$run_id
            echo "CI is still running for $VERSION; waiting for it after the local checks: $run_url"
        elif [ "$run_conclusion" != success ]; then
            echo "CI ended in $run_conclusion for $VERSION: $run_url" >&2
            echo "Fix it on main, or deploy with --skip-ci if the failure is unrelated." >&2
            exit 1
        fi
    else
        last=$(gh run list --workflow ci.yml --branch main --status completed --limit 1 \
            --json conclusion,headSha,url --jq '.[0] // empty | "\(.conclusion) \(.headSha) \(.url)"') \
            || { echo "Couldn't read CI runs from GitHub (gh auth status?); deploy with --skip-ci to go without." >&2; exit 1; }
        read -r last_conclusion last_sha last_url <<< "${last:-none - -}"
        if [ "$last_conclusion" != success ] && [ "$last_conclusion" != none ]; then
            echo "$VERSION hasn't been pushed, and CI on main last ended in $last_conclusion (${last_sha:0:7}): $last_url" >&2
            echo "Push main and let CI run, or deploy with --skip-ci." >&2
            exit 1
        fi
        echo "$VERSION hasn't been pushed, so CI hasn't run on it; the local checks are the only gate."
    fi
fi

# No remote side effects until the exact checkout has passed the complete test suite.
bash tools/check.sh
if [ "$(git branch --show-current)" != main ] || [ -n "$(git status --porcelain)" ] \
    || [ "$(git rev-parse --short HEAD)" != "$VERSION" ]; then
    echo "The checkout changed during checks; run the deployment again." >&2
    exit 1
fi
if [ -n "$ci_wait" ]; then
    echo "Waiting for CI on $VERSION"
    gh run watch "$ci_wait" --exit-status --interval 30 > /dev/null \
        || { echo "CI failed for $VERSION, or it couldn't be watched: $run_url" >&2; exit 1; }
    echo "CI passed for $VERSION."
fi

DUMP="plate-pre-$VERSION-$(date +%Y%m%d-%H%M).dump"
echo "Dumping the database to ~/$DUMP on wizard"
# $DUMP and $VERSION are meant to expand here, on the Mac; the escaped variables expand on wizard.
# shellcheck disable=SC2029
ssh wizard "docker exec plate-db sh -c 'pg_dump -Fc -U \"\$POSTGRES_USER\" \"\$POSTGRES_DB\"' > ~/$DUMP \
    && docker exec -i plate-db pg_restore --list < ~/$DUMP > /dev/null"

echo "Copying $VERSION"
copy

echo "Building and starting $VERSION"
# shellcheck disable=SC2029
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
