using System.Globalization;
using System.Text.RegularExpressions;
using GymTracker.Core.Nutrition;

namespace GymTracker.Web.Integrations;

/// <summary>
/// Stand-in used when no Gemini key is configured (local development and tests). Splits on commas,
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
            if (match.Success)
            {
                grams = decimal.Parse(match.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
                assumption = null;
                piece = (piece[..match.Index] + piece[(match.Index + match.Length)..]).Trim();
            }

            var state = piece.Contains("cooked", StringComparison.OrdinalIgnoreCase) ? FoodState.Cooked
                : piece.Contains("raw", StringComparison.OrdinalIgnoreCase) ? FoodState.Raw
                : FoodState.NotApplicable;

            items.Add(new ParsedFoodItem(piece, part.Trim(), grams, state, null, assumption, new Per100g(100, 5, 15, 3)));
        }

        return Task.FromResult(items.Count == 0
            ? FoodParseResult.Failed("No food found in that text.")
            : new FoodParseResult(items, null, ModelName));
    }

    [GeneratedRegex(@",|\band\b|\n|\bи\b", RegexOptions.IgnoreCase)]
    private static partial Regex Separator();

    [GeneratedRegex(@"(\d+(?:[.,]\d+)?)\s*(?:g|gr|grams|гр|г)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Amount();
}
