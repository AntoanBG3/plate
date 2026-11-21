# Portions, recipes and human comparison

## What can Plate infer from a description or photo?

### Takeaway

Food identity, nutritional density and quantity eaten are separate unknowns. Use a photo to suggest foods, then seek a measured weight, labelled unit or the user's own usual portion when quantity materially affects the meal.

### Cited Findings

- In a study of 52 standardized Swedish food photos (16 individual components and 36 complete meals), with plates and cutlery visible, GPT-4o and another commercial model both had a 35.8% mean absolute percentage error for energy against weighed foods and database nutrient calculations. Larger portions tended to be underestimated. This describes those specific models and photos. [Fridolfsson et al., 2025](https://pubmed.ncbi.nlm.nih.gov/41081011/)
- A 200-image university canteen study tested whether visual portions beat a per-food portion prior. For the same items each model correctly identified and quantified, the leave-one-image-out typical-serving predictor achieved 33–39 g mean absolute error across the model-specific item sets, compared with 35–54 g for the models. The predictor knew food identity and used this canteen's portion distribution. The result supports testing a Plate prior locally and offers no estimate of its performance on Bulgarian home cooking. [Canteen benchmark, sections 2.2.4 and 3](https://www.mdpi.com/2072-6643/18/21/3582)
- A Japanese hospital evaluated ten systems and ten registered dietitians on 15 standardized meals photographed under fixed conditions. GPT-4o was within 10% of reference energy on 11 of 15 photos. Even the three selected best energy performers overestimated fat by 23.6–30.4% on average. A calorie result can hide offsetting macro errors. This small hospital sample is a separate setting from the Swedish study. [Hospital study, sections 2–3](https://www.mdpi.com/2072-6643/18/6/966)
- Across five countries including 103 people from Bulgaria, a food-picture-book study found substantially higher agreement when matching food physically present to a reference photo (ICC 0.805) than when recalling a self-served portion one hour later (0.536). Per-food correct choices varied from 18% for cheese to 96% for ratatouille across the perception study. These are pooled findings with different tasks. [Vilela et al., 2018](https://doi.org/10.1017/S1368980017004153)

### Inferences

- Pilot a user-defined portion such as “my breakfast bowl, 180 g of this yogurt” measured once. Offer it when the identified food and context recur. A bowl's measured volume cannot establish a common mass for yogurt, cereal and rice. Offer an editable estimate and retain its origin.
- For a user entering “половин кофичка кисело мляко”, lookup the actual selected pack's net weight. A verified 400 g pack gives 200 g. If pack sizes differ, ask which size only when it matters. For “два броя от същата закуска”, multiply verified single-piece weight for that specific pastry. Preserve the raw count and unit; measuring the piece is stronger evidence.
- Render food identity and amount sources independently. A verified product plus “maybe half” still has portion uncertainty. Show separate assumption text or a factual range, such as totals for one and two candidate portions. An empirical “80% interval” needs held-out coverage testing.
- Use whole-meal photography experimentally to prefill suggestions and help users remember components. Weighed reference photos of the user's actual bowl and common foods are another opt-in experiment. Collect end-to-end error and time against typed entries before prioritizing additional photo requirements.

### Gaps

- No cited study tests Plate, its current deployed model, or a representative sample of Bulgarian users logging everyday meals in this PWA. Absolute error estimates cannot be carried over. Photographing a package nutrition panel is a separate transcription task with independent validation.

## How should mixed dishes and missing ingredients be handled?

### Takeaway

The largest opportunity for homogeneous homemade dishes is to learn the actual batch and its cooked yield once. Use a short, optional question where an unknown ingredient, preparation state or leftover would meaningfully change a meal.

### Cited Findings

- FAO's recipe composition method accounts for edible ingredient weights, waste, cooking-related changes in water and fat, and nutrient retention where appropriate, then expresses the final nutrients per cooked weight or portion. [FAO, Food Composition Data, Appendix 6](https://www.fao.org/4/y4705e/y4705e23.htm)
- In a controlled feeding study of 81 adults, researchers measured buffet portions and plate waste for three meals, then randomly assigned participants to a next-day web recall or interviewer-administered recall. Participants reported 80% and 83% of consumed items, respectively. Additions and ingredients were more often missed than main foods or drinks. These percentages describe item recall in a next-day US study. [Kirkpatrick et al., 2014](https://pubmed.ncbi.nlm.nih.gov/24787491/)
- Even stated restaurant energy varies from measured energy. Across 269 menu items collected from 42 US restaurants, measured values averaged 10 kcal above posted calories, while 50 of 269 (19%) exceeded the stated value by at least 100 kcal per portion. This illustrates meal-level variability even when a menu entry is known. [Urban et al., JAMA](https://pubmed.ncbi.nlm.nih.gov/21771989/)

### Inferences

- Save a private recipe and a version for each materially different batch. Capture edible ingredient weights and states, retained oil or discarded drippings where known, and final edible cooked weight. For an evenly mixed dish, portion nutrients = retained batch nutrients × grams eaten / final batch grams. A 1,200 kcal batch weighing 800 g yields 375 kcal for a 250 g portion. Extra water changes the per-100 g value. For a tray of whole chicken legs and potatoes, allow components to be served separately. Use raw ingredient and cooked weight changes once to avoid double-adjusting for yield. Preserve historical servings when a new recipe version is recorded.
- Distinguish “for the whole pot” from “for my serving”; in Bulgarian an entry like “добавих 20 г олио в цялата тенджера, изядох една четвърт” contributes 5 g of the oil if evenly retained. Ask about dressing or oil only on context-appropriate entries, make skipping easy, never invent an addition, and avoid double-counting oil in an already-prepared food or recipe. For items weighed before eating with bone/shell, or for meals left unfinished, ask for edible grams or fraction eaten only as applicable.
- Prioritize a single question by plausible spread in kcal or protein times the likelihood the assumption is wrong and the expected reduction in error per extra tap. “Беше ли претеглен оризът суров или сварен?” is more actionable than generic confidence. Avoid repeated questions about the same saved recipe.

### Gaps

- Household defaults, food density conversions and restaurant ranges require local evidence. Protein, kcal and macro truth for a specific meal still depend on composition-table accuracy, label rounding, fat retention and uneven serving. Do not attach a statistical confidence level to a hypothetical recipe range.

## What would demonstrate improvement over a human?

### Takeaway

Stage a same-evidence blinded comparison and a separate assisted-logging comparison. Score errors against a reference independent of Plate, and record interaction burden.

### Cited Findings

- Directly weighing what was served and discarded gives a practical reference for consumed quantity. The US recall study used independently measured portions and plate waste and scored both omissions and intrusions. [Kirkpatrick et al.](https://pubmed.ncbi.nlm.nih.gov/24787491/)
- In the canteen evaluation a model could score better on total meal weight because extra detected items offset underestimates on the items it actually recognized. Item-level recognition and portion accuracy and whole-meal totals answer different questions. [Canteen benchmark, results](https://www.mdpi.com/2072-6643/18/21/3582)

### Inferences

- Collect ordinary English and Bulgarian logging inputs before revealing the scale readings. Independently record edible amounts served minus leftovers, package identity and label, batch ingredients and cooked yield; flag uncertain references. Label energy as “reference estimate from weights and composition” and never as direct lab measurement. Keep the ordinary description as entered, including missing facts.
- Have an experienced human or household member and baseline Plate each estimate from the same description/photo with measurements blinded. On repeat cases, mirror access to prior household history. Add a distinct arm for Plate with one optional question or barcode/recipe lookup. Record how often the added evidence resolves the ambiguity and what it costs in taps and seconds. Outperforming a person with less information supports the workflow, not a same-information model claim.
- Run packaging, recipes, and vague takeaway cases separately. Use kcal absolute error per meal and protein/carbs/fat absolute error in grams, signed bias, tail errors, missed items, extra items, user corrections, and time-to-log. Guard against flattering averages and tiny-denominator percentages. Group repeats and translations of the same recipe/product during train/test splitting. Report paired results with uncertainty, numerator and denominator for prompt rates and skipped flows. Score photo-only where a valid reference exists. Keep unweighable meals in a separate qualitative uncertainty/abstention test.

### Gaps

- No matched local human/model measurements were collected in this research. Plate can truthfully claim it improved over a particular baseline only after a matched local holdout supports the claim.
