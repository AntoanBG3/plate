# AGENTS.md

Self-hosted gym and nutrition tracker ("Plate", the same Latin name in Bulgarian) used by Antoan, his father and friends he invites. The full specification is in `SPEC.md`; read it before starting any task and follow its build phases in order.

## Stack
.NET 10, ASP.NET Core MVC + Razor + htmx 2.0.x, EF Core + PostgreSQL 18 (Npgsql), ASP.NET Core Identity, OpenRouter API, USDA FoodData Central subset, Open Food Facts API v3, xUnit + FluentAssertions + Testcontainers, Playwright for browser tests, docker-compose. Deployed on the home server "wizard" at `~/docker/plate`, public at `https://plate.peychev.uk` through its Cloudflare Tunnel.

Projects: `src/Plate.Core` (domain and pure calculations), `src/Plate.Web` (MVC app, EF, CLI commands), `tests/Plate.Core.Tests`, `tests/Plate.Web.Tests` (integration, real Postgres), `tests/Plate.E2E.Tests` (Playwright, Chromium and WebKit), `tests/js` (Node tests for the offline queue and the deploy gate).

## Commands
- All checks, as CI and `tools/deploy.sh` run them: `bash tools/check.sh` (JS tests, restore, Release build, every .NET test).
- Build: `dotnet build`
- Test: `dotnet test`; one class: `dotnet test tests/Plate.Web.Tests --filter "FullyQualifiedName~FeedbackTests"`
- JS tests: `node --test tests/js/*.test.cjs` (Node 24 or newer)
- Tools: `dotnet tool restore` (dotnet-ef is a local tool)
- Add migration: `dotnet ef migrations add <Name> --project src/Plate.Web --output-dir Data/Migrations`
- Run locally: `docker compose -f docker/docker-compose.dev.yml up -d db`, then `dotnet run --project src/Plate.Web`
- Full stack (on wizard): `docker compose -f docker/docker-compose.yml up -d --build`
- Deploy: `tools/deploy.sh` from a clean `main` (it dumps the database, copies the checkout and waits for the new version). `tools/deploy.sh --dry-run` shows what would change.
- Feedback from the app (reports, parse ratings, errors): `ssh wizard docker exec plate-app dotnet Plate.Web.dll feedback digest`; `.agents/skills/feedback/SKILL.md` has the commands and the triage workflow.
- Admin over SSH (SPEC 12): `role grant|revoke|list`, `ai show|set-model|set-key|clear-key|test`, `admin audit` as further `dotnet Plate.Web.dll` commands. The web admin area at `/admin` is for Antoan's phone; agents use the CLI.

Test environment:
- Docker Desktop must be running. Web and E2E tests start their own `postgres:18` through Testcontainers; without Docker every one fails in about 1 ms, which is an environment problem, not a code one. `tools/check.sh` stops early with a message.
- If pulls fail with "authentication required", run `docker pull testcontainers/ryuk:0.14.0` once.
- Each Web and E2E test class gets its own database copied from a template, and classes run in parallel. New classes use `IClassFixture<PostgresFixture>` (Web) or `IClassFixture<E2EFixture>` (E2E), not a shared collection.
- Cadence: per change, run the affected test classes plus all Core and Web tests (under a minute). Run the full E2E suite (about two minutes) at the end of a workstream and before merging.
- A test script must exit non-zero on failure. Don't pipe test output through `grep` in a way that hides the exit code.

## Rules
- `Plate.Core` has no references to EF Core, ASP.NET Core, or HttpClient. All calculations live there as pure functions.
- Every calculation in SPEC.md section 5 gets unit tests covering the listed edge cases before it is wired into the UI.
- Every user-owned query is scoped to the current user. Never trust IDs from the client without an ownership check. New user-owned tables get a query filter on `UserId`, a cascade from `AspNetUsers`, an entry in `DataExport`, and a line in the account-deletion test.
- No business logic in controllers or views.
- No secrets in the repository. Configuration comes from environment variables; keep `.env.example` up to date. Never print the Telegram or OpenRouter credentials found on wizard.
- Weights in kg, energy in kcal. "Day" values are `DateOnly` in the user's time zone, never UTC dates.
- Use async EF calls with `CancellationToken` throughout.
- Do not add NuGet packages or front-end libraries beyond those in SPEC.md without a clear reason stated in the commit message.
- Mobile-first: test layouts at 390 px width. Touch targets at least 44 px.
- No CDNs. Front-end libraries are pinned copies in `wwwroot/lib`, precached by the service worker. A new script in `_Layout.cshtml` also goes in `sw.js`'s `PRECACHE`, with the cache name bumped.
- All persistent data uses bind mounts under the stack folder, never Docker named volumes.
- Anything on wizard that needs sudo (the backup script, system config) is written up as an exact command for Antoan to run.

## Text and translations
- Every user-facing string goes through the localizer (`L["..."]` in views and code, `t("...")` in scripts, listed in `Features/Localization/ScriptText.cs`). After adding or removing strings, regenerate the catalogue with `GYM_WRITE_KEYS=1 dotnet test tests/Plate.Web.Tests --filter CatalogueTests`. Then add the Bulgarian to `Resources/strings.bg.json`; `BulgarianCatalogueTests` fails on a missing or orphaned translation.
- Bulgarian uses the informal "ти" and the app's own screen and button names as they already appear in `strings.bg.json`. Edit that file in place: don't re-sort or reformat it, since a whole-file reorder buries the real change.
- Text never assumes there are only two users. Shared things are saved "for everyone" or for "other users".
- Numbers are written with a dot in both languages.

## Changes that need extra care
These came up in reviews; check them before calling a change done.
- **Client storage keys.** Pending sets, feedback, drafts and rest timers live in `localStorage` (`wwwroot/js/queue.js`, scoped per account). Renaming or versioning a key must migrate what is under the old key, or give the user a way to deal with it. Never leave pending work where nothing reads it. Write a `tests/js` test that starts with the old key populated.
- **Stored JSON.** A jsonb column or any persisted payload has fixed property names of its own, independent of the C# records it maps to, plus a test that pins a literal JSON sample (see `TargetDayValues` and `TargetDayValuesTests`). Otherwise a rename silently reads old rows back as zeros.
- **History.** Past days, charts and summaries must show the values in force then, not values recomputed from today's profile. When a change alters what existing rows display, compare the same past day before and after the change.
- **GET requests.** Don't write on every page load. Read first and write only when something changed.
- **Migrations.** Look at the generated migration and the model snapshot; `TargetHistoryModelTests` shows how to assert there are no pending model changes.
- **Regression tests.** A new test must fail without the fix. Check that once, by reverting the fix locally, before relying on the test.

## Workflow
- Work one phase at a time. Within a phase, finish and test one feature before starting the next.
- Work on a branch; `main` is what gets deployed.
- Commit as you go: small, focused commits with descriptive messages (what changed and why, no filler), one concern per commit. Don't leave a branch's work uncommitted for review.
- Run `dotnet build` and `dotnet test` (or `bash tools/check.sh`) before saying a task is done, and report the actual results. If tests could not run (no Docker, no network), say so instead of reporting a pass.
- If the spec is ambiguous or seems wrong, stop and ask instead of guessing. When a change alters behaviour described in SPEC.md, update SPEC.md (and README.md where it explains the behaviour) in the same commit.
- After every production deploy that users can notice, add a What's new entry once the deploy is verified: `ssh wizard docker exec plate-app dotnet Plate.Web.dll feedback changelog add --en "..." --bg "..." --version <sha>`, one entry per user-visible change. One or two plain sentences on what the user can now do and where to find it, no internal detail; Bulgarian uses the informal "ти" and the app's own screen and button names from `strings.bg.json`. Match the tone of `feedback changelog list`. Skip only deploys users can't see (admin-only, CLI, fixes to logs) and say so in the report.
- If you can't reach wizard over SSH, write the exact commands for Antoan to run instead of skipping the step.

## Communication
Antoan prefers concise answers in plain prose: no emojis, few headers, no filler or praise, and not too many em dashes. Be direct about problems in his approach or in existing code. When unsure, say so and verify (read the code, run it) instead of guessing.
