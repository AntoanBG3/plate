# Matcher benchmark cases

Input for `feedback matchbench` (SPEC 11). Each line is one entry with its parsed items and which catalogue foods are right for each.

- `cases.jsonl`: the development set, 118 items. Tune against this one.
- `held-out-families.jsonl`: 39 items from food families the development set doesn't use (Bulgarian plurals and inflected forms, transliterated Bulgarian, fat-level and flavour variants, brands, absent dishes). It was written after the identity and variant ranking changes, so nothing has been tuned on it. Keep it that way: don't change the matcher to fix one of its misses. A fix for one of them needs new cases to check against.

## Run without credit

With the matcher off the run needs no OpenRouter key. Point it at a disposable database with the normal seed (the app migrates and seeds it on start), not the dev database, and use the same catalogue for every revision you compare:

```sh
export ConnectionStrings__Default="Host=127.0.0.1;Port=<port>;Database=postgres;Username=postgres"
for corpus in cases held-out-families; do
  dotnet run --project src/Plate.Web -- feedback matchbench --models off < "tools/matchbench/${corpus}.jsonl"
done
```

Results on the local seed, matcher off, 2026-11-22. Before the identity and variant changes, development 84/107 (83 of 106 selections right, 1 of 10 absent foods declined) and held-out 33/39. After them (`ff50629`), development 97/107 (85 of 95 right, 10 of 10 declined) and held-out 34/39. The held-out misses left are light sour cream for plain, baby zucchini, turkey bacon, and "boza" and "lukanka" typed in Latin letters, which the search doesn't reach.

2026-11-25, after the Cyrillic reading of Latin-script Bulgarian (QA 2026-11-24 M4), matcher off, same seed for both: nine transliterated cases were added to the development set first (c97 to c105). Development, before (`c6a671e`) and after: 92 of 103 selections right, 10 of 10 absent foods declined, the same answer on every item. The parser's English names already reach eight of the nine new cases; "pastarma" still misses, as the reading gives "а" where the name has "ъ". Held-out: 26 of 29 selections right and 2 required matches declined before, 28 of 31 right and none declined after, 8 of 8 absent foods declined both times. "boza" and "lukanka" are now found, and nothing got worse.

2026-11-25, after poultry weighed raw or cooked and deli and rotisserie meat counted as niche (QA 2026-11-24 M7), matcher off, same seed for both: two grilled chicken cases were added first (c106, c107). Development 92 of 105 selections right before, 96 after (c106, c107, c29 and c93 now right, nothing worse); held-out unchanged at 28 of 31.
