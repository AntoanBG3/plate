using System.Globalization;
using System.Text.RegularExpressions;

namespace Plate.Core.Nutrition;

/// <summary>
/// Piece and pack weights from a product's printed serving and quantity (Open Food Facts' <c>serving_size</c> and
/// <c>quantity</c>). Only what the text says plainly counts: a serving of "2 biscuits (30 g)" is two pieces of 15 g, while
/// "30 g" or "1 portion (30 g)" says nothing about pieces. A volume is not a weight, so "500 ml" gives no pack weight.
/// </summary>
public static partial class UnitWeightText
{
    // Words that measure or portion rather than count pieces.
    private static readonly HashSet<string> NotPieces = new(StringComparer.OrdinalIgnoreCase)
    {
        "g", "gr", "gram", "grams", "г", "гр", "грам", "грама", "kg", "кг", "ml", "мл", "l", "л", "cl", "oz",
        "serving", "servings", "portion", "portions", "порция", "порции", "porción", "porcion", "portie", "portion(s)",
        "cup", "cups", "tbsp", "tsp", "tablespoon", "teaspoon", "чаша", "чаши", "лъжица", "лъжици", "шепа", "handful", "scoop", "мерителна",
    };

    /// <summary>One piece's weight, when <paramref name="servingSize"/> counts the pieces of a <paramref name="servingGrams"/> serving.</summary>
    public static decimal? PieceGrams(string? servingSize, decimal? servingGrams)
    {
        if (string.IsNullOrWhiteSpace(servingSize) || servingGrams is not ({ } grams and > 0))
        {
            return null;
        }

        var m = CountedServing().Match(servingSize);
        if (!m.Success || !int.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, out var count) || count is < 1 or > 50
            || NotPieces.Contains(m.Groups[2].Value.Trim().Split(' ')[0].TrimEnd('.')))
        {
            return null;
        }

        return Math.Round(grams / count, 1);
    }

    /// <summary>The pack's net weight in grams, from a quantity and its unit, or from the quantity text ("400 g").</summary>
    public static decimal? PackGrams(decimal? quantity, string? unit, string? quantityText)
    {
        decimal? grams = null;
        if (quantity is > 0 && unit is not null)
        {
            grams = unit.Trim().ToLowerInvariant() switch { "g" or "г" or "гр" => quantity, "kg" or "кг" => quantity * 1000, _ => null };
        }
        else if (quantityText is not null && Quantity().Match(quantityText) is { Success: true } q
                 && decimal.TryParse(q.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n > 0)
        {
            grams = q.Groups[2].Value.ToLowerInvariant() is "kg" or "кг" ? n * 1000 : n;
        }

        return grams is > 0 and <= FoodMath.MaxGrams ? Math.Round(grams.Value, 1) : null;
    }

    // "2 biscuits (30 g)", "3 бисквити = 25 г", "1 piece (25g)": a whole number, then a word.
    [GeneratedRegex(@"^\s*(\d+)\s+(\p{L}[\p{L}.()\s-]*?)\s*[(=:]", RegexOptions.CultureInvariant)]
    private static partial Regex CountedServing();

    [GeneratedRegex(@"^\s*(\d+(?:[.,]\d+)?)\s*(g|gr|г|гр|kg|кг)\.?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Quantity();
}
