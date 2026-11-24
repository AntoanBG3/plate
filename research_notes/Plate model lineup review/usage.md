# Production configuration and usage

## Which models were configured on November 24?

### Takeaway

A read-only query against wizard's running production database confirmed every model in the screenshot. Only the estimator has an explicitly stored effort.

### Cited Findings

- Active settings at 19:21 Europe/Sofia on 2026-11-24, from a read-only query of the allowlisted OpenRouter model and effort keys in production `AppSettings`. No credentials were queried. [Setting names](~/Projects/homeserver-gym/src/Plate.Web/Integrations/AiTasks.cs:98) [Stored effort behavior](~/Projects/homeserver-gym/src/Plate.Web/Integrations/OpenRouterSettings.cs:76)

| Task | Configured ID | Effort (production setting and screenshot) | Model last saved, Sofia |
|---|---|---|---|
| Food parser | `meta/muse-ember` | Lowest = minimal | Nov 21 14:52 |
| Food matcher | `google/gemini-decision-1` | No effort setting | Nov 21 12:37 |
| Setup chat | `google/gemini-4-carbon` | Lowest = low | Nov 24 19:16 |
| Weekly summary | `meta/muse-spark-1.3-contributor` | Lowest = minimal | Nov 24 19:14 |
| Label reader | `google/gemini-3.8-flash` | Lowest = low | Nov 24 19:14 |
| Food estimator | `google/gemini-4-carbon` | Medium | Nov 23 21:21 |

- Gemini Decision 1 returned `google/gemini-decision-1-20261111` in recorded OpenRouter responses, while Plate sends the configured unversioned ID. The parser model and effort also power the food namer. [Usage model resolution](~/Projects/homeserver-gym/src/Plate.Web/Integrations/AiUsageRecorder.cs:60) [Food namer settings](~/Projects/homeserver-gym/src/Plate.Web/Integrations/OpenRouterSettings.cs:210)

### Inferences

- The screenshot is suitable as the description of live configuration at review time. Several role settings had been saved just minutes before review.

### Gaps

- The database records explicitly selected efforts. The lowest mappings shown here also use the screenshot, since resolving them in the app would read the model catalog. The catalog researcher provides the independent check.

## What does Plate actually spend, and how fast are these calls?

### Takeaway

The app recorded $0.06904064 across 35 calls in its first approximately 23 hours of available telemetry. Carbon setup accounted for the bulk of that small sample even after likely admin checks were removed.

### Cited Findings

- Production aggregate from `AiCalls`, queried at 2026-11-24 19:20:59 Sofia with first row at Nov 23 20:22:25 and last row at Nov 24 19:16:17. All 35 had an OpenRouter usage cost, none had its transport-level failure flag set. No input text, responses, user identifiers, names or credentials were retrieved. [Live telemetry fields and semantics](~/Projects/homeserver-gym/src/Plate.Web/Integrations/AiUsageRecorder.cs:20)

| Task | Response model | Calls | Total USD | Average USD/call | Median HTTP seconds | p95 HTTP seconds |
|---|---|---:|---:|---:|---:|---:|
| Food parser | Muse Ember | 9 | 0.00241444 | 0.00026827 | 2.083 | 2.947 |
| Food matcher | Gemini Decision 1 (20261111) | 6 | 0.00154720 | 0.00025787 | 0.324 | 0.687 |
| Food namer | Muse Ember | 5 | 0.00067370 | 0.00013474 | 1.745 | 3.356 |
| Setup chat | Gemini 4 Carbon | 9 | 0.04910800 | 0.00545644 | 6.255 | 12.668 |
| Food estimator | Gemini 4 Carbon | 2 | 0.00872200 | 0.00436100 | 7.652 | 11.445 |
| Label reader | Gemini 3.8 Flash | 1 | 0.00138450 | 0.00138450 | 3.122 | 3.122 |
| Weekly summary | Muse Spark 1.3 Contributor | 1 | 0.00011250 | 0.00011250 | 2.603 | 2.603 |
| Label reader | Gemini 4 Carbon (other model) | 1 | 0.00483200 | 0.00483200 | 19.248 | 19.248 |
| Setup chat | Muse Spark 1.3 Contributor (other model) | 1 | 0.00024630 | 0.00024630 | 3.488 | 3.488 |

- Above percentiles use PostgreSQL discrete percentiles, so at these sample sizes p95 is the maximum and median of 2 is the lower one. Parser/matcher/namer/estimator HTTP duration does not equal end-to-end Food latency. Signed-in model/effort changes invoke synthetic task-specific checks, and the recorder captures these through the same clients. A read-only aggregate association of a same-task/same-account admin `key-test` event in the one second after the recorded response identified four likely checks costing $0.00667930. These were the sole Flash label, sole Spark weekly, sole Spark setup and one Carbon setup. The remaining 31 calls cost $0.06236134, with eight Carbon setup calls costing $0.044172 (70.8% of the remaining recorded spend, 0.00552 USD per call). No account identifiers were returned by the query. [Web model checks](~/Projects/homeserver-gym/src/Plate.Web/Controllers/AdminAiController.cs:90) [Test auditing](~/Projects/homeserver-gym/src/Plate.Web/Features/Admin/AiAdmin.cs:84)
- One call apiece to parser, matcher and namer at the sample averages is about $0.000661, about $0.66 for 1,000 inputs that take all three steps. Using the observed mix of those calls costs $0.00463534 across nine parser calls, roughly $0.52 per 1,000 parser calls for those three tasks. At the observed two estimator calls per nine parser calls, total food-pipeline modeled cost is $0.01335734, roughly $1.48 per 1,000 parser calls. The normalized figures are illustrations conditional on the sample's mix, not monthly forecasts. Historical setup Spark vs Carbon synthetic probes are one-off and don't match production turn payloads. [Recorded costs and task separation](~/Projects/homeserver-gym/src/Plate.Web/Integrations/AiUsageRecorder.cs:60)

### Inferences

- Carbon is the immediate latency/cost test target in setup and estimation. The 12-second estimator deadline was approached by one of two Carbon medium requests. Carbon is defensible as a premium on an infrequently used setup flow if Bulgarian language/fill correctness genuinely improves.
- Gemini Decision 1 averaged $0.000258 per recorded request, about $0.26 per 1,000 matcher calls. Retrieval, rejection and precision matter more than the small absolute savings a switch could yield.
- A synthetic Flash label check at 3.122 seconds and one Carbon label scan at 19.248 seconds are directionally consistent with keeping a fast vision model. They use different workloads.

### Gaps

- App usage does not cover earlier days, CLI tests/evals, other workloads with the same OpenRouter key, or any call the recorder was unable to persist. Transport flags can't identify a successful HTTP answer with invalid content, unsupported response or inaccurate nutrition. No monthly bill or comprehensive app reliability rates can be inferred. [Recorder scope and failure check](~/Projects/homeserver-gym/src/Plate.Web/Integrations/AiUsageRecorder.cs:14)

## Does live quality data support a swap?

### Takeaway

The active parser/matcher and label model have too few production outcomes to establish quality rankings. Flash label edits make labels the next visual quality check.

### Cited Findings

- Production 90-day aggregate from `ParseRuns` through the review: Muse Ember + Gemini Decision 1 versioned matcher had 13 parse runs, zero parser errors, 6 confirmed, one of those corrected, 3.860 seconds median end-to-end. Muse Ember without a recorded matcher had 3 runs, zero parser errors, all 3 confirmed, one corrected, 5.172 seconds median. The latter may include cases that bypassed the model because no item needed a decision. The existing report's `Enough` threshold is 20 runs. No user input or individual feedback was queried. [App model outcome definitions](~/Projects/homeserver-gym/src/Plate.Web/Features/Feedback/ModelAccuracyReport.cs:36) [Minimum sample](~/Projects/homeserver-gym/src/Plate.Core/Nutrition/ModelAccuracy.cs:12)
- Production 90-day aggregate from `LabelScans`: Gemini 3.8 Flash had 6 scans, one read error, three saves and all three saved scans edited, median 4.137 seconds. Gemini 4 Carbon had one scan at 19.459 seconds, no read error and no save. The app counts edits when those values differ from the model's normalized form, and the saved edits do not establish what was on the physical label. [Reusable label outcome definition](~/Projects/homeserver-gym/src/Plate.Web/Features/Feedback/ModelAccuracyReport.cs:26)
- Estimator-specific counts and accuracy rates are absent from the current model accuracy UI because the chosen estimator is recorded per resolved row. [Estimator accuracy UI limitation](~/Projects/homeserver-gym/src/Plate.Web/Features/Feedback/ModelAccuracyReport.cs:20)

### Inferences

- Current parser/matcher numbers are operational observations and cannot isolate a causal effect. Separate their judgments with a held-out shortlist benchmark and blinded bilingual logs.
- Flash vs Carbon vision needs identical photos with human transcription, including multiple photos, glare, Bulgarian decimal commas and per-100-g/per-100-ml/serving, before changing the reader. There is no live head-to-head controlled evidence. Keep Flash as the incumbent during testing.
- Use matched onboarding turns and unmatched weighed foods with the exact app schemas to establish whether Carbon gives enough benefit over a fast model or lower effort. Distinguish smaller token spend from a reduction in abandoned flows or corrections.

### Gaps

- No aggregate metric here controls for sample selection, language mix, comparable inputs, model routing, effort settings on historical rows, or product ground truth. No real current weekly model call was identified in the telemetry slice.
