# GymTracker

Self-hosted training and nutrition tracker for two users, running on the home server "wizard" and used from iPhones as an installed web app at `https://gym.peychev.uk`. See `SPEC.md` for the full specification and build phases.

Phase 1 (workout logging) is built: login, templates, sessions with set logging, history, and bodyweight on the Today page.

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

Add a migration: `dotnet ef migrations add <Name> --project src/GymTracker.Web --output-dir Data/Migrations`.

## Deploying on wizard

The repository is checked out at `~/docker/gymtracker`. The database lives in `~/docker/gymtracker/db` and the login cookie keys in `~/docker/gymtracker/keys`; both are git-ignored.

### First time

The repository is private (`github.com/AntoanBG3/gymtracker`). wizard reads it with a read-only deploy key:

```sh
ssh-keygen -t ed25519 -f ~/.ssh/gymtracker_deploy -N "" -C "wizard gymtracker deploy"
cat >> ~/.ssh/config <<'CFG'
Host github-gymtracker
    HostName github.com
    User git
    IdentityFile ~/.ssh/gymtracker_deploy
    IdentitiesOnly yes
CFG
cat ~/.ssh/gymtracker_deploy.pub   # add under the repo's Settings > Deploy keys, read-only
```

Then:

```sh
cd ~/docker
git clone github-gymtracker:AntoanBG3/gymtracker.git gymtracker
cd gymtracker
mkdir keys                                   # must exist before the first start so it is owned by antoan
cp docker/.env.example docker/.env
openssl rand -base64 32 | tr -d '/+='        # paste as POSTGRES_PASSWORD in docker/.env
docker compose -f docker/docker-compose.yml up -d --build
docker builder prune -f                      # the SDK build cache sits on the boot SSD
```

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

### Updating

```sh
cd ~/docker/gymtracker && git pull
docker compose -f docker/docker-compose.yml up -d --build && docker builder prune -f
```

Migrations run automatically when the app starts.

## Installing on iPhone

Open `https://gym.peychev.uk` in Safari, sign in, then Share → Add to Home Screen. Open it from the home screen icon from then on; the login lasts 30 days and renews with use.
