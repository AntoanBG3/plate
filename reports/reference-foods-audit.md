# Reference foods audit, 2026-11-21

The Bulgarian reference foods in `src/Plate.Web/Data/Seed/bg-foods.csv` (375 generic rows, compiled by `tools/foods` from Bulgarian calorie sites) link to a USDA food by their English name, which also tells Plate whether a food is raw or cooked. Of the 89 rows whose English name is exactly a USDA food, 38 differ from that food's energy by more than 25% (and 15 kcal). 23 come from macroscope.top (197 rows in all) and 14 from spidersport.com (60 rows), so the other rows from those two sites deserve the same suspicion even where there is no USDA twin to compare with: "Грозде - бяло" at 38 kcal is one.

Two kinds of error are mixed here. Some values are wrong for the food (strawberries at 79 kcal, spinach at 7). Some English names are wrong for the values (cooked lentils linked to "Lentils, raw", which also gives them the wrong state for the cooked-or-dry question). Plate now refuses a raw-or-cooked state when a staple's name and energy disagree, so the second kind no longer drives that question, but both still put wrong numbers in front of users.

`node tools/foods/audit-reference.mjs` prints the current list; `tests/js/reference-foods.test.cjs` fails on a new conflict and on a fixed one still listed in `tools/foods/reference-conflicts.known.txt`. 
| Bulgarian name | English link | kcal | USDA kcal | Source | Proposal |
|-|-|-|-|-|-|
| Гъби консерва | Mushrooms, canned, drained solids | 80 | 25 | www.spidersport.com | Values implausible for the named raw food; take the USDA twin's values, or drop the row (the USDA food has a Bulgarian name). |
| Ягоди | Strawberries, raw | 79 | 31 | macroscope.top | Values implausible for the named raw food; take the USDA twin's values, or drop the row (the USDA food has a Bulgarian name). |
| Ретро Горчица - Deroni | Mustard, prepared, yellow | 148 | 61 | macroscope.top | Product with its own values linked to a generic food: drop the English link. |
| Гъше | Goose, domesticated, meat only, raw | 370 | 161 | macroscope.top | Relink to "Goose, domesticated, meat and skin, raw" (371): the values include skin. |
| Сметана | Cream, sour, cultured | 428 | 198 | macroscope.top | Bulgarian сметана is cream, not sour cream; 428 kcal fits neither. Needs a source. |
| Свински бут | Pork, fresh, leg (ham), whole, separable lean only, raw | 269 | 136 | www.spidersport.com | The values and the meat-only or skin variant disagree; pick the variant the source meant, or a better source. |
| BBQ Sauce - OstroVit | Sauce, barbecue | 7 | 172 | macroscope.top | Product with its own values linked to a generic food: drop the English link. |
| Кокоше | Chicken, stewing, meat only, raw | 261 | 148 | macroscope.top | The values and the meat-only or skin variant disagree; pick the variant the source meant, or a better source. |
| Сливи сини | Plums, raw | 81 | 46 | macroscope.top | Values implausible for the named raw food; take the USDA twin's values, or drop the row (the USDA food has a Bulgarian name). |
| Пшенични трици | Wheat bran, crude | 368 | 216 | macroscope.top | Values implausible for the named raw food; take the USDA twin's values, or drop the row (the USDA food has a Bulgarian name). |
| Леща | Lentils, raw | 105 | 352 | www.spidersport.com | Values are cooked; relink to the cooked USDA food so the state is right. |
| Спанак | Spinach, raw | 7 | 23 | www.spidersport.com | Values implausible for the named raw food; take the USDA twin's values, or drop the row (the USDA food has a Bulgarian name). |
| Рапани (Задушени) | Mollusks, whelk, unspecified, cooked, moist heat | 90 | 275 | macroscope.top | Fish or seafood values off from USDA; check against the Bulgarian composition table. |
| Зрял боб | Beans, white, mature seeds, raw | 113 | 333 | www.spidersport.com | Values are cooked; relink to the cooked USDA food so the state is right. |
| Праз | Leeks, (bulb and lower leaf-portion), raw | 22 | 61 | www.spidersport.com | Values implausible for the named raw food; take the USDA twin's values, or drop the row (the USDA food has a Bulgarian name). |
| Овесени трици | Oat bran, raw | 403 | 246 | macroscope.top | Values implausible for the named raw food; take the USDA twin's values, or drop the row (the USDA food has a Bulgarian name). |
| Грейпфрут | Grapefruit, raw, pink and red, all areas | 20 | 42 | www.spidersport.com | Values implausible for the named raw food; take the USDA twin's values, or drop the row (the USDA food has a Bulgarian name). |
| Малини | Raspberries, raw | 25 | 51 | www.spidersport.com | Values implausible for the named raw food; take the USDA twin's values, or drop the row (the USDA food has a Bulgarian name). |
| Пилешки бут | Chicken, broilers or fryers, leg, meat and skin, raw | 117 | 214 | macroscope.top | The values and the meat-only or skin variant disagree; pick the variant the source meant, or a better source. |
| Цедено кисело мляко | Yogurt, Greek, plain, whole milk | 138 | 95 | dobrev-cheese.com | Bulgarian strained yogurt may be fattier than Greek whole-milk yogurt; check the label source. |
| Черни миди | Mollusks, mussel, blue, raw | 48 | 86 | www.spidersport.com | Fish or seafood values off from USDA; check against the Bulgarian composition table. |
| Цяло пуйка (пуешко) | Turkey, whole, meat only, raw | 165 | 115 | macroscope.top | The values and the meat-only or skin variant disagree; pick the variant the source meant, or a better source. |
| Манго | Mangos, raw | 86 | 60 | macroscope.top | Values implausible for the named raw food; take the USDA twin's values, or drop the row (the USDA food has a Bulgarian name). |
| Замразени зеленчуци | Vegetables, mixed, frozen, unprepared | 42 | 72 | www.spidersport.com | Values implausible for the named raw food; take the USDA twin's values, or drop the row (the USDA food has a Bulgarian name). |
| Кълнове от пшеница | Wheat germ, crude | 216 | 360 | macroscope.top | Values implausible for the named raw food; take the USDA twin's values, or drop the row (the USDA food has a Bulgarian name). |
| Пилешки плешка (горен бут) | Chicken, broilers or fryers, thigh, meat and skin, raw | 133 | 221 | macroscope.top | The values and the meat-only or skin variant disagree; pick the variant the source meant, or a better source. |
| Цяло пиле (пилешко) | Chicken, broilers or fryers, meat only, raw | 165 | 119 | macroscope.top | The values and the meat-only or skin variant disagree; pick the variant the source meant, or a better source. |
| Соя | Soybeans, mature seeds, raw | 277 | 446 | macroscope.top | Values implausible for the named raw food; take the USDA twin's values, or drop the row (the USDA food has a Bulgarian name). |
| Скумрия | Fish, mackerel, Atlantic, raw | 128 | 205 | macroscope.top | Fish or seafood values off from USDA; check against the Bulgarian composition table. |
| Кайсии | Apricots, raw | 31 | 48 | www.spidersport.com | Values implausible for the named raw food; take the USDA twin's values, or drop the row (the USDA food has a Bulgarian name). |
| Рапани (Сурови) | Mollusks, whelk, unspecified, raw | 90 | 137 | macroscope.top | Fish or seafood values off from USDA; check against the Bulgarian composition table. |
| Мандарини | Tangerines, (mandarin oranges), raw | 35 | 53 | www.spidersport.com | Values implausible for the named raw food; take the USDA twin's values, or drop the row (the USDA food has a Bulgarian name). |
| Аншоа консерва | Fish, anchovy, european, canned in oil, drained solids | 280 | 210 | www.spidersport.com | Fish or seafood values off from USDA; check against the Bulgarian composition table. |
| Лаврак | Fish, sea bass, mixed species, raw | 128 | 97 | macroscope.top | Fish or seafood values off from USDA; check against the Bulgarian composition table. |
| Грозде - розово | Grapes, red or green (European type, such as Thompson seedless), raw | 48 | 69 | macroscope.top | Values implausible for the named raw food; take the USDA twin's values, or drop the row (the USDA food has a Bulgarian name). |
| Круши | Pears, raw | 40 | 57 | www.spidersport.com | Values implausible for the named raw food; take the USDA twin's values, or drop the row (the USDA food has a Bulgarian name). |
| Ленено семе | Seeds, flaxseed | 380 | 534 | macroscope.top | Values implausible for the named raw food; take the USDA twin's values, or drop the row (the USDA food has a Bulgarian name). |
| Калкан | Fish, turbot, European, raw | 70 | 95 | macroscope.top | Fish or seafood values off from USDA; check against the Bulgarian composition table. |

A better long-term source for Bulgarian generic foods is the national composition table (ncpha.government.bg), after checking its terms of reuse, as the food-logging research suggested.

## Resolution, 2026-11-21

All 38 rows and one more without a USDA twin ("Грозде - бяло" at 38 kcal) were corrected, by these rules: values clearly wrong for a well-defined food take the USDA twin's values; values that fit another variant or state of the food are linked to that USDA food and take its values (so cooked lentils say cooked); products with their own label values lose the English link; local species that differ from the USDA food (European farmed sea bass, rapa whelk) get their own English name. `src/Plate.Web/Data/Seed/bg-foods.csv` carries the new values, `reference-fixes.csv` lists each change with the energy it replaced, `DataFixups` applies it at startup to rows still at that energy (a row corrected by hand since is left alone), and `merge.mjs` applies it when the seed is regenerated. Logged entries keep the totals they were saved with. The audit now finds no conflicts.

| Bulgarian name | kcal per 100 g | Change |
|-|-|-|
| Цедено кисело мляко | 138 → 94.5 | values from the USDA twin |
| Свински бут | 269 → 245 | linked to the matching USDA food and its values: Pork, fresh, leg (ham), whole, separable lean only, raw → Pork, fresh, leg (ham), whole, separable lean and fat, raw |
| Аншоа консерва | 280 → 210 | values from the USDA twin |
| Черни миди | 48 → 86 | values from the USDA twin |
| Грейпфрут | 20 → 42 | values from the USDA twin |
| Гъби консерва | 80 → 25 | values from the USDA twin |
| Замразени зеленчуци | 42 → 72 | values from the USDA twin |
| Кайсии | 31 → 48 | values from the USDA twin |
| Круши | 40 → 57 | values from the USDA twin |
| Малини | 25 → 51.4 | values from the USDA twin |
| Мандарини | 35 → 53 | values from the USDA twin |
| Праз | 22 → 61 | values from the USDA twin |
| Спанак | 7 → 23 | values from the USDA twin |
| Зрял боб | 113 → 139 | linked to the matching USDA food and its values: Beans, white, mature seeds, raw → Beans, white, mature seeds, cooked, boiled, without salt |
| Леща | 105 → 116 | linked to the matching USDA food and its values: Lentils, raw → Lentils, mature seeds, cooked, boiled, without salt |
| Гъше | 370 → 371 | linked to the matching USDA food and its values: Goose, domesticated, meat only, raw → Goose, domesticated, meat and skin, raw |
| Кокоше | 261 → 258 | linked to the matching USDA food and its values: Chicken, stewing, meat only, raw → Chicken, stewing, meat and skin, raw |
| Цяло пиле (пилешко) | 165 → 215 | linked to the matching USDA food and its values: Chicken, broilers or fryers, meat only, raw → Chicken, broilers or fryers, meat and skin, raw |
| Пилешки бут | 117 → 120 | linked to the matching USDA food and its values: Chicken, broilers or fryers, leg, meat and skin, raw → Chicken, broilers or fryers, leg, meat only, raw |
| Пилешки плешка (горен бут) | 133 → 121 | linked to the matching USDA food and its values: Chicken, broilers or fryers, thigh, meat and skin, raw → Chicken, broilers or fryers, dark meat, thigh, meat only, raw |
| Цяло пуйка (пуешко) | 165 → 144 | linked to the matching USDA food and its values: Turkey, whole, meat only, raw → Turkey, whole, meat and skin, raw |
| Рапани (Задушени) | 90 → 90 | own English name, values kept (a different species from the USDA food): Mollusks, whelk, unspecified, cooked, moist heat → Rapa whelk, stewed |
| Рапани (Сурови) | 90 → 90 | own English name, values kept (a different species from the USDA food): Mollusks, whelk, unspecified, raw → Rapa whelk, raw |
| Калкан | 70 → 95 | values from the USDA twin: Fish, turbot, European, raw → Fish, turbot, european, raw |
| Лаврак | 128 → 128 | own English name, values kept (a different species from the USDA food): Fish, sea bass, mixed species, raw → European sea bass, farmed, raw |
| Скумрия | 128 → 205 | values from the USDA twin |
| Сметана | 428 → 340 | linked to the matching USDA food and its values: Cream, sour, cultured → Cream, fluid, heavy whipping |
| Овесени трици | 403 → 246 | values from the USDA twin |
| Пшенични трици | 368 → 216 | values from the USDA twin |
| Соя | 277 → 446 | values from the USDA twin |
| Грозде - бяло | 38 → 69 | linked to the matching USDA food and its values: Grapes, green (European type, such as Thompson seedless), raw → Grapes, red or green (European type, such as Thompson seedless), raw |
| Грозде - розово | 48 → 69 | values from the USDA twin |
| Ленено семе | 380 → 534 | values from the USDA twin |
| Манго | 86 → 60 | values from the USDA twin |
| Сливи сини | 81 → 46 | values from the USDA twin |
| Ягоди | 79 → 31 | values from the USDA twin |
| Ретро Горчица - Deroni | 148 → 148 | English link removed (a product with its own label values): Mustard, prepared, yellow → (none) |
| Кълнове от пшеница | 216 → 198 | linked to the matching USDA food and its values: Wheat germ, crude → Wheat, sprouted |
| BBQ Sauce - OstroVit | 7 → 7 | English link removed (a product with its own label values): Sauce, barbecue → (none) |

Unverified: the rapa whelk values (90 kcal raw and stewed) have no second source, and stewed should be higher than raw; Сметана now uses heavy whipping cream (36% fat), the commonest Bulgarian cream, though shops also sell 20% and 32%.

## Addendum, 2026-11-24

The food logging QA (`docs/qa/2026-11-24-food-logging-qa.md`, H2) found three more generic rows with wrong values that the energy-only audit can't see, because two of them have about the right energy. They were corrected the same way and added to `reference-fixes.csv`.

| Bulgarian name | Before | After | Source of the new values |
|-|-|-|-|
| Туршия | 121 kcal, 1.5 g protein, 26.6 g carbs, 0.9 g fat (USDA sweet chow-chow relish) | 28 kcal, 1 g protein, 6 g carbs, 0 g fat | the two "Мешана туршия" labels in the seed (Брей!, Реколта) |
| Броколи | 33 kcal, 4.4 g protein, 1.8 g carbs, 1 g fat | 32 kcal, 2.57 g protein, 6.29 g carbs, 0.34 g fat | its USDA twin "Broccoli, raw" |
| Протеини | 400 kcal, 100 g protein, 0 g carbs, 0 g fat | 385 kcal, 66.67 g protein, 17.95 g carbs, 5.13 g fat | USDA "Beverages, ABBOTT, EAS whey protein powder" (FDC 173167) |

Comparing protein, carbs and fat with the USDA twin, not just energy, flags 15 more rows, mostly small fruit and vegetable differences: Кисело мляко 3.6%, Аспержи, Гъби, Карфиол, Кумкуат, Къпини, Лимони, Лук пресен, Маруля, Сини сливи, Сушени смокини, Свинско филе, Миди - Ядка (Задушени), Лефер and Херинга. Some are real local differences, such as Bulgarian yogurt and local fish. They are left for a separate pass.


## Second addendum, 2026-11-25

The 15 rows left for a separate pass on 2026-11-24 are corrected. Their values came from spidersport.com and macroscope.top, and several were plainly rounded or incomplete (kumquat with no protein, plums with 5 g of carbs, dried figs with 0.7 g of protein against USDA's 3.3). Each now takes its USDA twin's values, as Броколи did. Bulgarian yogurt is the exception: its USDA twin is an American whole-milk yogurt at 77 kcal, so it takes the median of the seven "Кисело мляко 3.6%" labels in the seed instead. The bluefish (лефер) and herring rows stay on USDA: they are the same species, and no Bulgarian table with a better source was at hand. Values are kcal / protein / carbs / fat per 100 g.

| Bulgarian name | Before | After | Source of the new values |
|-|-|-|-|
| Кисело мляко 3.6% | 60 / 3.2 / 2.5 / 3.6 | 62 / 3.2 / 4.2 / 3.6 | median of the seed's seven "Кисело мляко 3.6%" labels |
| Аспержи | 25 / 3 / 2 / 0.6 | 20 / 2.2 / 3.88 / 0.12 | USDA "Asparagus, raw" |
| Гъби | 13.5 / 1.8 / 0.4 / 0.5 | 22 / 3.09 / 3.26 / 0.34 | USDA "Mushrooms, white, raw" |
| Карфиол | 34 / 3.6 / 3 / 1 | 22.9 / 1.64 / 4.72 / 0.24 | USDA "Cauliflower, raw" |
| Кумкуат | 60 / 0 / 15 / 0 | 71 / 1.88 / 15.9 / 0.86 | USDA "Kumquats, raw" |
| Къпини | 56 / 0.7 / 14 / 0 | 43 / 1.39 / 9.61 / 0.49 | USDA "Blackberries, raw" |
| Лимони | 19 / 1 / 3 / 0 | 29 / 1.1 / 9.32 / 0.3 | USDA "Lemons, raw, without peel" |
| Лук пресен | 23 / 2 / 3 / 0.5 | 32 / 1.83 / 7.34 / 0.19 | USDA "Onions, spring or scallions (includes tops and bulb), raw" |
| Маруля | 14 / 0.8 / 1.7 / 0.5 | 17 / 1.24 / 3.24 / 0.26 | USDA "Lettuce, cos or romaine, raw" |
| Сини сливи | 35 / 0.5 / 5 / 0 | 46 / 0.7 / 11.42 / 0.28 | USDA "Plums, raw" |
| Сушени смокини | 227 / 0.7 / 52 / 0 | 249 / 3.3 / 63.9 / 0.92 | USDA "Figs, dried, uncooked" |
| Свинско филе | 134 / 20.5 / 0 / 5.5 | 109 / 20.95 / 0 / 2.17 | USDA "Pork, fresh, loin, tenderloin, separable lean only, raw" |
| Миди - Ядка (Задушени) | 140 / 25.6 / 5.1 / 2 | 172 / 23.8 / 7.39 / 4.48 | USDA "Mollusks, mussel, blue, cooked, moist heat" |
| Лефер | 103 / 19.8 / 0 / 2.5 | 124 / 20.04 / 0 / 4.24 | USDA "Fish, bluefish, raw" |
| Херинга | 129 / 20.7 / 0 / 4.9 | 158 / 17.96 / 0 / 9.04 | USDA "Fish, herring, Atlantic, raw" |

All 15 are in `reference-fixes.csv`, so databases seeded before the change are corrected at the next start, unless a row was changed by hand since. Logged entries keep the totals they were saved with.
