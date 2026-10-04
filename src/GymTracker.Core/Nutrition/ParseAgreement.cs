namespace GymTracker.Core.Nutrition;

/// <param name="Expected">Items the user saved.</param>
/// <param name="FoodAgreed">Saved items the new run matched to the same database food (or, for AI estimates, the same name).</param>
/// <param name="GramsAgreed">Of those, items whose grams are within the correction tolerance.</param>
/// <param name="Extra">Items the new run found that the user didn't save.</param>
public record AgreementResult(int Expected, int FoodAgreed, int GramsAgreed, int Extra)
{
    public static AgreementResult operator +(AgreementResult a, AgreementResult b) =>
        new(a.Expected + b.Expected, a.FoodAgreed + b.FoodAgreed, a.GramsAgreed + b.GramsAgreed, a.Extra + b.Extra);

    public static readonly AgreementResult Zero = new(0, 0, 0, 0);
}

/// <summary>
/// How far a fresh parse and match of an old entry agrees with what the user saved then (SPEC 11, feedback eval).
/// Items are paired by food, not by position, since a new run may order or split them differently.
/// </summary>
public static class ParseAgreement
{
    public static AgreementResult Score(IReadOnlyList<SavedItem> saved, IReadOnlyList<SuggestedItem> fresh)
    {
        var used = new bool[fresh.Count];
        var expected = 0;
        var food = 0;
        var grams = 0;
        foreach (var s in saved.Where(s => s.Included))
        {
            expected++;
            var match = -1;
            for (var n = 0; n < fresh.Count && match < 0; n++)
            {
                if (!used[n] && SameFood(s, fresh[n]))
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
            var correction = ParseCorrections.PerItem([fresh[match]], [s with { Description = fresh[match].Description, Per100 = fresh[match].Per100, FoodItemId = fresh[match].FoodItemId }]).Single();
            if (correction == ParseCorrection.None)
            {
                grams++;
            }
        }

        return new AgreementResult(expected, food, grams, used.Count(u => !u));
    }

    private static bool SameFood(SavedItem saved, SuggestedItem fresh) =>
        saved.FoodItemId is { } id
            ? fresh.FoodItemId == id
            : fresh.FoodItemId is null && Normalize(saved.Description) == Normalize(fresh.Description);

    private static string Normalize(string text) =>
        string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToLowerInvariant();
}
