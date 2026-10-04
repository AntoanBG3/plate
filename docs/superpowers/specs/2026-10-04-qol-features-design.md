# Quality-of-life features: design

Date: 2026-10-04. Status: approved in conversation, pending spec review.

Seven small features, built and committed one at a time in this order: duplicate template, exercise notes and bar weight, plate calculator, swap exercise, quick add, saved meals, undo. Each follows the project rules: calculations in `GymTracker.Core` as pure functions with unit tests first, every query scoped to the current user, async EF with `CancellationToken`, Bulgarian strings for all new UI, layouts checked at 390 px with 44 px touch targets. SPEC.md sections 4 and 7 are updated with each feature.

## 1. Duplicate template

- "Duplicate" item in the template row's "..." menu on Train → Templates.
- `TemplateService.DuplicateAsync(templateId)` copies the name as "{0} (copy)" (localized; truncated to the 100-character limit) and every `TemplateExercise` with its sort order, sets, rep range, increment and rest. The copy goes to the end of the list (`SortOrder = max + 1`).
- Redirects to the copy's edit page.
- Tests: copy belongs to the current user, exercises and settings match, original unchanged, another user's template id gives 404.

## 2. Exercise notes and bar weight

- New table `UserExerciseSettings`: `UserId`, `ExerciseId` (composite key), `Note` (string, max 200, null when empty), `BarWeightKg` (decimal, null = default). Owner query filter like the other user tables. Exercises are shared seed rows, so these settings cannot live on `Exercise`.
- New profile fields: `BarWeightKg` (default 20) and `PlatesKg` (list of plate weights; default 25, 20, 15, 10, 5, 2.5, 1.25). Settings shows bar weight as a number field and plates as checkboxes over 25, 20, 15, 10, 5, 2.5, 2, 1.25, 1, 0.5.
- "Notes & bar" item in the exercise menu on the session card and on the template edit page opens a sheet: note textarea and an optional bar weight field (placeholder shows the default). Saving an empty note and empty bar weight deletes the row.
- The note shows in small secondary text under the exercise name on the session card and the template edit page.
- Effective bar weight (Core): the exercise's `BarWeightKg` if set, else the profile's bar weight when the exercise's equipment is Barbell, else none. A plate-loaded machine gets the calculator by setting its bar weight (0 allowed).
- Tests: settings are per user (two users, same exercise, different notes), note trimming and length limit, effective bar weight rules.

## 3. Plate calculator

- Core: `PlateMath.PerSide(targetKg, barKg, platesKg)` returns the plates for one side (heaviest first) and the loaded total. Plates are available in any number of pairs. It finds the heaviest total at or below the target, using the fewest plates among equal totals; this is an exact search over 0.05 kg units, not greedy, so plates {15, 10} make 20 per side as 10 + 10.
- Edge cases with tests: target below the bar (no plates, total = bar, flagged), target equal to the bar, target not reachable exactly (nearest lower total, flagged), empty plate list, plates that don't divide evenly (1.25 with 2.5), large targets (300 kg).
- UI: on the session card, exercises with an effective bar weight get a small plates button beside each set's weight input. It opens a sheet loaded with htmx from `GET /train/plates?exerciseId=&weight=`, listing the plates per side and the total, with "Closest you can load: X kg" when not exact. The weight comes from the input's current value.

## 4. Swap an exercise in a session

- "Swap exercise" item in the session card menu, shown only while that session exercise has no sets logged (warmups included). The server enforces the same rule and refuses otherwise.
- Opens the existing exercise picker (`ExercisePicker` view) in swap mode, titled "Swap {0}". Ordering is a Core function: exercises with the same primary muscle first, then the rest, each group by name; the current exercise is excluded; archived exercises are hidden as today.
- Picking replaces `SessionExercise.ExerciseId` only. Sets, rep range, increment and rest of the slot stay; the template is not changed. The card's progression suggestion is computed for the new exercise from its own history.
- Tests: ordering function; swap refused once a set exists; swap of another user's session exercise gives 404; the template is untouched.

## 5. Quick add

- New `EntrySource.Quick`. A quick entry has no `FoodItem`, `Grams = 0`, a description (default "Quick add", localized), `Kcal` required (1 to 5000) and protein, carbs and fat optional (0 to 500, default 0).
- Each meal on the Food day gets a "Quick add" row under "Add to …". It opens a sheet with name, kcal, protein, carbs, fat and an Add button; saving reloads the day.
- Food day rows for quick entries show "{kcal} kcal · {protein} g protein" with no grams. The entry page for a quick entry edits kcal and the three macros directly instead of the amount.
- Quick entries are left out of the Recent list. "Copy yesterday's …" copies them as they are.
- Tests: validation limits, totals include quick entries, Recent excludes them, entry edit for a quick entry changes macros and never divides by grams.

## 6. Saved meals

- New tables `SavedMeal` (`Id`, `UserId`, `Name` max 60, `CreatedAt`) and `SavedMealItem` (`Id`, `SavedMealId`, `SortOrder`, `Description`, `Grams`, `Kcal`, `ProteinG`, `CarbsG`, `FatG`, `FoodItemId` nullable, `Source`). Items are snapshots of the logged entries, with stored totals like `FoodLogEntry`.
- Create: each meal section on the Food day gets a "..." menu (only when the meal has entries) with "Save as a meal". It opens a sheet asking for a name, prefilled with the meal's name; saving copies that meal's entries.
- Log: the Recent tab lists saved meals above recent foods. Tapping one opens a sheet (htmx) listing its items with editable grams (quick items show their kcal and no grams field) and an "Add to {meal}" button. Logging creates one entry per item, scaled by Core `FoodMath` from the snapshot's totals to the new grams.
- Manage: the same sheet has Rename and Delete (Delete asks to confirm; saved meals are not in the undo set).
- Tests: scaling (including grams 0 for quick items), save copies the right meal and day only, logging creates entries for the chosen date and meal, ownership on every saved-meal endpoint.

## 7. Undo instead of confirm

Approach: soft delete with EF Core named query filters.

- `DeletedAt` (`DateTimeOffset?`) on `FoodLogEntry`, `SetEntry`, `SessionExercise` and `TemplateExercise`.
- Each of these entities gets two named query filters: the existing owner filter and a `SoftDelete` filter (`DeletedAt == null`). `SetEntry`'s soft-delete filter also requires its `SessionExercise.DeletedAt == null`, so the sets of a removed exercise disappear from progression history, volume and export while it is deleted.
- Deleting sets `DeletedAt = now`. Undo is `POST /undo/{kind}/{id}` with kind `entry`, `set`, `session-exercise` or `template-exercise`; it loads the row with `IgnoreQueryFilters(["SoftDelete"])` so the owner filter still applies, accepts it only within 10 minutes of the delete, and clears `DeletedAt`. Restoring a session or template exercise renumbers that list's `SortOrder` by (SortOrder, Id) so positions stay unique.
- A hosted service runs hourly and permanently deletes rows whose `DeletedAt` is more than 24 hours old (`IgnoreQueryFilters()` then `ExecuteDeleteAsync`).
- UI: the four deletes lose their `data-confirm`. A toast shows "{thing} deleted" with an Undo button for 6 seconds.
  - Plain-form deletes (food entry, session exercise, template exercise) redirect as now, and carry the undo URL across the redirect in TempData next to the notice; the layout renders the toast with an Undo form.
  - The htmx set delete returns an `HX-Trigger` event with the text and undo URL; `gymToast` gains an optional action.
  - Undo posts with the antiforgery token and then reloads the current page through soft navigation.
- Confirms stay for: archive template, finish session, delete session, remove passkey, sign out everywhere, delete saved meal.
- Tests: deleted rows vanish from day totals, progression input and export; undo restores them; undo after 10 minutes is refused; undo of another user's row gives 404; purge removes only rows older than 24 hours; restoring renumbers sort order.

## Out of scope

AI features (label photo, plate photo, weekly review) are separate follow-ups. Saved-meal item editing beyond grams at log time, plate inventories with limited counts, and per-template notes are not included.
