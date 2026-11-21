namespace Plate.Core.Nutrition;

/// <param name="Expected">Items the user saved whose food is known.</param>
/// <param name="FoodAgreed">Saved items the new run matched to the same database food (or, for AI estimates, the same name).</param>
/// <param name="GramsAgreed">Of those, items whose grams are within the correction tolerance.</param>
/// <param name="Extra">Items the new run found that the saved items don't account for.</param>
/// <param name="LoggedLater">
/// New items the saved rows don't account for but an entry logged right after the confirm does: the user unticked a
/// wrong row and logged the food another way. Not extra, and their entries count in the saved totals.
/// </param>
/// <param name="Unknown">
/// Items saved without a known food: the record predates saved foods being kept, or the user kept a food they rated as
/// wrong. They are not expected, and each may account for one unpaired new item instead of it counting as extra.
/// </param>
public record AgreementResult(int Expected, int FoodAgreed, int GramsAgreed, int Extra, int Unknown = 0, int LoggedLater = 0)
{
    public static AgreementResult operator +(AgreementResult a, AgreementResult b) =>
        new(a.Expected + b.Expected, a.FoodAgreed + b.FoodAgreed, a.GramsAgreed + b.GramsAgreed, a.Extra + b.Extra, a.Unknown + b.Unknown, a.LoggedLater + b.LoggedLater);

    public static readonly AgreementResult Zero = new(0, 0, 0, 0);
}

/// <summary>What the saved log says about one parsed item: the reference a replay is scored against.</summary>
/// <param name="Included">Saved, rather than unticked.</param>
/// <param name="FoodKnown">The record says which food was saved (a database food, or an AI estimate by name).</param>
/// <param name="FoodItemId">The saved database food; null for an AI estimate.</param>
/// <param name="Description">The saved row's name, which identifies an AI estimate.</param>
/// <param name="Per100">The saved per-100 g values; null when the record doesn't have them.</param>
/// <param name="RejectedFoodItemId">The suggested food, when the user rated it as the wrong food.</param>
/// <param name="FoodName">The saved food's English name, so another database row of the same food also agrees.</param>
public record ReplayExpectation(bool Included, bool FoodKnown, int? FoodItemId, string Description, decimal Grams, Per100g? Per100, int? RejectedFoodItemId = null,
    string? FoodName = null);

/// <summary>An entry the user logged right after confirming the parse, outside it (SPEC 11, feedback eval).</summary>
public record LaterEntry(int? FoodItemId, string? FoodName, string Description, decimal Grams, Per100g Per100);

/// <summary>Energy and macros of a whole meal.</summary>
public record MealNutrients(decimal Kcal, decimal Protein, decimal Carbs, decimal Fat)
{
    public static MealNutrients Of(IEnumerable<(decimal Grams, Per100g Per100)> items)
    {
        decimal kcal = 0, protein = 0, carbs = 0, fat = 0;
        foreach (var (grams, per100) in items)
        {
            kcal += grams * per100.Kcal / 100;
            protein += grams * per100.Protein / 100;
            carbs += grams * per100.Carbs / 100;
            fat += grams * per100.Fat / 100;
        }

        return new MealNutrients(Math.Round(kcal, 1), Math.Round(protein, 1), Math.Round(carbs, 1), Math.Round(fat, 1));
    }
}

/// <param name="Saved">The saved meal's totals; null when any saved item lacks its per-100 g values.</param>
/// <param name="Fresh">The new run's totals over every item it found.</param>
/// <param name="RepeatedRejections">Items whose rejected food the new run picked again.</param>
public record ReplayScore(AgreementResult Agreement, int RepeatedRejections, MealNutrients? Saved, MealNutrients Fresh);

/// <summary>Per-meal differences between replayed and saved nutrients, over the meals where both are known.</summary>
public record NutrientAgreement(int Meals, decimal MeanAbsKcal, decimal MedianAbsKcal, decimal MeanSignedKcal, decimal MeanAbsProtein, decimal MeanAbsCarbs, decimal MeanAbsFat)
{
    public static NutrientAgreement Summarize(IEnumerable<(MealNutrients? Saved, MealNutrients Fresh)> meals)
    {
        var known = meals.Where(m => m.Saved is not null).Select(m => (Saved: m.Saved!, m.Fresh)).ToList();
        if (known.Count == 0)
        {
            return new NutrientAgreement(0, 0, 0, 0, 0, 0, 0);
        }

        var abs = known.Select(m => Math.Abs(m.Fresh.Kcal - m.Saved.Kcal)).OrderBy(x => x).ToList();
        var median = abs.Count % 2 == 1 ? abs[abs.Count / 2] : (abs[abs.Count / 2 - 1] + abs[abs.Count / 2]) / 2;
        return new NutrientAgreement(known.Count,
            Mean(known.Select(m => Math.Abs(m.Fresh.Kcal - m.Saved.Kcal))),
            Math.Round(median, 1),
            Mean(known.Select(m => m.Fresh.Kcal - m.Saved.Kcal)),
            Mean(known.Select(m => Math.Abs(m.Fresh.Protein - m.Saved.Protein))),
            Mean(known.Select(m => Math.Abs(m.Fresh.Carbs - m.Saved.Carbs))),
            Mean(known.Select(m => Math.Abs(m.Fresh.Fat - m.Saved.Fat))));
    }

    private static decimal Mean(IEnumerable<decimal> values) => Math.Round(values.Average(), 1);
}

/// <summary>
/// How far a fresh parse and match of an old entry agrees with the saved log (SPEC 11, feedback eval): the foods and
/// grams the user saved, not the ones first suggested. Agreement with the saved log is not accuracy, since a user can
/// accept a bad suggestion. Items are paired by food, not by position, since a new run may order or split them differently.
/// A food is the same database food, another row of it (<see cref="FoodEquivalence"/>), or for an AI estimate the same
/// name. Entries logged right after the confirm only back up new items nothing saved accounts for; they are never
/// expected, since the user may have logged something else entirely.
/// </summary>
public static class ParseAgreement
{
    public static AgreementResult Score(IReadOnlyList<SavedItem> saved, IReadOnlyList<SuggestedItem> fresh) =>
        Replay(saved.Select(s => new ReplayExpectation(s.Included, true, s.FoodItemId, s.Description, s.Grams, s.Per100)).ToList(), fresh).Agreement;

    public static ReplayScore Replay(IReadOnlyList<ReplayExpectation> expected, IReadOnlyList<SuggestedItem> fresh, IReadOnlyList<LaterEntry>? later = null)
    {
        var used = new bool[fresh.Count];
        int count = 0, food = 0, grams = 0, unknown = 0;
        foreach (var e in expected.Where(e => e.Included))
        {
            // Keeping a food after rating it wrong says nothing about which food is right.
            if (!e.FoodKnown || (e.FoodItemId is { } id && id == e.RejectedFoodItemId))
            {
                unknown++;
                continue;
            }

            count++;
            var match = -1;
            for (var n = 0; n < fresh.Count && match < 0; n++)
            {
                if (!used[n] && SameFood(e.FoodItemId, e.FoodName, e.Description, e.Per100, fresh[n]))
                {
                    match = n;
                }
            }

            if (match < 0)
            {
                continue;
            }

            used[match] = true;
            food++;
            var f = fresh[match];
            if (ParseCorrections.PerItem([f], [new SavedItem(true, f.Description, e.Grams, f.Per100, f.FoodItemId)]).Single() == ParseCorrection.None)
            {
                grams++;
            }
        }

        // A new item nothing saved accounts for may be food the user logged another way right after.
        var backing = new List<LaterEntry>();
        var spare = (later ?? []).ToList();
        for (var n = 0; n < fresh.Count; n++)
        {
            if (!used[n] && spare.FirstOrDefault(l => SameFood(l.FoodItemId, l.FoodName, l.Description, l.Per100, fresh[n])) is { } entry)
            {
                used[n] = true;
                spare.Remove(entry);
                backing.Add(entry);
            }
        }

        var unpaired = used.Count(u => !u);
        var repeated = expected.Count(e => e.RejectedFoodItemId is { } bad && fresh.Any(f => f.FoodItemId == bad));
        var included = expected.Where(e => e.Included).ToList();
        var savedTotals = included.All(e => e.Per100 is not null)
            ? MealNutrients.Of(included.Select(e => (e.Grams, e.Per100!)).Concat(backing.Select(b => (b.Grams, b.Per100))))
            : null;
        return new ReplayScore(new AgreementResult(count, food, grams, unpaired - Math.Min(unpaired, unknown), unknown, backing.Count), repeated,
            savedTotals, MealNutrients.Of(fresh.Select(f => (f.Grams, f.Per100))));
    }

    private static bool SameFood(int? id, string? name, string description, Per100g? per100, SuggestedItem fresh) =>
        id is { } food
            ? fresh.FoodItemId == food || (fresh.FoodItemId is not null && FoodEquivalence.Same(name, per100, fresh.FoodName, fresh.Per100))
            : fresh.FoodItemId is null && Normalize(description) == Normalize(fresh.Description);

    private static string Normalize(string text) =>
        string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToLowerInvariant();
}
