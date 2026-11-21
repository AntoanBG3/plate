# Weighed-meal pilot

Plate's eval measures agreement with what you saved, which is not accuracy: a bad suggestion you accepted scores as right. This pilot measures accuracy against weighed food, on 40 ordinary meals. Forty meals show failure modes and a rough error size; they can't support a claim like "better than a human logger".

## What you need

A kitchen scale that reads to 1 g, and the CSV template `reports/weighed-meals-template.csv`. Keep your filled-in file outside the repository unless you decide to commit it: it is your food diary.

## Which meals

About ten of each kind, in both languages (roughly half Bulgarian):

- **counted**: packaged or counted foods ("две закуски от Лидл", "2 eggs", "a yogurt").
- **staple**: weighed staples where raw or cooked matters ("150 г ориз", pasta, meat).
- **homemade**: a mixed dish you cooked (мусака, a stew, a salad you made).
- **loose**: loosely described meals ("малко ядки", "a bowl of fruit salad").

Keep repeats of the same product or recipe together when you later split meals into a first 20 (for tuning) and a last 20 (held out): a product in both halves leaks the answer.

## For each meal

1. **Log it in Plate first, the way you normally would.** Type what you would type, confirm as usual, and copy the exact text into the `text` column. Don't change the Plate entry after weighing; the pilot compares that ordinary text.
2. **Weigh what you eat.** Tare the plate, weigh each food as served, and weigh leftovers afterwards. `grams` is served minus leftovers, as eaten (cooked weight for cooked food; say in `notes` if you also know the raw weight). For something eaten from a pack, weigh the pack before and after.
3. **Find the values per 100 g**, in this order: the package label (photograph it), Open Food Facts for that barcode, the Bulgarian composition tables or USDA for generic foods. Put where they came from in `source`. Write numbers with a dot (a comma is accepted too).
4. **A homemade dish**: weigh every raw ingredient (and the oil that stays in), add up their kcal and macros, weigh the whole cooked dish, then per 100 g = batch total × 100 ÷ cooked weight. A 250 g serving of an 800 g batch with 1,200 kcal is 375 kcal. Write the batch numbers in `notes`. An uneven dish (meat on rice) is better weighed as separate items.
5. **Mark guesses.** If something couldn't be weighed or has no reliable values (a restaurant sauce), put `yes` in `uncertain`. Such meals are reported but left out of the averages.

One row per food. The meal's `eaten_at` (`2026-11-22 13:30`, Sofia time), `account` (your Plate email), `category` and `text` go on any one of its rows. Rows whose `meal` starts with `example` are skipped, so you can keep the template's examples while you start.

## Scoring

Copy the file to wizard and check it first (no OpenRouter calls):

```
ssh wizard 'docker exec -i plate-app dotnet Plate.Web.dll feedback pilot --check' < weighed-meals.csv
```

Then score it (one parse of OpenRouter credit per meal):

```
ssh wizard 'docker exec -i plate-app dotnet Plate.Web.dll feedback pilot' < weighed-meals.csv
```

Each meal is parsed and matched as your account, with your usual foods and last amounts as they were at `eaten_at`, and compared with the weighed totals: kcal difference per meal, protein, carbs and fat in grams, what you weighed beside what Plate picked. The summary gives the mean and median absolute kcal error, the signed bias, the macro errors, the same per kind of meal, and the largest misses. `--no-history` scores the parser and matcher alone; `--parser` and `--matcher` try other models; `--json` gives the numbers for a chart.

This scores Plate's first suggestion for your text, before any edit on Check foods. What you finally saved is in the parse run and can be compared later. A comparison with another person logging the same meals (same text or photo, no weights) needs their entries recorded the same way.
