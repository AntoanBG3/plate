using GymTracker.Core.Nutrition;
using GymTracker.Core.Text;

namespace GymTracker.Web.Features.Food;

public enum Confidence
{
    Low,
    Medium,
    High,
}

/// <summary>One row of the confirmation list shown after parsing text. Nothing is saved until the user confirms.</summary>
public record ConfirmItem(
    string Description,
    decimal Grams,
    Per100g Per100,
    int? FoodItemId,
    string? MatchedName,
    Confidence Confidence,
    IReadOnlyList<LocalText> Notes);

/// <summary>Turns parsed foods into database foods: the model reads the text, the database supplies the numbers (SPEC 6.1).</summary>
public class FoodMatcher(FoodItemService foods)
{
    private const double MatchThreshold = 0.5;
    private const double HighConfidence = 0.8;

    private static readonly string[] CookedWords = ["cooked", "boiled", "roasted", "grilled", "baked", "fried", "broiled", "steamed", "stewed", "braised"];

    // USDA entries that are rarely what someone means by a plain food name.
    private static readonly string[] Niche = ["babyfood", "restaurant", "fast food", "fast foods", "school lunch", "infant", "breaded", "with ", "mix", "flour", "juice", "chips", "glutinous", "dehydrated", "dried", "powder", "canned", "frozen"];

    public async Task<List<ConfirmItem>> ResolveAsync(IEnumerable<ParsedFoodItem> parsed, CancellationToken ct)
    {
        var result = new List<ConfirmItem>();
        foreach (var item in parsed)
        {
            var candidates = await foods.SearchAsync(item.NameEn, 120, MatchThreshold, ct);
            if (!string.Equals(item.NameOriginal, item.NameEn, StringComparison.OrdinalIgnoreCase))
            {
                // A Bulgarian name may match a food someone saved in Bulgarian.
                candidates.AddRange(await foods.SearchAsync(item.NameOriginal, 5, MatchThreshold, ct));
            }

            var best = candidates
                .Select(c => (Food: c, Score: Rank(c, item.NameEn, item.State)))
                .OrderByDescending(x => x.Score)
                .Select(x => x.Food)
                .FirstOrDefault();

            var notes = new List<LocalText>();
            if (!string.IsNullOrEmpty(item.Assumption))
            {
                notes.Add(new LocalText(item.Assumption));
            }

            if (best is not null)
            {
                result.Add(new ConfirmItem(
                    Description(item),
                    item.Grams,
                    best.Per100,
                    best.Id,
                    best.Name,
                    best.Score >= HighConfidence ? Confidence.High : Confidence.Medium,
                    notes));
                continue;
            }

            notes.Add(new LocalText("No database match, so these numbers are the AI's estimate."));
            if (FoodMath.MacrosDisagree(item.Estimated))
            {
                notes.Add(new LocalText("The calories don't add up from the macros."));
            }

            result.Add(new ConfirmItem(Description(item), item.Grams, item.Estimated, null, null, Confidence.Low, notes));
        }

        return result;
    }

    /// <summary>
    /// Trigram similarity ties often (many USDA names contain "chicken" and "breast"), so the ranking adds:
    /// the user's own foods first; USDA's main food word (before the first comma) being one the user wrote;
    /// how many of the user's words appear; the right raw or cooked state; and fewer niche variants.
    /// </summary>
    internal static double Rank(FoodMatch food, string query, FoodState state)
    {
        var name = food.Name.ToLowerInvariant();
        var cooked = CookedWords.Any(name.Contains);
        var raw = name.Contains("raw");
        var score = food.Score + (food.Own ? 0.15 : 0);

        var queryWords = Words(query).ToHashSet();
        var head = Words(name.Split(',')[0]).ToList();
        if (head.Count > 0 && head.All(queryWords.Contains))
        {
            score += 0.2;
        }
        else if (head.Count > 0 && queryWords.Contains(head[0]))
        {
            score += 0.1;
        }

        var nameWords = Words(name).ToHashSet();
        score += queryWords.Count == 0 ? 0 : 0.2 * queryWords.Count(nameWords.Contains) / queryWords.Count;
        // A niche word the user didn't write (asking for "dried apricots" is fine) counts against the entry.
        var q = query.ToLowerInvariant();
        score -= Niche.Count(n => name.Contains(n) && !q.Contains(n.Trim())) * 0.15;
        score += state switch
        {
            FoodState.Raw when raw => 0.1,
            FoodState.Raw when cooked => -0.2,
            FoodState.Cooked when cooked => 0.1,
            FoodState.Cooked when raw => -0.2,
            FoodState.NotApplicable when raw => 0.05, // fruit and vegetables are usually eaten as they are
            _ => 0,
        };

        // Shorter names are usually the plain version of a food rather than a niche variant.
        return score - name.Length / 2000.0;
    }

    /// <summary>Lower-case words with a plural "s" or "es" removed, so "bananas" matches "banana".</summary>
    private static IEnumerable<string> Words(string text) =>
        text.ToLowerInvariant()
            .Split([' ', ',', '(', ')', '-', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.EndsWith("oes") || w.EndsWith("ies") ? w[..^2] : w.EndsWith('s') && w.Length > 3 ? w[..^1] : w);

    private static string Description(ParsedFoodItem item)
    {
        var text = item.NameOriginal.Length > 0 ? item.NameOriginal : item.NameEn;
        if (!string.IsNullOrEmpty(item.Preparation) && !text.Contains(item.Preparation, StringComparison.OrdinalIgnoreCase))
        {
            text = $"{text}, {item.Preparation}";
        }

        return text.Length > 200 ? text[..200] : text;
    }
}
