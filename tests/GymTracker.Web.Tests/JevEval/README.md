# Jev food matching evaluation

Phase 0 of `docs/superpowers/plans/2026-10-04-jev-food-matching.md`. Compares three ways of choosing the database food for each parsed item:

- a) `FoodMatcher.ResolveAsync` as it is today (Rank's top pick),
- b) TypeSafe Jev choosing from Rank's top 5,
- c) Jev choosing from the plain trigram top 50, with Rank removed.

The tests skip unless `GYM_JEV_EVAL` is set, because they call OpenRouter with a real key (`OPENROUTER_API_KEY`, else `~/.config/gymtracker/openrouter-key`). They run against the dev database (`docker compose -f docker/docker-compose.dev.yml up -d db`, then start the app once so it seeds), which has the USDA, Bulgarian dish and product data; `GYM_EVAL_DB` overrides the connection string.

```sh
GYM_JEV_EVAL=prepare dotnet test tests/GymTracker.Web.Tests --filter JevEval   # parse new entries, write out/candidates.md
GYM_JEV_EVAL=score dotnet test tests/GymTracker.Web.Tests --filter JevEval     # write the results file
```

`prepare` parses only entries without items; `GYM_JEV_EVAL_REPARSE=1` parses all of them again, which clears their labels. `score` writes `docs/superpowers/plans/2026-10-04-jev-food-matching-results.md`, and the raw requests and responses to `out/` (git-ignored).

## The set

`jev-eval-set.json` holds each entry's text, the parser's output frozen at the time it was prepared, and a label per item. Labels name foods in a way that survives a reseed: `usda:<Name>`, `bg:<NameBg>` for the Bulgarian reference foods, `off:<Barcode>` for products. `["none"]` means no food in the database is right.

Labelling rules:

- A food is correct when it is the same food the user wrote, in the same form (raw, cooked, dry), with the same preparation where the database distinguishes it (scrambled, hard-boiled, boiled flesh).
- Every acceptable food that appears in any approach's list is labelled, not only the best one, so no approach loses an item for picking an equally good variant. Plain branded products count when they are the plain version of the food.
- `none` when nothing in the database is that food. A different food that shares a word (Halloween cereal for halloumi, sourdough bread for kvass) or an ingredient of the dish (pumpkin for тиквеник) is not a match.
- Judgement calls are written in each item's `note`.
