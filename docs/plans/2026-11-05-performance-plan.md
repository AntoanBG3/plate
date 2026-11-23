# Plate performance and reliability plan — 2026-11-05

Review plan only. Baseline `462ef118827b00839941eab87cceb2a106c93065`, branch `perf-audit`; no fixes, push, merge or deploy accompany this audit. Each accepted item gets its own branch, focused commit, relevant regression tests and independent deployment/rollback. Findings F1–F12 refer to [the audit](../perf/2026-11-05-audit.md); raw evidence is in `tools/perf/results/` and source evidence in the three review notes under `docs/perf/`.

The priority is reliable set logging and avoiding duplicate food or a misleading recovery result, followed by the measured food-search bottleneck. Keep the stack. Two-year history does not justify an analytics rewrite: local warm Today is 6.8 ms HTTP median, Progress 17.3 ms, and cached barcode 2.1 ms. Search is the exception: 89.6 ms median alone, 912.7 ms at five callers; a three-food parse with the fake parser takes 550.1 ms alone and 6,105.3 ms at five callers. Those are native arm64 local measurements, not wizard or iPhone timings.

## Status — 2026-11-23

Everything below this section is the plan as approved in November; this table is where each item stands now.

| Item | Status |
|---|---|
| P1, P2, P3, P4, P6, P7, P11, P14 | Merged and deployed 2026-11-07 (3c2adf6). |
| P5, P8 | Done by the 2026-11-21 project audit fixes: 239b937 (shortlists skipped for unusable matches) and e1e0549 (batched workout history); deployed as 2959a40. |
| P9, P10, P12, P17 | Deployed 2026-11-22 as 6b80fdc. P12 was built narrower than planned, a separate library cache instead of a hash manifest ([measurements](../perf/2026-11-22-precache-reuse.md)). P17's probe measured that deploy at 2 s unavailable. |
| P13 | Built on `changes/p13-dump-retention`: old pre-deploy dumps move to `~/predeploy-dumps-archive` after each deploy. |
| P15 | Open. |
| P16 | Restore proven: `tools/restore-drill.sh nightly --app` brought the app back from the nightly snapshot on 2026-11-22. The SMART checks and moving off the failing SSD remain Antoan's. |
| c5 slowdown | Explained 2026-11-23: a CPU-capped test database, not the app ([write-up](../perf/2026-11-23-c5-slowdown.md)). |

## Approved review corrections — 2026-11-06

P1 is implemented as eb16cc7 on changes/p1-set-send-deadline and remains unmerged.

Execute the remaining approved work in this order:

1. P2: restore validation as planned.
2. P14: its own commit, adding only -v to docker rm -f in restore-drill.sh's EXIT trap. No cleanup redesign or additional scope.
3. P6, first commit: correct the plain-POST failure toast in English and Bulgarian. The save may have succeeded; tell the user to check before retrying.
4. P6, following commit: assign a per-render request ID to every plain-POST food form that adds entries. Resending returns the first result without adding rows. Also make loadScript forget failed loads, and correct ConfirmAsync's misleading already-confirmed comment.
5. P7: accept using c1 timings, EXPLAIN plans, query counts and matched-result equivalence only.
6. P4, then P3.
7. Small P11: start attempt deadlines before body construction; treat the last good catalog as fresh for a few minutes after a failed refresh. Do not broaden into coalescing or background-refresh architecture.

Deferred: P5, P8, P9, P10, P12, P13, P15 and P17. P16 remains an Antoan-led inspection/recovery follow-up; no wizard writes, sudo or deployment are authorized by this correction.

No c5 or ramp value is a budget or acceptance target until the c5 search/parse slowdown has been explained. Historical measurements remain evidence, not acceptance criteria. The order above supersedes the original audit ranking below.

## Ranking and shipping rules

Within each group, rank by user impact × confidence / effort × risk. The ordinal scores below make the judgement visible: impact 1–5; confidence 1–3 in the reproduced mechanism and proposed remedy; effort S=1, M=2, L=4; risk 1–3. They are prioritization aids, not predicted incident rates. VM fault probes establish races and missing bounds; their production frequency is unknown. Five local duplicate-confirm trials establish duplicate insertion; production currently contains only 38 food rows and no sets, so current use cannot estimate future workout failure rates.

Every item must show a failing regression against baseline before its implementation is accepted. Run affected tests, all Core/Web tests and the full E2E suite at workstream completion, then `bash tools/check.sh` before merge. Use the same synthetic volume and resource limits for before/after timing, five sequential repetitions, cold and warm separated. No paid model tests are required: fake HTTP/catalog responses cover failure paths. Do not rename client storage keys, change persisted JSON names, trim historical input or relax ownership to meet a budget.

| Order | Item | Findings | Impact / confidence | Effort / risk | Score | Review / evidence gate |
|---|---|---|---|---|---:|---|
| P1 | Bound stalled set sends | F1 | 5 / 3 | S / 1 | 15 | Ordinary code review |
| P2 | Fail restore validation on missing tables | F8 | 4 / 3 | S / 1 | 12 | Ordinary code review; real wizard drill later needs authorized writes |
| P3 | Stop late camera streams after teardown | F3 | 3 / 3 | S / 1 | 9 | Ordinary code review; iPhone verification |
| P4 | Keep the latest navigation intent | F4 | 3 / 3 | S / 1 | 9 | Ordinary code review |
| P5 | Skip unusable matcher shortlist work | F5 | 3 / 3 | S / 1 | 9 | Ordinary code review |
| P6 | Make one food confirmation save once | F2 | 5 / 3 | M / 2 | 3.75 | Review request-ID and rollback design |
| P7 | Make food-search candidate filtering indexable | F5 | 4 / 3 | M / 2 | 3 | Match-equivalence tests and measured gain |
| P8 | Reuse/batch workout history | F6 | 2 / 3 | M / 1 | 3 | Preserve suggestions and history |
| P9 | Retain total food latency | F10 | 2 / 3 | M / 2 | 1.5 | Review retention/schema and historical compatibility |
| P10 | Count error occurrences by day | F10 | 2 / 3 | M / 2 | 1.5 | Review bounded daily aggregates |
| P11 | Bound model-catalog lookup and refresh | F7 | 3 / 2 | M / 2 | 1.5 | Resolve timeout/retry interpretation below |
| P12 | Reuse unchanged precache assets across deploys | F9 | 2 / 2 | M / 2 | 1 | Browser baseline and a demonstrated transfer/time benefit first |
| P13 | Bound predeploy backup retention | F11 | 2 / 2 | M / 2 | 1 | Dry-run retention inventory; wizard writes require Antoan |
| P14 | Remove drill-owned temporary volumes | F11 | 1 / 2 | S / 2 | 1 | Local fault tests; wizard cleanup requires Antoan |
| P15 | Serialize numbering for distinct simultaneous sets | F12 | 3 / 2 | M / 3 | 1 | Five c5 batches reproduced duplicate numbering; critical write-path tests |
| P16 | Prove recovery and move state off failing storage | F11 | 5 / 3 | L / 3 | 1.25 | Antoan's device/backup evidence and maintenance window |
| P17 | Measure replacement downtime before changing deployment | F11 | 1 / 2 | M / 2 | 0.5 | Future authorized deploy observation; no blue/green commitment |

The execution order in the review corrections controls this workstream. Deferred items remain documented for context, not authorized implementation in this pass. P16 is an Antoan-led inspection; F12's production frequency is unknown and P15 is deferred.

## Quick wins

### P1 — Bound stalled set sends

**Fixes:** F1. `session.js:112-160,422-440` retains its single-flight flag until fetch/body completion; four injected retry ticks plus reconnect still produced one unbounded send and one retained pending set.

**Change and proof:** give each send attempt an explicit timeout covering headers and body, release the send guard in `finally`, preserve the stored ClientId and retry using the existing schedule. Choose the deadline from controlled bad-signal tests, initially evaluate 10–15 seconds rather than silently adopting a browser-dependent timeout. The measurable gain is bounded queue recovery: a blackholed first request cannot block later attempts beyond that deadline plus the next retry trigger. This is a proposed target, not a measured production recovery time.

**Scope / risk / rollback:** S, low risk confined to set transport. A timeout can occur after the server saved; retain ClientId so replay remains idempotent. Revert the send function to roll back, with no queue migration or data removal.

**Tests / extra care / dependencies:** JS tests start with an existing account-scoped v2 pending item, stall before headers and during body reading, trigger online/foreground/interval, then assert exactly one stored server set after replay. Cover 401, owner mismatch, validation errors, denied storage and navigation. Client storage care applies even though keys stay unchanged. Add a SPEC 8 sentence defining attempt bounds. No dependencies; Antoan performs the final iPhone airplane-mode and blackhole checks.

### P2 — Fail restore validation on missing tables

**Fixes:** F8. The unchanged nightly drill returned success when the restored manifest omitted the whole FoodLog table; see `2026-11-05-restore-drill.json`.

**Change and proof:** compare restored/live table sets and required schema invariants independently of row-count drift. A missing table must exit nonzero. Continue accepting newer live rows only when the restored schema is complete. Do not claim count equality to a changing live database proves backup contents. Capturing a backup-time count manifest is a separate later extension if historical row validation is needed.

**Scope / risk / rollback:** S, low risk in a recovery checker. A stricter checker can reject a valid historical schema; document required migration compatibility rather than blanket-success mismatches. Rollback reverts the checker; it never alters the backup/archive.

**Tests / extra care / dependencies:** shell tests for missing and extra tables, complete older row counts, invalid archive and `pg_restore` failure. Assert nonzero exits through all pipelines. No app history/JSON/client-storage change, no SPEC behavior change; README drill guarantees must be accurate. No dependency. Actual throwaway restores on wizard are writes and require Antoan's authorization.

### P3 — Stop late camera streams after teardown

**Fixes:** F3. The controlled async camera probe produced zero stopped tracks, one video start and one polling interval after sheet close and module cleanup.

**Change and proof:** invalidate the current camera acquisition generation on close/cleanup; stop every track in a late returned stream before touching the video/timer. Starting another open must not revive the earlier request. Prove zero live tracks and scanner intervals after a delayed acquisition resolves into a closed page.

**Scope / risk / rollback:** S, low risk confined to scanner lifecycle. Preserve normal camera open and repeat scanning. Revert scanner lifecycle code to roll back; no stored state changes.

**Tests / extra care / dependencies:** delayed `getUserMedia` resolution after close, soft navigation and second open; rejected `video.play`; ordinary scan remains functional. JS regression plus browser lifecycle test, then iPhone camera-indicator check. No extra-care data changes, SPEC change or dependency. Antoan needed only for real-device verification.

### P4 — Keep the latest navigation intent

**Fixes:** F4. Controlled Train→Food requests resolved Food→Train and the earlier response replaced the chosen Food page.

**Change and proof:** assign a generation to navigation GET intents and reject obsolete results before scripts, swap, history updates and error fallback. Optionally abort superseded read requests. Preserve POST completion semantics: aborting a request does not mean the server did not save it. Prove the final page and URL always match the last GET choice under reversed response order.

**Scope / risk / rollback:** S, low/medium risk in shared navigation. Check Back/Forward and error handling as well as normal tabs. One-script revert rolls back; persisted queues stay untouched.

**Tests / extra care / dependencies:** real browser tests for reversed Train/Food responses, Back while loading, slow POST then GET, offline fallback, build-version change and pending set replay during navigation. GET semantics and client-state preservation need care. Document latest-choice behavior in SPEC 2/8 if it is made an explicit contract. No dependency or production permission required.

### P5 — Deferred: Skip unusable matcher shortlist work

**Fixes:** F5. Up to four catalog searches per bilingual parsed item are made before deciding whether the matcher is off or the usual/own match already wins (`FoodMatcher.cs:84-105`; `OpenRouterSettings.cs:174-177`). Local no-key three-food parsing still takes 550.1 ms median, overwhelmingly DB time.

**Change and proof:** expose matcher availability without building its questions; avoid Jev shortlist searches for off/no-key and already-decided items, while retaining the ranking's trace top five as required by SPEC 6.1/11. Reuse equivalent candidate reads only where threshold and ordering semantics stay identical. Count searches per item before/after and require identical confirmation results and trace values. Expect the eliminated command cost to disappear; no fixed total-latency reduction is promised before measurement.

**Scope / risk / rollback:** S, low risk confined to unnecessary local reads. Code-only rollback; no model change, no schema or client migration.

**Tests / extra care / dependencies:** no-key, matcher off/on, usual food, own top match, English/Bulgarian names and mixed usable/unusable questions. Assert query count and chosen food/trace equivalence. Read-path change only; no SPEC update intended, no Antoan dependency. Ships independently of P7.

## Medium changes

### P6 — Make every plain-POST food-add intent save once

**Fixes:** F2 and the misleading navigation fallback. A network or page-script failure after a plain POST does not establish that its write failed. Correct the EN/BG toast first, in its own commit, to say the change may have been saved and to check before trying again.

**Change and proof:** give every rendered plain-POST food form that adds entries a durable per-render request ID. Atomically claim the ID and save food rows; sequential/concurrent resends return the first result. A fresh render permits an intentional repeat. Cover parsed confirmation, cached/search/manual item logging, recent/repeat, quick add, meal copy and saved-meal logging. Verify actual forms/routes before implementation. Preserve ownership, historical values and validation correction. Make loadScript remove failed entries so its next call retries. Correct ConfirmAsync's comment: an already-confirmed run currently returns its ID.

**Scope / risk / rollback:** M, medium risk on food writes. Additive persistence with an explicit old-form policy, reviewed migration/snapshot and backward-compatible rollback. No client queue-key changes. Keep additive state on code rollback.

**Tests / extra care / dependencies:** dropped acknowledgement, page-script failure after save, same-ID races, separate renders, multi-item atomicity, validation failure then correction, absent/foreign IDs, account export/deletion and unchanged past days. Script failure must allow a later successful retry. First toast commit regenerates the localization catalogue and adds Bulgarian in place. Update SPEC/README for request-ID retry semantics. No new package, production model call or wizard action.

### P7 — Make food-search candidate filtering indexable

**Fixes:** F5. The c1 search baseline is 89.6 ms HTTP median / 97.1 ms p95. Five representative EXPLAINs show a catalog sequential scan with median 76.976 ms over 11,795 rows.

**Change and proof:** use indexable trigram candidates while preserving word-similarity thresholds, own-first/prefix ordering and final ranking. Accept only on c1 timings, EXPLAIN plans, query counts and exact matched-result equivalence on held-out English/Bulgarian fixtures. A proposed c1 p95 target is below 50 ms on this same environment, subject to equivalence. C5 and ramp results cannot accept or reject this change while their slowdown remains unexplained.

**Scope / risk / rollback:** M, medium matching-correctness risk. Prefer existing GIN indexes and code-only rollback. Any index migration needs separate lock/startup and snapshot review.

**Tests / extra care / dependencies:** threshold boundaries, partial multiword matches, punctuation/Cyrillic, private ownership, own/shared ranking, change-food sheets and parser matching. No intended SPEC behavior change or dependency on deferred P5. No new package or production load.

### P8 — Deferred: Reuse and batch workout history

**Fixes:** F6. A six-exercise session makes 29 commands, of which 24 come from the two repeated two-query loops (`TrainingAnalytics.cs:21-35,120-127,193-207`). Its warm HTTP median is 15.9 ms c1 / 27.2 ms c5. Two-year latest-session SQL itself is cheap: median 0.021 ms; reduce round trips rather than changing storage.

**Change and proof:** share per-exercise recent-session reads between Suggestions and Last time, and batch different exercise IDs where practical. Keep slot-specific progression rules and warmup-only skip rules. Target at most 12 commands for a realistic six-exercise session and a bounded count for twelve slots, with identical suggested targets/Last time. Latency savings must be measured on wizard-like limits; current absolute cost is small compared with public RTT.

**Scope / risk / rollback:** M, low/medium risk in workout read rendering. No write-path changes. Revert read service code to roll back.

**Tests / extra care / dependencies:** no history, one prior session, warmups only, duplicate exercise slots, mixed weights, historical session opened out of order, user isolation, and query-count fixtures at six/twelve slots. History care applies: compare the same past workout before/after; no persisted historical changes. No SPEC update or dependencies. Keep P1 independent.

### P9 — Deferred: Retain total food latency

**Fixes:** F10. Production has parser/match percentiles but neither includes naming; current container recreation removed prior success-call logs.

**Change and proof:** persist nullable total/namer durations for new ParseRuns, or bounded daily duration buckets if a schema review favors aggregate-only retention. Record success, timeout and failure separately. Reuse the existing parse retention policy. Expected gain is an answerable total-latency question after deployment, not a faster meal. Prove a fake 300 ms parser + 200 ms match + 100 ms name yields a total covering all stages and remains queryable after container replacement; historical rows read as unknown, never zero. M, medium schema risk; additive migration with old-reader compatibility; rollback code while retaining nullable columns. Pin any stored JSON sample if JSON is changed. No client-storage changes; update SPEC 11 for the additional diagnostics. No dependency on changing naming behavior.

### P10 — Deferred: Count error occurrences by day

**Fixes:** F10. Recent-window error totals cannot be reconstructed from lifetime counters filtered by last-seen time. Current production has zero active fingerprints, so this is an observability gap, not an incident-rate claim.

**Change and proof:** add bounded daily aggregate counts with atomic increments, preserving existing fingerprint detail. Prove older occurrences are excluded from a 30-day sum and concurrent reports do not lose increments. M, medium diagnostics/schema risk; independent additive rollout and code rollback, preserve old counters. Tests use fixed clock and concurrent events; owner/export/deletion requirements apply if user-owned records are introduced. Update SPEC 11 retention/counter semantics. No dependency on P9. Neither item needs Antoan, a new telemetry service or an external package; both must avoid text/credential logging.

Do not use sums of independently measured p95s as a total p95. Do not gate optimization on these changes when existing query measurements already identify a bottleneck.

### P11 — Put body construction inside task deadlines and back off failed catalog refreshes

**Fixes:** F7. Catalog preparation currently precedes attempt deadlines in several clients; naming passes the caller token into body building.

**Change and proof:** create each existing attempt deadline before building its body, and pass that deadline token through catalog lookup, request sending and required body reading. After a failed catalog refresh, cache the last good catalog as fresh for a few minutes so later calls do not immediately repeat the outage delay. Keep existing per-task timeout/retry values and safe fallback when no last-good catalog exists.

**Scope / risk / rollback:** small, focused cache/cancellation change across affected catalog-backed clients. Code-only rollback, no new background refresher, coalescing layer, persistence or package.

**Tests / extra care / dependencies:** controlled slow body/catalog preparation, deadline cancellation, last-good failure backoff and recovery after expiry, no-last-good fallback, task failures and cancellation. Preserve label-scan recording after abort. Do not change the weekly timeout or label retry semantics in this item; any SPEC mismatch remains a separate decision. No wizard action.

### P12 — Deferred: Reuse unchanged precache assets across deploys, only after browser evidence

**Fixes:** F9. Source inventory is 35 assets / 2,005,839 decoded bytes; WASM is 1,093,289 bytes. Controlled local Chromium at 390 px, 4× CPU, 1.5 MB/s identity-body proxy and 100/400 ms response delay measured cold worker-ready medians 2.954/7.903 s and synthetic cache-version update 2.422/6.133 s, five contexts/profile. Bare precache URLs and exact hashed runtime URLs differ. These are not production compressed bytes or proof that every real deploy downloads 2 MB; HTTP 304/body reuse and browser policy matter. Browser fixture was reseeded after write load:clean eight-entry days and a blank workout. All40warm tab changes had zero static requests/long tasks;first Progress paid two sequential scripts(220,512identity bytes),adding496/1,065ms to content at100/400ms profiles. See the audit/browser JSON for complete profile/byte caveats.

**Change and proof:** first Progress now establishes material cold-path duplication;build the precache manifest from actual content hashes and reuse unchanged validated entries while preserving offline scanner availability and atomic install failure behavior. Keep the full offline contract;removing WASM precaching needs a separate product/SPEC decision. Target first Progress content within200ms of its warm value on the same profile;this is a proposed acceptance target,not a promised measured gain. Test unchanged/changed worker updates separately before promising update-byte savings. Prove reduced update bytes or duration over five same-profile runs with normal conditional HTTP caching, no added warm requests, and a still-working offline scan/fallback. Do not ship manifest complexity without that measured benefit.

**Scope / risk / rollback:** M, medium blast radius all installed PWAs. New/old worker and page versions must coexist until activation safely completes. Roll back with a new worker/cache version that reads or copies compatible entries; do not clear localStorage/queued work. Never reuse an old cache name as a rollback trick.

**Tests / extra care / dependencies:** initial install, unchanged asset update, changed asset, failed one-asset install, old open clients, hash mismatch, offline first launch, pending set during update and one stale HTML response. Add CI coverage for `sw-check.mjs` or equivalent and byte/count checks. Persistent cache migration and account queues need care; update SPEC 8/2 if URL/cache behavior changes. Browser measurement and review required; Antoan verifies a future real iPhone update. No Cloudflare change is assumed necessary.

### P13 — Deferred: Bound predeploy backup retention

**Fixes:** F11. Predeploy dumps are created without retention (`deploy.sh:31-34`). Current root free space is 176 GiB; this is prevention, not an existing capacity incident.

**Change and proof:** define dedicated predeploy-backup retention with dry-run inventory,an exact filename/directory boundary and a protected newest valid copy. Initial implementation moves selected old archives to an explicitly configured recoverable quarantine/archive location;permanent deletion remains a separate Antoan-approved retention action. Preserve nightly backups and their own retention. Prove selection includes only expired predeploy files and always keeps the agreed minimum validated copies. Do not use broad deletion commands or silently select a retention period.

**Scope / risk / rollback:** M,medium operational risk. Review the exact candidate list and verify a restore point first. Roll back the code and move quarantined archives back using its exact manifest;newest validated copies stay protected. Do not bundle irreversible deletion into this independently reversible item.

**Tests / extra care / dependencies:** shell tests for dry-run, old/new filenames, nonmatching files, spaces/escaped paths, failed validation and newest-copy protection. No app history/key/schema change; keys must stay covered by the backup process. README documents the chosen policy. No code dependency, but Antoan chooses retention after inspecting existing recovery copies and authorizes actual wizard deletions.

### P14 — Remove the drill container’s anonymous volume

**Fixes:** F11.

**Change:** in its own commit, add -v to docker rm -f in restore-drill.sh's EXIT trap. Nothing more: no bind-directory redesign, volume inventory, pruning, new cleanup mechanism or unrelated changes.

**Scope / risk / rollback:** S; the flag removes anonymous volumes owned by this exact temporary container. Revert this one line to roll back. Existing unknown volumes and production containers are outside scope.

**Validation / dependencies:** shell syntax and the existing local restore-drill regressions/full check. Independent of P2 and no actual wizard drill or cleanup is run.

### P15 — Deferred: Serialize numbering for distinct simultaneous sets

**Fixes:** F12. The local c5 workload created56 distinct ClientId rows but only36 distinct set numbers,in five duplicate-number groups;all requests succeeded. This is artificial simultaneous-device traffic,not a claim that the serial phone queue loses sets.

**Change and proof:** serialize number/slot allocation per owned session exercise within the write transaction,while retaining ClientId duplicate success and soft-deleted replay behavior. A row lock is an option to evaluate;do not add a uniqueness constraint until working/warmup/deleted-slot semantics are proven. Five repeated parallel batches must store every distinct ClientId once with noncolliding numbers;sequential and repeated-ID controls must remain identical. Target zero numbering collisions,not an unmeasured speed gain.

**Scope / risk / rollback:** M,high risk within the set-write path;small blast radius per exercise,medium overall because gym logging is critical. Revert locking code independently,no client storage/key changes. If a later design adds an index,its migration needs a separate reviewed rollback. Preserve existing pending ClientIds.

**Tests / extra care / dependencies:** concurrent distinct and identical IDs,working/warmup separation,ownership rejection,deleted replay,slot growth/reload and transaction cancellation. Prove failure against baseline first. Extra care:client storage compatibility and history;model-snapshot checks if a schema change is chosen. SPEC8 should clarify concurrent allocation if its guarantee is documented;no product behavior change intended. Independent of P1,no Antoan action beyond ordinary review/device check.

## Structural and operational work

### P16 — Prove recovery and move state off the failing boot SSD

**Fixes:** F11. SPEC 10 identifies the boot SSD as failing, and actual DB/keys mounts live on the root filesystem. The audit confirms 176 GiB root and 368 GiB HDD free; it does not measure current SMART health, nightly/PC-copy freshness or restore fidelity. Space headroom does not settle device reliability.

**Change and proof:** first inspect device and backup evidence using the exact checks below. Exercise a real archive restore and confirm DB schema, owner counts, Data Protection keys and app login in an isolated target. If failing-device status is confirmed, schedule an independently reviewed storage migration/replacement with a frozen-write window, final consistent dump and complete verification before reopening the app. Keep the last verified source copy untouched until verification. This item reduces recovery uncertainty and hardware exposure; it has no credible millisecond gain.

**Scope / risk / rollback:** L, high blast radius to the whole server and all user data; Antoan-led maintenance, not an automated code PR. Identify physical device/mount paths first. Rollback points to the preserved source stack and verified dump/keys; never roll back to a writable stale DB after accepting new writes without reconciling them.

**Tests / extra care / dependencies:** isolated restored login, cross-account isolation, representative historical food/workout/target equality, keys decryptability, valid backups after cutover, HDD and PC copies; exact table counts alone do not prove values. All persistent state, history and recovery care applies. SPEC/README update only when actual paths/process change. P2 validator should be available, but waiting for code does not excuse delaying device/backup inspection. Needs sudo/device access and explicit write/cutover authorization.

### P17 — Deferred: Measure replacement downtime before changing deployment

**Fixes:** F11. One app is replaced; migrations, seeding and fixups happen before listening. Actual production downtime was not induced or measured. Native local startup results describe relative startup work; they do not establish tunnel availability during replacement.

**Change and proof:** first commit an observation-only probe for a future ordinary deploy: one sequential anonymous request/second, statuses/TTFB/version and container timestamps, with no sign-in/load or restart by the probe. Compare unavailable seconds with a stated user goal (propose under 5 seconds for a normal no-migration replacement; agree after the first sample). Only if measured downtime matters, design a separate independently reversible deployment change. Do not bundle blue/green, migration extraction and image-build relocation into this item.

**Scope / risk / rollback:** M for complete evaluation, low risk for the read-only probe; any later deployment redesign has its own risk/rollback review. Stop the probe to roll back observation. A future deployment change must preserve the old image and migration compatibility; container rollback alone cannot undo incompatible DB migrations.

**Tests / extra care / dependencies:** probe records failed requests and exits nonzero on its own errors; local successful and failed startup, version mismatch and timeout. Production observation needs Antoan's next authorized deploy, not a special disruptive audit deploy. No SPEC change for measurement; update SPEC 10/README with any later approved process change. P2/P16 recovery evidence is a prerequisite for risky deployment redesign, not for measuring.


## Proposed budgets and enforcement

The following apply to a dedicated native Release benchmark job, **not** wall-clock assertions in ordinary parallel xUnit tests. Run app 256 MiB / 1 CPU, DB 256 MiB, shared_buffers 32 MiB, pool 8, JIT off, five synthetic accounts and two years of daily-use rows. Keep the catalog around 11.8k items, six exercise slots, Maintain goal, weekly summaries disabled and no key; these fixture conditions are necessary for comparable query totals. Add separate Cut/first-estimate/stored-summary fixtures rather than hiding their extra legitimate work. Record commit/runtime/architecture and at least five repetitions. Baseline results are native linux-arm64 under Docker Desktop; compare changes on that same runner. A future amd64 runner needs its own reference before enforcing time limits.

### Query budgets

Count completed EF commands from Server-Timing with requests fully consumed. Warm fixture first so initial estimates/snapshots are separated. Fail exact fixture budgets in normal integration tests; this catches N+1 growth with little timing noise.

| Path/operation | Measured commands | Initial ceiling | Desired after relevant item |
|---|---:|---:|---|
| Today | 11 | 12 | Keep ≤12; add explicit Cut/weekly cases |
| Train | 4 | 5 | Keep |
| Six-exercise active session | 29 | 30 | ≤12 after P8; twelve-slot fixture bounded too |
| Food day | 8 | 9 | Keep |
| Recent foods | 3 | 4 | Keep |
| Search | 3 | 4 | Keep query count; time/plan catches expensive scan |
| Progress | 14 | 15 | Keep; 30-day and 730-day fixtures |
| Settings hub | 4 | 5 | Keep |
| Training history | 2 | 3 | Keep |
| Cached barcode | 3 | 4 | Keep; forbid external lookup on cache hit |
| New set POST | 6 | 7 | Keep and verify one row/ClientId; correctness first |
| Food log POST | 5 | 6 | Keep; allow reviewed idempotency command changes |
| Food repeat POST | 4 | 5 | Keep |
| Three-food fake parse | 12 | 13 | Lower measured budget after P5, matched-item equivalence required |

These are review proposals, not new passing tests added on the audit branch. Existing Today budget remains 12 (`ServerTimingTests.cs:25-35`) and is currently exercised mostly with a sparse account.

### Timing, payload and resources

| Area | Audit evidence | Proposed initial gate | Enforcement / limitation |
|---|---|---|---|
| Ordinary warm pages, c1 | HTTP p95 Today 8.3, Train 3.1, Food 6.7, Progress 20.1, Settings 3.0 ms | app p95 ≤30 ms for those fixtures | Five warm batches in isolated perf job; flag >25% reference change and rerun once before blocking |
| Session, c1 | HTTP p95 17.2 ms | Proposed app p95 ≤25 ms at c1 | Six-slot fixture; P8 is deferred |
| New set POST, c1 | HTTP p95 4.1 ms | Proposed app p95 ≤10 ms at c1; no unexpected statuses | Verify idempotency independently |
| Food search c1 | HTTP p95 97.1 ms | Proposed c1 HTTP p95 <50 ms after P7 | EXPLAIN, query count and matched-result equivalence; no c5/ramp acceptance |
| No-key three-food parse, c1 | HTTP p95 574.6 ms | Proposed app p95 ≤750 ms at c1 | Preserve matched results; P5 is deferred |
| Two-year growth | Today 6.2→6.8 ms median; Progress 8.0→17.3; search unchanged 89.6 | Compare all fixtures at 30/730 days; alert unbounded query counts or >2× beyond reference growth | Do not truncate EMA/targets to meet a ratio; history equality is mandatory |
| Static precache | 35 entries / 2,005,839 decoded bytes | ≤36 entries and ≤2.10 MB decoded initially | Small filesystem/manifest check in CI; deliberate library update needs reviewed baseline change; also validate every path exists |
| Warm PWA tabs | Source/existing worker check; browser table in audit | No unexpected static network requests after exact assets warm | Browser test with active worker; measure request count, not only reported resourceSize |
| PWA cold/update | Cold worker-ready median 2.954/7.903 s; synthetic-update 2.422/6.133 s at 100/400 ms proxy profiles, n=5 each | Proposed median ceilings 3.5/9.2 s cold and 2.9/7.2 s synthetic update, with unchanged profile/HTTP behavior | Informational until repeatable runner established; compare actual body bytes separately; never substitute decoded inventory for production transfer |
| App after representative load | Existing app limit 256 MiB | Establish a clean c1 memory baseline before adding a headroom gate | Stress/ramp memory is observation only, with no acceptance target |
| App after forced full GC | 197.2–197.3 MiB with diagnostic/tmpfs artifact | Record, no hard general-purpose CI gate yet | Gcdump adds overhead and is not managed-heap size; use consistent diagnostic method if comparing |
| Production memory | App 145.2 MiB, DB ~69 MiB; current containers no restarts/OOM | Preserve 256 MiB until representative time series proves change safe | Do not lower or raise limits from one sample, or treat current restart count as lifetime health |
| Search overload | Unexplained concurrency slowdown recorded in audit | No budget or acceptance test | Diagnose the c5 search/parse slowdown before using concurrency figures |

A fixed CI runner is required for hard server-time/memory gates. Start these as a recorded informational job, establish repeatability on three runs, then enforce the proposed ceilings. Query correctness, static inventory, deterministic races and restore failure exit codes can be enforced immediately. Cold startup/image size budgets should use the final audit measurements as a baseline plus explicit margin; do not invent a service-availability SLO from process start alone. Browser long-task/bytes/time budgets similarly need the final cold/warm mobile-profile table, then a controlled-runner baseline; actual iPhone numbers remain manual.

## Stack decision records

### D1 — Keep MVC, Razor, htmx and plain HTTP queue

**Evidence / target:** ordinary local page medians 2.3–17.3 ms, production anonymous login app median 1.4 ms versus 168.9 ms public TTFB. Failures identified are request ordering/deadlines/lifecycle, not Razor rendering throughput. Meet reliable offline replay and latest-page intent with P1/P3/P4.

**Keep and tune:** small scripts and query changes retain deployment/auth/localization and offline queues. Migration cost low; risk confined; each reverts independently. No evidence the in-place path cannot meet the target.

**Alternatives:** Blazor Server adds persistent circuits exactly where SPEC 2 rejects signal dependence; no measured gain, broad migration and hard UI rollback. A full SPA or native app could offer a different offline model but requires rebuilding navigation/auth/forms/state and supporting old installed clients; gain unmeasured, cost/risk high, reversal requires maintaining both interfaces. Do not start either migration for these findings.

### D2 — Keep EF Core and PostgreSQL

**Evidence / target:** most page queries are cheap; latest workout-ID SQL median 0.021 ms, all 730 weights 0.073 ms, all target snapshots 0.402 ms. Search's 76.976 ms sequential scan and four-per-exercise round trips identify query shape, not PostgreSQL itself, as the measured cost.

**Keep and tune:** reuse reads and indexable candidate filtering, preserve query filters and ownership. Moderate focused implementation, code rollback and additive indexes. Prove P7/P8 targets first.

**Alternatives:** raw SQL everywhere/Dapper would remove some materialization overhead that is not the dominant measured cost and makes owner filtering manual; broad correctness risk and mixed-path maintenance, no demonstrated need. SQLite removes a service but changes concurrency/trigram semantics/backup workflow and migration reversibility; a hosted DB adds network/dependency cost on gym requests. No measured component limit justifies either. Keep current memory/pool limits during query tuning.

### D3 — Keep .NET runtime, workstation GC and ReadyToRun

**Evidence / target:** app had no observed OOM/restart in local ramp or current production container; production 145.2 MiB, local stress 230.6 MiB, forced-GC diagnostic 197.2–197.3 MiB. The local architecture and diagnostics differ from wizard; absolute figures are not directly interchangeable.

**Keep and tune:** profile allocations only when a representative memory/time series shows the culprit, reduce identified request work first, preserve 256 MiB until evidence supports a change. Low migration cost and easy runtime-setting rollback; allocation changes need before/after proof.

**Alternatives:** enabling server GC again reverses the October footprint decision without evidence; runtime/language replacement has large cost and no measured gain. Native AOT does not meet this repository's MVC/cookie-auth constraints in SPEC 2; a trim/AOT redesign would change architecture, not simply publishing flags. No current evidence shows GC or JIT itself is the blocking component. No runtime replacement.

### D4 — Keep OpenRouter/OFF interfaces; improve local orchestration

**Evidence / target:** production parser n12 median 3.267 s / p95 5.237 s; matcher median 1.428 s / p95 3.245 s; label n6 median 4.262 s / p95 5.217 s, including one failure. Total food-submit and namer percentiles are unavailable after log rotation/container replacement. Local fake parse exposes a separate DB cost before blaming model latency.

**Keep and tune:** P5/P7 reduce measured local work; P9 makes total stages visible; P11 makes failure bounds dependable. Existing manual entry, cached barcode and ranking/name fallbacks remain. Moderate cost, isolated reversibility, no model evaluation spend required for fault tests.

**Alternatives:** another provider/model or a local model has unmeasured accuracy, latency, memory and operational cost; it cannot be justified with parser-only percentiles. Merging parser/matcher/namer can alter the explicit “model reads, database supplies nutrition” and validation boundaries. A background naming flow changes UI/persisted state. Evaluate each later only with end-to-end evidence and held-out accuracy; none is approved as a stack migration here.

### D5 — Keep service worker/PWA delivery and offline static guarantees

**Evidence / target:** 35 precache entries, ~2.006 MB decoded; WASM is over half. Warm browser tabs issue zero CSS/JS/library requests; cold/update medians are 2.954/2.422 s at the 100 ms profile and 7.903/6.133 s at 400 ms, five contexts each. These identity-proxy experiments establish a local reference, not real iPhone/production compressed transfer.

**Keep and tune:** P12, if measured worthwhile, gives a versioned manifest/compatible cache migration and continues to precache the scanner. Moderate risk across old clients; old-worker coexistence and offline tests establish reversibility.

**Alternatives:** removing the worker would abandon installed/offline guarantees; CDN delivery conflicts with SPEC 2/8; lazy remote scanner loading saves first-install bytes but breaks the stated offline contract. Native delivery is a separate product, not a proven fix for cache keys. No evidence that the PWA component cannot meet the target in place; keep it.

## Antoan's exact remaining procedures

These are future actions, not audit commands already performed. Production remains read-only until separately authorized. Avoid app CLI calls merely to inspect production: startup runs migrations/seed/fixups before CLI dispatch (`Program.cs:370-387`); the audit uses SELECT-only SQL for aggregates.

### Device, backup and recovery

1. On wizard, identify the physical boot device before the sudo health command. The audit places /boot and /boot/efi on sda;root is LVM and its physical backing device was not inventoried. Verify device identity on the host:

   ```bash
   lsblk -o NAME,PATH,MODEL,SERIAL,SIZE,FSTYPE,MOUNTPOINTS
   findmnt -no SOURCE,TARGET /
   sudo smartctl -x /dev/sda
   ```

   Use `/dev/sda` only if that inventory confirms it is the boot SSD. `smartctl` may need installing by Antoan; do not guess another device or install with sudo automatically. Keep serials/diagnostic logs locally if sharing only conclusions.

2. Check backup syntax without exposing contents:

   ```bash
   sudo bash -n /usr/local/bin/homeserver-backup
   sudo less /usr/local/bin/homeserver-backup
   ```

   `sudo less` shows the script for Antoan's local inspection without editing it. Verify the actual `pg_dump -Fc`, archive validation, exclusion of live `plate/db/`, 14-day retention, failure alert and PC pull. Do not paste the script or Telegram/OpenRouter credentials into chat. Verify newest successful HDD and PC snapshot timestamps at their actual configured paths; paths were not read in this audit, so no fabricated `ls` command is supplied.

3. After authorizing a throwaway restore that creates a container and temporary data:

   ```bash
   cd ~/docker/plate
   tools/restore-drill.sh nightly
   ```

   Until P2 is fixed, inspect complete schema/table manifests and exit status yourself; “Restore OK” with differing manifests is inconclusive. Preserve DB dump and Data Protection keys together and use a fresh isolated target, never overwrite the live DB for a drill. Storage relocation/retention deletion requires its own exact reviewed paths and commands after P16 inspection.

### Cloudflare

Inspect the Cache Rules/browser-origin TTL that match `/sw.js`. The audit saw `max-age=14400` and edge revalidation, not a proven stale installed worker. On the next ordinary deploy compare the old/new worker body version and phone's active version. Change rules only if this proves a stale update; record the previous rule and restore it to roll back. No automatic dashboard modification or requested change is part of this plan.

### iPhone and next deploy

Enable iPhone Safari → Advanced → Web Inspector, connect/trust the Mac, then select the installed Plate app from Mac Safari's Develop menu. Note phone/iOS version, viewport, Low Power Mode, app version and network. Record five warm Today→Train→Food→Progress cycles on gym mobile data, then five food text/search/cached-barcode/repeat journeys as appropriate. Keep HAR/cookies/food bodies locally; return only route/stage timings, bytes/request counts, long tasks and Server-Timing aggregates. A paid parse/label is Antoan's deliberate action, not required audit load.

With synced queues, observe the next normal deploy's first PWA launch and worker update. Do not clear website data/uninstall while pending work exists. Verify a planned offline set survives close/reopen/reconnect exactly once; separately check latest-tab intent and camera close during late permission/device acquisition. Cold install belongs on an expendable local/staging installation. For deploy interruption, collect one sequential anonymous login request/second before/during/after an already authorized replacement and correlate version/container timestamps; do not schedule a disruptive deploy just to measure it.

## Decisions still required before implementation

- Resolve SPEC/implementation differences for weekly timeout (15 s by reference versus 20 s implemented) and label transport retry (“5xx only” versus retry on transport exceptions). The audit does not decide which behavior to change.
- Select the food-confirm identity/backward-compatibility design for P6 and the queue attempt deadline for P1 from focused fault tests.
- Establish actual iPhone and PWA-update figures before promising phone speed gains or approving P12; the local profile is a relative desktop experiment.
- Confirm current SSD condition, backup freshness and restore values before storage/deployment redesign. No architecture migration is justified by the available measurements.
