using System.Globalization;
using System.Text.RegularExpressions;
using Plate.Core.Nutrition;

namespace Plate.Web.Integrations;

/// <summary>
/// Stand-in used when no OpenRouter key is configured (local development and tests). Splits on commas,
/// "and" and new lines, reads "200g" style amounts, and leaves the numbers to the database match.
/// </summary>
public partial class FakeFoodParser : IFoodParser
{
    public const string ModelName = "fake";

    public Task<FoodParseResult> ParseAsync(string text, CancellationToken ct)
    {
        var items = new List<ParsedFoodItem>();
        foreach (var part in Separator().Split(text ?? ""))
        {
            var piece = part.Trim();
            if (piece.Length == 0)
            {
                continue;
            }

            var grams = 100m;
            string? assumption = "assumed 100 g";
            var match = Amount().Match(piece);
            var given = match.Success;
            if (match.Success)
            {
                grams = decimal.Parse(match.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
                assumption = null;
                piece = (piece[..match.Index] + piece[(match.Index + match.Length)..]).Trim();
            }

            // "a banana", "2 eggs", "една баница": a count instead of a weight; the matcher makes it grams.
            decimal? units = null;
            var count = Count().Match(piece);
            if (!match.Success && count.Success && piece.Length > count.Length)
            {
                units = CountValue(count.Groups[1].Value);
                given = !Article().IsMatch(count.Groups[1].Value);
                piece = piece[count.Length..].Trim();
                grams = 100m * units.Value;
                assumption = null;
            }

            var state = piece.Contains("cooked", StringComparison.OrdinalIgnoreCase) ? FoodState.Cooked
                : piece.Contains("raw", StringComparison.OrdinalIgnoreCase) ? FoodState.Raw
                : FoodState.NotApplicable;

            // The name is what was written without its amount; the amount is in grams.
            items.Add(new ParsedFoodItem(piece, piece, grams, state, null, assumption, new Per100g(100, 5, 15, 3), units, given,
                units is null ? null : CountUnit.Piece, GramsStated: match.Success));
        }

        return Task.FromResult(items.Count == 0
            ? FoodParseResult.Failed("No food found in that text.")
            : new FoodParseResult(items, null, ModelName));
    }

    private static decimal CountValue(string word) => word.ToLowerInvariant() switch
    {
        "a" or "an" or "one" or "един" or "една" or "едно" => 1,
        "two" or "два" or "две" => 2,
        "three" or "три" => 3,
        "four" or "четири" => 4,
        "five" or "пет" => 5,
        var n => decimal.Parse(n, CultureInfo.InvariantCulture),
    };

    [GeneratedRegex(@"^\s*(a|an|one|two|three|four|five|един|една|едно|два|две|три|четири|пет|\d{1,2})\s+", RegexOptions.IgnoreCase)]
    private static partial Regex Count();

    [GeneratedRegex(@"^(a|an|един|една|едно)$", RegexOptions.IgnoreCase)]
    private static partial Regex Article();

    [GeneratedRegex(@",|\band\b|\n|\bи\b", RegexOptions.IgnoreCase)]
    private static partial Regex Separator();

    [GeneratedRegex(@"(\d+(?:[.,]\d+)?)\s*(?:g|gr|grams|гр|г)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Amount();
}
