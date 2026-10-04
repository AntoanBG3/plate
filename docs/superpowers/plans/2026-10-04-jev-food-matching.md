# Jev Food Matching Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let TypeSafe's Jev decision model pick which database food each parsed item is, from a shortlist, or say none fits, instead of trusting the top of `FoodMatcher.Rank`.

**Architecture:** The LLM parser is unchanged. `FoodMatcher` still searches and ranks, but only to build a shortlist per item; one Jev Decisions API call per parse answers one Choice question per item (shortlist plus a `none` option). Any Jev failure falls back silently to today's Rank top-1, so logging food never depends on Jev.

**Tech Stack:** .NET 10, ASP.NET Core MVC, EF Core + PostgreSQL 18 (`pg_trgm`), typed `HttpClient`, `System.Text.Json`, xUnit + FluentAssertions + Testcontainers. No new packages.

**Spec:** `SPEC.md` sections 2, 4 and 6.1 (updated in Task 6). The design decisions are in this document, in "Design" below, because the request that started this work is the design brief.

## Global Constraints

- `GymTracker.Core` has no references to EF Core, ASP.NET Core or `HttpClient`.
- Every user-owned query is scoped to the current user; `FoodItemService.SearchAsync` already filters to shared foods and the user's own.
- Async EF calls with `CancellationToken` throughout. No business logic in controllers or views.
- No new NuGet packages or front-end libraries.
- No secrets in the repository. Jev uses the stored OpenRouter key (Settings, else `OPENROUTER_API_KEY`); no second key.
- Every user-facing string goes through `L[...]` / `LocalText` and gets a Bulgarian translation (`CatalogueTests`).
- Mobile-first at 390 px, touch targets at least 44 px (the Settings toggle).
- Every task ends with `dotnet build` and `dotnet test`, both green, results reported as run.

## Verified facts about Jev (2026-10-04)

Checked against the OpenRouter Jev hub, tutorial and Decisions API reference, and TypeSafe's state, choice, confidence, models and jev-1.13 jaggedness pages.

- Endpoint `POST https://openrouter.ai/api/alpha/decisions`, `Authorization: Bearer <OpenRouter key>`. Relative to our existing base URL `https://openrouter.ai/api/v1/` it is `../alpha/decisions`.
- Request: `{ "model", "state", "questions": { "<id>": { "type": "choice", "instructions", "criteria": { "<key>": "<description>" } } } }`. A Choice takes up to 255 options.
- Response: `{ "id", "model": "typesafe/jev-1.13-20260917", "provider", "answers": { "<id>": { "type": "choice", "choice", "confidence", "probabilities" } }, "usage": { "input_tokens", "output_tokens", "cost" } }`. The `model` field is the dated snapshot that answered, so the resolved version can be recorded.
- Price $0.042 per million input tokens, output free. 32k tokens for state plus the longest question, 64k per request. 80 requests/s rate limit, "adjusting dynamically". Errors: 400, 401, 402, 403, 404, 413, 429, 500, 502, 503, 524, 529.
- All questions see the same state and are answered independently and in parallel.
- Language: "English is the primary training language and where accuracy is currently best. Other languages ... are handled but not equally well; test on your own content." This is the main risk for our Bulgarian entries and is what Phase 0 measures.
- Known weaknesses (jaggedness page): leans toward the first-listed Choice option; weak with numbers; accuracy drops with irrelevant detail in the state; reads instructions literally.
- `~typesafe/jev-latest` moves with each release; TypeSafe recommends pinning a version when thresholds are tuned against it.

## Design

**Where things live.** `IFoodMatchDecider` and its records go in `GymTracker.Core/Nutrition/FoodMatchDecider.cs`, next to `IFoodParser`; they use only `ParsedFoodItem` and `Per100g`. The Jev implementation is `GymTracker.Web/Integrations/JevFoodMatchDecider.cs` (typed `HttpClient`). Shortlisting and the confidence mapping stay in `FoodMatcher`, a Web feature service, as today.

```csharp
public record MatchCandidate(int Id, string Name, string? NameBg, Per100g Per100);
public record MatchQuestion(ParsedFoodItem Item, IReadOnlyList<MatchCandidate> Candidates);
/// FoodItemId null means the decider said none of the candidates is this food.
public record MatchDecision(int? FoodItemId, decimal Confidence);
/// Decisions in the same order as the questions; Model is the version that answered.
public record MatchDecisions(IReadOnlyList<MatchDecision?> Items, string Model);

public interface IFoodMatchDecider
{
    /// Null when no decision could be made (off, no key, timeout, error); the caller uses Rank's top-1.
    Task<MatchDecisions?> DecideAsync(string text, IReadOnlyList<MatchQuestion> questions, CancellationToken ct);
}
```

A null entry in `Items` means that one item could not be decided (missing answer, unknown option key) and falls back to Rank on its own.

**Request.** One request per parse. State: `{ "diary_entry": <the user's text>, "foods": { "i0": { "as_written": NameOriginal, "english": NameEn, "state": "raw"|"cooked"|"not applicable", "preparation": Preparation, "grams": Grams }, ... } }`. One Choice question per item, id `i0`, `i1`, ...; instructions name the item by path (`foods.i0`), per the indirection advice. Criteria keys are `f<FoodItemId>`, values `"<Name> | <NameBg> | <kcal> kcal, <protein> g protein per 100 g"` (NameBg part omitted when null), plus `"none": "None of these entries is this food, or only a different food or form of it is listed."` The exact instruction wording comes from Phase 0 (Task 0), copied verbatim.

**Shortlist.** Written for approach (b): Rank's top 5, merged across the English and Bulgarian searches exactly as `ResolveAsync` does today, deduplicated by id keeping the higher Rank score, in Rank order. If Phase 0 picks (c), only `FoodMatcher.Shortlist` changes: trigram top 40 (`SearchAsync(..., 50, 0.3)` for NameEn and NameOriginal, merged by max similarity), Rank not used for ordering. Items with no candidates are not sent; they take the existing no-match path.

**Mapping.** Jev picks `f<id>` in the shortlist: use that food; `Confidence.High` when Jev's confidence is at least `JevHighConfidence`, else `Medium`. Jev picks `none`: the existing no-match path (AI estimate, `Low`, existing notes). Unknown key or missing answer: Rank top-1 for that item, as today. `JevHighConfidence` is set from the Phase 0 data (the lowest confidence at which every Jev pick in the set was correct, rounded down to 0.05), recorded in the Task 0 results.

**Failure.** Per-call timeout `JevTimeout` = 3 s (Phase 0 reports p95 latency; if p95 is above 1 s, revisit before Task 2). No retry. Timeout, any non-2xx, network error or unreadable body: log a warning and return null. The parser keeps its own 15 s per attempt, so a parse stays within its existing budget plus at most 3 s.

**Version.** Pin `typesafe/jev-1.13` as a constant (`JevFoodMatchDecider.Model`). Moving to a new version means rerunning the Phase 0 eval and adjusting `JevHighConfidence`, so it is a code change, not configuration.

**Settings.** A shared on/off setting `OpenRouter:JevMatching` in `AppSettings`, like the model, default off. Shown under Settings → Food text parser as "Pick database matches with Jev", only when a key exists. Off, or no key: today's behaviour.

**Traceability.** New nullable `FoodLogEntry.MatchModel` (max 100), the Jev version that decided that row's match (including a "none"), null when Rank decided. Carried through the confirm form per row, like `FoodItemId`, and added to the CSV export. One log line per call: resolved model, item count, latency, input tokens, `usage.cost`.

**Rate limits.** The Jev call happens inside `POST /food/parse`, which is already limited to 30 per user per 10 minutes, so one parse is one parser call plus at most one Jev call. Nothing to add.

## Open decisions (recommendation first)

1. **Version: pin `typesafe/jev-1.13`** rather than `~typesafe/jev-latest`. Thresholds are tuned per version and the alias can move without a deploy; the response's `model` is logged either way.
2. **Toggle: shared on/off in Settings, default off**, rather than always on with a key. It doubles as a kill switch for an alpha endpoint, and lets Antoan turn it on after the deploy is checked.
3. **No production fake decider.** Without a key or with the toggle off, the decider returns null and the matcher runs today's Rank path, which is also the failure fallback. A production fake that copied Rank's top-1 would duplicate that path. Tests use a scripted `StubMatchDecider`. (This departs from the FakeFoodParser pattern on purpose; say if you want the fake anyway.)
4. **Traceability: a `MatchModel` column** rather than only a log line. SPEC keeps `AiModel` so "a shift in estimates after a model change can be traced"; a matcher version change shifts numbers the same way, and container logs don't survive. It costs one migration and one hidden field.
5. **One request per parse, all items in the state**, rather than one request per item. The other items are a small distractor (TypeSafe warns about irrelevant state), but the diary text is context for every item and one call keeps latency and cost predictable. Phase 0 runs it this way, so the measurement covers the trade-off.
6. **Shortlist (b) or (c)**: decided by Phase 0.

## Review Focus

- A Jev answer naming an option that was not offered (or a hallucinated `f<id>` of a food the user cannot see) must never become a match: only ids from that item's shortlist are accepted. Test in Task 1.
- A parse where every item has no candidates must not call Jev at all. Test in Task 4.
- Jev slow but under the HttpClient timeout: the 3 s per-call timeout, not the 10 s client timeout, must win. Test in Task 2.
- The user switches the toggle off while a key is set: no Jev calls. Test in Task 3.
- A confirmed row whose match the user did not change keeps its `MatchModel`; a row with no `FoodItemId` (estimate) still records `MatchModel` when Jev said none. Test in Task 5.

---

## Phase 0: prove it before building it

### Task 0: Evaluation harness and labelled set (no production code)

**Files:**
- Create: `tests/GymTracker.Web.Tests/JevEval/JevEvalTests.cs` (the harness; skipped unless `GYM_JEV_EVAL` is set)
- Create: `tests/GymTracker.Web.Tests/JevEval/jev-eval-set.json` (entries, frozen parser output, labels)
- Create: `tests/GymTracker.Web.Tests/JevEval/README.md` (how to run, labelling rules)
- Create: `docs/superpowers/plans/2026-10-04-jev-food-matching-results.md` (the measured results)

How it works:
- `GYM_JEV_EVAL=prepare dotnet test --filter JevEval` parses each entry's text with the real `OpenRouterFoodParser` (default model) and freezes the output into the set file, then writes a candidates report (Rank top 5, trigram top 50, and whether each item already has a label) for labelling. Existing parsed output is kept unless `GYM_JEV_EVAL_REPARSE=1`.
- Labels: per item, a list of acceptable foods (`usda:<Name>`, `bg:<NameBg>`, `off:<Barcode>`, stable across databases) or `none`.
- `GYM_JEV_EVAL=score dotnet test --filter JevEval` runs the three approaches on the frozen items: (a) `FoodMatcher.ResolveAsync` as it is today; (b) Jev over Rank's top 5; (c) Jev over trigram top 50 with Rank removed. Each Jev approach runs twice, options in listed and in reversed order, to measure the first-option bias. Writes the results report.
- Runs against the dev database (`docker/docker-compose.dev.yml`, which has the USDA, Bulgarian dish and product seeds; the Testcontainers fixture seeds USDA only). `GYM_EVAL_DB` overrides the connection string. Searches run as a user with no own foods, so only shared foods are candidates.
- Key from `OPENROUTER_API_KEY`, else `~/.config/gymtracker/openrouter-key`. Never printed or written anywhere.

Measured, overall and for Bulgarian and English entries separately:
- Accuracy: the pick is in the acceptable set, or the label is `none` and nothing was picked.
- "None" detection: on `none` items, how often nothing was picked; on matchable items, how often a correct food was wrongly rejected.
- Shortlist recall: for matchable items, how often an acceptable food is in Rank's top 5 and in the trigram top 30, 40 and 50.
- Order sensitivity: agreement between listed and reversed order.
- Latency per parse (each Jev call timed; median and max) and cost per parse (`usage.cost`, input tokens).
- Accuracy by Jev confidence bucket, to set `JevHighConfidence`.

**Go / no-go.** Continue to Phase 1 with the better of (b) and (c) only if it is clearly better on Bulgarian (at least 3 more items correct and at least 10 points above Rank), no worse on English (at most 1 item fewer correct than Rank), catches at least half of the `none` items, rejects at most 1 in 20 matchable items, has a max latency under 2 s, and is stable under reordering (at least 90% agreement). Otherwise stop and record why. With about 40 items one item is 2.5 points, so smaller differences are noise.

- [ ] **Step 1:** Write the harness and the entry texts; run `prepare`; draft labels from the database.
- [ ] **Step 2:** Antoan confirms the labels (they decide the result).
- [ ] **Step 3:** Run `score`; write the results file with the recommendation; commit.
- [ ] **Step 4:** Antoan decides: stop, (b) or (c). Fill `JevHighConfidence` and the instruction wording into this plan.

`dotnet build` and `dotnet test` must stay green with the harness present (it reports as skipped).

### Phase 0 outcome (2026-10-04)

Results: `2026-10-04-jev-food-matching-results.md` (jev-1.13-20260917, 41 items, labels confirmed before scoring). Neither Jev approach passes the go/no-go above, so Phase 1 is not started.

- (c) beats (b) on every measure; (b) is not worth pursuing.
- (c) against Rank: 36/41 against 31/41; it fixes 8 items (all four wrong "none" cases, scrambled and hard-boiled eggs, white cheese, dry pasta) and breaks 3 (кисело мляко, лютеница, cooked rice: correct candidates rejected as "none").
- Bars failed by (c): Bulgarian +2 items (bar: +3); 4/33 matchable items rejected (bar: 1 in 20); 85% agreement under reordering (bar: 90%). Passed: none caught 8/8, max latency 564 ms. Cost $0.0002 per parse.
- Label gap found after scoring: en05.1 omits one acceptable 2% USDA milk that (c) picked; correcting it gives (c) 21/22 English and changes no pass/fail. The confirmed labels were left as they are.
- The one variant worth testing, if anything: (c) with Jev's "none" trusted only at confidence 0.9 or more, otherwise Rank's top pick. That cut-off was chosen after seeing these results, so it needs a fresh held-out set and repeat runs (to separate run-to-run noise from order bias) before it counts as evidence.

### Phase 0b: held-out round for gated (c) (pre-registered 2026-10-04, before the new set exists)

- **Set:** `JevEval/jev-eval-set-2.json`, about 30 new items, none reused from round 1, mostly Bulgarian staples, plus English entries and about 6 foods with no correct match. Parsed by the parser once and frozen; labels drafted by the same rules and confirmed by Antoan before scoring.
- **Rule under test, "gated c":** Jev chooses from the trigram top 50 plus `none`. A food pick is used. A `none` with confidence 0.9 or more is used. A `none` below 0.9, a missing answer or an error falls back to Rank's top pick. Nothing about this rule changes after scoring.
- **Repeats:** Jev runs 3 times in listed order and 3 times in reversed order. Run-to-run agreement within the same order measures noise; listed against reversed, compared with that, measures order bias.
- **Reported:** a) Rank, plain c and gated c, each as the mean and the range over the runs; the same bars as Phase 0; agreement figures; latency and cost. Gated c on the round 1 set is reported as in-sample only.
- **Go:** gated c passes every Phase 0 bar on the held-out set in all 3 listed-order runs: Bulgarian at least 3 items and 10 points above Rank, English at most 1 item below Rank, at least half the `none` items caught, at most 1 in 20 matchable items rejected, max latency under 2 s, and at least 90% of items with the same final decision across all 6 runs. Otherwise stop.

---

## Phase 1: build it (only after the go decision)

### Task 1: Decider types and the Jev request and response

**Files:**
- Create: `src/GymTracker.Core/Nutrition/FoodMatchDecider.cs` (the records and interface from "Design")
- Create: `src/GymTracker.Web/Integrations/JevFoodMatchDecider.cs` (static `Body` and `Read` only in this task)
- Test: `tests/GymTracker.Web.Tests/JevTests.cs`

**Interfaces:**
- Produces: `internal static JsonObject JevFoodMatchDecider.Body(string text, IReadOnlyList<MatchQuestion> questions)`; `internal static MatchDecisions? JevFoodMatchDecider.Read(JsonNode? response, IReadOnlyList<MatchQuestion> questions)`; `public const string Model = "typesafe/jev-1.13"`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void One_request_holds_every_item_with_a_none_option()
// Two questions (chicken: 2 candidates, rice: 1). Body: model == "typesafe/jev-1.13";
// state.diary_entry == text; state.foods.i0.as_written == "пилешко филе", .english, .state == "cooked", .grams == 200;
// questions has exactly keys i0, i1, each type "choice"; i0.criteria keys == f11, f12, none (in that order);
// criteria f11 contains the English name, the Bulgarian name and "165 kcal"; i1.criteria has no Bulgarian part when NameBg is null.

[Fact] public void Picks_none_and_confidence_are_read_per_item()
// answers: i0 choice f12 confidence 0.91; i1 choice none confidence 0.7; model "typesafe/jev-1.13-20260917".
// Read => Items[0] == new MatchDecision(12, 0.91m), Items[1] == new MatchDecision(null, 0.7m), Model == "typesafe/jev-1.13-20260917".

[Fact] public void Unknown_or_missing_answers_leave_that_item_undecided()
// i0 choice "f999" (not in its shortlist), i1 missing => Items == [null, null]; no "answers" at all => null.
```

- [ ] **Step 2:** Run `dotnet test --filter JevTests`; expect compile failure.
- [ ] **Step 3:** Implement the records, the interface, `Body` and `Read`. Instruction text and the `none` description verbatim from the Task 0 results.
- [ ] **Step 4:** `dotnet build && dotnet test`; all pass.
- [ ] **Step 5:** Commit "Add the Jev match decision request and response".

### Task 2: The Jev HTTP call

**Files:**
- Modify: `src/GymTracker.Web/Integrations/JevFoodMatchDecider.cs`
- Modify: `src/GymTracker.Web/Program.cs` (typed client next to `OpenRouterFoodParser`, `OpenRouterClient(c)`, `c.Timeout = 10 s`)
- Test: `tests/GymTracker.Web.Tests/JevTests.cs` (a `StubJev : HttpMessageHandler` like `StubOpenRouter`, with status, body and delay)
- Modify: `tests/GymTracker.Web.Tests/JevEval/JevEvalTests.cs` (use `Body`/`Read`, so the eval keeps measuring the shipped request)

**Interfaces:**
- Produces: `public Task<MatchDecisions?> DecideAsync(string text, IReadOnlyList<MatchQuestion> questions, string apiKey, CancellationToken ct)` on `JevFoodMatchDecider(HttpClient http, ILogger<JevFoodMatchDecider> log)`; `internal static TimeSpan Timeout = 3 s` (settable by tests).

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task Posts_to_the_decisions_endpoint_with_the_key()
// stub sees POST .../api/alpha/decisions, Bearer key; result Items[0].FoodItemId == 11.

[Theory] [InlineData(500)] [InlineData(401)] [InlineData(429)] [InlineData(502)]
public async Task Http_errors_return_null_without_retrying(int status) // result null, stub.Calls == 1

[Fact] public async Task A_slow_answer_returns_null_after_the_timeout() // Timeout = 200 ms, stub delays 2 s => null within 1 s

[Fact] public async Task Malformed_json_returns_null() // body "<html>" => null
```

- [ ] **Step 2:** Run; expect failures.
- [ ] **Step 3:** Implement: linked CTS with `Timeout`; any exception except caller cancellation, non-2xx or unreadable body logs a warning and returns null. On success log Information "Jev {Model} decided {Count} items in {Ms} ms, {Tokens} input tokens, ${Cost}".
- [ ] **Step 4:** `dotnet build && dotnet test`; all pass. Run `GYM_JEV_EVAL=score dotnet test --filter JevEval` once and check the numbers match Task 0's within noise.
- [ ] **Step 5:** Commit "Call Jev's Decisions API with a short timeout and no retry".

### Task 3: Settings toggle and the switching decider

**Files:**
- Modify: `src/GymTracker.Web/Integrations/OpenRouterSettings.cs` (`JevSettingName = "OpenRouter:JevMatching"`, `GetJevMatchingAsync`, `SetJevMatchingAsync(bool)`; add `SwitchingMatchDecider : IFoodMatchDecider` beside `SwitchingFoodParser`)
- Modify: `src/GymTracker.Web/Program.cs` (`AddScoped<IFoodMatchDecider, SwitchingMatchDecider>()`)
- Modify: `src/GymTracker.Web/Controllers/SettingsController.cs` (`POST /settings/jev-matching`, with the existing per-user settings rate limit)
- Modify: `src/GymTracker.Web/Views/Settings/Index.cshtml` (a toggle in the Food text parser section, shown only when a key is set)
- Modify: `src/GymTracker.Web/Resources/strings.bg.json`
- Test: `tests/GymTracker.Web.Tests/OpenRouterTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task Jev_matching_is_off_until_turned_on_and_needs_a_key()
// No key, on => SwitchingMatchDecider returns null and the stub sees no call.
// Key, default (off) => null, no call. Key, SetJevMatchingAsync(true) => stub called once, result not null.
// SetJevMatchingAsync(false) => null again.
```

- [ ] **Step 2:** Run; expect failures.
- [ ] **Step 3:** Implement. Strings: "Pick database matches with Jev", "Jev, a decision model, chooses which database food each item is, or says none fits. Uses the same key; costs a fraction of a cent per entry." Bulgarian drafted with `tools/i18n/translate.mjs` and reviewed.
- [ ] **Step 4:** `dotnet build && dotnet test` (CatalogueTests included); check the toggle at 390 px in the browser.
- [ ] **Step 5:** Commit "Add a Settings switch for Jev food matching".

### Task 4: FoodMatcher asks the decider

**Files:**
- Modify: `src/GymTracker.Web/Features/Food/FoodMatcher.cs` (constructor takes `IFoodMatchDecider`; `ResolveAsync(string text, IEnumerable<ParsedFoodItem> parsed, CancellationToken ct)`; new `internal Shortlist`; `JevHighConfidence` constant)
- Modify: `src/GymTracker.Web/Controllers/FoodController.cs:68` (pass `text`)
- Modify: `tests/GymTracker.Web.Tests/UserScope.cs` (`Matcher` built with a settable `StubMatchDecider`, default returns null)
- Create: `tests/GymTracker.Web.Tests/StubMatchDecider.cs`
- Test: `tests/GymTracker.Web.Tests/NutritionTests.cs`, `tests/GymTracker.Web.Tests/FoodMatchingTests.cs` (new, Postgres collection)

**Interfaces:**
- Consumes: `IFoodMatchDecider`, `MatchQuestion`, `MatchDecisions` (Task 1).
- Produces: `ConfirmItem` gains `string? MatchModel` (last positional parameter).

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task The_deciders_pick_replaces_ranks_top_choice()
// "chicken breast" raw: stub picks the shortlist's second candidate with 0.95 => FoodItemId == that id,
// Confidence.High, MatchModel == stub's model. Stub saw one call with one question of at most 5 candidates.

[Fact] public async Task None_from_the_decider_uses_the_ai_estimate()
// stub answers none => FoodItemId null, Confidence.Low, Per100 == the parser's estimate, the "No database match" note.

[Fact] public async Task Low_confidence_picks_are_medium() // 0.3 => Confidence.Medium

[Fact] public async Task Without_a_decision_the_matcher_behaves_as_before()
// stub returns null => identical ConfirmItems to the current tests' expectations (rerun the three existing matcher tests).

[Fact] public async Task Items_without_candidates_are_not_sent()
// "zzqx florp" alone => stub never called; with "banana" => one question.

[Fact] public async Task An_undecided_item_falls_back_to_rank_alone() // Items == [decision, null]
```

Plus an end-to-end test through `/food/parse` with `AppFactory` and `StubMatchDecider` registered, asserting the confirm page shows the stub's pick and carries its `MatchModel` in a hidden field (that field lands in Task 5; in this task assert the matched name only).

- [ ] **Step 2:** Run; expect failures.
- [ ] **Step 3:** Implement: rank as today; build the shortlist; call the decider once with every item that has candidates; apply the mapping from "Design".
- [ ] **Step 4:** `dotnet build && dotnet test`; all pass.
- [ ] **Step 5:** Commit "Let Jev choose the database food from Rank's shortlist".

### Task 5: Record which model chose the match

**Files:**
- Modify: `src/GymTracker.Core/Domain/Nutrition.cs` (`FoodLogEntry.MatchModel`), `src/GymTracker.Web/Data/AppDbContext.cs` (`HasMaxLength(100)`)
- Create: migration `AddMatchModel` (`dotnet ef migrations add AddMatchModel --project src/GymTracker.Web --output-dir Data/Migrations`)
- Modify: `src/GymTracker.Web/Features/Food/FoodLogService.cs` (`NewEntry` gains `string? MatchModel = null`), `src/GymTracker.Web/Controllers/FoodController.cs` (`ConfirmRow.MatchModel`), `src/GymTracker.Web/Views/Food/Confirm.cshtml` (hidden `items[i].MatchModel`), `src/GymTracker.Web/Features/Export/DataExport.cs` (column after `AiModel`)
- Test: `tests/GymTracker.Web.Tests/FoodMatchingTests.cs`, `tests/GymTracker.Web.Tests/ExportTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task Confirmed_rows_keep_the_match_model()
// POST /food/confirm with two rows: one with FoodItemId and MatchModel "typesafe/jev-1.13-20260917",
// one estimate (no FoodItemId) with the same MatchModel => both saved with MatchModel; a third row without => null.
// Export food_log.csv header contains "MatchModel" and the row value.
```

- [ ] **Step 2:** Run; expect failures.
- [ ] **Step 3:** Implement; the confirm form trims `MatchModel` to 100 characters like other free text.
- [ ] **Step 4:** `dotnet build && dotnet test`; all pass (the fixture applies the migration).
- [ ] **Step 5:** Commit "Record the model that chose each AI entry's match".

### Task 6: Spec, README and deploy notes

**Files:**
- Modify: `SPEC.md`
  - Section 2, LLM row: add "Food matches chosen by TypeSafe Jev (`typesafe/jev-1.13`) through OpenRouter's Decisions API when switched on in Settings."
  - Section 3: Integrations line adds "Jev match decider".
  - Section 4, FoodLogEntry: `MatchModel` (nullable; the Jev version that chose the match, so a shift after a version change can be traced).
  - Section 6.1, "Resolving nutrition values": step 1 becomes search and rank to a shortlist (b: Rank top 5; c: trigram top 40); new step 2: when Jev matching is on and a key exists, one `POST /api/alpha/decisions` per parse with one Choice per item plus `none`, pinned version, 3 s timeout, no retry, any failure uses the ranking's top food; `none` goes to the estimate path; confidence mapping with the Phase 0 threshold. Note the English-first language caveat and that the eval (`tests/GymTracker.Web.Tests/JevEval`) must be rerun before changing the version.
  - Section 7, Settings: the Jev switch.
- Modify: `README.md`: the Settings paragraph under "First time" (the switch, default off, same key, cost); "Food data" gets one sentence on the eval and how to run it.
- `docker/.env.example`: no change (no new configuration). State this in the commit message.

- [ ] **Step 1:** Edit the three documents.
- [ ] **Step 2:** `dotnet build && dotnet test`.
- [ ] **Step 3:** Commit "Document Jev food matching".
- [ ] **Step 4:** Deploy notes for Antoan: rsync, `up -d --build` (the migration runs on start), switch Jev on in Settings, check `docker logs gymtracker-app | grep "Jev "` after a few entries.
