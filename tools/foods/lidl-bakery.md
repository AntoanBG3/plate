# Lidl Bulgaria bakery (Пекарна Lidl)

Collected 2026-11-20 for feedback #7-#10 (Lidl bakery items matched unrelated foods). The rows are kept in `tools/foods/lidl-bakery.csv`, which `merge.mjs` appends to `src/Plate.Web/Data/Seed/bg-foods.csv`; edit them there and copy the change into the seed (`tests/js/curated-foods.test.cjs` checks they match). They are generic foods named "<product>, Пекарна Лидл", brand Lidl. `serving_g` and `piece_g` are both the piece weight below, and `basis` is the Basis column (`label` or `estimate`); the app shows a note on rows whose values are an estimate.

## Sources

- The current range: https://www.lidl.bg/c/pekarna/s10021543 (32 products in four sections: bread, rolls and baguettes, savoury, sweet).
- lidl.bg product pages no longer show nutrition. Their 2021-2022 versions ("От фурната на Lidl") printed the label: energy, fat, saturates, carbohydrate, sugars, protein, salt per 100 g. Those pages are read from the Wayback Machine; the links below are the snapshots used.
- The 2024 product pages give each product's current ingredient list (with QUID percentages) and piece weight, but no nutrition, except Баница със сирене и спанак.
- Open Food Facts has two of these (2000000137967 Закуска пица, 2000000137966 Кренвиршка) with the same ingredients and kcal as the 2021 pages.
- "Закуска със сирене и лютеница" (90 g) is not on lidl.bg; it appears in a July 2026 Lidl promotion (veliko.bg).

## Label values and estimates

`label`: values printed by Lidl Bulgaria (year in the note). Recipes may have changed since; where the 2024 ingredient list exists it was compared and matches unless noted. Fibre was not printed on the 2021-2022 labels and is left empty.

`estimate`: no published values. Computed from the current ingredient list and its percentages with per-100 g ingredient values (EU carbohydrate, i.e. without fibre), a 12% baking loss, and the closest Lidl label as the base dough. The same model reproduces two labelled products: Бял хляб 240 kcal (label 241), Кроасан с масло 424 kcal (label 419). Expect roughly ±10% on estimates, more for the filled savoury pastries, whose fat content is not given as a percentage.

Left out: Хлебче с маслини and Многозърнест хляб (2021). Their printed protein disagrees with their energy by more than 20%, and neither is sold now.

| Product | Piece g | kcal | Protein | Carbs | Fat | Sat. fat | Fibre | Basis | Note | Source |
|-|-|-|-|-|-|-|-|-|-|-|
| Бял хляб |  | 241 | 7.8 | 50 | 0.4 | 0.1 |  | label | lidl.bg label 2021 | [page](https://web.archive.org/web/20210927094934/https://www.lidl.bg/p/ot-furnata-na-lidl/byal-hlyab/p9924) |
| Кръгъл картофен хляб с лук |  | 270 | 8 | 56 | 1.5 | 0.2 |  | label | label 2021, sold then as "Хляб с картофено пюре и лук" | [page](https://web.archive.org/web/20210927094320/https://www.lidl.bg/p/ot-furnata-na-lidl/hlyab-s-kartofeno-pyure-i-luk/p9504) |
| Хляб със семена |  | 355 | 8 | 52 | 8.5 | 1.1 |  | label | label 2021 | [page](https://web.archive.org/web/20210927101835/https://www.lidl.bg/p/ot-furnata-na-lidl/hlyab-ss-semena/p10026) |
| Ръжено-пшеничен хляб |  | 256 | 7.5 | 53 | 0.3 | 0.1 |  | label | label 2021 | [page](https://web.archive.org/web/20210927103658/https://www.lidl.bg/p/ot-furnata-na-lidl/rzheno-pshenichen-hlyab/p25509) |
| Бял кайзер земел | 60 | 260 | 8.5 | 53 | 1.6 | 0.5 |  | label | label 2022, sold then as "Бял земел" | [page](https://web.archive.org/web/20221127155345/https://www.lidl.bg/p/ot-furnata-na-lidl/bial-zemel/p10024) |
| Кайзер земел със сусам | 60 | 266 | 8.5 | 53 | 2.2 | 0.6 |  | label | label 2022; printed protein "53 g" is a typo, 8.5 g from the energy | [page](https://web.archive.org/web/20221127145741/https://www.lidl.bg/p/ot-furnata-na-lidl/bial-zemel-sas-susam/p8839) |
| Чабата |  | 233 | 8.1 | 46.7 | 1 | 0.3 |  | label | label 2021 | [page](https://web.archive.org/web/20210927103811/https://www.lidl.bg/p/ot-furnata-na-lidl/chabata/p9131) |
| Минифранзела със семена | 110 | 268 | 8.5 | 53 | 2.4 | 0.6 |  | label | label 2022 | [page](https://web.archive.org/web/20221208001922/https://www.lidl.bg/p/ot-furnata-na-lidl/minifranzela-sas-semena/p8616) |
| Багета с чеснов пълнеж | 165 | 313 | 7.9 | 46 | 10.2 | 6.4 |  | label | label 2021, sold then as "Багета с чесново масло" | [page](https://web.archive.org/web/20210927095908/https://www.lidl.bg/p/ot-furnata-na-lidl/bageta-s-chesnovo-maslo/p8625) |
| Брецел | 95 | 291 | 9.1 | 54.2 | 3.5 | 1.5 |  | label | label 2021 | [page](https://web.archive.org/web/20210927105111/https://www.lidl.bg/p/ot-furnata-na-lidl/brecel/p8635) |
| Кроасан с масло | 57 | 419 | 6.5 | 40.3 | 25.3 | 17.8 |  | label | label 2021; 27% butter then and in 2024 | [page](https://web.archive.org/web/20210927092642/https://www.lidl.bg/p/ot-furnata-na-lidl/kroasan-s-maslo/p8641) |
| Кроасан с шунка и кашкавал | 95 | 347 | 9.2 | 30.2 | 20.7 | 13.6 |  | label | label 2021 | [page](https://web.archive.org/web/20210927104632/https://www.lidl.bg/p/ot-furnata-na-lidl/kroasan-s-shunka-i-kashkaval/p8637) |
| Кроасан с кренвирш | 95 | 330 | 10.1 | 28 | 19.2 | 8.2 |  | label | label 2021, sold then as "Кроасан с пуешки кренвирш" | [page](https://web.archive.org/web/20210927104448/https://www.lidl.bg/p/ot-furnata-na-lidl/kroasan-s-pueshki-krenvirsh/p8642) |
| Закуска пица | 90 | 226 | 8.3 | 23 | 11 | 5.6 |  | label | label 2021 | [page](https://web.archive.org/web/20221207224455/https://www.lidl.bg/p/ot-furnata-na-lidl/zakuska-pitsa/p8644) |
| Кренвиршка | 100 | 334 | 8.6 | 23 | 23 | 9.7 |  | label | label 2021; same ingredients in 2024 | [page](https://web.archive.org/web/20210927100155/https://www.lidl.bg/p/ot-furnata-na-lidl/krenvirshka/p8643) |
| Баница със сирене и спанак | 140 | 303 | 7 | 33 | 14 | 4.5 | 1.8 | label | label 2024 | [page](https://web.archive.org/web/20240224030651/https://www.lidl.bg/p/banita-s's-sirene-i-spanak/p10013749) |
| Поничка с плодов пълнеж | 65 | 344 | 6.8 | 43 | 15.7 | 6 |  | label | label 2022 | [page](https://web.archive.org/web/20221208000612/https://www.lidl.bg/p/ot-furnata-na-lidl/ponichka-s-plodov-palnej/p9288) |
| Шоколадов донат | 56 | 422 | 7 | 46.5 | 22.6 | 10.9 |  | label | label 2021, sold then as "Донат с шоколадова глазура" | [page](https://web.archive.org/web/20211021145618/https://www.lidl.bg/p/product-recommendation/donat-s-shokoladova-glazura/p8647) |
| Кроасан с нуга пълнеж и лешникова поръска | 85 | 439 | 6.6 | 44.3 | 25.5 | 13.8 |  | label | label 2022 of "Кроасан с нуга крем и шоколадова поръска"; the topping changed since | [page](https://web.archive.org/web/20221208002251/https://www.lidl.bg/p/ot-furnata-na-lidl/kroasan-s-nuga-krem-i-shokoladova-poraska/p8638) |
| Баничка със сирене |  | 322 | 8 | 32 | 18 | 8.5 |  | label | label 2022 | [page](https://web.archive.org/web/20221208000259/https://www.lidl.bg/p/ot-furnata-na-lidl/banichka-sas-sirene/p8951) |
| Пълнозърнеста питка с лимец |  | 270 | 8 | 52 | 2.5 | 0.4 |  | label | label 2022 | [page](https://web.archive.org/web/20221207230111/https://www.lidl.bg/p/ot-furnata-na-lidl/palnozarnesta-pitka-s-limets/p8950) |
| Пълнозърнест земел с ленено семе | 60 | 276 | 8.5 | 56 | 2 | 0.5 |  | label | label 2022 | [page](https://web.archive.org/web/20221207224104/https://www.lidl.bg/p/ot-furnata-na-lidl/palnozarnest-zemel-s-leneno-seme/p9128) |
| Бял хляб със сусамена поръска |  | 284 | 9 | 53 | 4 | 0.9 |  | label | label 2021 | [page](https://web.archive.org/web/20210927111017/https://www.lidl.bg/p/ot-furnata-na-lidl/byal-hlyab-ss-susamena-porska/p9251) |
| Зеленчуков хляб |  | 290 | 8 | 58 | 1.5 | 0.5 |  | label | label 2021 | [page](https://web.archive.org/web/20210927110904/https://www.lidl.bg/p/ot-furnata-na-lidl/zelenchukov-hlyab/p36886) |
| Розов донат с пълнеж | 70 | 482 | 4.9 | 47.2 | 30.1 | 16.2 |  | label | label 2021 | [page](https://web.archive.org/web/20210927104415/https://www.lidl.bg/p/ot-furnata-na-lidl/rozov-donat-s-plnezh/p8649) |
| Донат с пълнеж с вкус на ванилия | 70 | 480 | 4.7 | 47 | 30 | 15.3 |  | label | label 2022 | [page](https://web.archive.org/web/20221127151600/https://www.lidl.bg/p/ot-furnata-na-lidl/donat-s-palnej-s-vkus-na-vanilia/p9822) |
| Ръжен хляб с малц и течна ръжена закваска |  | 245 | 7 | 49 | 1 | 0.2 | 6.5 | estimate | 42% rye flour; scaled from the Ръжено-пшеничен хляб label (36% rye) | [page](https://web.archive.org/web/20240224035340/https://www.lidl.bg/p/r'zen-klyb-s-malt-i-tecna-r'zena-zakvaska/p10013711) |
| Хляб с три вида семена |  | 262 | 8.9 | 47.2 | 3.1 | 0.4 | 4.7 | estimate | wheat-rye bread plus 6% sunflower, soy and flax | [page](https://web.archive.org/web/20240301022239/https://www.lidl.bg/p/klyb-s-tri-vida-semena/p10013712) |
| Фермерски хляб |  | 210 | 6.6 | 43.3 | 0.7 | 0.1 | 2 | estimate | 48.7% flour, 4.6% potato flakes, no fat; 12% baking loss | [page](https://web.archive.org/web/20240224035419/https://www.lidl.bg/p/fermerski-klyb/p10013721) |
| Хляб Сомун |  | 244 | 7.6 | 51 | 0.6 | 0.1 | 2.2 | estimate | flour, yeast, salt, corn starch; lean white bread | [page](https://web.archive.org/web/20240301004737/https://www.lidl.bg/p/klyb-somun/p10013727) |
| Селско хлебче | 80 | 266 | 7.8 | 52 | 2.3 | 0.2 | 2.8 | estimate | 63% wheat flour, 10% potato, 5% rye flour, some oil | [page](https://web.archive.org/web/20240301000039/https://www.lidl.bg/p/selsko-klebce/p10013724) |
| Фокача с маслиново масло и маслини | 150 | 296 | 6.5 | 44 | 10 | 1.5 | 2 | estimate | 10.7% olives, 7.3% olive oil; checked against the 2021 Средиземноморска фокача label (307 kcal) | [page](https://web.archive.org/web/20240224024902/https://www.lidl.bg/p/fokaca-s-maslinovo-maslo-i-maslini/p10013747) |
| Мини пърленка | 50 | 245 | 8.2 | 50 | 0.8 | 0.1 | 2.3 | estimate | flour, water, salt, yeast, gluten; no fat | [page](https://web.archive.org/web/20240224021244/https://www.lidl.bg/p/mini-p'rlenka/p10013725) |
| Пърленка | 160 | 245 | 8.2 | 50 | 0.8 | 0.1 | 2.3 | estimate | flour, water, salt, yeast, gluten; no fat | [page](https://web.archive.org/web/20240224023151/https://www.lidl.bg/p/p'rlenka/p10013726) |
| Мини багета Рустик | 110 | 260 | 8.5 | 53 | 1.6 | 0.5 |  | estimate | same ingredient list as Бял кайзер земел; its 2022 label | [page](https://web.archive.org/web/20240224041753/https://www.lidl.bg/p/mini-bageta-rustik/p10013723) |
| Багета по френска рецепта |  | 260 | 8.5 | 53 | 1.6 | 0.5 |  | estimate | same ingredient list as Бял кайзер земел; its 2022 label | [page](https://web.archive.org/web/20240224025902/https://www.lidl.bg/p/bageta-po-frenska-retepta/p10013728) |
| Геврек със сирене и сусам | 120 | 246 | 7.4 | 34.5 | 8.3 | 3.7 | 1.3 | estimate | 67% margarine dough, 33% curd and cheese filling | [page](https://web.archive.org/web/20240224030027/https://www.lidl.bg/p/gevrek-s's-sirene-i-susam/p10013748) |
| Закуска с гауда | 90 | 297 | 10 | 31 | 14.5 | 7.5 | 1.3 | estimate | flour-first dough with 17% gouda and vegetable fat | [page](https://web.archive.org/web/20240224040846/https://www.lidl.bg/p/zakuska-s-gauda/p10013752) |
| Закуска със сирене и лютеница | 90 | 233 | 7.6 | 23.3 | 11.9 | 6.1 | 1.3 | estimate | same 90 g range as Закуска пица; its dough with about 10% cheese and 10% lyutenitsa | [promo](https://www.veliko.bg/promo/lidl-zakuska-sies-sirene-i-liutenica) |
| Бутерка с шоколад | 70 | 390 | 6.9 | 46 | 19.3 | 11.7 | 2.3 | estimate | 16% butter, 10% dark chocolate; same model gives 424 kcal for the croissant label of 419 | [page](https://web.archive.org/web/20240224040705/https://www.lidl.bg/p/buterka-s-sokolad/p10013732) |
| Геврек със сусам | 120 | 310 | 9.7 | 46.6 | 8.7 | 1.3 | 3.8 | estimate | white roll dough with 12.3% sesame | [page](https://web.archive.org/web/20240224024733/https://www.lidl.bg/p/gevrek-s's-susam/p10013845) |
| Баница със сирене | 130 | 305 | 7.5 | 33 | 14.2 | 4.7 | 1.3 | estimate | same dough as the spinach banitsa; its 2024 label | [page](https://web.archive.org/web/20240224034657/https://www.lidl.bg/p/banita-s's-sirene/p10013750) |
| Мини кроасан с масло | 20 | 357 | 6.6 | 41.7 | 17.8 | 10.8 | 1.6 | estimate | 18% butter, egg and sugar; croissant model | [page](https://web.archive.org/web/20240301003325/https://www.lidl.bg/p/mini-kroasan-s-maslo/p10013746) |
