# Measure deterministic food selection

1. Observe the full grouped ranking during an offline benchmark. Retain the production trace limit for saved parse runs. Record the rank of the first acceptable food and the reason for the deterministic outcome.
2. Score the final selection against the item label even when retrieval missed it. Count selected foods, correct selections, absent foods declined and required matches declined. Report both denominators and percentages.
3. Use the existing bilingual corpus for development. Freeze a second file with separate food families, including transliterated text and close variants. Run the two files separately on the same seeded catalogue with the decision model off.
4. Verify the reporting contract through the CLI and compare the sibling matcher branches on both files. An acceptable match uses the case's name, rejection and energy rules.

Review this branch against local main. Both matcher branches start from this instrumentation.
