using System.Text.RegularExpressions;

namespace Plate.Core.Text;

/// <summary>Food entry names (SPEC 7 Food): the amount is shown beside the name, so the name never carries one.</summary>
public static partial class FoodText
{
    /// <summary>
    /// "150 g cooked rice" becomes "cooked rice": a leading number with a weight or volume unit (English or Bulgarian),
    /// and an "of"/"от" after it, is removed. Text that would be left empty is kept as it was.
    /// </summary>
    public static string StripAmount(string text)
    {
        var match = LeadingAmount().Match(text);
        if (!match.Success)
        {
            return text;
        }

        var rest = text[match.Length..].Trim();
        return rest.Length == 0 ? text : rest;
    }

    /// <summary>
    /// The name as a food diary shows it: the first letter upper case ("голям дюнер" becomes "Голям дюнер"), the rest as
    /// written, so brands and abbreviations keep their case.
    /// </summary>
    public static string Capitalize(string text)
    {
        var trimmed = text.TrimStart();
        return trimmed.Length == 0 || !char.IsLower(trimmed[0])
            ? text
            : string.Concat(char.ToUpperInvariant(trimmed[0]).ToString(), trimmed.AsSpan(1));
    }

    [GeneratedRegex(@"^\s*\d+(?:[.,]\d+)?\s*(?:kg|g|grams?|gr\.?|ml|l|кг|г\.?|гр\.?|грама?|мл|л)(?=\s|$)\s*(?:of\s+|от\s+)?", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingAmount();
}
