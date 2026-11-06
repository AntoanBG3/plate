# P7: indexed food-search candidates

P7 keeps the original word-similarity score, threshold comparison, ownership checks, ordering and picker deduplication. It adds an indexable `%>` candidate predicate on the existing English and Bulgarian GIN indexes. There is no migration or persisted-data change; rollback is a code revert.

The candidate threshold is one float step below the requested threshold, followed by the original `>=` comparison. This preserves exact boundary matches despite the operator's float threshold. Zero and unusual thresholds keep the original candidate set. The threshold is transaction-local; a surrounding transaction's setting and earlier writes survive the search. See the [PostgreSQL 18 pg_trgm reference](https://www.postgresql.org/docs/18/pgtrgm.html) for operator/index support and threshold types.

## Measurement contract

Captured November 6, 2026 on Mac16,13, native Apple Silicon: 10 CPUs, 16 GiB RAM; Docker Desktop arm64 VM with 10 CPUs and 7.75 GiB RAM. Native linux-arm64 Release/ReadyToRun, .NET SDK 10.0.107, same pinned runtime family as the audit. App: Production, 1 CPU, 256 MiB, pool 8. PostgreSQL 18: 256 MiB, no CPU cap, `shared_buffers=32MB`, `max_connections=15`, JIT off.

The audit's synthetic generator seeded five accounts, 11,795 catalog foods, 29,200 food entries and 730 days anchored to November 5. No production data or paid model call. Baseline application sources equal local main `462ef11`; the audit checkout adds measurement/docs only. Candidate sources are pinned by SHA-256 in the EXPLAIN results. Both publishes use the same database, keys and container configuration; the candidate has its own publish directory.

Warm measurements only: five sequential batches of ten requests per case after five warmups, complete HTTP bodies, one workload at a time. p50 is the median; p95 is nearest rank. The baseline ran first, then the candidate after a container replacement. These local relative figures are not wizard timings. No c5/ramp target or measurement accepts this change; the earlier c5 slowdown remains unexplained.

## c1 HTTP results

| Case | Baseline p50 / p95 ms | Candidate p50 / p95 ms | EF commands before / after |
|---|---:|---:|---:|
| Search: chicken | 99.27 / 132.74 | 25.75 / 27.40 | 3 / 4 |
| Search: banana | 96.38 / 99.81 | 27.13 / 36.93 | 3 / 4 |
| Search: ориз | 88.58 / 90.20 | 15.80 / 17.13 | 3 / 4 |
| Search: кисело мляко | 116.95 / 144.17 | 35.98 / 40.07 | 3 / 4 |
| Search: chikcen | 96.10 / 103.75 | 27.67 / 49.91 | 3 / 4 |
| Fake text parse, three foods | 560.96 / 588.87 | 140.54 / 146.69 | 12 / 18 |

All 300 paired responses returned identical ordered food IDs: 20 per picker response, three per parse. All statuses were 200. Raw samples retain batch/index, HTTP/app/DB durations and query counts in [baseline](../../tools/perf/results/p7-baseline-c1.json) and [candidate](../../tools/perf/results/p7-candidate-c1.json).

Each search adds one EF command for the local threshold and a short read transaction. `Server-Timing` counts EF commands, not transaction-control messages. The fake parse still performs six searches; eliminating redundant searches is deferred P5. Its improvement measures local matching, not total latency with real OpenRouter calls. The observed p95s meet the proposed c1 target on these cases; they are not a universal latency guarantee, especially for very broad queries or another server.

## Plans

Five alternating baseline/candidate `EXPLAIN (ANALYZE, BUFFERS)` pairs per case, warm buffers, normal planner settings, app idle, no concurrent build/tests/load. SQL bodies are extracted from baseline/current service source with typed literals substituted for EF parameters; these are source-shaped queries, not byte-for-byte captured EF commands. A separate integration regression captures the actual EF command and checks its normal plan.

| Shape and query | Baseline execution median [min–max] ms | Candidate execution median [min–max] ms |
|---|---:|---:|
| Picker, chicken | 90.321 [85.958–91.095] | 17.781 [17.300–18.178] |
| Picker, banana | 88.683 [88.535–91.249] | 19.889 [19.186–20.023] |
| Picker, ориз | 83.845 [81.944–84.537] | 7.571 [7.331–7.752] |
| Picker, кисело мляко | 110.091 [108.100–112.442] | 26.336 [25.910–26.958] |
| Picker, chikcen | 90.524 [88.829–91.004] | 18.412 [18.235–19.343] |
| Matcher 0.3, chicken | 84.358 [84.041–87.649] | 12.800 [12.588–13.321] |
| Matcher 0.5, chicken | 84.361 [82.102–84.574] | 8.720 [8.508–9.035] |
| Matcher 0.5, rice | 78.457 [77.526–79.894] | 8.717 [8.678–9.099] |
| Matcher 0.5, banana | 80.129 [78.302–80.691] | 1.657 [1.599–1.685] |

Every baseline plan scans `FoodItems` sequentially. Every candidate plan uses bitmap scans on both `IX_FoodItems_Name` and `IX_FoodItems_NameBg`. [Raw plans](../../tools/perf/results/p7-explain.json) retain node/row/buffer counts, durations and source hashes. Exploratory plans taken during test execution were discarded from these acceptance results.

## Correctness and checks

The independent oracle pins both original SQL shapes. Twelve held-out English/Bulgarian queries compare complete records and order at matcher thresholds 0, 0.3, 0.5, 0.8 and 1, plus picker results. Fixtures include own/private/shared foods, partial multiword names and punctuation. Separate tests cover exact score boundaries and preserving an existing transaction and its threshold. Existing search and parser matching regressions also pass: 34 focused tests.

The final captured-command planner regression failed against main and passed with the fix. Its fixture runs normal vacuum/analyze after bulk seeding, and does not disable sequential scans. Full check passed: 29 JavaScript, 470 Core, 555 Web and 241 E2E tests; Release build succeeded with zero warnings/errors.

## Reproduce

Use the audit branch's `tools/perf/setup.sh 730` to create the disposable local stack, as documented there. Keep baseline and candidate native publishes in separate directories. Replace only `repa-perf-app` between runs, retaining the same database and limits; the candidate invocation differs only in its read-only `/app` bind path. Do not run tests or builds alongside timings.

```sh
NODE_TLS_REJECT_UNAUTHORIZED=0 node tools/perf/search-c1.mjs baseline tools/perf/results/p7-baseline-c1.json
# After publishing/starting the candidate with identical limits:
NODE_TLS_REJECT_UNAUTHORIZED=0 node tools/perf/search-c1.mjs candidate tools/perf/results/p7-candidate-c1.json
python3 tools/perf/search-compare.py tools/perf/results/p7-baseline-c1.json tools/perf/results/p7-candidate-c1.json
python3 tools/perf/search-explain.py > tools/perf/results/p7-explain.json
bash tools/check.sh
```

The HTTP harness is fixed to loopback and the synthetic account. TLS verification is disabled only for that disposable local certificate. The EXPLAIN harness is fixed to `repa-perf-db` / `gym_perf`. No app package, new index, model policy or production configuration changes.
