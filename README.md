# Repa (Ряпа)

Self-hosted training and nutrition tracker (code name GymTracker), running on the home server "wizard" and used from iPhones as an installed web app at `https://gym.peychev.uk`. See `SPEC.md` for the full specification and build phases.

All five build phases are in place:

- Workouts: templates, sessions with double-progression suggestions, a rest timer, and an offline queue so sets logged on bad signal are never lost.
- Training charts: estimated 1RM per exercise and weekly sets per muscle.
- Food: logging by text (parsed by an LLM through OpenRouter, numbers taken from the food database, optionally with a decision model choosing the database food), barcode, search and recents.
- Targets: trend weight, a weekly adaptive maintenance estimate, and calorie and protein targets for a cut, maintenance or bulk, with weight and calorie charts. Until there are about three weeks of logs, maintenance comes from the NASEM 2023 energy equations (sex, age, height, weight and activity level).
- Setup: on first sign-in a short AI chat fills in the profile and ends on an editable summary; without an OpenRouter key it is a plain form.
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

- USDA FoodData Central (public domain): `tools/usda/build_usda_subset.py` rebuilds `src/GymTracker.Web/Data/Seed/usda-foods.csv`. Its `name_bg` column holds Bulgarian names, added by `node tools/foods/translate-usda.mjs` (an LLM through OpenRouter; run it again after a rebuild). Databases seeded before the column existed get the names on the next start. The `fiber` and `satfat` columns are blank where USDA has no value, and are added to older databases the same way.
- Bulgarian foods and dishes (`Data/Seed/bg-foods.csv`, kind `generic`): compiled by `tools/foods` from public Bulgarian calorie tables and per-dish web pages found with firecrawl. Every row lists the pages its values came from; values from several pages are combined by median. Sites whose terms forbid reuse (BB-Team) are excluded.
- Products sold in Bulgaria from [Open Food Facts](https://world.openfoodfacts.org) (kind `product`), © Open Food Facts contributors, available under the [Open Database License](https://opendatacommons.org/licenses/odbl/1-0/).

To refresh the Bulgarian data (needs the `firecrawl` CLI and an OpenRouter key in `~/.config/gymtracker/openrouter-key` or `OPENROUTER_API_KEY`; `OPENROUTER_MODEL` overrides the model):

```sh
node tools/foods/collect-tables.mjs     # after scraping the two tables, see the comment at the top
node tools/foods/collect-dishes.mjs     # one firecrawl search per line of tools/foods/dishes.txt
node tools/foods/collect-off.mjs        # Open Food Facts search API, one page a second
node tools/foods/merge.mjs              # an LLM removes duplicates against USDA and between sources
node tools/foods/add-fiber.mjs          # fiber and saturated fat for the products, by barcode
```

Dishes have no fiber or saturated fat: the pages they come from don't give them, so the app treats them as unknown.

### Language and theme

Each account chooses its language (English or Bulgarian) and theme (dark, light or match phone) in Settings. Interface text goes through `L["English text"]` in views and `LocalText` in code; Bulgarian lives in `src/GymTracker.Web/Resources/strings.bg.json`. `CatalogueTests` fails when a string has no translation. To add translations:

```sh
GYM_WRITE_KEYS=1 dotnet test --filter CatalogueTests   # lists every string in Resources/strings.keys.json
node tools/i18n/translate.mjs                           # an LLM drafts the missing ones; review them before committing
```

The design tokens (colour, type, spacing, motion) are in `wwwroot/css/tokens.css`; components in `wwwroot/css/app.css`; shared motion in `wwwroot/js/motion.js`. `design-samples/` holds the three directions the design was chosen from and the screenshot tooling.

Add a migration: `dotnet ef migrations add <Name> --project src/GymTracker.Web --output-dir Data/Migrations`.

### Browser tests

`tests/GymTracker.E2E.Tests` drives the real app in Playwright's Chromium and WebKit (WebKit is the engine closest to iOS Safari) at 390 and 320 px. It runs with `dotnet test` like the other projects; the first run downloads the two browsers into the Playwright cache, which takes a minute. The app runs on `http://localhost` with `Auth:HostCookies=false`, because the `__Host-` cookie needs https and the local development certificate isn't trusted. Docker must be running, as for the other database tests.

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

For food text parsing, save an [OpenRouter](https://openrouter.ai/keys) API key in the admin area under AI, `/admin/ai` (stored encrypted with the Data Protection keys, shared by both accounts, with a Test button), or put it in `OPENROUTER_API_KEY` in `docker/.env` (without one the app falls back to a simple built-in parser). The same page has one model per AI task:

- Food parser: picked from OpenRouter's catalog (default `openai/gpt-6-luna`, or `OPENROUTER_MODEL`).
- Food matcher: Off (the database ranking picks the food, the default) or TypeSafe's Jev 1.13, a decision model that chooses which database food each item is, or says none fits. It uses the same key and costs about $0.0002 per entry. Picking it runs a test decision. Entries record which Jev version chose their match (`match_model` in the export); `docker logs gymtracker-app | grep "Jev "` shows each call's latency and cost.

Add an email address in `OFF_CONTACT_EMAIL` for Open Food Facts.

The app joins the external `tunnel` network and publishes no host port. Memory limits are 256 MB per container; replace them with values from Beszel after a week of use.

### Users

Public registration does not exist. Accounts come from a single-use invite link or from the CLI; the app never sends email.

To invite someone, make a link in the admin area (Admin → Invites, with Copy and Share) or over SSH, and send it yourself:

```sh
ssh wizard docker exec gymtracker-app dotnet GymTracker.Web.dll invite create --note "Ivan from the gym" --expires 7d
ssh wizard docker exec gymtracker-app dotnet GymTracker.Web.dll invite list --all
ssh wizard docker exec gymtracker-app dotnet GymTracker.Web.dll invite revoke <id>
```

The link (`https://gym.peychev.uk/join/...`) is shown once; only its hash is stored. It works once, for 1 to 30 days (7 by default), and at most 5 invites are open at a time. The person picks a name, email, password and language, is signed in, and goes through the setup chat. Each use is audited and sends a Telegram message. The join page is limited to 10 requests per address per 15 minutes (`RateLimits__JoinPerWindow`).

Or create an account directly:

```sh
docker exec -it gymtracker-app dotnet GymTracker.Web.dll create-user --email someone@example.com --name "Display name"
```

There is no emailed password reset. To set a new password (it clears a lockout and signs the account out everywhere; passkeys keep working), or to delete an account with all its data:

```sh
ssh -t wizard docker exec -it gymtracker-app dotnet GymTracker.Web.dll reset-password --email someone@example.com
ssh -t wizard docker exec -it gymtracker-app dotnet GymTracker.Web.dll delete-user --email someone@example.com
```

`delete-user` asks for the email again (or takes `--yes`), refuses to delete the last `Dev` account, and keeps shared foods and the audit log. Offer the person the export in Settings first; the nightly dumps still hold the data for their 14 days.

Passwords need at least 12 characters. Five wrong passwords lock the account for 15 minutes. Sign-in attempts are also limited per address (10 per 5 minutes) and across all addresses together (30 per 15 minutes); failed attempts are logged with the reason and address (`docker logs gymtracker-app | grep "Failed sign-in"`).

At first sign-in each account goes through the setup chat (or its form) at `/welcome`; "Skip for now" leaves it, and Settings → Run the setup chat again brings it back. Accounts created before the setup chat existed see it once too, with what they had already entered filled in. The chat sends the answers to the onboarding model through OpenRouter and is limited to 40 messages per 10 minutes per account (`RateLimits__AiOnboardingPerWindow`).

After signing in on each iPhone, add a passkey under Settings → Passkeys; from then on "Sign in with a passkey" (or the keyboard's autofill bar) signs in with Face ID. Passwords keep working. Passkeys are bound to `gym.peychev.uk` (`Passkeys__ServerDomain` in the compose file); changing the host name makes them stop working.

The data protection keys in `~/docker/gymtracker/keys` can forge sign-in cookies and decrypt the stored OpenRouter key. Keep the folder private (once, on wizard):

```sh
sudo chown -R 1000:1000 ~/docker/gymtracker/keys
sudo chmod 700 ~/docker/gymtracker/keys
```

The nightly backup copies it to the HDD and the PC, so keep those private too.

### Cloudflare

In the Zero Trust dashboard, add a public hostname to the "wizard" tunnel: `gym.peychev.uk` → `http://gymtracker-app:8080`. Do not add a Cloudflare Access application for the whole host (SPEC section 9 explains why); only `/admin` gets one (Admin, below).

In the zone settings for `peychev.uk`, for this host:

- Speed → Optimization: turn off Rocket Loader. Scrape Shield: turn off Email Address Obfuscation. Web Analytics: no automatic injection for `gym.peychev.uk`. Each one injects scripts the Content Security Policy blocks.
- Caching → Configuration: set Browser Cache TTL to "Respect Existing Headers".

The app only answers requests addressed to `gym.peychev.uk` or `localhost` (`AllowedHosts` in the compose file). After the first deploy, `curl -s -o /dev/null -w "%{http_code}\n" https://gym.peychev.uk/account/login` must print `200`; a 400 means the tunnel sends another Host header, which then needs adding to `AllowedHosts`.

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

Run the same rsync from the Mac, then build on wizard from the Mac so the image carries the commit it was built from (feedback reports and errors record it):

```sh
ssh wizard "cd ~/docker/gymtracker && APP_VERSION=$(git -C ~/Projects/homeserver-gym rev-parse --short HEAD) docker compose -f docker/docker-compose.yml up -d --build && docker builder prune -f"
```

`$(...)` is expanded on the Mac before the command is sent. Building on wizard directly works too; the version then shows as `unknown`.

Migrations run automatically when the app starts.

Deleted food entries, sets and exercises are kept for 24 hours so Undo can bring them back, then purged. Rolling back the `AddSoftDelete` migration drops that column, so anything deleted in the last 24 hours would reappear; restore from the pre-deploy dump instead.

### Feedback

Both accounts can report problems and ideas in Settings → Send feedback (with a screenshot), rate how typed food was read on the Check foods screen, and use the "Report" links on barcode results, weight suggestions, the maintenance estimate and AI food entries. Errors in the app and the browser are recorded on their own. Everything stays in the database; the CLI reads it and answers it (SPEC section 11):

```sh
ssh wizard docker exec gymtracker-app dotnet GymTracker.Web.dll feedback digest       # what came in this week
ssh wizard docker exec gymtracker-app dotnet GymTracker.Web.dll feedback show 12
ssh wizard docker exec gymtracker-app dotnet GymTracker.Web.dll feedback status 12 fixed --reply "Fixed, thanks." --version abc1234
```

`feedback help` lists every command. In the coding agent, `/feedback` runs the same commands and the triage workflow in `docs/feedback-triage.md`. Replies show under Settings → Your feedback with a badge, and `feedback changelog add` puts a What's new note on both users' Today page.

For a Telegram message on each new report and each new kind of error, set `TELEGRAM_BOT_TOKEN` and `TELEGRAM_CHAT_ID` in `docker/.env` (the backup script's bot works) and restart the app. Only a report's number, the reporter's name, kind, area and first 80 characters are sent.

The feedback CLI starts a second .NET process in the app container. If `docker stats gymtracker-app` shows the 256 MB limit being approached while it runs, use `docker compose -f docker/docker-compose.yml run --rm --no-deps app feedback ...` from `~/docker/gymtracker` instead.

### Admin

The developer's account has an admin area at `/admin` (Settings → Admin): reports with status and reply, parse runs, errors, What's new, the OpenRouter key and AI models, the parse evaluation, invites, users and an audit log (SPEC section 12). Other accounts see nothing about the AI models in Settings and get a 404 there. Getting in takes three things:

1. The `Dev` role, given only over SSH:

   ```sh
   ssh wizard docker exec gymtracker-app dotnet GymTracker.Web.dll role grant dev --email <your account email>
   ssh wizard docker exec gymtracker-app dotnet GymTracker.Web.dll role list
   ```

   It shows up in a signed-in session within 5 minutes, or at the next sign-in. `role revoke dev --email ...` closes the area at once.

2. Cloudflare Access in front of `/admin`. In the Zero Trust dashboard: Access → Applications → Add an application → Self-hosted. Name it "GymTracker admin", with the public hostname `gym.peychev.uk` and the path `admin`. Set the session duration to 24 hours. Add a policy "Developer" with the action Allow, and include Emails → `<your account email>`. Use the One-time PIN login method. Save it, then open the application's Overview and copy the Application Audience (AUD) tag. The team domain is under Settings → Custom Pages (`<team>.cloudflareaccess.com`). Put both in `~/docker/gymtracker/docker/.env`:

   ```sh
   CF_ACCESS_TEAM_DOMAIN=<team>.cloudflareaccess.com
   CF_ACCESS_AUD=<the AUD tag>
   ```

   Then restart with `docker compose -f docker/docker-compose.yml up -d`. Until both are set, the admin area stays off in production (a warning at startup says so). The app checks the token Access adds to each request, so the area can't be reached around Access. Afterwards, `curl -sI https://gym.peychev.uk/admin` should be answered by Cloudflare (a redirect to its login), not by the app.

3. A passkey unlock with Face ID on `/admin/unlock`, using a passkey of this account (Settings → Passkeys). It lasts 30 minutes after the last admin page, and 4 hours at most. Lock now, log out and Log out everywhere end it. Each unlock sends a Telegram message.

If the Access login doesn't hand back to the installed home-screen app, open `https://gym.peychev.uk/admin` in Safari instead.

The same things work over SSH:

```sh
ssh wizard docker exec gymtracker-app dotnet GymTracker.Web.dll ai show
ssh wizard docker exec gymtracker-app dotnet GymTracker.Web.dll ai set-model matcher off
ssh wizard docker exec gymtracker-app dotnet GymTracker.Web.dll ai set-model onboarding off   # setup form only; unset, the chat uses the parser's model
ssh -t wizard docker exec -it gymtracker-app dotnet GymTracker.Web.dll ai set-key     # asks for the key without echoing it
ssh wizard docker exec gymtracker-app dotnet GymTracker.Web.dll admin audit --since 30d
```

`ai set-key` reads the key from standard input and never from an argument, so it stays out of shell history and the process list.

### Label photos

On the Manual tab, after a barcode Open Food Facts doesn't know, and from "Photograph a label" on the Scan tab, a photo of a nutrition table fills in the manual food form (SPEC 6.6). A separate vision model reads it, `google/gemini-3.5-flash-lite` unless another is picked under AI → Label reader; Off hides the feature for both accounts. Nothing is saved until the form is. Photos are kept 30 days so misreads can be checked under Admin → Labels or with `feedback labels --edited`, and the scans 180 days.

```sh
ssh wizard docker exec gymtracker-app dotnet GymTracker.Web.dll ai set-model label google/gemini-3.5-flash-lite
ssh wizard docker exec gymtracker-app dotnet GymTracker.Web.dll ai test label   # reads a bundled sample label, expects 250 kcal
```

`tools/labels/make-sample.sh` re-renders that sample and the browser tests' label photos.

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
