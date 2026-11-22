# Matcher benchmark cases

Input for `feedback matchbench` (SPEC 11). Each line is one entry with its parsed items and which catalogue foods are right for each.

- `cases.jsonl`: the development set, 107 items. Tune against this one.
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
