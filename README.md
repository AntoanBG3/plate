# GymTracker

Self-hosted training and nutrition tracker for two users, running on the home server "wizard" and used from iPhones as an installed web app at `https://gym.peychev.uk`. See `SPEC.md` for the full specification and build phases.

All five build phases are in place:

- Workouts: templates, sessions with double-progression suggestions, a rest timer, and an offline queue so sets logged on bad signal are never lost.
- Training charts: estimated 1RM per exercise and weekly sets per muscle.
- Food: logging by text (parsed by Gemini, numbers taken from the food database), barcode, search and recents.
- Targets: trend weight, a weekly adaptive maintenance estimate, and calorie and protein targets for a cut, maintenance or bulk, with weight and calorie charts.
- Settings: profile, CSV export of all your data, password change and logging out everywhere.

## Local development (Mac)

Prerequisites: .NET 10 SDK and Docker Desktop.

```sh
dotnet tool restore                                         # dotnet-ef, pinned in dotnet-tools.json
docker compose -f docker/docker-compose.dev.yml up -d db    # Postgres 18 on 127.0.0.1:5432
dotnet run --project src/GymTracker.Web                     # http://localhost:5130, applies migrations on start
```

Create a local user (the password is read from stdin when piped, otherwise prompted):

```sh
dotnet run --project src/GymTracker.Web -- create-user --email me@example.test --name Me
```

Tests: `dotnet build && dotnet test`. The integration tests start their own `postgres:18` container through Testcontainers. If Docker Hub pulls fail with "authentication required", pull the helper image once with `docker pull testcontainers/ryuk:0.14.0`.

The generic foods come from USDA FoodData Central (public domain). `tools/usda/build_usda_subset.py` rebuilds `src/GymTracker.Web/Data/Seed/usda-foods.csv`; the app imports it on first start.

Add a migration: `dotnet ef migrations add <Name> --project src/GymTracker.Web --output-dir Data/Migrations`.

The app icon is drawn in `tools/icons/icon.svg`. After changing it, run `cd tools/icons && swift render.swift ../../src/GymTracker.Web/wwwroot/icons` and bump the cache name in `wwwroot/sw.js` so installed apps pick it up.

## Deploying on wizard

The repository is checked out at `~/docker/gymtracker`. The database lives in `~/docker/gymtracker/db` and the login cookie keys in `~/docker/gymtracker/keys`; both are git-ignored.

### First time

Copy the checkout from the Mac (git-ignored state such as `db/`, `keys/` and `docker/.env` is never sent):

```sh
rsync -av --delete --filter=':- .gitignore' --exclude .git ~/Projects/homeserver-gym/ wizard:docker/gymtracker/
```

Then on wizard:

```sh
cd ~/docker/gymtracker
mkdir keys                                   # must exist before the first start so it is owned by antoan
cp docker/.env.example docker/.env
openssl rand -base64 32 | tr -d '/+='        # paste as POSTGRES_PASSWORD in docker/.env
docker compose -f docker/docker-compose.yml up -d --build
docker builder prune -f                      # the SDK build cache sits on the boot SSD
```

For food text parsing, put a Gemini API key in `GEMINI_API_KEY` in `docker/.env` (without one the app falls back to a simple built-in parser), and an email address in `OFF_CONTACT_EMAIL` for Open Food Facts.

The app joins the external `tunnel` network and publishes no host port. Memory limits are 256 MB per container; replace them with values from Beszel after a week of use.

### Users

Public registration does not exist. Create each account once:

```sh
docker exec -it gymtracker-app dotnet GymTracker.Web.dll create-user --email someone@example.com --name "Display name"
```

Passwords need at least 12 characters. Five wrong passwords lock the account for 15 minutes.

### Cloudflare

In the Zero Trust dashboard, add a public hostname to the "wizard" tunnel: `gym.peychev.uk` → `http://gymtracker-app:8080`. Do not add a Cloudflare Access application for it (SPEC section 9 explains why).

### Backups

The nightly `/usr/local/bin/homeserver-backup` needs two changes, made with sudo:

1. After the Nextcloud dump, add:

   ```bash
   # GymTracker database
   docker exec gymtracker-db sh -c 'pg_dump -Fc -U "$POSTGRES_USER" "$POSTGRES_DB"' \
       > "$SNAP/gymtracker-db.dump.tmp" || fail "GymTracker database dump failed"
   docker exec -i gymtracker-db pg_restore --list < "$SNAP/gymtracker-db.dump.tmp" > /dev/null \
       || fail "GymTracker database dump is unreadable"
   mv "$SNAP/gymtracker-db.dump.tmp" "$SNAP/gymtracker-db.dump"
   ```

2. Add `--exclude=gymtracker/db/` to the rsync line next to `--exclude=nextcloud/db_data/`. A file copy of a running database is not a consistent backup; the dump is.

The `keys` folder and `docker/.env` are still copied by the rsync, so a restore keeps users signed in.

### Restoring from a backup

```sh
cd ~/docker/gymtracker
docker compose -f docker/docker-compose.yml stop app
docker exec gymtracker-db sh -c 'dropdb -U "$POSTGRES_USER" "$POSTGRES_DB" && createdb -U "$POSTGRES_USER" "$POSTGRES_DB"'
docker run --rm -v /mnt/storage:/s:ro alpine cat /s/backups/latest/gymtracker-db.dump \
    | docker exec -i gymtracker-db sh -c 'pg_restore -U "$POSTGRES_USER" -d "$POSTGRES_DB" --no-owner'
docker compose -f docker/docker-compose.yml start app
```

### Restore drill

`tools/restore-drill.sh` restores a dump into a throwaway `postgres:18` container with no network and compares row counts per table with the live database. It touches nothing live. Run it on wizard after changing the backup or the database setup, and now and then to make sure the nightly dumps still restore:

```sh
cd ~/docker/gymtracker
tools/restore-drill.sh            # dumps the live database now, the same way the nightly backup does
tools/restore-drill.sh nightly    # uses /mnt/storage/backups/latest/gymtracker-db.dump
```

### Memory limits

Both containers start with `mem_limit: 256m`. Right after deployment the app used about 115 MB and Postgres about 38 MB. After a week of real use, read the peaks in Beszel and set each limit to roughly twice its peak in `docker/docker-compose.yml`.

### Updating

Run the same rsync from the Mac, then on wizard:

```sh
cd ~/docker/gymtracker
docker compose -f docker/docker-compose.yml up -d --build && docker builder prune -f
```

Migrations run automatically when the app starts.

## Installing on iPhone

Open `https://gym.peychev.uk` in Safari, sign in, then Share → Add to Home Screen. Open it from the home screen icon from then on; the login lasts 30 days and renews with use.

Your data can be downloaded at any time from Settings → Export all data: one CSV file per kind of data in a zip, readable in Excel or Numbers.
