# Server and data review evidence, 2026-11-05

Baseline inspected: `462ef118827b00839941eab87cceb2a106c93065`. These are source observations and measurement hypotheses, not production incident claims. No application code was changed. Parent audit owns timings and final F/P numbering.

## Critical journey map

| Journey | Routes | Main implementation |
|---|---|---|
| Log set / replay | `POST /train/sets`, `GET /train/sessions/{id}` | `Controllers/TrainController.cs:198-205`; `Features/Training/SessionService.cs:96-150`; `Features/Analytics/TrainingAnalytics.cs:21-35,120-135` |
| Food text | `POST /food/parse`, `POST /food/confirm` | `Controllers/FoodController.cs:81-118`; `Features/Feedback/ParseRunService.cs:28-80`; `Features/Food/FoodMatcher.cs:77-162` |
| Barcode | `GET /food/barcode`, `POST /food/log-item` | `Controllers/FoodController.cs:126-185`; `Features/Food/FoodItemService.cs:112-137`; `Integrations/OpenFoodFactsClient.cs:24-85` |
| Search / repeat | `GET /food/add?tab=search&q=...`, `GET /food/add?tab=recent`, `POST /food/log-recent` | `Controllers/FoodController.cs:59-78,245-250`; `Features/Food/FoodLogService.cs:199-218`; `Features/Food/RepeatLoggingService.cs:15-39` |
| Label | `POST /food/label` | `Controllers/FoodController.cs:196-242`; `Features/Food/LabelScanService.cs:39-64`; `Integrations/OpenRouterLabelReader.cs:84-124` |
| Today / tabs | `GET /today`, `/train`, `/food`, `/progress`, `/settings` | `Controllers/TodayController.cs:19-69`; `Features/Food/NutritionTargets.cs:30-84` |
| Progress | `GET /progress?range=...` | `Controllers/ProgressController.cs:13-30`; `Features/Analytics/TrainingAnalytics.cs:137-189`; `Features/Body/BodyAnalytics.cs:22-70` |

Paths above are relative to `src/GymTracker.Web/`.

## Findings to validate/rank

### Workout history is queried four times per exercise

Verified cause: `TrainingAnalytics.SuggestionsAsync` loops session exercises (21-35); each `RecentSessionSetsAsync` reads recent session IDs and their sets in two separate commands (193-207). `LastTimeAsync` loops again and makes the same two reads for the last session (120-127). That is **4N history commands** per active workout GET, before the split session load and profile/settings/layout. A six-exercise session contributes 24 history commands. Duplicate exercise slots repeat work again. The data is bounded to recent sessions, so this is a round-trip issue, not an unbounded history download. Measure exact request query totals and DB time at 6 and 12 exercise slots; EXPLAIN the latest-ID query under two-year history.

Candidate isolated change: batch or reuse the earlier-session history for both suggestions and Last time, keeping each slot's progression rules and warmup-only skip behavior. Prove query total independent of exercise count, with identical suggestions/Last time output. Effort M; low/medium risk confined to workout GET; rollback reverts service code, no stored data change. Tests: mixed-weight, warmup-only, duplicate exercise slots, earlier-than-current historical sessions, owner isolation, query budget for realistic session. No SPEC behavior change intended; historical results must match.

### Food matching scans the catalog repeatedly, including with matcher off

Verified source: `FoodMatcher.cs:84-95,213-225` performs one or two ranking searches and one or two shortlist searches **per parsed item**. Bulgarian original names differing from canonical English trigger up to four commands per item. Shortlists are built before usual/own-food bypass (100-105), and before Off is detected in `Integrations/OpenRouterSettings.cs:174-177`. A parser-only/no-key local request still pays shortlist construction.

`FoodItemService.cs:36-46,79-97` filters with `GREATEST(word_similarity(...), word_similarity(...)) >= threshold`. GIN trigram indexes exist (`Data/AppDbContext.cs:172-173`) but this expression does not use an indexable trigram operator. **Sequential scan is a hypothesis pending EXPLAIN**, not a measurement. Catalog is shared and grows when products are cached; expected benefit depends on actual row count and measured DB cost.

Candidate isolated changes, ship separately: (a) skip shortlist construction when the matcher cannot run and for usual/own items; (b) an indexable candidate filter preserving present word-similarity threshold and ranking. Do not casually switch `word_similarity` to ordinary `similarity` or change own-first ordering. Effort S/M; risk low for bypass, medium for query ranking; rollback code only; tests pin identical ranked IDs for EN/BG, threshold boundaries, multiword partial matches, private ownership and usual foods. No SPEC change if results remain identical; index migration needs model snapshot/no-pending-change checks if any index changes are necessary.

### Food text's visible latency includes an unrecorded sequential namer stage

Verified: `ParseRunService.cs:31-44` awaits parser, matcher, then namer in series. Stored `ParserMs` and `MatchMs` stop before namer (`32,41,59-60`). Namer success is logged (`OpenRouterFoodNamer.cs:85-86`), but SQL percentiles of parser+matcher are **not total parse latency**, and sums of independent p95s are not an end-to-end p95. Namer is deliberately bounded at five seconds and can use the database name on failure (`18,89-97`). Incidence and gain must use production aggregate namer logs and, later, measured total time.

Candidate isolated measurement change: add total/namer latency to parse instrumentation (diagnostics-only) before deciding whether caching names or progressive name enrichment is worth the added state/UI complexity. Any persistent JSON/schema change must pin literal old samples and review migration; adding a nullable scalar column has ordinary migration risk. A target such as “no more than 5 s added by cosmetic naming” needs actual request-wide deadline coverage below. Do not simply remove naming: it is explicit SPEC 6.1 behavior.

### Catalog refresh lies outside operation deadlines and retries a failed refresh on every call

Verified: parser awaits `BodyAsync` before creating attempt timeout (`OpenRouterFoodParser.cs:99-103`), label does the same (`OpenRouterLabelReader.cs:86-90`), weekly writer likewise (`OpenRouterWeeklySummaryWriter.cs:58-62`). Body construction calls `catalog.FindAsync`. Catalog HTTP timeout is **10 s** (`Program.cs:126-129`); fresh cache is one hour (`OpenRouterModels.cs:39-53`). A failed refresh returns LastGood without refreshing any short failure cache (`56-61`), so every following parse can wait another failing catalog call. Namer starts its five-second cancellation source, but passes the original request `ct` into `BodyAsync` (`OpenRouterFoodNamer.cs:69-76`), so a slow catalog request can exceed the nominal five-second namer deadline.

This is a **verified control-flow risk, unmeasured production incidence**. It affects cold startup/hourly refresh during catalog slowness, including requests when a last-good catalog already exists. Do not present the source-derived 10 s upper timeout as observed latency.

Candidate isolated change: cache last-good and refresh without blocking ordinary configured-model calls, suppress concurrent refreshes and add short failed-refresh backoff; enforce cancellation throughout catalog lookup/body building. Keep cold-model parameter capability behavior safe. Effort M; medium blast radius catalog-backed AI tasks; rollback code only; fake HTTP tests for slow catalog, expired/fresh cache, concurrent calls, failure then recovery, cancellation, and task deadline. SPEC update only if refresh behavior is newly specified.

### History reads grow, but need two-year measurements before optimization

Verified: all weights are loaded once/request (`BodyweightService.cs:15-30`), and weight trend is deliberately calculated from the first weigh-in before slicing (`BodyAnalytics.cs:25-34`). This preserves EMA semantics; a naive date filter silently changes historical values. Progress's 28-day calorie chart nevertheless reads every maintenance estimate and target snapshot to today (`BodyAnalytics.cs:46-59`), then deserializes all snapshots and searches lists 28 times. E1RM series reads every working set for the selected exercise (`TrainingAnalytics.cs:151-165`); records reads every earlier working set for the current workout's exercise IDs (`60-80`). At two years this may still be cheap; measure before adding snapshot caches or precomputed aggregates.

If calorie snapshots prove material, a narrow independent change can fetch the last predecessor snapshot/estimate plus rows within the chart range. That preserves carry-forward semantics without loading all JSON. Effort S/M; history-sensitive: compare exact past-day outputs before/after, including missing-day snapshot gaps and profile edits; rollback code only. Weights need an exact carry-in EMA value, not a truncated recalculation. Avoid proposing generalized read-model infrastructure without measured need.

### Concurrent set numbering risk under multiple tabs/devices

Verified code shape: `SessionService.cs:110-129` does read-max/read-count then insert/update; uniqueness is on ClientId only (`AppDbContext.cs:124`). Two different client IDs can choose the same SetNumber and race on slot counts. This is a **hypothesis pending concurrent replay**, not a claim that one phone loses sets: the normal queue sends one item at a time by SPEC 8. If c5 benchmark posts to one exercise, inspect duplicate-number groups and clearly label the artificial workload.

Candidate only if reproduced: serialize numbering per exercise transaction with ownership and idempotency intact, assert all distinct sets survive and occupy stable rows after reload. Effort M; medium risk critical write path; rollback code/migration independently planned; tests concurrent distinct ClientIds and duplicate ClientIds, warmup/working segregation, soft-deleted replay, across-account rejection. Extra-care history/queued pending sets and migrations if new indexes are introduced; no client-key rename.

### Double food confirmation is not idempotent

Verified source: `ParseRunService.ConfirmAsync` returns existing `run.Id` when already confirmed (`116-119`); `FoodController.Confirm` still unconditionally calls `FoodLogService.AddAsync` (`109-116`), which inserts fresh entries (`86-110`) with no request client ID. The current signature's comment about returning null for already confirmed (`ParseRunService.cs:105-107`) does not match its body. Production frequency unknown. This is worth a controlled duplicate POST on synthetic data, particularly after a response-loss retry or browser back/resubmit. Other food POSTs also lack an idempotency token. Do not attribute actual production duplicate meals without aggregate evidence.

Candidate isolated fix if reproduced: confirmation request ID with atomic “confirm once + insert once”; distinguish permitted re-logging from retrying one intent. Effort M; risk medium, blast radius food confirms; rollback must preserve newly stored request identifiers; tests duplicate sequential and simultaneous submit, lost-response retry, separate valid confirmations, owner scoping. Stored JSON/migration and history care if the implementation adds persisted request state; SPEC should document retry semantics.

## Measured-and-fine candidates, subject to parent measurements

- No new client/server architecture is needed to fix the verified query pattern: bounded HTTP and EF queries remain suitable for intermittent gym signal; no persistent circuit needed.
- Profile service caches one tracked profile per request (`ProfileService.cs:11-20`); bodyweights use AsNoTracking and share one request read (`BodyweightService.cs:15-30`); do not re-propose the previous pass's repeated profile/weight query elimination.
- Food day, week, recents and repeat amounts have explicit date bounds (`FoodLogService.cs:35,49,201-205`; `RepeatLoggingService.cs:22-28`, 90-day Core window). FoodLog has both `(UserId,Date)` and `(UserId,FoodItemId,CreatedAt)` indexes (`AppDbContext.cs:195-196`).
- Set retries use unique ClientId, check ownership on the stored set, and recognize deleted-set replays (`SessionService.cs:98-101,143-147,383-400`); existing `SetReplayTests` covers deleted replay. This supports single-phone write-ahead retries without duplicate insert.
- Parser 15 s × at most two attempts; Jev 3 s/no retry; namer 5 s/no retry; OFF 10 s/no retry and explicit 429 fallback; label 20 s with 5xx retry but no timeout retry are present. Catalog overhead above is separate. Label code retries HttpRequestException too (`OpenRouterLabelReader.cs:114-120`), while SPEC says “5xx only”; weekly writer uses 20 s (`17`) while SPEC 6.5 refers to setup chat's 15 s. These are implementation/spec divergences, not assumed bottlenecks; align behavior in a narrowly scoped later change, without guessing which requirement to change.
- Weekly model work is moved to `/today/weekly-summary` (`TodayController.cs:73-78`). However `WeeklySummaryService.StateAsync` can build Facts on first eligible visit (`51`) and WriteAsync builds them again (`66`); measure first-of-week separately from stored-summary steady state.

## Local EXPLAIN helper

`tools/perf/explain.sql` contains source-based representative query shapes. Run **five times sequentially**, discard/label first cold run separately, record timings and buffers. It intentionally does not change indexes or application SQL and requires only the synthetic account's email. Header has a sample container name; use the actual audit container. Actual EF-emitted SQL from pg_stat_statements should be used if exact shape differs.
