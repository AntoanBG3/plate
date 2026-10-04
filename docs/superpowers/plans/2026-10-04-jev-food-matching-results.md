# Jev food matching: Phase 0 results

Run 2026-10-04 14:36. 41 items from 33 entries; 19 Bulgarian, 22 English; 8 labelled none. Jev answered as typesafe/jev-1.13-20260917.

## Accuracy

| Approach | All | Bulgarian | English | None caught | Matchable wrongly rejected |
|---|---|---|---|---|---|
| a) Rank top-1 (today) | 31/41 (76%) | 14/19 (74%) | 17/22 (77%) | 4/8 (50%) | 0/33 (0%) |
| b) Jev over Rank top 5 | 34/41 (83%) | 16/19 (84%) | 18/22 (82%) | 8/8 (100%) | 6/33 (18%) |
| b) reversed order | 34/41 (83%) | 16/19 (84%) | 18/22 (82%) | 8/8 (100%) | 6/33 (18%) |
| c) Jev over trigram top 50 | 36/41 (88%) | 16/19 (84%) | 20/22 (91%) | 8/8 (100%) | 4/33 (12%) |
| c) reversed order | 37/41 (90%) | 17/19 (89%) | 20/22 (91%) | 8/8 (100%) | 3/33 (9%) |

## Shortlist recall (matchable items with a correct food in the list)

| List | All | Bulgarian | English |
|---|---|---|---|
| Rank top 1 | 27/33 (82%) | 13/15 (87%) | 14/18 (78%) |
| Rank top 5 | 30/33 (91%) | 14/15 (93%) | 16/18 (89%) |
| Trigram top 30 | 28/33 (85%) | 14/15 (93%) | 14/18 (78%) |
| Trigram top 40 | 28/33 (85%) | 14/15 (93%) | 14/18 (78%) |
| Trigram top 50 | 30/33 (91%) | 14/15 (93%) | 16/18 (89%) |

## Order sensitivity (same pick with the options reversed)

- b: 29/37 (78%)
- c: 34/40 (85%)

## Latency and cost per parse (one call per entry)

| Approach | Median ms | Max ms | Mean input tokens | Mean cost | Max cost | Errors |
|---|---|---|---|---|---|---|
| b | 303 | 698 | 908 | $0.000038 | $0.000061 | 0 |
| b-reversed | 303 | 511 | 908 | $0.000038 | $0.000061 | 0 |
| c | 330 | 564 | 4586 | $0.000193 | $0.000346 | 0 |
| c-reversed | 336 | 596 | 4586 | $0.000193 | $0.000346 | 0 |

## Accuracy by Jev confidence (listed order; correct / items)

| Confidence | b | c |
|---|---|---|
| 0.00 to 0.50 | 10 / 13 | 5 / 8 |
| 0.50 to 0.70 | 5 / 7 | 9 / 10 |
| 0.70 to 0.85 | 3 / 3 | 5 / 5 |
| 0.85 to 0.95 | 2 / 3 | 1 / 2 |
| 0.95 to 1.00 | 10 / 11 | 15 / 15 |

## Per item

✓ correct, ✗ wrong; the name is what was picked (— means no match).

| Item | Text | Label | a | b | c |
|---|---|---|---|---|---|
| en01.0 | chicken breast | usda:Chicken, breast, boneless, skinless, raw or usda:Chicken, broiler or fryers, breast, skinless, boneless, meat only, raw | ✓ usda:Chicken, breast, boneless, skinless, raw | ✓ usda:Chicken, broiler or fryers, breast, skinless, boneless, meat only, raw (0.65) | ✓ usda:Chicken, breast, boneless, skinless, raw (0.99) |
| en01.1 | white rice | usda:Rice, white, short-grain, enriched, cooked or usda:Rice, white, medium-grain, enriched, cooked or usda:Rice, white, short-grain, cooked, unenriched or usda:Rice, white, medium-grain, cooked, unenriched or usda:Rice, white, long-grain, regular, enriched, cooked or usda:Rice, white, long-grain, parboiled, enriched, cooked or usda:Rice, white, long-grain, parboiled, unenriched, cooked or usda:Rice, white, long-grain, regular, cooked, enriched, with salt or usda:Rice, white, long-grain, regular, cooked, unenriched, with salt or usda:Rice, white, long-grain, precooked or instant, enriched, prepared or usda:Rice, white, long-grain, regular, unenriched, cooked without salt | ✓ usda:Rice, white, short-grain, enriched, cooked | ✓ usda:Rice, white, medium-grain, cooked, unenriched (0.26) | ✓ usda:Rice, white, long-grain, regular, enriched, cooked (0.61) |
| en02.0 | banana | bg:Банан or bg:Банани or usda:Bananas, raw or usda:Bananas, overripe, raw or usda:Bananas, ripe and slightly ripe, raw | ✓ bg:Банан | ✓ bg:Банани (0.28) | ✓ bg:Банан (0.55) |
| en03.0 | eggs | usda:Egg, whole, cooked, scrambled | ✗ usda:Egg, whole, raw, fresh | ✓ usda:Egg, whole, cooked, scrambled (0.81) | ✓ usda:Egg, whole, cooked, scrambled (0.87) |
| en04.0 | greek yogurt 0% | usda:Yogurt, Greek, plain, nonfat or usda:Yogurt, Greek, nonfat, plain, CHOBANI or usda:Yogurt, Greek, plain, nonfat (Includes foods for USDA's Food Distribution Program) | ✓ usda:Yogurt, Greek, plain, nonfat | ✓ usda:Yogurt, Greek, plain, nonfat (0.68) | ✓ usda:Yogurt, Greek, plain, nonfat (0.70) |
| en04.1 | honey | usda:Honey or bg:Мед | ✓ usda:Honey | ✓ bg:Мед (0.43) | ✓ usda:Honey (0.61) |
| en05.0 | oats | usda:Oats, whole grain, steel cut or usda:Oats, whole grain, rolled, old fashioned or usda:Oats (Includes foods for USDA's Food Distribution Program) or usda:Cereals, QUAKER, Quick Oats, Dry or usda:Cereals, oats, instant, fortified, plain, dry or usda:Cereals, QUAKER, Quick Oats with Iron, Dry or usda:Cereals, oats, regular and quick, not fortified, dry | ✓ usda:Oats, whole grain, steel cut | ✗ — (0.21) | ✓ usda:Cereals, oats, regular and quick, not fortified, dry (0.57) |
| en05.1 | semi skimmed milk | off:3800231731134 Прясно мляко - 1.4% (Clever) or off:3800748002420 Прясно мляко 1.5% (Верея) or off:5998200483592 Прясно Мляко 1.5% (Billa) or off:3800217252318 Прясно мляко 2% (Каймакъ) or usda:Milk, reduced fat, fluid, 2% milkfat, with added vitamin A and vitamin D or usda:Milk, reduced fat, fluid, 2% milkfat, without added vitamin A and vitamin D | ✗ usda:Yogurt, plain, skim milk | ✗ — (0.99) | ✗ usda:Milk, reduced fat, fluid, 2% milkfat, with added nonfat milk solids and vitamin A and vitamin D (0.38) |
| en06.0 | salmon fillet | usda:Fish, salmon, Atlantic, farmed, cooked, dry heat or usda:Fish, salmon, Atlantic, wild, cooked, dry heat or usda:Fish, salmon, pink, cooked, dry heat or usda:Fish, salmon, chum, cooked, dry heat or usda:Fish, salmon, chinook, cooked, dry heat or usda:Fish, salmon, sockeye, cooked, dry heat or usda:Fish, salmon, coho, wild, cooked, dry heat or usda:Fish, salmon, coho, farmed, cooked, dry heat | ✓ usda:Fish, salmon, Atlantic, farmed, cooked, dry heat | ✓ usda:Fish, salmon, Atlantic, farmed, cooked, dry heat (0.97) | ✓ usda:Fish, salmon, Atlantic, farmed, cooked, dry heat (0.99) |
| en06.1 | potatoes | usda:Potatoes, boiled, cooked in skin, flesh, without salt or usda:Potatoes, boiled, cooked without skin, flesh, without salt or usda:Potatoes, boiled, cooked in skin, flesh, with salt or usda:Potatoes, boiled, cooked without skin, flesh, with salt | ✓ usda:Potatoes, boiled, cooked in skin, flesh, without salt | ✓ usda:Potatoes, boiled, cooked without skin, flesh, without salt (0.43) | ✓ usda:Potatoes, boiled, cooked in skin, flesh, without salt (0.65) |
| en07.0 | big mac | usda:McDONALD'S, BIG MAC | ✓ usda:McDONALD'S, BIG MAC | ✓ usda:McDONALD'S, BIG MAC (0.95) | ✓ usda:McDONALD'S, BIG MAC (0.98) |
| en08.0 | peanut butter | usda:Peanut butter, creamy or usda:Peanut butter, reduced sodium or usda:Peanut butter, chunk style, without salt or usda:Peanut butter, smooth style, without salt or usda:Peanut butter, chunk style, with salt or usda:Peanut butter, smooth style, with salt or usda:Peanut butter, smooth, vitamin and mineral fortified or usda:Peanut butter, chunky, vitamin and mineral fortified or usda:Peanut Butter, smooth (Includes foods for USDA's Food Distribution Program) or usda:Peanut butter, smooth style, with salt (Includes foods for USDA's Food Distribution Program) | ✓ usda:Peanut butter, creamy | ✓ usda:Peanut butter, smooth style, without salt (0.31) | ✓ usda:Peanut butter, smooth style, with salt (0.29) |
| en08.1 | whole wheat bread | usda:Bread, whole-wheat, prepared from recipe or usda:Bread, whole-wheat, commercially prepared or bg:Хляб пълнозърнест | ✓ usda:Bread, whole-wheat, prepared from recipe | ✓ usda:Bread, whole-wheat, commercially prepared (0.54) | ✓ usda:Bread, whole-wheat, commercially prepared (0.62) |
| en09.0 | kombucha | none | ✓ — | ✓ — (undecided) | ✓ — (undecided) |
| en10.0 | salmon | usda:Fish, salmon, chum, raw or usda:Fish, Salmon, Chum, raw (Alaska Native) or usda:Fish, salmon, coho, wild, raw or usda:Fish, salmon, sockeye, raw or usda:Fish, salmon, sockeye (red), raw (Alaska Native) or usda:Fish, salmon, pink, raw or usda:Fish, salmon, chinook, raw or usda:Fish, salmon, coho, farmed, raw or usda:Fish, salmon, Atlantic, wild, raw or usda:Fish, salmon, Atlantic, farmed, raw or usda:Fish, salmon, Atlantic, farm raised, raw or usda:Fish, salmon, coho (silver), raw (Alaska Native) or usda:Fish, salmon, king (chinook), raw (Alaska Native) | ✓ usda:Fish, salmon, chum, raw | ✗ — (0.60) | ✓ usda:Fish, salmon, Atlantic, farm raised, raw (0.38) |
| en11.0 | halloumi | none | ✗ usda:Cereals ready-to-eat, QUAKER, CAP'N CRUNCH'S Halloween Crunch | ✓ — (1.00) | ✓ — (0.98) |
| en12.0 | tuna in water | usda:Fish, tuna, white, canned in water, drained solids or usda:Fish, tuna, light, canned in water, drained solids or usda:Fish, tuna, light, canned in water, drained solids (Includes foods for USDA's Food Distribution Program) or usda:Fish, tuna, white, canned in water, without salt, drained solids or usda:Fish, tuna, light, canned in water, without salt, drained solids | ✓ usda:Fish, tuna, white, canned in water, drained solids | ✓ usda:Fish, tuna, light, canned in water, drained solids (0.39) | ✓ usda:Fish, tuna, light, canned in water, drained solids (0.55) |
| en12.1 | avocado | usda:Avocados, raw, Florida or usda:Avocados, raw, California or usda:Avocados, raw, all commercial varieties or usda:Avocado, Hass, peeled, raw | ✓ usda:Avocados, raw, Florida | ✓ usda:Avocados, raw, all commercial varieties (0.78) | ✓ usda:Avocados, raw, all commercial varieties (0.72) |
| en13.0 | apple | bg:Ябълки or usda:Apples, raw, with skin (Includes foods for USDA's Food Distribution Program) | ✗ bg:Сос Ябълков Пай - OstroVit | ✗ usda:Apples, raw, without skin (0.50) | ✗ — (0.94) |
| en14.0 | паста | usda:Pasta, dry, enriched or usda:Pasta, dry, unenriched or off:3800211640036 Паста Макарони (Хръстев) or off:3800232890069 Паста от твърда пшеница (Sole Mio) | ✗ off:3800235560204 Паста (Нз) | ✓ usda:Pasta, dry, unenriched (0.50) | ✓ usda:Pasta, dry, unenriched (0.58) |
| en15.0 | shakshuka | none | ✓ — | ✓ — (undecided) | ✓ — (1.00) |
| en16.0 | seitan | none | ✓ — | ✓ — (undecided) | ✓ — (1.00) |
| bg01.0 | пилешко филе | off:3800202649925 Пилешко филе (КФМ) or off:3800837440508 Пилешко филе (Тандем) or usda:Chicken, broiler or fryers, breast, skinless, boneless, meat only, raw or off:3800137000433 Пилешко филе нарязано (Меско) or usda:Chicken, broilers or fryers, breast, meat only, cooked, roasted or usda:Chicken, broiler or fryers, breast, skinless, boneless, meat only, cooked, grilled or usda:Chicken, broilers or fryers, breast, skinless, boneless, meat only, with added solution, raw or usda:Chicken, broiler or fryers, breast, skinless, boneless, meat only, with added solution, cooked, grilled or usda:Chicken, breast, boneless, skinless, raw | ✓ off:3800202649925 Пилешко филе (КФМ) | ✗ — (0.44) | ✓ usda:Chicken, broiler or fryers, breast, skinless, boneless, meat only, cooked, grilled (0.66) |
| bg02.0 | боб чорба | bg:Боб чорба | ✓ bg:Боб чорба | ✓ bg:Боб чорба (0.94) | ✓ bg:Боб чорба (0.98) |
| bg03.0 | баница със сирене | bg:Баница със сирене | ✓ bg:Баница със сирене | ✓ bg:Баница със сирене (0.98) | ✓ bg:Баница със сирене (0.99) |
| bg04.0 | кисело мляко | off:3800000101298 Кисело мляко (LB) or bg:Кисело мляко 3.6% or usda:Yogurt, plain, whole milk or off:3800226522792 Кисело мляко 2% (Billa) or off:20710088 Кисело Мляко 4% (Родна Стряха) or off:3800500399119 Кисело мляко 2% (Добротица) or off:3800200790100 Кисело мляко 4,5% (Лактима Балкан) or off:20963811 Кисело мляко 3.6% (Без) or off:3800748002376 Кисело мляко 2.9% (Верея) or off:4056489473749 Кисело Мляко 3,6% (Родна Стряха) or off:3801234567898 Кисело мляко 3,6% (Верея) or off:20708894 Кисело мляко 3.6% (Родна стряха) or off:3800204181003 Кисело мляко 3.6% (Болярче) or off:3800000101281 Кисело мляко 3,6% (LB Bulgaricum) or off:3800200790315 Кисело мляко 3.6% (Лактима Балкан) or off:3800205770343 Кисело мляко 3.6% (Beliisa) | ✓ off:3800000101298 Кисело мляко (LB) | ✓ usda:Yogurt, plain, whole milk (0.48) | ✗ — (0.16) |
| bg05.0 | бяло сирене | bg:Бяло саламурено сирене краве or bg:Бяло саламурено сирене овче or off:3800231301146 Краве сирене БДС (LaCrima) or off:3800212494164 Краве сирене без лактоза (Бор-Чвор) or off:3800231301139 Българско бяло саламуренено краве сирене БДС (LaCrima) or off:3800233810387 Краве сирене (Егрек) or off:3800143980194 Краве сирене (Каймакъ) or off:3800231301245 Краве сирене (LaCrima) or off:3800226520743 Краве сирене (Саяна) | ✗ off:5711953043116 Бяло сирене на кубчета (Arla) | ✓ bg:Бяло саламурено сирене краве (0.68) | ✓ bg:Бяло саламурено сирене краве (0.72) |
| bg06.0 | шопска салата | bg:Шопска салата | ✓ bg:Шопска салата | ✓ bg:Шопска салата (0.98) | ✓ bg:Шопска салата (0.99) |
| bg07.0 | кебапчета | off:4049726006138 Кебапчета &quot;Стара платина&quot; (Брей!) or off:4049726011484 Кебапчета Стара Планина от свинско месо (Брей!) or bg:Кебапче or off:4049726008439 Телешки кебапчета (Шеф месар) or off:3800231500075 Телешки кебапчета (Няма) or off:3800236510208 Свински кебапчета (Осем) or off:3800779050186 Свинси кебапчета &quot;Стара Планина&quot; (Българска ферма) or off:3800229320869 Охладени свински кебапчета &quot;Стара Планина&quot; (Metro chef) | ✓ off:4049726006138 Кебапчета &quot;Стара платина&quot; (Брей!) | ✓ bg:Кебапче (0.70) | ✓ bg:Кебапче (0.74) |
| bg07.1 | пържени картофи | bg:Пържени картофи or usda:Restaurant, family style, french fries or usda:Fast foods, potato, french fried in vegetable oil | ✓ bg:Пържени картофи | ✓ bg:Пържени картофи (0.92) | ✓ bg:Пържени картофи (0.84) |
| bg08.0 | пълнозърнест хляб | usda:Bread, whole-wheat, commercially prepared or bg:Хляб пълнозърнест or off:3800228184011 Пълнозърнест хляб (Симид) or off:3800091785124 Пълнозърнест Хляб (Вита) or off:3800091730476 Пълнозърнест хляб (Вита) or off:20324452 Пълнозърнест хляб (Tastino) or off:3800228184219 Пълнозърнест хляб (Симид) or usda:Bread, whole-wheat, prepared from recipe | ✓ off:3800228184011 Пълнозърнест хляб (Симид) | ✗ — (0.27) | ✓ bg:Хляб пълнозърнест (0.44) |
| bg08.1 | лютеница | bg:Лютеница or off:3800740022150 Лютеница (Deroni) or off:3800740024192 Лютеница (Deroni) or off:20528553 Лютеница (Freshona) or off:4049726010142 Лютеница (Брей!) or off:3800740031527 Лютеница or off:3800012195124 Семейна лютеница (Олинеза) or off:20431884 Лютеница домашна (Freshona) or off:3800128906973 Лютеница Първомай (Булконс) or off:20322786 Лютеница финомляна (Freshona) or off:20962418 Лютеница едромляна (Родна стряха) or off:3800099201503 Лютеница финосмляна (Консервинвест) or off:4049726010128 Лютеница едросмляна (Брей!) or off:3800099207734 Лютеница едросмляна (Хоро) or off:3800099201107 Лютеница фино смляна (Консервинвест) or off:3800060030217 Лютеница едро смляна (Златна нива) or off:3800012191126 Лютеница фино смляна (Олинеза) or off:3800128901251 Лютеница първомай БДС (Булконс) or off:3800740021238 Лютеница домашна едросмляна (Царица) or off:3800740014773 Лютеница домашна едросмляна (Велика) | ✓ off:3800740022150 Лютеница (Deroni) | ✓ bg:Лютеница (0.46) | ✗ — (0.33) |
| bg09.0 | ориз | usda:Rice, white, short-grain, enriched, cooked or usda:Rice, white, medium-grain, enriched, cooked or usda:Rice, white, short-grain, cooked, unenriched or usda:Rice, white, medium-grain, cooked, unenriched | ✓ usda:Rice, white, short-grain, enriched, cooked | ✓ usda:Rice, white, medium-grain, cooked, unenriched (0.26) | ✗ — (0.68) |
| bg10.0 | мусака | bg:Мусака | ✓ bg:Мусака | ✓ bg:Мусака (0.95) | ✓ bg:Мусака (0.98) |
| bg11.0 | таратор | bg:Таратор | ✓ bg:Таратор | ✓ bg:Таратор (0.98) | ✓ bg:Таратор (0.99) |
| bg12.0 | яйца | usda:Egg, whole, cooked, hard-boiled | ✗ usda:Egg, whole, raw, fresh | ✗ — (0.91) | ✓ usda:Egg, whole, cooked, hard-boiled (0.98) |
| bg13.0 | попара | none | ✗ usda:Beverages, chocolate drink, milk and soy based, ready to drink, fortified | ✓ — (1.00) | ✓ — (0.99) |
| bg14.0 | тиквеник | none | ✗ bg:Тиква | ✓ — (1.00) | ✓ — (0.47) |
| bg15.0 | овесени ядки | off:3800202288100 Овесени ядки (Dr. Keskin) or off:3800105100011 Овесени ядки (Фамилия Никола) or off:3800202287837 Овесени Ядки (Dr. Keskin) or off:3800202288124 Овесени ядки (Dr. Keskin) or off:3800005002330 овесени ядки (Слънчо) or off:3800081420233 Овесени ядки (Bioset) or off:4056489254157 Био овесени ядки (Crownfield) or off:9008684036620 Овесени ядки, едри (Clever) or usda:Oats, whole grain, steel cut or usda:Cereals, QUAKER, Quick Oats, Dry or usda:Oats, whole grain, rolled, old fashioned or usda:Cereals, QUAKER, Quick Oats with Iron, Dry or usda:Cereals, oats, regular and quick, not fortified, dry | ✓ off:3800202288100 Овесени ядки (Dr. Keskin) | ✓ off:3800202288100 Овесени ядки (Dr. Keskin) (0.18) | ✓ usda:Cereals, oats, regular and quick, not fortified, dry (0.46) |
| bg16.0 | трилече | none | ✓ — | ✓ — (undecided) | ✓ — (0.96) |
| bg17.0 | квас | none | ✗ off:3800205450238 Селски хляб с квас (Диамант) | ✓ — (0.98) | ✓ — (0.99) |
