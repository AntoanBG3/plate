# Jev food matching: results

Set `jev-eval-set-2`, run 2026-10-04 14:50. 32 items from 28 entries; 23 Bulgarian, 9 English; 8 labelled none. Jev answered as typesafe/jev-1.13-20260917; 3 run(s) per order. "Gated": a none counts only at confidence 0.9 or more, otherwise Rank's top pick. With several runs a cell shows the mean and, in brackets, the lowest and highest run.

## Accuracy

| Approach | All | Bulgarian | English | None caught | Matchable wrongly rejected |
|---|---|---|---|---|---|
| a) Rank top-1 (today) | 24/32 (75%) | 19/23 (83%) | 5/9 (56%) | 1/8 (13%) | 0/24 (0%) |
| c plain, listed | 29/32 (29–29) | 21/23 (21–21) | 8/9 (8–8) | 7/8 (7–7) | 0/24 (0–0) |
| c plain, reversed | 29/32 (29–29) | 21/23 (21–21) | 8/9 (8–8) | 7/8 (7–7) | 1/24 (1–1) |
| c gated, listed | 29/32 (29–29) | 21/23 (21–21) | 8/9 (8–8) | 7/8 (7–7) | 0/24 (0–0) |
| c gated, reversed | 30/32 (30–30) | 22/23 (22–22) | 8/9 (8–8) | 7/8 (7–7) | 0/24 (0–0) |

## Go / no-go bars (listed order, per run)

**c plain.** Same final decision in all 6 runs: 28/32 (88%) ✗; max latency 1011 ms ✓.

| Bar | run 1 | run 2 | run 3 |
|---|---|---|---|
| Bulgarian: at least +3 items and +10 points over Rank (19/23) | +2 items, +9 pts ✗ | +2 items, +9 pts ✗ | +2 items, +9 pts ✗ |
| English: at most 1 item below Rank (5/9) | +3 ✓ | +3 ✓ | +3 ✓ |
| None caught: at least half | 7/8 ✓ | 7/8 ✓ | 7/8 ✓ |
| Matchable rejected: at most 1 in 20 | 0/24 ✓ | 0/24 ✓ | 0/24 ✓ |

**c gated.** Same final decision in all 6 runs: 29/32 (91%) ✓; max latency 1011 ms ✓.

| Bar | run 1 | run 2 | run 3 |
|---|---|---|---|
| Bulgarian: at least +3 items and +10 points over Rank (19/23) | +2 items, +9 pts ✗ | +2 items, +9 pts ✗ | +2 items, +9 pts ✗ |
| English: at most 1 item below Rank (5/9) | +3 ✓ | +3 ✓ | +3 ✓ |
| None caught: at least half | 7/8 ✓ | 7/8 ✓ | 7/8 ✓ |
| Matchable rejected: at most 1 in 20 | 0/24 ✓ | 0/24 ✓ | 0/24 ✓ |

## Shortlist recall (matchable items with a correct food in the list)

| List | All | Bulgarian | English |
|---|---|---|---|
| Rank top 1 | 23/24 (96%) | 18/19 (95%) | 5/5 (100%) |
| Rank top 5 | 23/24 (96%) | 18/19 (95%) | 5/5 (100%) |
| Trigram top 30 | 23/24 (96%) | 19/19 (100%) | 4/5 (80%) |
| Trigram top 40 | 23/24 (96%) | 19/19 (100%) | 4/5 (80%) |
| Trigram top 50 | 23/24 (96%) | 19/19 (100%) | 4/5 (80%) |

## Agreement (items whose raw Jev answer is the same in every run of the group)

- c: listed runs 31/32 (97%), reversed runs 32/32 (100%), all runs 28/32 (88%)

## Latency and cost per parse (one call per entry)

| Calls | Median ms | Max ms | Mean input tokens | Mean cost | Max cost | Errors |
|---|---|---|---|---|---|---|
| c | 333 | 613 | 4397 | $0.000185 | $0.000328 | 0 |
| c-reversed | 317 | 1011 | 4397 | $0.000185 | $0.000328 | 0 |

## Accuracy by Jev confidence (raw answer, all listed runs; correct / answers)

| Confidence | c |
|---|---|
| 0.00 to 0.50 | 15 / 18 |
| 0.50 to 0.70 | 18 / 19 |
| 0.70 to 0.85 | 6 / 8 |
| 0.85 to 0.90 | 0 / 0 |
| 0.90 to 0.95 | 3 / 6 |
| 0.95 to 1.00 | 45 / 45 |

## Per item

✓ correct, ✗ wrong; the name is what was picked (— means no match). Jev columns are run 1, listed order, with its confidence; "runs" is how many of all runs gave the most common raw answer.

| Item | Text | Label | a | c plain | c gated | c runs |
|---|---|---|---|---|---|---|
| r2en01.0 | corn flakes | usda:Cereals ready-to-eat, RALSTON Corn Flakes | ✓ usda:Cereals ready-to-eat, RALSTON Corn Flakes | ✓ usda:Cereals ready-to-eat, RALSTON Corn Flakes (0.98) | ✓ usda:Cereals ready-to-eat, RALSTON Corn Flakes | 6/6 |
| r2en01.1 | milk | usda:Milk, whole, 3.25% milkfat, without added vitamin A and vitamin D (+2) | ✓ usda:Milk, whole, 3.25% milkfat, without added vitamin A and vitamin D | ✗ usda:Milk, producer, fluid, 3.7% milkfat (0.93) | ✗ usda:Milk, producer, fluid, 3.7% milkfat | 6/6 |
| r2en02.0 | Snickers bar | usda:Candies, MARS SNACKFOOD US, SNICKERS Bar | ✓ usda:Candies, MARS SNACKFOOD US, SNICKERS Bar | ✓ usda:Candies, MARS SNACKFOOD US, SNICKERS Bar (0.99) | ✓ usda:Candies, MARS SNACKFOOD US, SNICKERS Bar | 6/6 |
| r2en03.0 | orange juice | usda:Orange juice, raw (Includes foods for USDA's Food Distribution Program) (+9) | ✓ usda:Orange juice, raw (Includes foods for USDA's Food Distribution Program) | ✓ usda:Orange juice, chilled, includes from concentrate (0.43) | ✓ usda:Orange juice, chilled, includes from concentrate | 6/6 |
| r2en04.0 | butter croissant | usda:Croissants, butter | ✓ usda:Croissants, butter | ✓ usda:Croissants, butter (1.00) | ✓ usda:Croissants, butter | 6/6 |
| r2en05.0 | falafel wrap | none | ✗ usda:Falafel, home-prepared | ✓ — (0.98) | ✓ — | 6/6 |
| r2en06.0 | bubble tea | none | ✗ usda:Beverages, SNAPPLE, tea, black and green, ready to drink, lemon, diet | ✓ — (0.99) | ✓ — | 6/6 |
| r2en07.0 | chicken pad thai | none | ✗ usda:Chicken patty, frozen, cooked | ✓ — (0.98) | ✓ — | 6/6 |
| r2en08.0 | cappuccino | none | ✗ usda:Beverages, chocolate syrup, prepared with whole milk | ✓ — (0.99) | ✓ — | 6/6 |
| r2bg01.0 | кюфтета | off:3800224353589 Свински кюфтета (Цър Пър) (+11) | ✗ usda:Turkey, ground, 93% lean, 7% fat, patties, broiled | ✗ off:4056489941552 Велико кюфте от свинско и говеждо (Грил Майстор) (0.18) | ✗ off:4056489941552 Велико кюфте от свинско и говеждо (Грил Майстор) | 3/6 |
| r2bg02.0 | шкембе чорба | bg:Шкембе чорба | ✓ bg:Шкембе чорба | ✓ bg:Шкембе чорба (1.00) | ✓ bg:Шкембе чорба | 6/6 |
| r2bg03.0 | кавърма с ориз | bg:Кавърма | ✓ bg:Кавърма | ✓ bg:Кавърма (0.54) | ✓ bg:Кавърма | 3/6 |
| r2bg04.0 | катми | bg:Катми | ✓ bg:Катми | ✓ bg:Катми (0.98) | ✓ bg:Катми | 6/6 |
| r2bg04.1 | мед | usda:Honey (+10) | ✓ usda:Honey | ✓ bg:Мед (0.49) | ✓ bg:Мед | 3/6 |
| r2bg05.0 | мишмаш | bg:Мишмаш (+1) | ✓ bg:Мишмаш | ✓ bg:Мишмаш (0.73) | ✓ bg:Мишмаш | 6/6 |
| r2bg06.0 | свинска пържола | usda:Pork, chop, center cut, raw (+6) | ✓ usda:Pork, chop, center cut, raw | ✓ usda:Pork, chop, center cut, raw (0.53) | ✓ usda:Pork, chop, center cut, raw | 6/6 |
| r2bg07.0 | бира | off:3800052314110 Бира (Stella Artois) (+7) | ✓ off:3800052314110 Бира (Stella Artois) | ✓ usda:Alcoholic beverage, beer, regular, all (0.83) | ✓ usda:Alcoholic beverage, beer, regular, all | 6/6 |
| r2bg08.0 | айрян | bg:Айрян (+24) | ✓ off:8606107983981 Айрян (Meggle) | ✓ bg:Айрян (0.31) | ✓ bg:Айрян | 6/6 |
| r2bg09.0 | картофена салата | usda:Potato salad, home-prepared (+1) | ✓ usda:Potato salad, home-prepared | ✓ usda:Potato salad, home-prepared (0.59) | ✓ usda:Potato salad, home-prepared | 6/6 |
| r2bg10.0 | кренвирша | usda:Frankfurter, pork (+18) | ✓ usda:Frankfurter, pork | ✓ usda:Frankfurter, meat (0.34) | ✓ usda:Frankfurter, meat | 6/6 |
| r2bg11.0 | баклава | bg:Баклава (+3) | ✓ bg:Баклава | ✓ bg:Баклава (0.96) | ✓ bg:Баклава | 6/6 |
| r2bg12.0 | гювеч | bg:Гювеч (+2) | ✓ off:4056489177579 Гювеч (Родна стряха) | ✓ bg:Гювеч (0.95) | ✓ bg:Гювеч | 6/6 |
| r2bg13.0 | пилешка супа | bg:Пилешка супа | ✓ bg:Пилешка супа | ✓ bg:Пилешка супа (0.97) | ✓ bg:Пилешка супа | 6/6 |
| r2bg14.0 | лозови сарми | off:3800501392010 Лозови сарми (Mrs. Emmy) (+5) | ✓ off:3800501392010 Лозови сарми (Mrs. Emmy) | ✓ bg:Сарми с лозов лист (0.55) | ✓ bg:Сарми с лозов лист | 6/6 |
| r2bg15.0 | кашкавал пане | none | ✗ off:2857063006764 Кашкавал (Наборъ) | ✓ — (0.98) | ✓ — | 6/6 |
| r2bg16.0 | чушки бюрек | none | ✗ bg:Чушки | ✓ — (0.96) | ✓ — | 6/6 |
| r2bg17.0 | пататник | none | ✗ usda:Potato pancakes | ✗ usda:Potato pancakes (0.76) | ✗ usda:Potato pancakes | 6/6 |
| r2bg18.0 | капама | none | ✓ — | ✓ — (0.90) | ✓ — | 6/6 |
| r2bg19.0 | бял хляб | usda:Bread, white, commercially prepared (+13) | ✓ off:3800204570104 Бял хляб | ✓ usda:Bread, white, commercially prepared (0.45) | ✓ usda:Bread, white, commercially prepared | 5/6 |
| r2bg19.1 | масло | usda:Butter, salted (+31) | ✓ usda:Butter, salted | ✓ off:4033854003619 Краве масло (0.55) | ✓ off:4033854003619 Краве масло | 6/6 |
| r2bg20.0 | боза | off:3800200320048 Боза (Бомакс) (+8) | ✓ off:3800200320048 Боза (Бомакс) | ✓ off:3800500137025 Боза (0.48) | ✓ off:3800500137025 Боза | 6/6 |
| r2bg20.1 | мекица | bg:Мекица | ✓ bg:Мекица | ✓ bg:Мекица (0.99) | ✓ bg:Мекица | 6/6 |
