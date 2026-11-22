# Plate

Plate is a self-hosted training and nutrition tracker running on the home server "wizard" and used from iPhones as an installed web app at `https://plate.peychev.uk`. See `SPEC.md` for the full specification and build phases.

All five build phases are in place:

- Workouts: templates, sessions with double-progression suggestions, a rest timer, and an offline queue so sets logged on bad signal are never lost.
- Training charts: estimated 1RM per exercise and weekly sets per muscle.
- Food: logging by text (parsed by an LLM through OpenRouter, numbers taken from the food database, optionally with a decision model choosing the database food), barcode, search and recents. Text matching considers named brands, preparation, flavour and fat content.
- Targets: trend weight, a weekly adaptive maintenance estimate, and calorie and protein targets for a cut, maintenance or bulk, with weight and calorie charts. Until there are about three weeks of logs, maintenance comes from the NASEM 2023 energy equations (sex, age, height, weight and activity level).
- Setup: on first sign-in a short AI chat fills in the profile and ends on an editable summary; without an OpenRouter key it is a plain form.
- Settings: profile, CSV export of all your data, password change and logging out everywhere.

## Local development (Mac)

Prerequisites: .NET 10 SDK, Node.js 24 or newer, and Docker Desktop.

```sh
dotnet tool restore                                         # dotnet-ef, pinned in dotnet-tools.json
docker compose -f docker/docker-compose.dev.yml up -d db    # Postgres 18 on 127.0.0.1:5432
dotnet run --project src/Plate.Web                     # http://localhost:5130, applies migrations on start
```

Create a local user (the password is read from stdin when piped, otherwise prompted):

```sh
dotnet run --project src/Plate.Web -- create-user --email me@example.test --name Me
```

Checks: `bash tools/check.sh` runs the JavaScript regressions, restores dependencies, builds, and runs all .NET tests. The same checks run on pushes and pull requests, and must pass before `tools/deploy.sh` touches the server. The integration tests start their own `postgres:18` container through Testcontainers. If Docker Hub pulls fail with "authentication required", pull the helper image once with `docker pull testcontainers/ryuk:0.14.0`.

Pending sets and feedback belong to the account that created them. A set send is aborted after 15 seconds if the request or response body stalls; the same pending set and ClientId are retried on reconnect, foregrounding or the existing 15-second retry timer. When browser storage is unavailable they stay in memory and continue sending, with a warning to keep the page open. A feedback report kept in memory is locked while waiting; Send retries the same report. An account change pauses sending until reload; a waiting message is shown once, not on every retry. The ownerless queues of the previous version are handled by kind. Sets are adopted by whoever signs in: the server finds a session exercise only through that user's filter, so another account's set gets a 404, goes back to the old queue for its owner and is not offered to that account again; sets older than 30 days are dropped. Reports and ratings are never sent on their own: Your feedback lists them under "Saved before an update" with Send from this account and Discard. Old tabs get a retryable 503 for queued feedback, so they keep those reports until they reload. The old rest timer and typed drafts are cleared.

Nutrition targets are captured by user and day. Changes apply to today's snapshot, while previous days and charts keep the captured targets. Dates without a visit carry the last captured targets forward. Days from before snapshots existed are captured once per user, the first time their targets are (any page with targets, or a Settings or diet-break save, which captures before changing the profile): calories and protein as the stored estimate had them, with the fat, carb and fibre ranges fitted to those two numbers, so a day's protein matches across the Food page, the charts and the weekly summary. A page load that changes nothing writes nothing. The weekly summary judges a week against the targets of its last day. The stored JSON has fixed names (`TargetDayValues`, pinned by `TargetDayValuesTests`), independent of the Core records. The new table is included in exports and deleted with the account. Its migration runs on startup as usual.

### Food data

Three sources are imported on start, each only adding what is missing:

- USDA FoodData Central (public domain): `tools/usda/build_usda_subset.py` rebuilds `src/Plate.Web/Data/Seed/usda-foods.csv`. Its `name_bg` column holds Bulgarian names, added by `node tools/foods/translate-usda.mjs` (an LLM through OpenRouter; run it again after a rebuild). Databases seeded before the column existed get the names on the next start. The `fiber` and `satfat` columns are blank where USDA has no value, and are added to older databases the same way.
- Bulgarian foods and dishes (`Data/Seed/bg-foods.csv`, kind `generic`): compiled by `tools/foods` from public Bulgarian calorie tables and per-dish web pages found with firecrawl. Every row lists the pages its values came from; values from several pages are combined by median. Sites whose terms forbid reuse (BB-Team) are excluded. The file also holds the Lidl Bulgaria bakery range, kept by hand in `tools/foods/lidl-bakery.csv` (sources and method in `tools/foods/lidl-bakery.md`): Lidl's label values where it published them, estimates from the ingredient lists otherwise, with each piece's weight so "две закуски" is two pieces. `merge.mjs` appends that file unchanged, and `tests/js/curated-foods.test.cjs` fails if the seed loses one of its rows.
- Products sold in Bulgaria from [Open Food Facts](https://world.openfoodfacts.org) (kind `product`), © Open Food Facts contributors, available under the [Open Database License](https://opendatacommons.org/licenses/odbl/1-0/).

To refresh the Bulgarian data (needs the `firecrawl` CLI and an OpenRouter key in `~/.config/plate/openrouter-key` or `OPENROUTER_API_KEY`; `OPENROUTER_MODEL` overrides the model):

```sh
node tools/foods/collect-tables.mjs     # after scraping the two tables, see the comment at the top
node tools/foods/collect-dishes.mjs     # one firecrawl search per line of tools/foods/dishes.txt
node tools/foods/collect-off.mjs        # Open Food Facts search API, one page a second
node tools/foods/merge.mjs              # an LLM removes duplicates against USDA and between sources
node tools/foods/add-fiber.mjs          # fiber and saturated fat for the products, by barcode
```

Dishes have no fiber or saturated fat: the pages they come from don't give them, so the app treats them as unknown.

Home dishes are better entered as recipes (Recent tab → Recipes → New recipe): the ingredients typed or found once, with the dish's weight after cooking, give its values per 100 g, fiber included when the ingredients have it, and it is then logged by weight like any food. Recipes are private to the account.

### Language and theme

Each account chooses its language (English or Bulgarian) and theme (dark, light or match phone) in Settings. Interface text goes through `L["English text"]` in views and `LocalText` in code; Bulgarian lives in `src/Plate.Web/Resources/strings.bg.json`. `CatalogueTests` fails when a string has no translation. To add translations:

```sh
GYM_WRITE_KEYS=1 dotnet test --filter CatalogueTests   # lists every string in Resources/strings.keys.json
node tools/i18n/translate.mjs                           # an LLM drafts the missing ones; review them before committing
```

The design tokens (colour, type, spacing, motion) are in `wwwroot/css/tokens.css`; components in `wwwroot/css/app.css`; shared motion in `wwwroot/js/motion.js`. `design-samples/` holds the three directions the design was chosen from and the screenshot tooling.

Add a migration: `dotnet ef migrations add <Name> --project src/Plate.Web --output-dir Data/Migrations`.

### Browser tests

`tests/Plate.E2E.Tests` drives the real app in Playwright's Chromium and WebKit (WebKit is the engine closest to iOS Safari) at 390 and 320 px. It runs with `dotnet test` like the other projects; the first run downloads the two browsers into the Playwright cache, which takes a minute. The app runs on `http://localhost` with `Auth:HostCookies=false`, because the `__Host-` cookie needs https and the local development certificate isn't trusted. Docker must be running, as for the other database tests.

The app icon is drawn in `tools/icons/icon.svg`. After changing it, run `cd tools/icons && swift render.swift ../../src/Plate.Web/wwwroot/icons` and bump the cache name in `wwwroot/sw.js` so installed apps pick it up.

## Deploying on wizard

The repository is checked out at `~/docker/plate`. The database lives in `~/docker/plate/db` and the login cookie keys in `~/docker/plate/keys`; both are git-ignored.

### First time

Copy the checkout from the Mac. Git-ignored files are not sent, and the `P` filters protect the server's own state from `--delete`. Keep them: the `.gitignore` filter alone does not stop rsync deleting `db/` and `keys/` on wizard (it deleted `keys/` on 2026-10-02 and only failed on `db/` because Postgres owns it).

```sh
rsync -av --delete --filter='P /db/' --filter='P /keys/' --filter='P /docker/.env' \
    --filter=':- .gitignore' --exclude .git ~/Projects/homeserver-gym/ wizard:docker/plate/
```

Then on wizard:

```sh
cd ~/docker/plate
mkdir keys                                   # must exist before the first start so it is owned by antoan
cp docker/.env.example docker/.env
openssl rand -base64 32 | tr -d '/+='        # paste as POSTGRES_PASSWORD in docker/.env
docker compose -f docker/docker-compose.yml up -d --build
docker builder prune -f                      # the SDK build cache sits on the boot SSD
```

For food text parsing, save an [OpenRouter](https://openrouter.ai/keys) API key in the admin area under AI, `/admin/ai` (stored encrypted with the Data Protection keys, shared by all accounts, with a Test button), or put it in `OPENROUTER_API_KEY` in `docker/.env` (without one the app falls back to a simple built-in parser). The same page has one model per AI task:

- Food parser: picked from OpenRouter's catalog (default `openai/gpt-6-luna`, or `OPENROUTER_MODEL`).
- Food matcher: Off (the database ranking picks a food when the name identifies it and uses the estimate otherwise, the default) or TypeSafe's Jev 1.13, a decision model that chooses which database food each item is, or says none fits. It uses the same key and costs about $0.0002 per entry. Picking it runs a test decision. Entries record which Jev version chose their match (`match_model` in the export); `docker logs plate-app | grep "Jev "` shows each call's latency and cost.

Add an email address in `OFF_CONTACT_EMAIL` for Open Food Facts.

The app joins the external `tunnel` network and publishes no host port. Memory limits are 256 MB per container; replace them with values from Beszel after a week of use.

### Users

Public registration does not exist. Accounts come from a single-use invite link or from the CLI; the app never sends email.

To invite someone, make a link in the admin area (Admin → Invites, with Copy and Share) or over SSH, and send it yourself:

```sh
ssh wizard docker exec plate-app dotnet Plate.Web.dll invite create --note "Ivan from the gym" --expires 7d
ssh wizard docker exec plate-app dotnet Plate.Web.dll invite list --all
ssh wizard docker exec plate-app dotnet Plate.Web.dll invite revoke <id>
```

The link (`https://plate.peychev.uk/join/...`) is shown once; only its hash is stored. It works once, for 1 to 30 days (7 by default), and at most 5 invites are open at a time. The person picks a name, email, password and language, is signed in, and goes through the setup chat. Each use is audited and sends a Telegram message. The join page is limited to 10 requests per address per 15 minutes (`RateLimits__JoinPerWindow`).

Or create an account directly:

```sh
docker exec -it plate-app dotnet Plate.Web.dll create-user --email someone@example.com --name "Display name"
```

There is no emailed password reset. To set a new password (it clears a lockout and signs the account out everywhere; passkeys keep working), or to delete an account with all its data:

```sh
ssh -t wizard docker exec -it plate-app dotnet Plate.Web.dll reset-password --email someone@example.com
ssh -t wizard docker exec -it plate-app dotnet Plate.Web.dll delete-user --email someone@example.com
```

`delete-user` asks for the email again (or takes `--yes`), refuses to delete the last `Dev` account, and keeps shared foods and the audit log. Offer the person the export in Settings first; the nightly dumps still hold the data for their 14 days.

Passwords need at least 12 characters. Five wrong passwords lock the account for 15 minutes. Sign-in attempts are also limited per address (10 per 5 minutes) and across all addresses together (30 per 15 minutes); failed attempts are logged with the reason and address (`docker logs plate-app | grep "Failed sign-in"`).

At first sign-in each account goes through the setup chat (or its form) at `/welcome`; "Skip for now" leaves it, and Settings → Run the setup chat again brings it back. Accounts created before the setup chat existed see it once too, with what they had already entered filled in. The chat sends the answers to the onboarding model through OpenRouter and is limited to 40 messages per 10 minutes per account (`RateLimits__AiOnboardingPerWindow`).

After signing in on each iPhone, add a passkey under Settings → Passkeys; from then on "Sign in with a passkey" (or the keyboard's autofill bar) signs in with Face ID. Passwords keep working. Passkeys are bound to `plate.peychev.uk` (`Passkeys__ServerDomain` in the compose file); changing the host name makes them stop working.

The data protection keys in `~/docker/plate/keys` can forge sign-in cookies and decrypt the stored OpenRouter key. Keep the folder private (once, on wizard):

```sh
sudo chown -R 1000:1000 ~/docker/plate/keys
sudo chmod 700 ~/docker/plate/keys
```

The nightly backup copies it to the HDD and the PC, so keep those private too.

### Cloudflare

In the Zero Trust dashboard, add a public hostname to the "wizard" tunnel: `plate.peychev.uk` → `http://plate-app:8080`. Do not add a Cloudflare Access application for the whole host (SPEC section 9 explains why); only `/admin` gets one (Admin, below).

The app lived at `gym.peychev.uk` until 2026-11-09. That hostname keeps its proxied DNS record and a redirect rule (Rules → Redirect Rules) that sends every request to `https://plate.peychev.uk` with the same path and query, so old bookmarks, invite links and home-screen apps land on the new host. Passkeys made on the old host don't work on the new one; people sign in with their password once and add a passkey again.

In the zone settings for `peychev.uk`, for this host:

- Speed → Optimization: turn off Rocket Loader. Scrape Shield: turn off Email Address Obfuscation. Web Analytics: no automatic injection for `plate.peychev.uk`. Each one injects scripts the Content Security Policy blocks.
- Caching → Configuration: set Browser Cache TTL to "Respect Existing Headers".

The app only answers requests addressed to `plate.peychev.uk` or `localhost` (`AllowedHosts` in the compose file). After the first deploy, `curl -s -o /dev/null -w "%{http_code}\n" https://plate.peychev.uk/account/login` must print `200`; a 400 means the tunnel sends another Host header, which then needs adding to `AllowedHosts`.

### Content Security Policy

The app sends its CSP report-only at first and logs what it would block. After a few days of normal use on a few phones, check the log:

```sh
docker logs plate-app 2>&1 | grep "CSP violation"
```

If it is empty, set `CSP_ENFORCE=true` in `docker/.env` and run `docker compose -f docker/docker-compose.yml up -d`.

### Backups

The nightly `/usr/local/bin/homeserver-backup` needs two changes, made with sudo:

1. After the Nextcloud dump, add:

   ```bash
   # Plate database
   docker exec plate-db sh -c 'pg_dump -Fc -U "$POSTGRES_USER" "$POSTGRES_DB"' \
       > "$SNAP/plate-db.dump.tmp" || fail "Plate database dump failed"
   docker exec -i plate-db pg_restore --list < "$SNAP/plate-db.dump.tmp" > /dev/null \
       || fail "Plate database dump is unreadable"
   mv "$SNAP/plate-db.dump.tmp" "$SNAP/plate-db.dump"
   ```

2. Add `--exclude=plate/db/` to the rsync line next to `--exclude=nextcloud/db_data/`. A file copy of a running database is not a consistent backup; the dump is.

The `keys` folder and `docker/.env` are still copied by the rsync, so a restore keeps users signed in.

### Restoring from a backup

```sh
cd ~/docker/plate
docker compose -f docker/docker-compose.yml stop app
docker exec plate-db sh -c 'dropdb -U "$POSTGRES_USER" "$POSTGRES_DB" && createdb -U "$POSTGRES_USER" "$POSTGRES_DB"'
docker run --rm -v /mnt/storage:/mnt/storage:ro alpine cat /mnt/storage/backups/latest/plate-db.dump \
    | docker exec -i plate-db sh -c 'pg_restore -U "$POSTGRES_USER" -d "$POSTGRES_DB" --no-owner'
docker compose -f docker/docker-compose.yml start app
```

### Restore drill

`tools/restore-drill.sh` restores a dump into a throwaway `postgres:18` container on an internal-only Docker network and compares row counts per table with the live database. It touches nothing live. Both modes reject missing/extra tables or different column definitions. An immediate dump must also match live row counts; nightly counts may differ because the live data has changed. This checks compatibility with the current schema, so a nightly dump from before a schema migration can be rejected even when its archive is valid. It does not verify backup-time row counts, indexes, constraints or every stored value. Run it on wizard after changing the backup or database setup, and now and then to check the nightly archive:

```sh
cd ~/docker/plate
tools/restore-drill.sh                 # dumps the live database now, the same way the nightly backup does
tools/restore-drill.sh nightly         # uses /mnt/storage/backups/latest/plate-db.dump
tools/restore-drill.sh nightly --app   # also brings the app back from that snapshot and signs in
```

`--app` proves the snapshot is enough to bring Plate back, not only that the dump restores. After the table checks (made before the app's startup migrations change the copy), it copies the Data Protection keys from the same snapshot, says whether they are identical to the live ones, and starts the `plate-app` image already on wizard (no build) against the restored database, on the drill's internal network, with nothing from `.env`: no Telegram or OpenRouter. A `curlimages/curl` container on that network stands in for Cloudflare's proxy. The drill creates a throwaway account in the copy and signs in with it; then it signs in again with an antiforgery cookie and token fetched from plate.peychev.uk, which only works when the backed-up keys decrypt what the live keys encrypted (sign-in cookies use the same keys). It fails if the app doesn't start, if either sign-in fails, or if the app writes a new key. Every container, the network and the temporary files are removed at the end, pass or fail.

Last run, 2026-11-22, on the 2026-11-22 snapshot: tables and columns matched, the keys were identical to the live ones, version 9c5ad06 started on the copy, and both sign-ins worked. With an empty key ring instead, the live-token sign-in failed as it should.

### Checks that need sudo or the PC

The drill reads the backups but can't judge the disks or the PC copy. Run these on wizard now and then (the boot SSD is the Kingston SA400 `sda`, holding `/` and with it `~/docker/plate/db` and `keys`; the backups are on the WD HDD `sdb` at `/mnt/storage`):

```sh
sudo smartctl -H -A /dev/sda    # SSD: overall health, reallocated/pending sectors, wear, power-on hours
sudo smartctl -H -A /dev/sdb    # backup HDD
sudo smartctl -l error /dev/sda # errors the SSD has logged
sudo tail -5 /mnt/storage/backups/backup.log   # the last nightly runs
```

On the PC, check that the backup pull task's newest archive has the date `ssh wizard ~/bin/export-latest-backup name` prints, and that it opens (`tar -tzf <archive> | grep plate-db.dump`).

### Memory limits

Both containers start with `mem_limit: 256m`. The app uses workstation garbage collection (one heap; server GC on wizard's 4 cores held about 200 MB) and has `cpus: "1.0"`. After a week of real use, read the peaks in Beszel and set each limit to roughly twice its peak in `docker/docker-compose.yml`. `docker logs plate-app | grep "GC:"` shows the GC mode the app started with.

### Updating

From the Mac, on a clean `main`:

```sh
tools/deploy.sh --dry-run   # what the copy would change on wizard
tools/deploy.sh
```

It dumps the live database to `~/plate-pre-<commit>-<time>.dump` on wizard, runs the rsync above, builds on wizard with `APP_VERSION` set to the commit (feedback reports and errors record it), and waits until `plate.peychev.uk` serves that commit. Building on wizard directly works too; the version then shows as `unknown`.

To measure how long the site is unavailable during a deploy (P17 in `docs/plans/2026-11-05-performance-plan.md`), start the probe in a second terminal first and stop it with Ctrl-C once the deploy has finished:

```sh
node tools/perf/deploy-probe.mjs --out /private/tmp/deploy-probe.json --ssh wizard
```

It requests the sign-in page once a second, anonymously and one request at a time, and records each status, time to first byte and served version, plus the containers' start times before and after. The summary gives the longest stretch without a 200 and the versions seen. It changes nothing on wizard. The first sample of a deploy without migrations sets the baseline; the plan proposes under 5 seconds as the goal.

### Deploying from anywhere

Away from home, `ssh wizard` (and so the deploy script, rsync and the feedback commands) goes through the Cloudflare Tunnel. The Mac's `~/.ssh/config` has, above `Host wizard`:

```
Match host wizard exec "! /usr/bin/nc -z -G 2 <wizard's LAN address> 22 >/dev/null 2>&1"
    HostName <ssh hostname on the tunnel>
    HostKeyAlias <wizard's LAN address>
    ProxyCommand /opt/homebrew/bin/cloudflared access ssh --hostname %h
```

It needs `brew install cloudflared`, and in the Zero Trust dashboard a public hostname on the "wizard" tunnel, `<ssh hostname on the tunnel>` → `ssh://172.30.0.1:22` (sshd on the `tunnel` network's gateway; ufw allows it), with a self-hosted Access application on that hostname allowing only Antoan's email. The first connection, and the first after the Access session ends, opens a browser to sign in. Login is still by SSH key.

Migrations run automatically when the app starts.

Deleted food entries, sets and exercises are kept for 24 hours so Undo can bring them back, then purged. Rolling back the `AddSoftDelete` migration drops that column, so anything deleted in the last 24 hours would reappear; restore from the pre-deploy dump instead.

### Feedback

You can reload Check foods or return with Back to continue editing a recent food review in the same tab. Once saved, reopening the review takes you to the food log, where you can edit the entries.

Every account can report problems and ideas in Settings → Send feedback (with a screenshot), rate how typed food was read on the Check foods screen, and use the "Report" links on barcode results, weight suggestions, the maintenance estimate and AI food entries. Errors in the app and the browser are recorded on their own. Everything stays in the database; the CLI reads it and answers it (SPEC section 11):

```sh
ssh wizard docker exec plate-app dotnet Plate.Web.dll feedback digest       # what came in this week, with each account's activity and AI cost
ssh wizard docker exec plate-app dotnet Plate.Web.dll feedback show 12
ssh wizard docker exec plate-app dotnet Plate.Web.dll feedback status 12 fixed --reply "Fixed, thanks." --version abc1234
```

`feedback help` lists every command. In the coding agent, `/feedback` runs the same commands and the triage workflow in `docs/feedback-triage.md`. Replies show under Settings → Your feedback with a badge, and `feedback changelog add` puts a What's new note on every user's Today page.

For a Telegram message on each new report and each new kind of error, set `TELEGRAM_BOT_TOKEN` and `TELEGRAM_CHAT_ID` in `docker/.env` (the backup script's bot works) and restart the app. Only a report's number, the reporter's name, kind, area and first 80 characters are sent.

The feedback CLI starts a second .NET process in the app container. If `docker stats plate-app` shows the 256 MB limit being approached while it runs, use `docker compose -f docker/docker-compose.yml run --rm --no-deps app feedback ...` from `~/docker/plate` instead.

### Admin

The developer's account has an admin area at `/admin` (Settings → Admin): reports with status and reply, parse runs, errors, What's new, the OpenRouter key and AI models, the parse evaluation, invites, users and an audit log (SPEC section 12). Other accounts see nothing about the AI models in Settings and get a 404 there. Getting in takes three things:

1. The `Dev` role, given only over SSH:

   ```sh
   ssh wizard docker exec plate-app dotnet Plate.Web.dll role grant dev --email <your account email>
   ssh wizard docker exec plate-app dotnet Plate.Web.dll role list
   ```

   It shows up in a signed-in session within 5 minutes, or at the next sign-in. `role revoke dev --email ...` closes the area at once.

2. Cloudflare Access in front of `/admin`. In the Zero Trust dashboard: Access → Applications → Add an application → Self-hosted. Name it "Plate admin", with the public hostname `plate.peychev.uk` and the path `admin`. Set the session duration to 24 hours. Add a policy "Developer" with the action Allow, and include Emails → `<your account email>`. Use the One-time PIN login method. Save it, then open the application's Overview and copy the Application Audience (AUD) tag. The team domain is under Settings → Custom Pages (`<team>.cloudflareaccess.com`). Put both in `~/docker/plate/docker/.env`:

   ```sh
   CF_ACCESS_TEAM_DOMAIN=<team>.cloudflareaccess.com
   CF_ACCESS_AUD=<the AUD tag>
   ```

   Then restart with `docker compose -f docker/docker-compose.yml up -d`. Until both are set, the admin area stays off in production (a warning at startup says so). The app checks the token Access adds to each request, so the area can't be reached around Access. Afterwards, `curl -sI https://plate.peychev.uk/admin` should be answered by Cloudflare (a redirect to its login), not by the app.

3. A passkey unlock with Face ID on `/admin/unlock`, using a passkey of this account (Settings → Passkeys). It lasts 30 minutes after the last admin page, and 4 hours at most. Lock now, log out and Log out everywhere end it. Each unlock sends a Telegram message.

If the Access login doesn't hand back to the installed home-screen app, open `https://plate.peychev.uk/admin` in Safari instead.

The same things work over SSH:

```sh
ssh wizard docker exec plate-app dotnet Plate.Web.dll ai show
ssh wizard docker exec plate-app dotnet Plate.Web.dll ai set-model matcher off
ssh wizard docker exec plate-app dotnet Plate.Web.dll ai set-model onboarding off   # setup form only; unset, the chat uses the parser's model
ssh -t wizard docker exec -it plate-app dotnet Plate.Web.dll ai set-key     # asks for the key without echoing it
ssh wizard docker exec plate-app dotnet Plate.Web.dll admin audit --since 30d
```

`ai set-key` reads the key from standard input and never from an argument, so it stays out of shell history and the process list.

### Label photos

On the Manual tab, after a barcode Open Food Facts doesn't know, and from "Photograph a label" on the Scan tab, a photo of a nutrition table fills in the manual food form (SPEC 6.6). A separate vision model reads it, `google/gemini-3.5-flash-lite` unless another is picked under AI → Label reader; Off hides the feature for every account. Nothing is saved until the form is. A label that gives values per 100 ml makes the food an ml food: its amounts are entered and shown in ml from then on (the form's "Per 100 g / Per 100 ml" choice can be changed before saving). Photos are kept 30 days so misreads can be checked under Admin → Labels or with `feedback labels --edited`, and the scans 180 days.

```sh
ssh wizard docker exec plate-app dotnet Plate.Web.dll ai set-model label google/gemini-3.5-flash-lite
ssh wizard docker exec plate-app dotnet Plate.Web.dll ai test label   # reads a bundled sample label, expects 250 kcal
```

`tools/labels/make-sample.sh` re-renders that sample and the browser tests' label photos.

### Updating base images

The Dockerfile pins the .NET images by digest, so a rebuild never pulls a different image by surprise. About once a month, look up the current digests and replace the `@sha256:...` parts. Postgres is not pinned: the compose file uses the floating `postgres:18` tag, and wizard's nightly `~/bin/container-update` pulls its patch releases.

```sh
docker buildx imagetools inspect mcr.microsoft.com/dotnet/sdk:10.0 --format '{{json .Manifest.Digest}}'
docker buildx imagetools inspect mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra --format '{{json .Manifest.Digest}}'
```

The build also fails when a NuGet package has a known high or critical advisory; update that package when it does.

## Installing on iPhone

Open `https://plate.peychev.uk` in Safari, sign in, then Share → Add to Home Screen. Open it from the home screen icon from then on; the login lasts 30 days and renews with use.

Settings → Export all data downloads a ZIP of CSV files readable in Excel or Numbers. It includes private foods, saved meals with their ingredients, recipes with theirs, exercise notes and bar settings, and profile preferences. Saved meal and food IDs link the files.

Food forms carry a new request ID each time they are rendered. Resending the same form after a lost response returns its first successful result without adding food twice. Check the log after an uncertain-save warning; open a new form to add another entry. Food-request receipts are included in the export and removed with the account.
