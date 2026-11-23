#!/bin/bash
# Pre-deploy dump retention (P13), run on wizard. tools/deploy.sh leaves a dump in ~ before every deploy and
# nothing removed them. This keeps the newest KEEP dumps and every one younger than DAYS days, and moves the rest
# into ~/predeploy-dumps-archive. It never deletes: emptying the archive is a separate decision (README).
#   tools/prune-predeploy-dumps.sh           list what would move (nothing changes)
#   tools/prune-predeploy-dumps.sh --apply   move them
# Only regular files directly in ~ named plate-pre-*.dump or gymtracker-pre-*.dump (the app's earlier name) count.
# Nothing moves unless the newest dump kept reads as a Postgres archive (pg_restore --list), so a broken latest
# dump never leaves the older good ones as the only copies outside the archive.
set -euo pipefail
KEEP=${KEEP:-5}
DAYS=${DAYS:-30}
HOME_DIR=${PRUNE_HOME:-$HOME}
ARCHIVE="$HOME_DIR/predeploy-dumps-archive"
APPLY=no
case "${1:-}" in
    "") ;;
    --apply) APPLY=yes ;;
    *) echo "usage: tools/prune-predeploy-dumps.sh [--apply]" >&2; exit 2 ;;
esac

# Newest first, by modification time. Portable to the Mac's bash 3.2 and BSD tools, where the tests run.
mtime() { perl -e 'print +(stat $ARGV[0])[9]' -- "$1"; }
listing=""
for f in "$HOME_DIR"/plate-pre-*.dump "$HOME_DIR"/gymtracker-pre-*.dump; do
    [ -f "$f" ] && [ ! -L "$f" ] || continue
    listing+="$(mtime "$f")"$'\t'"$f"$'\n'
done
dumps=()
times=()
while IFS=$'\t' read -r t f; do
    [ -n "$f" ] || continue
    times+=("$t")
    dumps+=("$f")
done < <(printf '%s' "$listing" | sort -rn)
if [ "${#dumps[@]}" -le "$KEEP" ]; then
    echo "${#dumps[@]} pre-deploy dumps; keeping all (the newest $KEEP are always kept)."
    exit 0
fi

newest=${dumps[0]}
if ! docker exec -i plate-db pg_restore --list < "$newest" > /dev/null 2>&1; then
    echo "The newest dump, $newest, doesn't read as a Postgres archive. Nothing moved; check it first." >&2
    exit 1
fi

cutoff=$(( $(date +%s) - DAYS * 86400 ))
move=()
for i in "${!dumps[@]}"; do
    [ "$i" -lt "$KEEP" ] && continue
    [ "${times[$i]}" -ge "$cutoff" ] && continue
    move+=("${dumps[$i]}")
done

echo "${#dumps[@]} pre-deploy dumps; keeping the newest $KEEP and those from the last $DAYS days; $newest reads fine."
if [ "${#move[@]}" -eq 0 ]; then
    echo "Nothing to move."
    exit 0
fi

for f in "${move[@]}"; do
    if [ "$APPLY" = yes ]; then
        mkdir -p "$ARCHIVE"
        # -n: never overwrite a dump already archived under the same name.
        if mv -n -- "$f" "$ARCHIVE/"; [ -e "$f" ]; then
            echo "kept (an archived dump has the same name): $(basename -- "$f")"
        else
            echo "moved: $(basename -- "$f")"
        fi
    else
        echo "would move: $(basename -- "$f")"
    fi
done
[ "$APPLY" = yes ] || echo "Dry run: run with --apply to move them into $ARCHIVE."
