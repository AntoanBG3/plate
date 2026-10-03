using GymTracker.Core.Text;

namespace GymTracker.Core.Nutrition;

public record Per100g(decimal Kcal, decimal Protein, decimal Carbs, decimal Fat);

public record Totals(decimal Kcal, decimal ProteinG, decimal CarbsG, decimal FatG)
{
    public static readonly Totals Zero = new(0, 0, 0, 0);

    public Totals Add(Totals other) =>
        new(Kcal + other.Kcal, ProteinG + other.ProteinG, CarbsG + other.CarbsG, FatG + other.FatG);
}

public static class FoodMath
{
    public const decimal MaxGrams = 3000m;

    /// <summary>Totals for a portion, rounded to 0.1.</summary>
    public static Totals For(Per100g per100, decimal grams)
    {
        var f = grams / 100m;
        return new Totals(R(per100.Kcal * f), R(per100.Protein * f), R(per100.Carbs * f), R(per100.Fat * f));
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

        if (per100.Kcal < 0 || per100.Protein < 0 || per100.Carbs < 0 || per100.Fat < 0)
        {
            return "Nutrition values can't be negative.";
        }

        if (per100.Kcal > 900 || per100.Protein + per100.Carbs + per100.Fat > 100.5m)
        {
            return "Values per 100 g are impossible (over 900 kcal or over 100 g of macros).";
        }

        return null;
    }

    private static decimal R(decimal value) => Math.Round(value, 1, MidpointRounding.AwayFromZero);
}
