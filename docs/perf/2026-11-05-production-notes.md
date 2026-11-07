# Production and reliability evidence, 2026-11-05

Audit-only working evidence for the main report. No production writes, restarts, sign-ins, load tests, app CLI calls or model calls were made. Personal data stayed on wizard; SQL returned counts, sizes and percentiles only. The collector reduces retained application logs on wizard before returning aggregates.

## Environment and reproduction

Baseline source and deployed `data-version`: `462ef118827b00839941eab87cceb2a106c93065` / `462ef11`. Production resources were sampled at 08:42:44–08:42:54 UTC (10:42 Sofia), on wizard's x86-64 four-core shared host. PostgreSQL reports 18.6; app limit is 256 MiB / 1 CPU, DB limit 256 MiB with no CPU cap. Actual DB settings are `shared_buffers=32MB`, `max_connections=15`, `jit=off`; nine PostgreSQL connections/process entries were visible in `pg_stat_activity`, including background processes and the inspection connection.

```bash
python3 tools/perf/production-probe.py > tools/perf/results/2026-11-05-production.json
python3 tools/perf/public-probe.py > tools/perf/results/2026-11-05-public.json
python3 tools/perf/restore-drill-probe.py > tools/perf/results/2026-11-05-restore-drill.json
```

`production.sql` sets `default_transaction_read_only=on` and a 30-second statement timeout through `PGOPTIONS`. The Python wrapper uses one SSH connection and five sequential resource samples. On this Mac SSH needed sandbox escalation for keychain access; a later separate connection also timed out during the tunnel banner exchange. The successful final capture is complete. Those transport failures are not evidence of an app outage.

## Measured production baseline

| Metric | n | Median | Min–max / p95 | Interpretation |
|---|---:|---:|---:|---|
| App working memory (`docker stats`) | 5 | 145.2 MiB | 145.2–145.2 MiB | 56.7% of limit; observation during normal activity, not a forced idle/GC measurement |
| DB working memory | 5 | 68.98 MiB | 68.98–69.51 MiB | 26.9–27.2% of limit |
| App CPU | 5 | 1.31% | 1.23–1.42% | Docker sampling window; shared host |
| DB CPU | 5 | 0% | 0–0% | Same windows |
| App/DB restart counters | 1 inspect each | 0 / 0 | OOMKilled=false / false | Current containers only; not lifetime reliability |
| Root disk available | 1 | 176 GiB | 15% used | No present disk-capacity crisis |
| HDD available | 1 | 368 GiB | 16% used | Backup contents/health not inspected |
| Host RAM available | 1 | 4,634 MiB | 7,276 MiB total; 0 MiB swap used | Not memory constrained in this snapshot |
| Database size | 1 | 36,296,383 bytes | — | Actual data is sparse |
| Parser latency, records created in last 30 days | 12 | 3,267 ms | p95 5,236.65 ms | 0 stored parser errors; includes parser path's setup/catalog work |
| Matcher latency, same records | 12 | 1,427.5 ms | p95 3,244.65 ms | Recorded matching time, not isolated Jev time |
| Label-read latency, last 30 days | 6 | 4,262 ms | p95 5,217 ms | Includes one failed read; 1/6 is a sample count, not a reliable future error-rate estimate |
| Error fingerprints active in last 30 days | 1 aggregate | 0 | 2 fingerprints stored overall | Exact time-window occurrence counts are not retained |

The app container began at 2026-11-04 21:45:48 UTC; DB at 17:43:30 UTC. Neither has restarted since its current creation. Asking `docker logs --since 720h` cannot recover logs removed with previous containers: the current app log contains no successful food-namer or Jev call. Their latency, tokens and cost remain **unmeasured**, not zero-latency. Successful and failed AI calls are not load-tested against paid services.

| Actual table | Exact rows | Table bytes | Index bytes |
|---|---:|---:|---:|
| AspNetUsers / Profiles | 5 / 5 | 16,384 / 16,384 | 49,152 / 16,384 |
| FoodItems | 11,804 | 4,603,904 | 13,049,856 |
| FoodLog | 38 | 16,384 | 81,920 |
| Bodyweights | 5 | 16,384 | 32,768 |
| Sessions / Sets | 1 / 0 | 16,384 / 8,192 | 65,536 / 49,152 |
| ParseRuns | 16 | 90,112 | 49,152 |
| LabelScans / LabelScanPhotos | 6 / 8 | 16,384 / 7,659,520 | 65,536 / 32,768 |
| NutritionTargetDays | 34 | 57,344 | 16,384 |

Anonymous account counts in capture order: food entries 7/10/21/0/0, weighs 1/1/1/1/1, sessions 0/0/1/0/0, sets all zero, parses 0/2/14/0/0, label scans 0/1/5/0/0. The local five-account/two-year daily-use dataset should preserve account count and seeded catalog size while deliberately projecting substantial history. It must not be described as extrapolated from observed workout use: production has no logged sets yet.

## Anonymous public round trips

Five fresh curl connections per URL, sequential, 08:32 UTC, from the Mac through the public tunnel. These include DNS/TCP/TLS, do not reproduce iPhone mobile data, and cannot establish signed-in page performance.

| URL | n | Median TTFB | Min–max TTFB | Wire/body evidence |
|---|---:|---:|---:|---|
| `/account/login` | 5 | 168.937 ms | 145.619–208.632 ms | gzip ~5.1 KiB; `no-cache, no-store`; CF DYNAMIC |
| `/sw.js` | 5 | 158.242 ms | 144.203–230.709 ms | gzip 1,302 bytes; `max-age=14400`; MISS then REVALIDATED |
| `/lib/chartjs/chart.umd.min.js` | 5 | 156.149 ms | 152.590–270.659 ms | gzip ~70.6 kB; `max-age=14400`; MISS then REVALIDATED |

Login `Server-Timing` app median 1.4 ms, range 1.2–5.5 ms; all five have 0 DB queries. The ~169 ms median round trip still dominates this anonymous server render. The previous 2026-10-03 production range was 85–330 ms across main pages; today's login range falls within it, but route and network differ, so this is not a controlled regression comparison. All 15 requests returned 200.

The service-worker response's four-hour cache policy deserves a dashboard check, but **does not prove a stale update**. Browser worker-update fetches and the edge's revalidation behavior matter; do not change Cloudflare based on this header alone.

## Reliability findings and limits

1. **Verified false success in nightly restore drill.** `tools/restore-drill.sh:25-33` compares live/restored table-count manifests, then accepts every mismatch in nightly mode as expected new activity. The scratch Docker mock reproduces a restored manifest with the entire FoodLog table missing (`Profiles=5` vs `FoodLog=38 Profiles=5`): the unchanged script exits 0 and prints Restore OK. This establishes a validator defect, not an actually broken nightly backup. `pg_restore --exit-on-error` still catches invalid archives. Proposed independent change: require identical table sets and required invariants, compare to a manifest captured with the backup for historical row counts, and return nonzero on a missing table. Add shell tests with valid newer live rows, missing tables and nonzero restore failures. Existing backups were not restored because that creates containers/writes state on wizard.

2. **Verified observability gap: 30-day error occurrence totals are unavailable.** `ErrorRecorder.cs:37-46` increments one lifetime counter per fingerprint; `FeedbackAdmin.cs:227-241` filters by LastSeenAt. Summing Count for recent fingerprints overstates a recent-window count if older occurrences exist; simultaneous updates can also overwrite increments. SQL deliberately labels the value cumulative. The current active-window aggregate is empty. If incident-rate budgets are wanted, add bounded daily counters with atomic upserts, not a new full stack.

3. **Verified retained-log coverage is shorter than the requested AI window.** Current app was recreated yesterday, uses `json-file` with empty container log config, and has no retained namer/Jev samples. Container options alone do not prove unlimited daemon defaults. Add persisted, bounded latency aggregates for the unrecorded naming stage if optimizing it becomes a priority. `ParseRunService.cs:30-44` records parser and matcher separately, then awaits naming outside both stored times, so neither stored percentile represents total food-submit latency. Retained success-call logging is insufficient after container replacement.

4. **Storage and recovery exposure, not measured failure.** Actual bind mounts place keys and the DB under `~/docker/gymtracker`, on the host root filesystem. SPEC 10 identifies the boot SSD as failing; its current SMART state and backup/PC-copy freshness were not read. Free space is ample. `tools/deploy.sh:31-34` adds a home-directory predeploy dump every deploy without a retention step; this is an unbounded-growth mechanism, not evidence of imminent disk exhaustion. `restore-drill.sh:19-23` makes a temporary PostgreSQL container with no bind mount and its cleanup at line 8 omits `-v`; verify/remove temporary anonymous volumes in a separately authorized recovery-tool change. The host reports five unused local volumes totaling only 52.64 MB today; their origins were not inspected.

5. **Single-instance deploy gap is unmeasured.** `tools/deploy.sh:39-49` replaces the only app container and polls for a version; `Program.cs:370-377` runs migrations, seed upgrades and fixups before listening. The version check neither measures unavailable time nor checks an authenticated journey. Measure a local replacement and a future Antoan-observed real deploy before choosing blue/green: no production restart was authorized here. A clean predeploy dump and test gate already exist.

6. **Dependency timeout boundaries need targeted fault tests.** Parser, onboarding, matcher and namer have explicit fallbacks and bounded attempts; OFF maps 404/429/unavailability without breaking the page (`OpenFoodFactsClient.cs:58-84`, 10-second HttpClient timeout at `Program.cs:182-185`), with successful barcode cache reads first (`FoodItemService.cs:124-137`). Model-catalog fetch is awaited before the attempt timer (`OpenRouterFoodParser.cs:99-103`, `OpenRouterLabelReader.cs:86-90`); a cold/expired catalog can add up to its 10-second timeout (`Program.cs:126-129`, `OpenRouterModels.cs:39-61`). The catalog has a last-good fallback but does not refresh the fresh-cache TTL after a failure, so sequential calls can repeatedly pay the outage delay. This is code-supported worst-case behavior; no paid live outage was induced. Weekly code uses 20-second attempts while SPEC 6.5 references setup/parser's 15; label code retries HttpRequestException though SPEC 6.6 says 5xx only. Flag these as decisions to reconcile with Antoan before implementing any behavioral change.

Do not invoke the apparently read-only app feedback CLI during this audit: `Program.cs:370-387` runs migration/seeding/fixups before dispatching it. The SQL path avoids that write possibility.

## Exact remaining checks for Antoan

These are procedures to run later, not actions performed by the audit.

- **SSD / sudo:** on wizard, run `sudo smartctl -x /dev/sda` (/boot and /boot/efi are on sda; root-LVM backing is unverified, so identify the physical device locally first). Check failed/self-test and media-error evidence. Run `sudo bash -n /usr/local/bin/homeserver-backup`; inspect the backup script locally for its `pg_dump`, `pg_restore --list`, `--exclude=gymtracker/db/`, 14-day retention and failure alert. Do not paste the script: it may contain Telegram credentials.
- **Backup restoration / approved writes:** after authorizing a throwaway restore, `cd ~/docker/gymtracker && tools/restore-drill.sh nightly`. Until the validator is improved, inspect schema/table manifests manually and treat its mismatch message as inconclusive. Confirm the HDD snapshot and PC pull timestamps locally. The drill creates a container and writes a temporary archive, so the audit did not run it.
- **Cloudflare dashboard:** inspect Cache Rules and browser/origin cache TTL for `/sw.js`, and verify it revalidates after a deploy. Record the active response/body version on the phone before changing rules. No dashboard change is established as necessary by the current samples.
- **iPhone, Safari Web Inspector:** connect the iPhone to the Mac, enable Settings → Safari → Advanced → Web Inspector, use Safari's Develop menu to select the installed Repa page. At 390 px or the device's actual viewport, capture Network and Timelines for five repetitions each of Today→Food→Train→Progress, text parse/confirm, cached barcode, search and repeated food. Record from tap to visible content, resource bytes/requests, Server-Timing and long tasks. Report first-open and warmed navigation separately. Mobile data must be the actual network for mobile measurements; do not label desktop throttling as an iPhone result.
- **Offline/device update:** first sync all pending work. In a disposable workout, log a set, switch airplane mode on, log one more, close/reopen the PWA and restore signal. Verify the same ClientId reaches one stored set and pending work clears. Keep the PWA open with a queued set across the next normal deploy and observe update/reload; never clear Safari website data while a queue is pending. Also test camera/label upload cancel and resume. These manual checks do not call paid models unless Antoan chooses a real parse/label read.
- **Deploy interruption:** on a future authorized deploy, make one sequential anonymous login GET per second from a separate terminal, record statuses/TTFB/version before, during and after replacement, and compare the new container's start time to first successful response. The audit did not restart production to obtain this window.
