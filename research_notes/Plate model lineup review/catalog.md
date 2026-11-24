# OpenRouter catalog and provider pricing

## Which models and prices are current?

### Takeaway

The live public chat and Decisions catalogs on November 24, 2026 confirm all five model identities and their displayed effort levels. Flash 3.8 has different catalog and endpoint rates, so Plate's recorded cost is the best evidence of what it paid.

### Cited findings

| Plate role | OpenRouter model | Catalog in/out per million (USD) | Effort in screenshot |
| --- | --- | ---: | --- |
| Food parser | [meta/muse-ember](https://openrouter.ai/meta/muse-ember) | $0.10 / $0.50 | Minimal |
| Food matcher | [google/gemini-decision-1](https://openrouter.ai/google/gemini-decision-1) | $0.10 / $0 | Decisions API |
| Setup chat | [google/gemini-4-carbon](https://openrouter.ai/google/gemini-4-carbon) | $2.00 / $10.00 | Low |
| Weekly summary | [meta/muse-spark-1.3-contributor](https://openrouter.ai/meta/muse-spark-1.3-contributor) | $0.10 / $0.20 | Minimal |
| Label reader | [google/gemini-3.8-flash](https://openrouter.ai/google/gemini-3.8-flash) | $0.825 / $4.125 | Low |
| Food estimator | [google/gemini-4-carbon](https://openrouter.ai/google/gemini-4-carbon) | $2.00 / $10.00 | Medium |

- Lowest eligible effort in live model descriptors is minimal for both Meta models and low for Carbon and Flash 3.8. All five chat slots use models that advertise strict structured outputs. Flash 3.8 accepts images. Gemini Decision 1 serves typed questions through the Decisions API, charging for input tokens only. [Live chat model catalog](https://openrouter.ai/api/v1/models), [live Decisions catalog](https://openrouter.ai/api/v1/models?output_modalities=decisions), [Gemini Decision 1](https://openrouter.ai/google/gemini-decision-1).
- The live Flash 3.8 model list reports exactly $0.825/$4.125 per million, which Plate rounds to $0.83/$4.13. Its live endpoint list reports Google AI Studio and Google Vertex both at $0.75/$3.75 standard, $0.375/$1.875 Flex and $1.35/$6.75 Priority, all reflecting a 50% discount. Image input costs the corresponding input rate. No listed endpoint quote matched the app's model-level number exactly. [Live chat model catalog](https://openrouter.ai/api/v1/models), [live endpoint API](https://openrouter.ai/api/v1/models/google/gemini-3.8-flash/endpoints), [Flash 3.8 provider prices](https://openrouter.ai/google/gemini-3.8-flash/providers).
- Carbon standard AI Studio and Vertex in/out are $2/$10 per million, AI Studio Flex is $1/$5, and Priority is $3.60/$18. Vertex's region-specific EU endpoint listed $2.20/$11 on the providers page. [Live endpoint API](https://openrouter.ai/api/v1/models/google/gemini-4-carbon/endpoints), [Carbon providers](https://openrouter.ai/google/gemini-4-carbon/providers).
- Muse Ember has Meta and Google Vertex listings at $0.10/$0.50. Muse Spark 1.3 Contributor has one listed provider, Meta, and OpenRouter expressly says that prompts and outputs may be used to improve Meta's products. The standard Muse Spark 1.3 is $1.25/$4.25. [Ember endpoints](https://openrouter.ai/api/v1/models/meta/muse-ember/endpoints), [Contributor endpoints](https://openrouter.ai/api/v1/models/meta/muse-spark-1.3-contributor/endpoints), [Contributor model page](https://openrouter.ai/meta/muse-spark-1.3-contributor), [standard Muse Spark](https://openrouter.ai/meta/muse-spark-1.3).
- Reasoning tokens are billed at output rates. Provider selection, image input and token counts each change cost per successful task, even for models quoted at equal list price. [Reasoning token billing](https://openrouter.ai/docs/guides/best-practices/reasoning-tokens), [provider selection](https://openrouter.ai/docs/guides/routing/provider-selection).

### Gaps

- Public catalog data did not identify which endpoint Plate used. The endpoint prices and screenshot reflect different levels of the catalog. Avoid attributing the exact displayed Flash 3.8 rate to a specific provider.

## Which alternatives merit a Plate comparison?

### Takeaway

Keep the app-tested parser and matcher. Weekly summary is the clear first replacement candidate because the Contributor trade-off applies to user-derived food, weight and workout facts at a very small absolute cost saving. For the premium routes, test cheaper effort levels and models on their own tasks before standardizing them.

### Cited findings

- Parser incumbent Muse Ember minimal reached 83.1% fully right, found 100% of items, had p50/p95 2,174/3,938 ms, zero failures and $0.000231 mean per call in Plate's Nov 21 benchmark. GPT-6 Luna (lowest, none) was 76.6%, found 96.3%, p50/p95 2,341/4,107 ms, zero failures and $0.000094. Both list $0.10/$0.50. DeepSeek V4.1 Flash default case was 75.8% and $0.000207. Low effort scored 79.8% and cost $0.000704, with much higher tail latency. The benchmark's known issues include flawed single-item gold labels, concurrent model load and timeouts in some competitors. [Plate parse benchmark](<~/Projects/homeserver-gym/reports/parsebench-2026-11-21.txt:1>), [GPT-6 Luna](https://openrouter.ai/openai/gpt-6-luna), [DeepSeek V4.1 Flash](https://openrouter.ai/deepseek/deepseek-v4.1-flash).
- Matcher incumbent Gemini Decision 1 98.1% final at 3s vs Jev 1.14 95.3%, Perplexity Decider v1.3 free 93.8%, and ranking only 76.6%. Gemini's p95 was 451 ms and 100% of no-right-food cases classified correctly, vs Jev's 261 ms and 100%, and Perplexity's 879 ms and 70%. Paying $0.0823 instead of $0.0499 across 288 case invocations buys the 2.8-point lead over Jev. Rough benchmark equivalent $0.286 vs $0.173 per 1,000 cases, a $0.113 difference. The comparison uses fixed parser outputs. [Plate match benchmark](<~/Projects/homeserver-gym/reports/matchbench-2026-11-21.txt:1>), [Jev 1.14 $0.042/$0](https://openrouter.ai/typesafe/jev-1.14), [Decisions catalog](https://openrouter.ai/api/v1/models?output_modalities=decisions).
- Weekly summary has up to one stored generation per eligible user per week under normal use. It includes goals, weight trends and food and training aggregates including custom exercise names. Names, email and notes are omitted. [Plate SPEC weekly summary](~/Projects/homeserver-gym/SPEC.md:475).
- Three credible Contributor replacements with an available ZDR endpoint are GPT-6 Luna none at $0.10/$0.50, Gemini 3.5 Flash Lite minimal at $0.30/$2.50, and Mistral Small 4 none at $0.15/$0.60. The live catalog filtered by `zdr=true` lists all three and Muse Ember, and excludes Contributor. Mistral Small 4 specifically advertises the `mistral/zdr` endpoint with strict structured output at list price. OpenRouter's provider table lists Mistral as no training and zero retention. Availability of a qualifying endpoint is distinct from choosing and enforcing it. OpenRouter recommends checking the account or workspace's strict ZDR-only settings, since Safety Retention can otherwise be permitted. [Live ZDR catalog](https://openrouter.ai/api/v1/models?zdr=true), [catalog filter definition](https://openrouter.ai/docs/api/api-reference/models/get-models), [Mistral endpoint API](https://openrouter.ai/api/v1/models/mistralai/mistral-small-2603/endpoints), [provider policy table](https://openrouter.ai/providers?fmt=cards), [OpenRouter ZDR routing](https://learn.openrouter.ai/courses/control-routing/provider-selection).
- For setup, test GPT-6 Luna and Flash 3.5 Lite first for faithful Bulgarian and English field extraction and chat polish. Carbon has only a modest system cost when setup is infrequent. The app validates limits and defaults and computes the targets on the server. Carbon's low effort is already the lowest supported. [GPT-6 Luna](https://openrouter.ai/openai/gpt-6-luna), [Flash 3.5 Lite](https://openrouter.ai/google/gemini-3.5-flash-lite), [Plate setup chat](~/Projects/homeserver-gym/SPEC.md:459).
- For label reading, Flash 3.5 Lite is the existing Plate default and it accepts images and strict structured outputs. At $0.30/$2.50 it is about 60% cheaper on input/image and 33% cheaper on output at the Flash 3.8 standard endpoint price. Test true field-by-field label fidelity before a swap, especially decimal commas, per 100 g vs per 100 ml vs per serving, kJ vs kcal, sugar/carbs and missing values. [Flash 3.5 Lite](https://openrouter.ai/google/gemini-3.5-flash-lite), [Flash 3.8 endpoints](https://openrouter.ai/api/v1/models/google/gemini-3.8-flash/endpoints), [Plate label specification](~/Projects/homeserver-gym/SPEC.md:489).
- For the estimator, compare incumbent medium to Carbon low first, then Flash 3.8 low. Gemini 4 Hydrogen is a useful closer-cost intermediate at $1.50/$7.50 (25% lower per token than Carbon at catalog prices). Gemini 4 Neon is $3/$15 on OpenRouter, 50% higher than current Carbon catalog rates even though the product blurb positions it as fast. [Carbon](https://openrouter.ai/google/gemini-4-carbon), [Hydrogen](https://openrouter.ai/google/gemini-4-hydrogen), [Neon](https://openrouter.ai/google/gemini-4-neon).

### Inferences

- For weekly, GPT-6 Luna is the lowest-friction price increase among these ZDR candidates. Assume 1,500 billed input and 300 billed output tokens including reasoning, equal across models only for illustration. Contributor costs $0.00021 and Luna $0.00030, a difference of $0.00468 per user for 52 weeks. Mistral Small 4 costs $0.000405 and Flash Lite $0.0012 under the same assumptions. Pick the winner on Bulgarian tone, factual fidelity and stated data policy after a small test.
- Comparing Carbon medium to low on estimation is a cheaper experiment than changing families. Finance or coding benchmarks measure different outcomes from recipe estimates and label OCR.

### Gaps

- No comparable setup, weekly-summary, or label head-to-head on Plate was supplied in this research component. Catalog support and published rates identify candidates. Cost and correctness need the production route or a workload-matched benchmark.
- The model catalog's ZDR flag says a qualifying endpoint exists. Actual routing and strict retention depend on the workspace, key or request settings.
