using GymTracker.Core.Text;

namespace GymTracker.Core.Nutrition;

/// <param name="Fiber">Null when the food has no value; unknown is not zero.</param>
/// <param name="SaturatedFat">Null when the food has no value; unknown is not zero.</param>
public record Per100g(decimal Kcal, decimal Protein, decimal Carbs, decimal Fat, decimal? Fiber = null, decimal? SaturatedFat = null);

/// <param name="FiberG">The sum of the known values; null when none is known (SPEC 5.8 coverage says how much is).</param>
/// <param name="SaturatedFatG">The sum of the known values; null when none is known.</param>
public record Totals(decimal Kcal, decimal ProteinG, decimal CarbsG, decimal FatG, decimal? FiberG = null, decimal? SaturatedFatG = null)
{
    public static readonly Totals Zero = new(0, 0, 0, 0);

    public Totals Add(Totals other) =>
        new(Kcal + other.Kcal, ProteinG + other.ProteinG, CarbsG + other.CarbsG, FatG + other.FatG,
            SumKnown(FiberG, other.FiberG), SumKnown(SaturatedFatG, other.SaturatedFatG));

    private static decimal? SumKnown(decimal? a, decimal? b) => a is null && b is null ? null : (a ?? 0) + (b ?? 0);
}

public static class FoodMath
{
    public const decimal MaxGrams = 3000m;

    /// <summary>Totals for a portion, rounded to 0.1.</summary>
    public static Totals For(Per100g per100, decimal grams)
    {
        var f = grams / 100m;
        return new Totals(R(per100.Kcal * f), R(per100.Protein * f), R(per100.Carbs * f), R(per100.Fat * f), R(per100.Fiber * f), R(per100.SaturatedFat * f));
    }

    /// <summary>
    /// True when the stated calories disagree with 4/4/9 kcal per gram of protein/carbs/fat by more
    /// than 15% of the stated calories (SPEC 6.1). Fibre and alcohol cause small honest gaps.
    /// </summary>
    public static bool MacrosDisagree(Per100g per100)
    {
        var fromMacros = 4 * per100.Protein + 4 * per100.Carbs + 9 * per100.Fat;
        if (per100.Kcal <= 0)
        {
            return fromMacros > 0;
        }

        return Math.Abs(per100.Kcal - fromMacros) > 0.15m * per100.Kcal;
    }

    /// <summary>Returns an error message, or null when the values are acceptable.</summary>
    public static LocalText? Validate(decimal grams, Per100g per100)
    {
        if (grams <= 0 || grams > MaxGrams)
        {
            return new LocalText("Grams must be above 0 and at most {0}.", MaxGrams.ToString("0", System.Globalization.CultureInfo.InvariantCulture));
        }

        if (per100.Kcal < 0 || per100.Protein < 0 || per100.Carbs < 0 || per100.Fat < 0 || per100.Fiber < 0 || per100.SaturatedFat < 0)
        {
            return "Nutrition values can't be negative.";
        }

        if (per100.Kcal > 900 || per100.Protein + per100.Carbs + per100.Fat > 100.5m)
        {
            return "Values per 100 g are impossible (over 900 kcal or over 100 g of macros).";
        }

        // Fibre sits inside carbs on some labels and outside on others, so it is checked on its own.
        if (per100.Fiber > 100 || per100.SaturatedFat > per100.Fat + 0.5m)
        {
            return "Fiber can't be over 100 g, and saturated fat can't be more than fat.";
        }

        return null;
    }

    /// <summary>Checks a quick entry (calories typed directly). Returns an error, or null when acceptable.</summary>
    public static LocalText? ValidateQuick(decimal kcal, decimal protein, decimal carbs, decimal fat, decimal? fiber = null, decimal? saturatedFat = null)
    {
        if (kcal is < 1 or > 5000)
        {
            return "Calories must be between 1 and 5000.";
        }

        if (protein is < 0 or > 500 || carbs is < 0 or > 500 || fat is < 0 or > 500)
        {
            return "Protein, carbs and fat must be between 0 and 500 g.";
        }

        if (fiber is < 0 or > 200 || saturatedFat < 0 || saturatedFat > fat + 0.5m)
        {
            return "Fiber must be between 0 and 200 g, and saturated fat between 0 and the fat.";
        }

        return null;
    }

    /// <summary>Totals stored for one amount, rescaled to another. Items without grams (quick entries) are unchanged.</summary>
    public static Totals Scale(Totals totals, decimal fromGrams, decimal toGrams)
    {
        if (fromGrams <= 0)
        {
            return totals;
        }

        var f = toGrams / fromGrams;
        return new Totals(R(totals.Kcal * f), R(totals.ProteinG * f), R(totals.CarbsG * f), R(totals.FatG * f), R(totals.FiberG * f), R(totals.SaturatedFatG * f));
    }

    /// <summary>The per-100 g values of a stored food.</summary>
    public static Per100g Per100(this Domain.FoodItem f) =>
        new(f.KcalPer100g, f.ProteinPer100g, f.CarbsPer100g, f.FatPer100g, f.FiberPer100g, f.SaturatedFatPer100g);

    /// <summary>The stored totals of a logged entry.</summary>
    public static Totals TotalsOf(Domain.FoodLogEntry e) => new(e.Kcal, e.ProteinG, e.CarbsG, e.FatG, e.FiberG, e.SaturatedFatG);

    private static decimal R(decimal value) => Math.Round(value, 1, MidpointRounding.AwayFromZero);

    private static decimal? R(decimal? value) => value is { } v ? R(v) : null;
}
