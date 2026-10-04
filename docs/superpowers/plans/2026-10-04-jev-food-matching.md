# Jev Food Matching Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let TypeSafe's Jev decision model pick which database food each parsed item is, from a shortlist, or say none fits, instead of trusting the top of `FoodMatcher.Rank`.

**Architecture:** The LLM parser is unchanged. `FoodMatcher` builds a plain trigram shortlist per item; one Jev Decisions API call per parse answers one Choice question per item (shortlist plus `none`). Jev's food pick is used; its `none` only at confidence 0.9 or more; anything else, and any failure, falls back to today's Rank top pick, so logging food never depends on Jev. The AI model setting is split per task (parser, matcher).

**Tech Stack:** .NET 10, ASP.NET Core MVC, EF Core + PostgreSQL 18 (`pg_trgm`), typed `HttpClient`, `System.Text.Json`, xUnit + FluentAssertions + Testcontainers. No new packages.

**Spec:** `SPEC.md` sections 2, 3, 4, 6.1 and 7 (updated in Task 6). The design decisions are in this document, in "Design" below, because the request that started this work is the design brief.

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

Decided after Phase 0b (2026-10-04): gated (c). Antoan chose to proceed with it and to split the AI model setting per task.

**Where things live.** `IFoodMatchDecider` and its records go in `GymTracker.Core/Nutrition/FoodMatchDecider.cs`, next to `IFoodParser`; they use only `ParsedFoodItem` and `Per100g`. The Jev implementation is `GymTracker.Web/Integrations/JevFoodMatchDecider.cs` (typed `HttpClient`). Shortlisting, the gate and the confidence mapping stay in `FoodMatcher`, a Web feature service, as today.

```csharp
public record MatchCandidate(int Id, string Name, string? NameBg, string? Brand, Per100g Per100);
public record MatchQuestion(ParsedFoodItem Item, IReadOnlyList<MatchCandidate> Candidates);
/// FoodItemId null means the decider said none of the candidates is this food.
public record MatchDecision(int? FoodItemId, decimal Confidence);
/// Decisions in the same order as the questions (null: that item was not decided); Model is the version that answered.
public record MatchDecisions(IReadOnlyList<MatchDecision?> Items, string Model);

public interface IFoodMatchDecider
{
    /// Null when no decision could be made (matcher off, no key, timeout, error); the caller uses Rank's top pick.
    Task<MatchDecisions?> DecideAsync(string text, IReadOnlyList<MatchQuestion> questions, CancellationToken ct);
}
```

**Request** (as measured in Phase 0). One request per parse to `POST ../alpha/decisions` (relative to the OpenRouter base URL). State: `{ "diary_entry": <text>, "foods": { "i0": { "as_written", "english", "state": "raw"|"cooked"|"not applicable", "preparation", "grams" }, ... } }`. One Choice question per item that has candidates, id `i<n>`. Criteria keys `f<FoodItemId>` in shortlist order, then `none`. Copied verbatim from the evaluation:
- Instructions: `Which database entry is the food described in foods.{n}? The diary_entry is what the user typed. Pick the entry that is the same food in the same form: raw or cooked as the food's state says, and the preparation if one is given. Pick none if no entry is this food, for example when the entries are only a different food, a different form of it, or a dish that merely contains it.`
- Candidate description: `{Name}{" / " + NameBg when present and different}{" (" + Brand + ")" when present}: {kcal:0} kcal, {protein:0.#} g protein per 100 g` (invariant culture).
- `none`: `None of these entries is this food.`

**Shortlist.** Plain trigram top 50: `SearchAsync(NameEn, 50, 0.3)` plus `SearchAsync(NameOriginal, 50, 0.3)` when the two differ, merged by id keeping the higher similarity, ordered by similarity then shorter name, first 50. Rank is not used for the shortlist. Items with no candidates are not sent.

**Gate and mapping.** Rank still runs as today and supplies the fallback.
- Jev picks `f<id>` from that item's shortlist: that food; `Confidence.High` at Jev confidence 0.95 or more (`JevHighConfidence`: the only cut-off where every Jev food pick in both rounds was correct, with or without the label slips), otherwise `Medium`.
- Jev picks `none` at confidence 0.9 or more (`NoneGate`, pre-registered): the existing no-match path (AI estimate, `Low`, existing notes).
- `none` below 0.9, an unknown key, a missing answer, or no decision at all: Rank's top pick exactly as today.

**Failure.** Per-call timeout 3 s (Phase 0 max was 1,011 ms). No retry. Timeout, non-2xx, network error or unreadable body: log a warning and return null.

**AI model settings, per task.** `AiTask { Parser, Matcher }` with one shared setting each, built so a later task is one more enum value and one more entry:
- Parser: OpenRouter's chat catalog, as today. The stored name stays `OpenRouter:Model`, so the model already chosen on wizard carries over; fallback `OPENROUTER_MODEL`, then `openai/gpt-6-luna`.
- Matcher: a fixed list, because decision models are not in `/api/v1/models`: `off` ("Database ranking only") and `typesafe/jev-1.13`. Stored as `OpenRouter:Model:Matcher`, default `off`. Only versions that were evaluated are offered; a new Jev version means evaluating it and adding it to the list.
- Settings shows one row per task under the OpenRouter section, each opening `/settings/model/{task}`, the existing picker reused. Choosing a Jev model runs a one-question test decision and reports the outcome, like the parser's test.

**Traceability.** New nullable `FoodLogEntry.MatchModel` (max 100): the Jev version that decided the row's match (a food or a gated none), null when Rank decided. Carried through the confirm form per row and added to the CSV export. One log line per call: resolved model, item count, latency, input tokens, `usage.cost`.

**Rate limits.** The Jev call happens inside `POST /food/parse`, already limited to 30 per user per 10 minutes. Nothing to add.

## Decisions

1. **Version: pinned `typesafe/jev-1.13`**, offered as the only matcher model; the response's dated snapshot is recorded.
2. **Matcher model setting, default off**, replacing the earlier on/off toggle: the same thing, in the shape of the per-task model settings.
3. **No production fake decider.** With the matcher off or no key the decider returns null and Rank runs as today, which is also the failure path. Tests use a scripted `StubMatchDecider`.
4. **`MatchModel` column** rather than only a log line.
5. **One request per parse, all items in the state**, as measured.
6. **Gated (c)**, decided by Phase 0b and Antoan.
7. **The evaluation files are deleted when everything is implemented** (Antoan, 2026-10-04): the harness and sets under `tests/GymTracker.Web.Tests/JevEval/`. The plan and the two results files stay as the record; the harness, both labelled sets and their frozen parser output stay in git history (last at commit 8dd4262) if a new Jev version needs evaluating. Before deletion, the shipped `FoodMatcher` with the real Jev reproduced the Phase 0b gated (c) result exactly on the held-out set: 29/32, Bulgarian 21/23, English 8/9, 7/8 none caught, 0/24 rejected.

## Review Focus

- A Jev answer naming an option that was not offered for that item must never become a match: only ids from that item's shortlist are accepted. Test in Task 1.
- A parse where every item has no candidates must not call Jev at all. Test in Task 4.
- Jev slow but under the HttpClient timeout: the 3 s per-call timeout must win. Test in Task 2.
- The matcher set to Off while a key is set: no Jev calls. Test in Task 3.
- An estimate row (no `FoodItemId`) still records `MatchModel` when Jev's gated none decided it. Test in Task 5.

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

### Phase 0b outcome (2026-10-04)

Results: `2026-10-04-jev-food-matching-results-2.md` (32 items, labels confirmed before scoring, 3 runs per order, 168 calls, about $0.03).

- As scored with the confirmed labels, gated c is 29/32 in every listed run against Rank's 24/32, and passes every bar except Bulgarian: +2 items and +9 points against a bar of +3 and +10. That is a no-go by one item.
- Labelling slip found after scoring: r2bg01 (кюфтета). Its confirmed note accepts "plain pork or pork-and-beef кюфтета, generic or branded", but the regex that built the list missed "Велико кюфте от свинско и говеждо" (off:4056489941552), which gated c picks in all three listed runs. Counted under the note as written, gated c is +3 items and +13 points on Bulgarian and passes every bar in all runs. The same kind of slip affects r2en01.1 ("Milk, producer, fluid, 3.7% milkfat" is whole milk within the note's 3 to 3.7%), which changes no bar. Labels are not edited until Antoan decides.
- Every other gain over Rank is a no-match case caught (falafel wrap, bubble tea, pad thai, cappuccino, кашкавал пане, чушки бюрек). On matchable items gated c rejected none in any run; the gate turned the three reversed-order "none" answers for кавърма back into Rank's correct pick.
- Noise against order: the raw answer was the same in 31/32 items across the three listed runs and 32/32 across the reversed runs, but only 28/32 across all six, so the instability is mostly the option order, not randomness. Gated c's final decision was the same in 29/32 (91%).
- Latency: median 333 ms, max 1,011 ms. Cost $0.00019 per parse.

---

## Phase 1: build it

Executed inline in this session (executing-plans). Every task ends with `dotnet build` and `dotnet test` green, then a commit.

### Task 1: Decider types and the Jev request and response

**Files:** create `src/GymTracker.Core/Nutrition/FoodMatchDecider.cs` (the records and interface above) and `src/GymTracker.Web/Integrations/JevFoodMatchDecider.cs` (static `Body` and `Read`); test `tests/GymTracker.Web.Tests/JevTests.cs`.

**Produces:** `internal static JsonObject Body(string model, string text, IReadOnlyList<MatchQuestion> questions)`; `internal static MatchDecisions? Read(JsonNode? response, IReadOnlyList<MatchQuestion> questions)`; `public const string Jev113 = "typesafe/jev-1.13"`.

- [ ] Tests first:
  - `One_request_holds_every_item_with_a_none_option`: two questions (chicken: candidates 11 and 12, rice: 13 with no NameBg). `model`, `state.diary_entry`, `state.foods.i0.as_written/english/state/grams`; `questions` has keys `i0`, `i1`, type `choice`; `i0.criteria` keys are `f11`, `f12`, `none` in that order; `f11` contains the English name, the Bulgarian name and `165 kcal`; `f13` has no " / ".
  - `Items_without_candidates_get_no_question`: the second item has none, so `questions` has only `i0`, but `state.foods` still has `i1`.
  - `Picks_none_and_confidence_are_read_per_item`: `i0` → `f12` at 0.91, `i1` → `none` at 0.7, model `typesafe/jev-1.13-20260917` → `[(12, 0.91), (null, 0.7)]` and that model.
  - `Unknown_or_missing_answers_leave_that_item_undecided`: `i0` → `f999`, `i1` missing → `[null, null]`; no `answers` → null.
- [ ] Implement; build and test; commit "Add the Jev match decision request and response".

### Task 2: The Jev HTTP call

**Files:** modify `JevFoodMatchDecider.cs` and `Program.cs` (typed client, `OpenRouterClient(c)`, `c.Timeout` 10 s); test `JevTests.cs` with a `StubJev` handler (status, body, delay, call count).

**Produces:** `public Task<MatchDecisions?> DecideAsync(string text, IReadOnlyList<MatchQuestion> questions, string apiKey, string model, CancellationToken ct)`; `public Task<LocalText?> TestAsync(string apiKey, string model, CancellationToken ct)` (one banana question with "Bananas, raw" and "Pepper, banana, raw"; null when it picks the banana); `internal TimeSpan Timeout` (default 3 s, settable in tests).

- [ ] Tests first: `Posts_to_the_decisions_endpoint_with_the_key` (path ends `/api/alpha/decisions`, Bearer key, result decided); `Http_errors_return_null_without_retrying` (500, 401, 429, 502 → null, one call); `A_slow_answer_returns_null_after_the_timeout` (200 ms timeout, 2 s delay → null within 1 s); `Malformed_json_returns_null`; `The_test_decision_reports_what_went_wrong` (401 → message about the key; good answer → null).
- [ ] Implement; build and test; commit "Call Jev's Decisions API with a short timeout and no retry".

### Task 3: Model settings per AI task, and the switching decider

**Files:** create `src/GymTracker.Web/Integrations/AiTasks.cs` (`AiTask`, the matcher's fixed model list, `Off`); modify `OpenRouterSettings.cs` (`GetModelAsync(AiTask, ct)`, `SetModelAsync(AiTask, string, ct)`, `ListModelsAsync(AiTask, ct)`; `SwitchingMatchDecider : IFoodMatchDecider`; `SwitchingFoodParser` uses the parser task), `Program.cs`, `SettingsController.cs` (`GET`/`POST /settings/model/{task}`; the matcher save runs `TestAsync` when a key exists), `ViewModels.cs` (`ParserStatus` becomes the OpenRouter status with one entry per task; `ModelPickerViewModel` gains the task), `Views/Settings/Index.cshtml`, `Views/Settings/Model.cshtml` (title and footer per task), `Resources/strings.bg.json`; test `OpenRouterTests.cs`.

- [ ] Tests first:
  - `Each_task_has_its_own_model`: parser default and override as today; matcher defaults to `off`; setting the matcher to `typesafe/jev-1.13` leaves the parser unchanged; the parser still reads the existing `OpenRouter:Model` row.
  - `Only_listed_models_can_be_picked_per_task`: `openai/gpt-x` for the matcher and `typesafe/jev-1.13` for the parser are rejected.
  - `The_matcher_runs_only_with_a_key_and_a_model`: no key + Jev → null, no call; key + off → null, no call; key + Jev → one call, decided.
  - Controller: `POST /settings/model/matcher` with `typesafe/jev-1.13` saves it (AppFactory).
- [ ] Implement; strings through `L[...]`, Bulgarian added; check the section and both pickers at 390 px.
- [ ] Build and test (CatalogueTests included); commit "Split the AI model setting into parser and matcher".

### Task 4: FoodMatcher asks the decider

**Files:** modify `Features/Food/FoodMatcher.cs` (takes `IFoodMatchDecider`; `ResolveAsync(string text, IEnumerable<ParsedFoodItem> parsed, CancellationToken ct)`; `internal static` shortlist merge; `NoneGate`, `JevHighConfidence`; `ConfirmItem` gains `string? MatchModel`), `Controllers/FoodController.cs` (pass `text`), `tests/GymTracker.Web.Tests/UserScope.cs` (matcher with a settable `StubMatchDecider`, default null); create `tests/GymTracker.Web.Tests/StubMatchDecider.cs`; test `tests/GymTracker.Web.Tests/FoodMatchingTests.cs` (Postgres collection).

- [ ] Tests first:
  - `A_jev_pick_replaces_ranks_top_choice`: chicken breast raw; the stub picks a different candidate at 0.97 → that id, `High`, `MatchModel` set; the stub saw one question with at most 50 candidates, in trigram order.
  - `Picks_below_the_high_cut_are_medium`: 0.6 → `Medium`.
  - `A_confident_none_uses_the_ai_estimate`: none at 0.95 → no `FoodItemId`, `Low`, the parser's numbers, the "No database match" note, `MatchModel` set.
  - `An_unsure_none_falls_back_to_rank`: none at 0.5 → Rank's pick and confidence, `MatchModel` null.
  - `Without_a_decision_the_matcher_behaves_as_before`: stub null → the existing NutritionTests matcher expectations hold.
  - `Items_without_candidates_are_not_sent`: "zzqx florp" alone → no call; with "banana" → one question.
  - End to end: `/food/parse` with the stub registered shows the stub's pick on the confirm page.
- [ ] Implement; build and test; commit "Let Jev choose the database food, gated by confidence".

### Task 5: Record which model chose the match

**Files:** `Core/Domain/Nutrition.cs` (`FoodLogEntry.MatchModel`), `Data/AppDbContext.cs` (max 100), migration `AddMatchModel`, `FoodLogService.cs` (`NewEntry` gains `string? MatchModel = null`), `FoodController.cs` (`ConfirmRow.MatchModel`), `Views/Food/Confirm.cshtml` (hidden `items[i].MatchModel`), `Export/DataExport.cs` (column after `AiModel`); test `FoodMatchingTests.cs`, `ExportTests.cs`.

- [ ] Test first: `Confirmed_rows_keep_the_match_model`: a matched row and an estimate row with `MatchModel` and one without are saved as sent (trimmed to 100); the export has a `MatchModel` column with the value.
- [ ] Implement; build and test; commit "Record the model that chose each AI entry's match".

### Task 6: Spec, README and env

- [ ] `SPEC.md`: section 2 LLM row (matching by Jev when chosen in Settings); section 3 Integrations line; section 4 `FoodLogEntry.MatchModel`; section 6.1 the per-task model settings, the matcher, the shortlist, the gate, the thresholds, the failure path, and the English-first caveat; section 7 Settings (parser and matcher models).
- [ ] `README.md`: the OpenRouter paragraph under "First time" (two model settings, matcher default off, same key, cost).
- [ ] `docker/.env.example`: no new variable; say so in the commit message.
- [ ] Build and test; commit "Document Jev food matching".

### Task 7: Check against the real API, then delete the evaluation

- [ ] Run the round 2 set once through the production `FoodMatcher` with the real `JevFoodMatchDecider` (temporary eval method) and check it matches the Phase 0b gated (c) result within one item.
- [ ] Delete `tests/GymTracker.Web.Tests/JevEval/` and its `.gitignore` line. Build and test; commit "Remove the Jev evaluation harness".
- [ ] Deploy notes for Antoan: rsync, `up -d --build` (the migration runs on start), choose Jev under Settings → Food matcher, then `docker logs gymtracker-app | grep "Jev "` after a few entries.
