---
name: feedback
description: Read and triage the Plate feedback stored on wizard (reports, parse ratings and corrections, browser and server errors), then fix and answer them. Use when the user says /feedback, asks what feedback or bugs have come in, asks to look at a report by number, or asks to work through reported problems.
---

# Feedback triage

Users report problems and ideas from the app (Settings → Send feedback, thumbs on Check foods, "Report this" links), and errors are recorded automatically. Everything lives in the production database on wizard and is read with the app's own CLI over SSH (SPEC section 11). Antoan also has an admin area at `/admin` on his phone (SPEC section 12) that does the same; coding agents use the CLI. There is no HTTP API.

## Running the CLI

```sh
ssh wizard docker exec plate-app dotnet Plate.Web.dll feedback <command>
```

Logs go to stderr, so stdout can be piped. Add `--json` to any command for JSON. Commands:

- `digest [--since 7d|2026-10-01]`: new reports, errors and parse figures. Start here.
- `list [--status new|open|all|Fixed,...] [--since 7d] [--area TextParsing] [--ratings]`: open reports by default; `--ratings` adds bare thumbs up.
- `show <id>`: the full report: message, context (page, app version, device, recent browser errors), the snapshot of what it was about, the linked parse run with candidates and what was saved, the linked error with its stack.
- `attachment <id> > file`: the screenshot as raw bytes.
- `status <id> <New|Seen|Planned|Fixed|WontFix|NeedsInfo> [--reply "..."] [--version <sha>]`: the reporter sees the status and reply in Settings → Your feedback, with an unread badge.
- `parses [--since 30d] [--flagged] [--export-eval]`: parse runs; `--flagged` keeps only corrected or rated-down ones; `--export-eval` prints regression cases as JSONL.
- `eval [--since 365d] [--flagged] [--limit 20] [--parser <model>] [--matcher off|jev] [--no-history]`: re-runs the live parser and matcher on confirmed parses, with each account's usual foods and last amounts as they were at the parse (`--no-history` turns that off; `--stored-parse` reuses the parse from the time, to compare matchers on the same items), skips runs the user typed again soon after,, and scores agreement with the saved log: the food each item was saved as (not the one suggested, another row of the same food counts), grams, and each meal's kcal and macros against the saved values. Entries logged within 30 minutes after the confirm back up new items ("logged after") instead of counting them as extra. It spends OpenRouter credit; ask before running it with a large `--limit`. `eval --help` prints the usage and an unknown option is refused, so neither starts a run. A case that doesn't fully agree lists what the user saved beside what the parser and matcher give now.
- `matchbench --models <id,id,...> [--repeat 3] [--timeout 10] < tools/matchbench/cases.jsonl`: compares decision models on the labelled matcher benchmark (pipe the file in with `docker exec -i`). Decision calls only, a fraction of a cent each.
- `pilot [--file <csv>|-] [--account <email>] [--check]`: scores the weighed-meal pilot against what Plate gives for each meal's text (`reports/weighed-meal-pilot.md`). Pipe the file in with `docker exec -i ... feedback pilot < file.csv`. One parse of credit per meal; run `--check` first.
- `labels [--since 30d] [--edited] [--failed]`: label photo reads (SPEC 6.6); `--edited` keeps scans saved with a value changed, which is where misreads show.
- `label <id>`: what the label model read, the values the form offered and what was saved. `label-photo <id> [n] > file.jpg` gives the nth photo (1 by default; a read can have up to three) while it is kept (30 days); each download is audited.
- `errors [--since 7d]`: error groups with counts, versions and stacks.
- `changelog add --en "..." --bg "..." [--feedback <id>] [--version <sha>]`: a What's new line every user sees once on Today. `changelog list` and `changelog remove <id>` fix mistakes.

Locally against the dev database, use `dotnet run --project src/Plate.Web -- feedback <command>`.

The CLI runs a second .NET process inside the app container, which has a 256 MB limit. If `docker stats plate-app` shows it near the limit while a command runs, use a one-off container instead: `ssh wizard "cd ~/docker/plate && docker compose -f docker/docker-compose.yml run --rm --no-deps app feedback <command>"`.

## Screenshots

Save it to the session's scratchpad directory and look at it with Read (`show` gives the image type):

```sh
ssh wizard docker exec plate-app dotnet Plate.Web.dll feedback attachment 12 > <scratchpad>/feedback-12.jpg
```

## Workflow

1. `digest`, then `show` each new report and each new error group. Mark what you have read as `Seen` so the next digest is shorter.
2. Tell the user what came in and what you propose, grouped by fix rather than by report. Several reports are often one bug.
3. Reproduce before fixing. Snapshots are built for this:
   - a parse report has the typed text, the parser's items and the ranking's top candidates: write a `FoodMatchingTests` case, or add the export line to `tests/Plate.Web.Tests/Fixtures/` (see below);
   - a progression report has the rules and the last two sessions' sets: write a `ProgressionTests` case in Core;
   - a maintenance report has the inputs to `AdaptiveMaintenance.Calculate`: write an `AdaptiveTargetsTests` case;
   - an error has the stack, page and the version (commit) it happened on: `git show <version>` for the code at the time.
4. Fix it following AGENTS.md (one feature at a time, tests first for Core, build and test before calling it done).
5. After the fix is deployed (with `APP_VERSION` set, see README), set `status <id> Fixed --version <sha> --reply "..."`. Write the reply in the reporter's language (the context's `language` field), plainly, one or two sentences. Add a `changelog add` line in both languages only when the fix is a major new user-facing feature (see AGENTS.md), not for ordinary bug fixes; it goes to every user, so never write "both of you".
6. For ideas, set `Planned` or `WontFix` with a short reason once the user decides; do not decide for them.

## Parse regression cases

`parses --export-eval --flagged` gives one JSON line per parse with a verdict per item: `accepted` (saved as suggested), `wrong-food` (rated down as the wrong food), `corrected` or `removed`, the suggested food, and under `saved` the row as saved (its food may differ from the suggestion). `ParseRegressionTests` replays files matching `tests/Plate.Web.Tests/Fixtures/parse-corrections*.jsonl` against the ranking: an item must get the seed food it was saved as (older lines without `saved` record that only for accepted items) and wrong-food items must not get the rejected food again. Failures not yet fixed go in `parse-corrections.known-failures.txt` (`GYM_WRITE_KNOWN=1` rewrites it).

The lines contain what the users typed. Show the user the exact lines and get their OK before committing any of them.

## Privacy

Reports hold users' food, weight and health data and their own words. Keep them on this machine and in this session. Never paste them into web searches, external services or public places, and never commit them without the user's OK. Telegram notices (when configured) are sent by the app itself and carry only a report's first line.

Every status change and changelog edit, from the CLI or the admin area, lands in the admin audit log (`dotnet Plate.Web.dll admin audit`).
