# Measure deterministic food selection

1. Observe the full grouped ranking during an offline benchmark. Retain the production trace limit for saved parse runs. Record the rank of the first acceptable food and the reason for the deterministic outcome.
2. Score the final selection against the item label even when retrieval missed it. Count selected foods, correct selections, absent foods declined and required matches declined. Report both denominators and percentages.
3. Use the existing bilingual corpus for development. Freeze a second file with separate food families, including transliterated text and close variants. Run the two files separately on the same seeded catalogue with the decision model off.
4. Verify the reporting contract through the CLI and compare the sibling matcher branches on both files. An acceptable match uses the case's name, rejection and energy rules.

Review this branch against local main. Both matcher branches start from this instrumentation.

## Reproduce the comparison

Run from the review worktree against a disposable development database with the normal Plate seed. Use the same catalogue for every revision. With the matcher off, the run needs no OpenRouter key or credit.

```sh
for corpus in cases held-out-families; do
  dotnet run --project src/Plate.Web -- feedback matchbench --models off < "tools/matchbench/${corpus}.jsonl"
done
```

The development baseline scored 84/107 overall. It made 106 database selections, of which 83 were correct, and declined one of ten absent foods. The family holdout has eight items and is a smoke check. Its six catalogue foods and two absent foods were all correct at baseline.
