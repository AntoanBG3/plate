# GymTracker

Self-hosted training and nutrition tracker for two users, running on the home server "wizard" and used from iPhones as an installed web app at `https://gym.peychev.uk`. See `SPEC.md` for the full specification and build phases.

All five build phases are in place:

- Workouts: templates, sessions with double-progression suggestions, a rest timer, and an offline queue so sets logged on bad signal are never lost.
- Training charts: estimated 1RM per exercise and weekly sets per muscle.
- Food: logging by text (parsed by an LLM through OpenRouter, numbers taken from the food database), barcode, search and recents.
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

### Food data

Three sources are imported on start, each only adding what is missing:

- USDA FoodData Central (public domain): `tools/usda/build_usda_subset.py` rebuilds `src/GymTracker.Web/Data/Seed/usda-foods.csv`. Its `name_bg` column holds Bulgarian names, added by `node tools/foods/translate-usda.mjs` (an LLM through OpenRouter; run it again after a rebuild). Databases seeded before the column existed get the names on the next start.
- Bulgarian foods and dishes (`Data/Seed/bg-foods.csv`, kind `generic`): compiled by `tools/foods` from public Bulgarian calorie tables and per-dish web pages found with firecrawl. Every row lists the pages its values came from; values from several pages are combined by median. Sites whose terms forbid reuse (BB-Team) are excluded.
- Products sold in Bulgaria from [Open Food Facts](https://world.openfoodfacts.org) (kind `product`), © Open Food Facts contributors, available under the [Open Database License](https://opendatacommons.org/licenses/odbl/1-0/).

To refresh the Bulgarian data (needs the `firecrawl` CLI and an OpenRouter key in `~/.config/gymtracker/openrouter-key` or `OPENROUTER_API_KEY`; `OPENROUTER_MODEL` overrides the model):

```sh
node tools/foods/collect-tables.mjs     # after scraping the two tables, see the comment at the top
node tools/foods/collect-dishes.mjs     # one firecrawl search per line of tools/foods/dishes.txt
node tools/foods/collect-off.mjs        # Open Food Facts search API, one page a second
node tools/foods/merge.mjs              # an LLM removes duplicates against USDA and between sources
```

### Language and theme

Each account chooses its language (English or Bulgarian) and theme (dark, light or match phone) in Settings. Interface text goes through `L["English text"]` in views and `LocalText` in code; Bulgarian lives in `src/GymTracker.Web/Resources/strings.bg.json`. `CatalogueTests` fails when a string has no translation. To add translations:

```sh
GYM_WRITE_KEYS=1 dotnet test --filter CatalogueTests   # lists every string in Resources/strings.keys.json
node tools/i18n/translate.mjs                           # an LLM drafts the missing ones; review them before committing
```

The design tokens (colour, type, spacing, motion) are in `wwwroot/css/tokens.css`; components in `wwwroot/css/app.css`; shared motion in `wwwroot/js/motion.js`. `design-samples/` holds the three directions the design was chosen from and the screenshot tooling.

Add a migration: `dotnet ef migrations add <Name> --project src/GymTracker.Web --output-dir Data/Migrations`.

The app icon is drawn in `tools/icons/icon.svg`. After changing it, run `cd tools/icons && swift render.swift ../../src/GymTracker.Web/wwwroot/icons` and bump the cache name in `wwwroot/sw.js` so installed apps pick it up.

## Deploying on wizard

The repository is checked out at `~/docker/gymtracker`. The database lives in `~/docker/gymtracker/db` and the login cookie keys in `~/docker/gymtracker/keys`; both are git-ignored.

### First time

Copy the checkout from the Mac. Git-ignored files are not sent, and the `P` filters protect the server's own state from `--delete`. Keep them: the `.gitignore` filter alone does not stop rsync deleting `db/` and `keys/` on wizard (it deleted `keys/` on 2026-10-02 and only failed on `db/` because Postgres owns it).

```sh
rsync -av --delete --filter='P /db/' --filter='P /keys/' --filter='P /docker/.env' \
    --filter=':- .gitignore' --exclude .git ~/Projects/homeserver-gym/ wizard:docker/gymtracker/
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

For food text parsing, save an [OpenRouter](https://openrouter.ai/keys) API key in the app under Settings → Food text parser (stored encrypted with the Data Protection keys, shared by both accounts, with a Test button), or put it in `OPENROUTER_API_KEY` in `docker/.env` (without one the app falls back to a simple built-in parser). The model is picked from OpenRouter's catalog on the same Settings page (default `google/gemini-3.5-flash-lite`, or `OPENROUTER_MODEL`). Add an email address in `OFF_CONTACT_EMAIL` for Open Food Facts.

The app joins the external `tunnel` network and publishes no host port. Memory limits are 256 MB per container; replace them with values from Beszel after a week of use.

### Users

Public registration does not exist. Create each account once:

```sh
docker exec -it gymtracker-app dotnet GymTracker.Web.dll create-user --email someone@example.com --name "Display name"
```

Passwords need at least 12 characters. Five wrong passwords lock the account for 15 minutes. Sign-in attempts are also limited per address (10 per 5 minutes) and across all addresses together (30 per 15 minutes); failed attempts are logged with the reason and address (`docker logs gymtracker-app | grep "Failed sign-in"`).

After signing in on each iPhone, add a passkey under Settings → Passkeys; from then on "Sign in with a passkey" (or the keyboard's autofill bar) signs in with Face ID. Passwords keep working. Passkeys are bound to `gym.peychev.uk` (`Passkeys__ServerDomain` in the compose file); changing the host name makes them stop working.

The data protection keys in `~/docker/gymtracker/keys` can forge sign-in cookies and decrypt the stored OpenRouter key. Keep the folder private (once, on wizard):

```sh
sudo chown -R 1000:1000 ~/docker/gymtracker/keys
sudo chmod 700 ~/docker/gymtracker/keys
```

The nightly backup copies it to the HDD and the PC, so keep those private too.

### Cloudflare

In the Zero Trust dashboard, add a public hostname to the "wizard" tunnel: `gym.peychev.uk` → `http://gymtracker-app:8080`. Do not add a Cloudflare Access application for it (SPEC section 9 explains why).

In the zone settings for `peychev.uk`, for this host:

- Speed → Optimization: turn off Rocket Loader. Scrape Shield: turn off Email Address Obfuscation. Web Analytics: no automatic injection for `gym.peychev.uk`. Each one injects scripts the Content Security Policy blocks.
- Caching → Configuration: set Browser Cache TTL to "Respect Existing Headers".

The app only answers requests addressed to `gym.peychev.uk` or `localhost` (`AllowedHosts` in the compose file). After the first deploy, `curl -sI https://gym.peychev.uk/account/login | head -1` must print `HTTP/2 200`; a 400 means the tunnel sends another Host header, which then needs adding to `AllowedHosts`.

### Content Security Policy

The app sends its CSP report-only at first and logs what it would block. After a few days of normal use on both phones, check the log:

```sh
docker logs gymtracker-app 2>&1 | grep "CSP violation"
```

If it is empty, set `CSP_ENFORCE=true` in `docker/.env` and run `docker compose -f docker/docker-compose.yml up -d`.

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

Both containers start with `mem_limit: 256m`. The app uses workstation garbage collection (one heap; server GC on wizard's 4 cores held about 200 MB) and has `cpus: "1.0"`. After a week of real use, read the peaks in Beszel and set each limit to roughly twice its peak in `docker/docker-compose.yml`. `docker logs gymtracker-app | grep "GC:"` shows the GC mode the app started with.

### Updating

Run the same rsync from the Mac, then on wizard:

```sh
cd ~/docker/gymtracker
docker compose -f docker/docker-compose.yml up -d --build && docker builder prune -f
```

Migrations run automatically when the app starts.

### Updating base images

The Dockerfile and compose file pin the .NET and Postgres images by digest, so a rebuild never pulls a different image by surprise. About once a month, look up the current digests and replace the `@sha256:...` parts:

```sh
docker buildx imagetools inspect mcr.microsoft.com/dotnet/sdk:10.0 --format '{{json .Manifest.Digest}}'
docker buildx imagetools inspect mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra --format '{{json .Manifest.Digest}}'
docker buildx imagetools inspect postgres:18 --format '{{json .Manifest.Digest}}'
```

The build also fails when a NuGet package has a known high or critical advisory; update that package when it does.

## Installing on iPhone

Open `https://gym.peychev.uk` in Safari, sign in, then Share → Add to Home Screen. Open it from the home screen icon from then on; the login lasts 30 days and renews with use.

Your data can be downloaded at any time from Settings → Export all data: one CSV file per kind of data in a zip, readable in Excel or Numbers.
