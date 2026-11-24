# Food logging QA, 2026-11-24

Hands-on pass of food logging and its accuracy, done as an invented user ("Maria", below) logging ten days of ordinary Bulgarian and English eating through the real UI. The build was `main` at e6a90db, run locally on a fresh `gym_qa` database. The browser was the desktop app's built-in browser emulating a 390 px wide phone.

AI parsing used live OpenRouter models with this lineup:

- parser `meta/muse-ember` at minimal effort;
- matcher `google/gemini-decision-1`;
- estimator `google/gemini-4-carbon` at medium effort;
- namer and other tasks on the parser's model.

The lineup came from the notes of the 2026-11-21 and 2026-11-23 sessions, not from production, which I could not read over SSH. No result in this report came from the fallback parser.

Coverage: 31 parses for Maria, 3 re-checks on a second, empty account, Search, Recent, a saved meal, Copy yesterday's meal, Quick add, Manual (per 100 g and per 100 ml), editing an entry on a past day, and Delete with Undo. Nothing was logged to production.

## Method and limits

- **Reference values.** They are standard values: USDA for generic foods, typical Bulgarian labels for products and dishes. A row is **ok** within ±20% of the reference kcal, **off** from 20% to 35% or for a clearly wrong macro split, and **wrong** past 35% or for a different food. Where the miss comes from a portion or a recipe the text leaves open, for example how much oil is in "салата" or how big a bakery piece is, the row is capped at **off**, because the reference is a guess too.
- **Evidence.** Rows marked *DB* are confirmed from the database itself, for example a seed row whose values are impossible or a better product sitting unused. The other rows rest on my reference numbers and carry their uncertainty, especially for home-made dishes and vague portions.
- **Repeats.** Each phrase ran once for Maria. The worst matches were typed again on a second, empty account (no usual foods, nothing saved), and the report says which ones reproduced.
- **Clicks.** Typing went through real key events. For about half the actions, Send and Save were triggered with a scripted `click()` on the real button rather than a tap at coordinates. Every other control was tapped.
- **Language.** The UI was in English throughout. The Bulgarian name and status display is being changed in another session and is not covered here.

## Summary

Parsing itself is good. Every food I typed got its own row in all 34 runs, with nothing dropped and nothing duplicated, in Bulgarian, English, mixed text, with typos ("goliam") and in Latin-script Bulgarian ("omlet ot 3 qica sus sirene"). Amounts for vague portions were sensible. The Lidl pastry piece weights, usual amounts, saved meals, Copy yesterday's meal, Quick add, Manual entry, Undo and the already-saved review all worked as specified.

The accuracy problems come from matching and from the data, not from reading. Ranked by how much they cost a real user:

1. **A wrong match locks itself in** (H3). Saving a "Check match" row without changing it turns that food into "Your usual" for those words. On day 10 the wrong coffee from day 1 came back marked "Your usual", with no warning left.
2. **Some common Bulgarian foods match the wrong food** (H1). Кюфтета became US elk patties twice, although a reference "Кюфте" row exists and never reached the top five candidates. Coffee with milk became a sweetened bottled coffee drink in 2 of 3 tries.
3. **Some seed and product rows have wrong values** (H2). Туршия carries sweet chow-chow relish values, "Протеини" is 100% protein, generic Броколи has its protein and carbs out of proportion, and "мюсли" picks an Open Food Facts row at 134 kcal per 100 g. All are confirmed in the database, and the туршия and muesli results reproduced on a second account.
4. **The confidence chip says little** (M1). "Check match" sat on 31 of 49 matched rows, and only 3 of those were wrong. "Database" sat on 14, and 2 of those were wrong. A user learns to ignore the warning.
5. **Estimates are slow and sometimes silently fall back** (M2). All 7 parses that needed the estimator took 9.4 to 15.7 s, against 3.0 to 7.7 s for the rest. Two of the 7 hit the 12 s limit and kept the parser's guesses.
6. **A wrong match can't be fixed after saving** (M6). The entry page has no Change food, so a wrong match means deleting and re-adding the entry, and the usual food stays wrong.

Severity: **H** = wrong numbers a user is unlikely to notice, or that repeat themselves; **M** = clearly wrong or slow, with a workaround; **L** = polish.

## Problems

### High

**H1. Common foods match the wrong food: кюфтета → elk patties; "кафе с мляко" → sweetened bottled coffee.**

- *Where:* day 7 (21 Nov), lunch, "кюфтета 4 броя, картофено пюре половин чиния" (run 19); day 1 (15 Nov), breakfast, "…, кафе с мляко" (run 1).
- *Steps:* Type, enter either text, read.
- *What happened, кюфтета:* the row read "Ground elk patties", from "Elk, free range, ground, cooked patties (Shoshone Bannock)". That is 143 kcal and 29.5 g protein per 100 g, so 300 g gave 429 kcal and 88 g protein, chip "Check match".
  - The parser's English name was "meatballs, beef and pork, cooked".
  - The ranking's top five were elk (1.282), "Кюфтета с чушки" (1.142), "Кюфтета с боб яхния", "Кюфтета по чирпански" and "Кюфтета по ориенталски". The decision model picked elk at 0.58.
  - The reference row "Кюфте" / "Bulgarian grilled minced-meat patty (kyufte)" (id 8050, `bg-foods.csv:28`, 284 kcal) was not among them. Neither were the dozen plain pork and beef кюфтета products (227 to 302 kcal).
  - The likely cause is that the plural "кюфтета" doesn't meet the singular "Кюфте" after the light stem, and the English "meatballs" doesn't meet "patty". I did not trace this in code.
  - The reference row's own protein (30.2 g per 100 g) looks high for кюфте and is worth checking at the same time.
- *What happened, coffee:* the row was "Beverages, coffee, ready to drink, milk based, sweetened", at 71 kcal per 100 g, so 150 g gave 106 kcal. Plain coffee with a splash of milk is 20 to 35 kcal. The brewed-coffee rows ("Кафе, варено / филтърно" at 1 kcal, "Кафе еспресо" at 9) were not used.
- *Reproduced:* on the empty account, кюфтета became elk again, with the same top five (320 g, 458 kcal, 94 g protein).
  - "кафе с мляко" has now been read three times. Inside a list it was wrong once (run 1) and right once (run 32, split into brewed coffee plus 50 g of milk). Alone it was wrong (run 34, 200 g, 142 kcal).
  - So the coffee result varies between runs, and is wrong in 2 of 3.
- *What should happen:* кюфтета should land on a Bulgarian meatball at about 250 to 270 kcal and 16 to 18 g protein per 100 g, so four pieces come to about 730 kcal and 48 g protein. "Кафе с мляко" should be brewed coffee plus milk, or a coffee-with-milk row of about 15 to 25 kcal per 100 g.

**H2. Wrong values in seed and product rows.** All of these are confirmed in the database and the seed file.

- **Туршия** (`bg-foods.csv:60`, generic "Mixed vegetables, pickled (tursiya)"): 121 kcal, 26.6 g carbs per 100 g, copied from USDA's sweet chow-chow relish (row 5629 has the same numbers). Bulgarian brined туршия is about 20 to 30 kcal. Day 4 dinner, "туршия малко", 50 g gave 60 kcal instead of about 12. It reproduced on the empty account. An Open Food Facts "Туршия смес" at 17.2 kcal is in the database.
- **Протеини / Protein powder** (`bg-foods.csv:272`): 400 kcal, 100 g protein, 0 carbs, 0 fat per 100 g, which no product has. Day 9 snack, "протеинов шейк 1 мерителна лъжица с вода", 30 g gave 30 g protein. A typical whey concentrate gives about 23 g, so protein is 30% too high. On the empty account the same words matched a different row ("Beverages, nutritional shake mix, high protein", 53.6 g protein), which isn't whey either.
- **Броколи** (`bg-foods.csv:97`, "Broccoli, raw", sourced from spidersport.com): 33 kcal, 4.4 g protein, 1.8 g carbs, 1 g fat. USDA raw broccoli is 32 kcal, 2.6 g protein and 6.3 g carbs. The energy is fine, but protein is 70% too high and carbs 70% too low. Day 7 dinner, "броколи на пара 200g".
- **Мюсли** (`bg-foods.csv:510`, Open Food Facts 3800218835633, brand "Нз"): 134 kcal, 15 g carbs per 100 g, which looks like a prepared or mis-entered product. The other "Мюсли" (Гиоре) is 372. Day 8 breakfast, "мюсли с кисело мляко 2%", 50 g gave 67 kcal instead of about 185, chip "Database". It reproduced on the empty account (40 g, 54 kcal). The stored ranking shows the cause: the "Мюсли" group appears once, as product 8532 (the 134 kcal one, score 1.698), and the 372 kcal product is not offered. The same-name grouping in SPEC 6.1 takes the middle energy, and with two members it landed on the outlier.
- *What should happen:* generic reference rows should agree with USDA where a USDA equivalent exists, and pass the 4/4/9 check. A product far outside its category should not stand for a same-named group.

**H3. A wrong match saved once becomes "Your usual" and stops warning.**

- *Where:* day 1 breakfast (run 1), then day 10 (24 Nov) breakfast, "кафе с мляко" (run 29).
- *Steps:* on day 1, save the review with the coffee row still marked "Check match". On day 10, type "кафе с мляко".
- *What happened:* the row came back as "Sweetened milk coffee", chip **Your usual**, "Your usual amount, 150 g", 106 kcal. The decision model was not asked (match time 63 ms). The same happens to every row saved unchanged, including the elk patties had I kept them.
- *What should happen:* saving a row the app itself marked "Check match", without touching it, should not count as the user confirming that food. Otherwise the most-repeated foods are exactly the ones that never get corrected.
- *Note:* this is the specified behaviour. SPEC 6.1 "Usual foods" writes a usual food for every saved item that has a food, so the fix needs a SPEC change.

### Medium

**M1. The chip doesn't track correctness.** Across Maria's 56 rows:

| Chip | Rows | Wrong | Off |
|---|---|---|---|
| Check match | 31 | 3 (coffee, туршия, elk) | 4 (chicken soup, cucumber salad, ayran, protein powder) |
| Database | 14 | 2 (7days croissant, muesli) | 4 (grilled chicken, broccoli, "салата", tarator) |
| Your usual | 4 | 1 (coffee) | 0 |
| AI estimate | 7 | 0 | 1 (banitsa portion) |

Correct, ordinary matches such as Bulgarian-style chicken soup, banana, whole-wheat bread and oats were flagged, while the 7days croissant and muesli errors showed the green "Database" chip. With "Check match" on 63% of matched rows, users will stop reading it, which feeds H3.

**M2. The estimator is slow and sometimes silently falls back.**

- *Where:* runs 11, 14, 15, 18, 23, 26, 31: the doner, banitsa, pizza, palachinki, boza, bolognese and omelette.
- *What happened:* parses with an estimate took 9.4 to 15.7 s. Parses without one took 3.0 to 7.7 s, with a median of 6.2 s over all 31. `gemini-4-carbon` at medium effort finished 5 of the 7 calls in 7.1 to 11.1 s and timed out on 2 at 12 s (boza, run 23; omelette, run 31). Those rows kept the parser's guesses with nothing to show it, after the longest waits. The CLI self-test after `set-effort estimator medium` also timed out ("took longer than 12 s … no usable estimate for a sample food").
- *What should happen:* an estimate should come back well inside the deadline. A faster model or effort, or a deadline that matches the model, would do it.

**M3. A typed brand doesn't steer the match.**

- *Where:* day 5 (19 Nov), snack, "кроасан 7days, капучино" (run 12).
- *What happened:* the croissant matched USDA "Croissants, apple" at 254 kcal per 100 g, so 60 g gave 152 kcal, with chip "Database". The stored run explains it:
  - The parser's English name was plain "croissant".
  - Three USDA croissants (apple, cheese, butter) tied at 1.291. The only 7 Days product in the top five, "Кроасан 7Days Max Крем Брюле", scored 1.136.
  - The decision model picked the apple croissant at confidence 0.302, just over its 0.3 floor.
  - The database has 11 "7 Days" croissants at 429 to 473 kcal. Their brand field reads "7 Days" or "7 days", and I suspect the missing space ("7days") is why no brand bonus applied.
- *Fixing it by hand:* Change food, pre-filled with "кроасан 7days", did list the 7 Days croissants, and I picked "КРОАСАН МАКС КАКАО". None of them has a piece weight, so the app said "No weight per piece for this food" and I set 80 g myself.
- *Reproduced:* partly. On the empty account the same words matched "Кроасан 7Days Max Крем Брюле" (447 kcal), whose product name contains "7Days". So it says nothing about the brand field. It used a guessed 55 g, where a Max croissant is about 80 g.
- *What should happen:* the parser should keep the brand, the ranking should credit it with spaces and case ignored, and a tie between unrelated USDA variants should not be settled at 0.30 confidence. Piece weights for the common 7 Days products would fix the amount.

**M4. Bulgarian typed in Latin letters doesn't reach Cyrillic-only products.**

- *Where:* day 8 (22 Nov), snack, "boza 1 чаша" (run 23).
- *What happened:* the stored run has no ranking candidates at all for "boza", and the decision model answered "none" at 0.99. So the row became an estimate (250 g, 150 kcal) after a 15 s wait, and the estimator then timed out. The database has nine "Боза" products, all with Cyrillic-only names.
- *What should happen:* "boza" should find "Боза". "omlet ot 3 qica sus sirene" went fine only because it ended up an estimate anyway.

**M5. Search ranks look-alike words above the food** (not the name-language issue).

- "банан" lists "Банан нектар" (a Florina juice) first and "Bananas, raw" second (day 3, 17 Nov, lunch).
- "кафе" in Change food lists a canned latte, coffee beans, chocolate-coated coffee beans and coffee liqueurs, while "Кафе еспресо" and "Кафе, варено / филтърно" are in the database (day 1, breakfast). So the user can't easily fix H1's coffee.
- "мандарина" also lists "Пилешко филе с билкова марина" and other "марина" marinades (day 10, snack).
- "кисело мляко" returns 20 near-identical branded tubs and no generic row first (day 3, lunch).

**M6. An entry's food can't be changed after saving.** The entry page has Description, Amount and Meal only (day 5, trout, entry 39). Fixing a wrong match such as the coffee, the туршия or the 7days croissant means deleting the entry and adding it again, and H3's usual food stays wrong. SPEC 7 doesn't describe changing the food on an entry, so this would be a new feature.

**M7. "Grilled" meat is counted at cooked weight from a niche row, and the app never asks.** On day 2 (16 Nov), dinner, "chicken breast 200g grilled" matched "Chicken, broiler, rotisserie, BBQ, breast, meat only" (cooked), giving 288 kcal and 56 g protein. On day 9 (23 Nov), dinner, "пилешко филе 150г" was treated as raw (120 kcal per 100 g), which is the SPEC default. If Maria weighed the 200 g raw, the right figure is 240 kcal and 45 g protein. If she weighed it cooked, it is 330 kcal and 62 g from plain grilled breast. The app chose neither and didn't ask, as it would for rice. Severity is medium because protein targets depend on it.

### Low

**L1. "When did you weigh it?" is asked although "сух" was typed.** Day 7, dinner, "ориз басмати 80г сух" (run 20). The parser returned `nameOriginal` "ориз басмати" and `nameEn` "basmati rice, dry", dropping "сух". `FoodStates.Stated` only reads the original name and the preparation (`FoodMatcher.cs:198`). The value was right (dry, 286 kcal). It's only an unneeded question.

**L2. Oats never get the cooked-or-dry question.** Day 2, breakfast, "oats 60g". The matched USDA name "Cereals, oats, regular and quick, not fortified, dry" contains "cereal", which is in `FoodStates.MadeFrom` (`FoodStates.cs:28`), so the staple check returns null. SPEC 6.1 step 4 lists oats as a staple. In practice nearly everyone weighs oats dry, so the effect is small, but the code and the spec disagree.

**L3. Salads and dressings.** "cucumber salad" became plain cucumber (150 g, 15 kcal; day 2, dinner), and "салата" became romaine lettuce (150 g, 26 kcal; day 9, dinner). A Bulgarian side salad with oil is about 100 to 150 kcal. Nothing prompts for oil.

**L4. Recent.**

- The same food is listed twice when its two entries have different names: "Banana fruit" from a parse and "Bananas, raw" from Search (day 3).
- Each Add on Recent goes back to the Food day, so re-logging a three-item breakfast is three round trips (day 3, breakfast). The saved meal is the fast way, but a new user won't have one yet.
- The double listing follows SPEC 5.10 ("grouped by food and name"), so merging needs a SPEC change.

**L5. Count foods default to 100 g in Search and Recent.** Snickers (a 50 g bar), banana and mandarin have no piece weight, so Search offers 100 g (day 3, day 10). Two mandarins needed a typed 150 g. This follows SPEC 7's "else 100 g". The fix is piece weights in the data, not a code change.

**L6. Live numbers.** The entry page's kcal and protein don't change while the amount is edited, only after Save (day 5, trout). The saved-meal sheet shows no kcal per item or in total while grams are edited (day 4, breakfast).

**L7. Names.**

- Search entries keep the raw USDA name in the log: "Candies, MARS SNACKFOOD US, SNICKERS Bar" (day 3, snack).
- The namer dropped the brand: "Retro beer" for Загорка Бира Ретро (day 1, dinner).
- The namer renamed plain foods oddly: "Oat cereal" for oats, "Low calorie cola with caffeine" for Coca-Cola Zero.
- Brewed coffee got the status chip "cooked" (empty account).

**L8. Variant picks.**

- "бира загорка" became Загорка Бира Ретро (37 kcal) rather than Загорка Специално пиво (41 kcal). That is −10%, so within tolerance.
- "ябълка" became "Apples, raw, without skin" (48 kcal; with skin is 52).
- "rice a plate of" became "Rice, white, steamed, Chinese restaurant" (151 kcal, against 130 for plain cooked rice, +16%).

## Accuracy table

The app's figures are as saved. Evidence: *DB* = confirmed from database rows; *ref* = my reference values. On the empty account, кюфтета (H1) and туршия and muesli (H2) reproduced. Coffee was wrong in 2 of 3 runs, and "7days" reproduced only partly (M3).

| Day, meal | Typed | App: food → kcal / protein | Reference kcal / protein | Verdict | Evidence |
|---|---|---|---|---|---|
| 1 B | 2 яйца на очи | Egg, fried, 100 g → 196 / 13.6 | 180–200 / 12.5–13.6 | ok | ref |
| 1 B | филия пълнозърнест хляб | Whole-wheat bread 30 g → 76 / 3.7 | 70–80 / 3.5 | ok | ref |
| 1 B, 10 B | кафе с мляко | Sweetened RTD coffee 150 g → 106 / 3.0 | 20–35 / 1.5 | **wrong** (H1, H3) | DB |
| 1 L | пилешка супа една купа | BG chicken soup 350 g → 126 / 8.8 | 160–190 / 9–11 | off (low) | ref |
| 1 S | ябълка | Apple without skin 182 g → 87 / 0.5 | 95 / 0.5 | ok | ref |
| 1 D | шопска салата | Shopska 300 g → 219 / 8.7 | 250–300 / 9–10 | ok | ref |
| 1 D | 150г кебапчета | Kebapche 150 g → 411 / 22.5 | 380–430 / 24–27 | ok | ref |
| 1 D | 1 бира загорка 500мл | Загорка Ретро 500 g → 185 / 2.0 | 205 / 2 | ok (variant) | DB |
| 2 B | oats 60g | Oats, dry 60 g → 227 / 7.9 | 228 / 7.9 | ok | ref |
| 2 B | milk 200ml | Milk 3.7% 200 g → 128 / 6.6 | 120–128 / 6.6 | ok | ref |
| 2 L | lidl закуска с лютеница и сирене x2 | Lidl pastry 2 × 90 g → 419 / 13.7 | 419 / 13.7 (SPEC test) | ok | DB |
| 2 S | a handful of almonds | Almonds 28 g → 162 / 5.9 | 160–175 / 6 | ok | ref |
| 2 D | chicken breast 200g grilled | Rotisserie BBQ breast 200 g → 288 / 56.1 | 240 / 45 raw-weighed, or 330 / 62 cooked | off (M7) | ref |
| 2 D | rice a plate of | Rice, Chinese restaurant 200 g → 302 / 6.4 | 260 / 5.4 | ok (+16%) | ref |
| 2 D | cucumber salad | Cucumber 150 g → 15 / 1.1 | 60 with oil / 1 | off (L3) | ref |
| 3 L | Search: кисело мляко 400 g | Добротица 2% → 192 / 13.2 | 190–210 / 13–14 | ok | ref |
| 3 S | Search: Snickers 50 g | USDA Snickers → 246 / 3.8 | 245 / 4.3 | ok | ref |
| 3 D | мусака една порция | BG moussaka 350 g → 455 / 21.0 | 450–520 / 20–25 | ok | ref |
| 4 S | Manual protein bar 60 g | 216 / 19.8 | 216 / 19.8 (label) | ok | DB |
| 4 D | боб чорба 1 купа | Bean soup 350 g → 228 / 17.9 | 230–280 / 15–18 | ok | ref |
| 4 D | туршия малко | "Tursiya" (relish values) 50 g → 60 / 0.8 | 12 / 0.5 | **wrong** (H2) | DB |
| 5 L | doner kebap goliam | Estimate 400 g → 924 / 44 | 800–1000 / 40–50 | ok | ref |
| 5 S | кроасан 7days | USDA apple croissant 60 g → 152 / 4.4 | 7 Days 60–80 g → 275–365 / 4–5 | **wrong** (M3) | DB |
| 5 S | капучино | Hell canned cappuccino 150 g → 84 / 3.9 | 60–80 / 3–4 | ok | ref |
| 5 D | пъстърва на фурна 300г | Trout, cooked 300 g → 570 / 79.9 | 570 / 80 if 300 g is edible fish | ok (portion unclear) | ref |
| 6 B | баница с извара едно парче | Estimate 120 g → 312 / 10.2 | 400–550 / 12–16 (bakery piece 150–200 g) | off (low portion) | ref |
| 6 B | айрян | Ayran 250 g → 65 / 5.0 | 90–110 / 5–6 | off (low) | ref |
| 6 L | pizza margarita 2 slices | Estimate 240 g → 600 / 25.2 | 450–650 / 20–28 | ok | ref |
| 6 S | coca cola zero 330ml | Low-cal cola 330 g → 7 / 0.4 | 1 / 0 | ok | ref |
| 6 D | 2 чаши червено вино | Red wine 300 g → 255 / 0.2 | 250 / 0 | ok | ref |
| 6 D | малко сирене / шепа орехи | Sirene 30 g → 90 / 5.4; walnuts 30 g → 196 / 4.6 | 80–90 / 5; 196 / 4.6 | ok | ref |
| 7 B | palachinki 3 бр с мед | Estimate 180 g → 437 / 10.3 | 450–580 / 12 | ok (low edge) | ref |
| 7 L | кюфтета 4 броя | Elk patties 300 g → 429 / 88.4 | 730 / 48 | **wrong** (H1) | DB |
| 7 L | картофено пюре половин чиния | Mashed potato 150 g → 170 / 2.8 | 150–180 / 3 | ok | ref |
| 7 D | сьомга 180 g | Atlantic salmon, raw → 374 / 36.8 | 374 / 37 | ok | ref |
| 7 D | броколи на пара 200g | Reference broccoli → 66 / 8.8 | 70 / 5.2 | off (protein; H2) | DB |
| 7 D | ориз басмати 80г сух | Basmati, dry → 286 / 5.0 | 285 / 6 | ok | ref |
| 8 B | мюсли (с кисело мляко) | "Мюсли" (Нз) 50 g → 67 / 2.5 | 185 / 5 | **wrong** (H2) | DB |
| 8 B | кисело мляко 2% | Reference yogurt 2% 200 g → 104 / 8.0 | 100–110 / 7–8 | ok | ref |
| 8 L | пълнени чушки с кайма 3 бр | Stuffed peppers 450 g → 500 / 26.6 | 500–620 / 25–30 | ok | ref |
| 8 S | boza 1 чаша | Estimate 250 g → 150 / 1.3 | 90–200 (by type) | ok, but no match (M4) | DB |
| 8 D | леща яхния купа | Lentil stew 300 g → 288 / 20.1 | 270–330 / 15–20 | ok | ref |
| 8 D | half a pack of Milka | Milk chocolate 50 g → 268 / 3.8 | 265 / 3.5 | ok | ref |
| 9 B | 200г извара с мед и орехи | Izvara 200 g → 308 / 25.2; honey 20 g → 61; walnuts 30 g → 196 | izvara 200–310 / 25–34 | ok | ref |
| 9 L | pasta bolognese, a plate | Estimate 400 g → 568 / 27.2 | 520–640 / 25–30 | ok | ref |
| 9 S | протеинов шейк 1 мерителна лъжица | "Протеини" 30 g → 120 / 30.0 | 115 / 23 | off (protein +30%; H2) | DB |
| 9 D | пилешко филе 150г | Chicken breast, raw → 180 / 33.8 | 180 / 34 | ok | ref |
| 9 D | салата | Romaine lettuce 150 g → 26 / 1.9 | 100–150 with oil | off (L3) | ref |
| 10 L | таратор | Tarator 300 g → 144 / 6.3 | 180–240 / 6–8 | off (low edge) | ref |
| 10 S | Search: мандарина 150 g | Mandarin, raw → 83 / 1.6 | 80 / 1.2 | ok | ref |
| 10 D | omlet ot 3 qica sus sirene | Estimate (fallback) 180 g → 324 / 21.6 | 300–340 / 22–24 | ok | ref |

Of the 56 rows Maria's parses produced, 6 are **wrong** and 9 are **off**. The wrong rows are the coffee twice, туршия, the 7days croissant, elk and muesli. The largest single miss was day 7 lunch, about 300 kcal short and 40 g of protein over before I changed the food by hand.

## What worked

- **Parsing:** one row per food in every run, with no items dropped and no duplicates. Bulgarian, English, mixed text, typos and Latin-script Bulgarian were all read.
- **Lidl pastry:** "lidl закуска с лютеница и сирене x2" gave exactly the SPEC test result (2 × 90 g, 419 kcal, with the estimated-values note).
- **Usual amounts and foods:** "Your usual amount" worked for bread (30 g), walnuts (30 g) and the banana (120 g).
- **Saved meal:** saving a meal and re-logging it with edited grams (oats changed to 50 g gave 190 kcal), plus Copy yesterday's breakfast ("Copied 3 items").
- **Quick add:** totals and macros as typed, with "≥" and an explanation for fibre and saturated fat on that day.
- **Manual entry:** per 100 g (the bar came to 216 kcal) and per 100 ml (500 ml of ayran showed "500 ml" and 200 kcal).
- **Editing a past day:** the trout went from 300 to 250 g and gave 475 kcal.
- **Delete, Undo and repeat submissions:** Delete showed its toast, and Undo restored the entry. Reopening a saved review showed "This review was already saved." without adding anything again. A double tap on a Recent Add added the food once.
- **Speed:** no server errors in the log for the whole session. Parses without an estimate took 3 to 8 s.

## Seen but outside food logging

- **Onboarding Rate per week:** the field shows a placeholder "0.5" that reads as a value. Save does nothing visible until a value is typed; a validation bubble scrolls out of view.
- **Today:** "Trend weight 0kg" right after setup logged 68 kg.
- **Food day with a manual drink:** "≥0 g" for fibre and saturated fat when no food on the day has them.
- **Unexplained loss of text:** once, on day 4 dinner, the typed text vanished before submit and no request reached the server. It did not happen again and has no trace, so it may have been my tooling.

## Proposed fixes

Ordered by value for effort. None are made yet; pick the ones to do.

1. **Seed data (H2).**
   - Correct Туршия, Протеини and Броколи in `bg-foods.csv`. Броколи can take USDA's raw broccoli values. Туршия should be a brined, not sweet, pickle (my rough figure is 25 kcal). Протеини should be a whey concentrate profile (my rough figure is 390 kcal, 78 g protein, 7 g carbs, 6 g fat). Those two figures are recalled, so source them from a label or table before using them.
   - Check the protein of the existing "Кюфте" row (30.2 g per 100 g).
   - Then run the 4/4/9 and USDA-equivalent check over every `generic` row sourced from calories24, spidersport, macroscope or zdravoslovno, the way the 39 rows were audited on 2026-11-21.
   - For "Мюсли" (Нз, 134 kcal), mark the product as bad, or make same-name grouping skip members far from the group's median energy. With two members, "middle" is undefined, so a rule is needed there anyway.
2. **Usual food lock-in (H3).** Don't write a `UsualFood` for a row saved unchanged while it was marked "Check match". Or write it but keep the chip at "Check match" until the user confirms it once. This is a small change in the confirm path, but it changes SPEC 6.1 "Usual foods".
3. **Plural and singular in the ranking (H1).** Find out why "кюфтета" / "meatballs" never reaches "Кюфте" / "patty (kyufte)": the stem, the English name, or the trigram cut-off. A ranking test with the "кюфтета 4 броя" case and the 8050 row would pin it.
4. **Coffee and similar drink pairs (H1).** Add a "coffee with milk" reference row, or have the parser always split "X с мляко" drinks into coffee plus milk. It did that in 1 of 3 runs.
5. **Brand (M3).** Have the parser keep a typed brand, and compare brands with spaces, punctuation and case removed, so "7days" equals "7 Days". Add piece weights for the common 7 Days and Snickers-type products (L5).
6. **Estimator speed (M2).** Try `gemini-4-carbon` at low effort, or the parser's model, against the same 7 phrases. Or raise the deadline to match what medium actually takes. Either way, note on the row when it fell back to the parser's guess.
7. **Change food on a saved entry (M6).** Add the review's Change food sheet to the entry page, keeping the grams, and move the usual food along with it. This is new behaviour, so SPEC 7 needs a line.
8. **Chip calibration (M1).** Look at what produces "Check match" for rows like "Bulgarian-style chicken soup". For example, show it only below a decider confidence threshold or when the top two candidates are close. Show "Database" only when the ranking and the decider agree. The decider's confidence on the rows I read was 0.30 to 0.58 whether right or wrong, so the threshold alone won't separate them.
9. **Latin-script Bulgarian (M4).** Transliterate `NameOriginal` to Cyrillic as an extra search term, or have the parser return a Bulgarian name.
10. **Search ranking (M5).** Rank names whose first word is the query (after the light Bulgarian stem) above names that only contain it. Push drinks, juices and candies below the plain food for one-word fruit and drink queries.
11. **Small ones (L1, L2, L4, L6).** Check `NameEn` and the typed text for dry words. Don't let "cereal" in a USDA name block oats. Merge Recent rows by food id, which needs a SPEC 5.10 change. Update the entry page's numbers live as the amount changes.

No What's new entry applies: this is a report, not a feature.

## Appendix: the persona and the ten days

Maria, 31, an accountant in Plovdiv, 166 cm, 68 kg, cutting towards 62 kg at 0.5% a week. She lifts three times a week and is "Low active". Her targets on the account were 1906 kcal and 150 g protein. She cooks at home, gets lunch from Lidl or a place near the office, and eats out at weekends. She types fast, mixes languages and rarely weighs anything except oats and meat.

Text in quotes is exactly what was typed into Type.

- **Day 1, Sun 15 Nov (Type):**
  - B "2 яйца на очи, филия пълнозърнест хляб, кафе с мляко"
  - L "пилешка супа една купа и 2 филии хляб"
  - S "ябълка"
  - D "шопска салата и 150г кебапчета, 1 бира загорка 500мл"
- **Day 2, Mon 16 Nov (Type):**
  - B "oats 60g with milk 200ml and a banana"
  - L "lidl закуска с лютеница и сирене x2"
  - S "a handful of almonds"
  - D "chicken breast 200g grilled, rice a plate of, cucumber salad"
- **Day 3, Tue 17 Nov:**
  - B: Recent (oats, banana, milk), then Save as a meal ("Овесена закуска")
  - L: Search "кисело мляко" (Добротица 2%, 400 g) and Search "банан" (120 g)
  - S: Search "snickers" (50 g)
  - D: Type "мусака една порция"
- **Day 4, Wed 18 Nov:**
  - B: saved meal with oats changed to 50 g
  - L: Quick add "Happy - салата Цезар" 520 kcal, P 28, C 22, F 36
  - S: Manual protein bar, 360 / 33 / 30 / 12 per 100 g, 60 g
  - D: Type "боб чорба 1 купа, туршия малко, 1 филия хляб"
- **Day 5, Thu 19 Nov:**
  - B: Copy yesterday's breakfast
  - L: Type "doner kebap goliam"
  - S: Type "кроасан 7days, капучино", croissant changed to a 7 Days product at 80 g; cappuccino later deleted
  - D: Type "пъстърва на фурна 300г с картофи", trout later edited to 250 g
- **Day 6, Fri 20 Nov (Type):**
  - B "баница с извара едно парче, айрян"
  - L "pizza margarita 2 slices"
  - S "coca cola zero 330ml"
  - D "2 чаши червено вино и малко сирене, шепа орехи"
- **Day 7, Sat 21 Nov (Type):**
  - B "palachinki 3 бр с мед"
  - L "кюфтета 4 броя, картофено пюре половин чиния", elk changed to "Кюфтета по Цариградски"
  - D "сьомга 180 g, броколи на пара 200g, ориз басмати 80г сух"
- **Day 8, Sun 22 Nov (Type):**
  - B "мюсли с кисело мляко 2%"
  - L "пълнени чушки с кайма 3 бр"
  - S "boza 1 чаша"
  - D "леща яхния купа, half a pack of Milka"
- **Day 9, Mon 23 Nov (Type):**
  - B "200г извара с мед и орехи"
  - L "pasta bolognese, a plate"
  - S "протеинов шейк 1 мерителна лъжица с вода"
  - D "пилешко филе 150г, салата"
- **Day 10, Tue 24 Nov:**
  - B: Type "кафе с мляко", plus the saved meal
  - L: Type "таратор, 2 филии хляб"
  - S: Search "мандарина" (150 g)
  - D: Type "omlet ot 3 qica sus sirene"

Re-checks on the empty account, not saved except the coffee:

- "кафе с мляко, кроасан 7days, мюсли с кисело мляко 2%"
- "кюфтета 4 броя, туршия малко, протеинов шейк 1 мерителна лъжица"
- "кафе с мляко"
