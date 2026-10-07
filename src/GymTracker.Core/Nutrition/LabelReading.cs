using GymTracker.Core.Text;

namespace GymTracker.Core.Nutrition;

public enum LabelBasis
{
    Per100g,
    Per100ml,
    PerServing,
}

/// <summary>What the label model read from a photo, as printed in the column it chose (SPEC 6.6). Nothing is computed.</summary>
public record LabelReading(
    bool IsLabel,
    string? Name,
    string? Brand,
    LabelBasis Basis,
    decimal? ServingGrams,
    decimal? EnergyKcal,
    decimal? EnergyKj,
    decimal? Protein,
    decimal? Carbs,
    decimal? Fat,
    decimal? SaturatedFat,
    decimal? Fiber);

/// <param name="Raw">The model's answer as returned, stored on the scan.</param>
public record LabelReadResult(LabelReading? Reading, string? Raw, LocalText? Error, string? Model)
{
    public static LabelReadResult Failed(LocalText error, string? model = null, string? raw = null) => new(null, raw, error, model);
}

public interface ILabelReader
{
    Task<bool> IsAvailableAsync(CancellationToken ct);

    Task<LabelReadResult> ReadAsync(byte[] image, string contentType, CancellationToken ct);
}

[Flags]
public enum LabelField
{
    None = 0,
    Kcal = 1,
    Protein = 2,
    Carbs = 4,
    Fat = 8,
    SaturatedFat = 16,
    Fiber = 32,
}

public record LabelOutcome(LabelValues? Values, LocalText? Error);

/// <summary>Per-100 g values for the manual food form, made from a label reading (SPEC 6.6). Null means leave the field empty.</summary>
/// <param name="Converted">Fields computed from kJ or from a per-serving column, highlighted for checking.</param>
public record LabelValues(
    string? Name,
    string? Brand,
    decimal? Kcal,
    decimal? Protein,
    decimal? Carbs,
    decimal? Fat,
    decimal? SaturatedFat,
    decimal? Fiber,
    decimal? ServingGrams,
    LabelField Converted,
    IReadOnlyList<LocalText> Notes,
    IReadOnlyList<LocalText> Warnings)
{
    public static readonly LocalText NotFound = "Couldn't find a nutrition table in that photo.";
    public static readonly LocalText PerServingOnly = "The label only gives values per serving. Enter them by hand or photograph the per-100 g column.";
    public static readonly LocalText EnergyDisagrees = "The label's kJ and kcal disagree; the kcal value was used.";
    public static readonly LocalText MillilitresAsGrams = "Per 100 ml, used as per 100 g.";
    public static readonly LocalText LeftEmpty = "Some values were outside what a food can have and were left empty.";
    public static readonly LocalText MacrosOver100 = "Protein, carbs and fat add up to more than 100 g. Check them.";
    public static readonly LocalText SaturatedAboveFat = "Saturated fat is more than fat. Check both.";
    public static readonly LocalText EnergyMismatch = "The calories don't match the protein, carbs and fat. Check the energy row.";

    private const decimal KjPerKcal = 4.184m;

    public static LabelOutcome Normalize(LabelReading r)
    {
        if (!r.IsLabel || r is { EnergyKcal: null, EnergyKj: null, Protein: null, Carbs: null, Fat: null })
        {
            return new LabelOutcome(null, NotFound);
        }

        var notes = new List<LocalText>();
        var warnings = new List<LocalText>();
        var converted = LabelField.None;

        var kcal = r.EnergyKcal;
        if (kcal is null && r.EnergyKj is { } kjOnly)
        {
            kcal = kjOnly / KjPerKcal;
            converted |= LabelField.Kcal;
        }
        else if (kcal is > 0 && r.EnergyKj is { } kj && Math.Abs(kj / KjPerKcal - kcal.Value) > 0.05m * kcal.Value)
        {
            notes.Add(EnergyDisagrees);
        }

        var factor = 1m;
        if (r.Basis == LabelBasis.PerServing)
        {
            if (r.ServingGrams is not > 0)
            {
                return new LabelOutcome(null, PerServingOnly);
            }

            factor = 100m / r.ServingGrams.Value;
            converted |= Flag(kcal, LabelField.Kcal) | Flag(r.Protein, LabelField.Protein) | Flag(r.Carbs, LabelField.Carbs)
                | Flag(r.Fat, LabelField.Fat) | Flag(r.SaturatedFat, LabelField.SaturatedFat) | Flag(r.Fiber, LabelField.Fiber);
        }
        else if (r.Basis == LabelBasis.Per100ml)
        {
            notes.Add(MillilitresAsGrams);
        }

        var dropped = false;
        decimal? Keep(decimal? value, decimal max, int decimals)
        {
            if (value is not { } v)
            {
                return null;
            }

            var scaled = Math.Round(v * factor, decimals, MidpointRounding.AwayFromZero);
            if (scaled < 0 || scaled > max)
            {
                dropped = true;
                return null;
            }

            return scaled;
        }

        var outKcal = Keep(kcal, 900, 0);
        var protein = Keep(r.Protein, 100, 1);
        var carbs = Keep(r.Carbs, 100, 1);
        var fat = Keep(r.Fat, 100, 1);
        var saturated = Keep(r.SaturatedFat, 100, 1);
        var fiber = Keep(r.Fiber, 100, 1);
        if (dropped)
        {
            notes.Add(LeftEmpty);
        }

        if ((protein ?? 0) + (carbs ?? 0) + (fat ?? 0) > 100)
        {
            warnings.Add(MacrosOver100);
        }

        if (saturated > fat)
        {
            warnings.Add(SaturatedAboveFat);
        }

        if (outKcal is { } k && protein is { } p && carbs is { } c && fat is { } f && FoodMath.MacrosDisagree(new Per100g(k, p, c, f)))
        {
            warnings.Add(EnergyMismatch);
        }

        var serving = r.ServingGrams is > 0 and <= FoodMath.MaxGrams ? Math.Round(r.ServingGrams.Value, 1, MidpointRounding.AwayFromZero) : (decimal?)null;
        return new LabelOutcome(
            new LabelValues(Clean(r.Name), Clean(r.Brand), outKcal, protein, carbs, fat, saturated, fiber, serving, converted, notes, warnings),
            null);
    }

    /// <summary>Whether what was saved differs from what was suggested beyond rounding (0.5 kcal, 0.05 g).</summary>
    public static bool Edited(LabelValues suggested, Per100g saved, decimal? savedServing) =>
        Differs(suggested.Kcal, saved.Kcal, 0.5m)
        || Differs(suggested.Protein, saved.Protein, 0.05m)
        || Differs(suggested.Carbs, saved.Carbs, 0.05m)
        || Differs(suggested.Fat, saved.Fat, 0.05m)
        || Differs(suggested.SaturatedFat, saved.SaturatedFat, 0.05m)
        || Differs(suggested.Fiber, saved.Fiber, 0.05m)
        || Differs(suggested.ServingGrams, savedServing, 0.05m);

    private static bool Differs(decimal? a, decimal? b, decimal tolerance) =>
        a is null != b is null || a is { } x && b is { } y && Math.Abs(x - y) > tolerance;

    private static LabelField Flag(decimal? value, LabelField field) => value is null ? LabelField.None : field;

    private static string? Clean(string? s)
    {
        var t = s?.Trim();
        return string.IsNullOrEmpty(t) ? null : t.Length > 200 ? t[..200] : t;
    }
}
