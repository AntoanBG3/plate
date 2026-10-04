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
    IReadOnlyList<LocalText> Notes,
    string? MatchModel = null);

/// <summary>How one item was matched, kept with the parse run for feedback (SPEC 11): the ranking's top candidates and the decider's answer.</summary>
public record MatchTrace(IReadOnlyList<TraceCandidate> Top, bool Asked, int? DeciderPick, decimal? DeciderConfidence);

public record TraceCandidate(int Id, string Name, double Score, bool Own);

/// <summary>
/// Turns parsed foods into database foods: the model reads the text, the database supplies the numbers (SPEC 6.1).
/// A decider (Jev) chooses from a trigram shortlist when one is set up; its "none" counts only when it is sure.
/// Anything it doesn't decide falls back to Rank's top pick.
/// </summary>
public class FoodMatcher(FoodItemService foods, IFoodMatchDecider decider)
{
    private const double MatchThreshold = 0.5;
    private const double HighConfidence = 0.8;
    private const int ShortlistSize = 50;
    private const double ShortlistThreshold = 0.3;
    private const int TraceSize = 5;

    /// <summary>A "none" below this falls back to Rank (pre-registered in the Phase 0b evaluation).</summary>
    internal const decimal NoneGate = 0.9m;

    /// <summary>The lowest Jev confidence at which every food pick in both evaluation rounds was correct.</summary>
    internal const decimal JevHighConfidence = 0.95m;

    private static readonly string[] CookedWords = ["cooked", "boiled", "roasted", "grilled", "baked", "fried", "broiled", "steamed", "stewed", "braised"];

    // USDA entries that are rarely what someone means by a plain food name.
    private static readonly string[] Niche = ["babyfood", "restaurant", "fast food", "fast foods", "school lunch", "infant", "breaded", "with ", "mix", "flour", "juice", "chips", "glutinous", "dehydrated", "dried", "powder", "canned", "frozen",
        ", skin,", "skin only", "peel", // the skin of a potato or fruit, eaten on its own
        "plant-based", "vegetarian", "vegan", "meatless", "imitation", "substitute",
        "растителен", "вегетариан", "веган", "заместител",
        // The same niches in the Bulgarian names (the English ones above don't appear in them).
        "консерв", "замразен", "сушен", "на прах", "бебешк", "ресторант", "панир", "с кожа"];

    private static readonly string[] CookedWordsBg = ["варен", "печен", "на скара", "пържен", "задушен", "сготвен", "на фурна", "запечен"];

    /// <param name="trace">When given, receives one <see cref="MatchTrace"/> per item.</param>
    public async Task<List<ConfirmItem>> ResolveAsync(string text, IEnumerable<ParsedFoodItem> parsed, CancellationToken ct, List<MatchTrace>? trace = null)
    {
        var items = parsed.ToList();
        var ranked = new List<(FoodMatch Food, double Score)?>();
        var tops = new List<List<(FoodMatch Food, double Score)>>();
        var shortlists = new List<List<FoodMatch>>();
        foreach (var item in items)
        {
            var all = await RankAsync(item, ct);
            ranked.Add(all.Count == 0 ? null : all[0]);
            tops.Add(all.Take(TraceSize).ToList());
            var found = await foods.SearchAsync(item.NameEn, ShortlistSize, ShortlistThreshold, ct);
            if (!string.Equals(item.NameOriginal, item.NameEn, StringComparison.OrdinalIgnoreCase))
            {
                found.AddRange(await foods.SearchAsync(item.NameOriginal, ShortlistSize, ShortlistThreshold, ct));
            }

            shortlists.Add(Shortlist(found));
        }

        // A food the user saved, at the top of the ranking, stands: saving it is how they say "this is what I eat".
        // That item still goes in as context, but not as a question.
        for (var n = 0; n < items.Count; n++)
        {
            if (ranked[n] is { Food.Own: true })
            {
                shortlists[n] = [];
            }
        }

        // Every item goes in, so the others are context; only items with candidates become questions.
        var decisions = shortlists.Any(l => l.Count > 0)
            ? await decider.DecideAsync(text, items.Select((item, n) => new MatchQuestion(item, shortlists[n].Select(Candidate).ToList())).ToList(), ct)
            : null;

        var result = new List<ConfirmItem>();
        for (var n = 0; n < items.Count; n++)
        {
            var item = items[n];
            var notes = new List<LocalText>();
            if (!string.IsNullOrEmpty(item.Assumption))
            {
                notes.Add(new LocalText(item.Assumption));
            }

            var decision = shortlists[n].Count == 0 ? null : decisions?.Items.ElementAtOrDefault(n);
            trace?.Add(new MatchTrace(
                tops[n].Select(t => new TraceCandidate(t.Food.Id, t.Food.DisplayName, Math.Round(t.Score, 3), t.Food.Own)).ToList(),
                shortlists[n].Count > 0 && decisions is not null, decision?.FoodItemId, decision?.Confidence));
            if (decision is { FoodItemId: { } id } && shortlists[n].FirstOrDefault(c => c.Id == id) is { } pick)
            {
                var confidence = decision.Confidence >= JevHighConfidence ? Confidence.High : Confidence.Medium;
                result.Add(new ConfirmItem(Description(item), item.Grams, pick.Per100, pick.Id, pick.DisplayName, confidence, notes, decisions!.Model));
            }
            else if (decision is { FoodItemId: null } && decision.Confidence >= NoneGate)
            {
                result.Add(Estimate(item, notes, decisions!.Model));
            }
            else if (ranked[n] is { } best)
            {
                var confidence = best.Score >= HighConfidence ? Confidence.High : Confidence.Medium;
                result.Add(new ConfirmItem(Description(item), item.Grams, best.Food.Per100, best.Food.Id, best.Food.DisplayName, confidence, notes));
            }
            else
            {
                result.Add(Estimate(item, notes, null));
            }
        }

        return result;
    }

    /// <summary>
    /// The decider's options: the plain trigram matches of the English and the original name, merged by food with
    /// the better similarity, best first. This order is the one measured in the evaluation; Jev leans to the first.
    /// </summary>
    internal static List<FoodMatch> Shortlist(IEnumerable<FoodMatch> found) =>
        found
            .GroupBy(c => c.Id)
            .Select(g => g.MaxBy(c => c.Score)!)
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Name.Length)
            .Take(ShortlistSize)
            .ToList();

    private static MatchCandidate Candidate(FoodMatch f) => new(f.Id, f.Name, f.NameBg, f.Brand, f.Per100);

    /// <summary>Rank's candidates, best first, across the English search and, for text in another language, the original-name search.</summary>
    private async Task<List<(FoodMatch Food, double Score)>> RankAsync(ParsedFoodItem item, CancellationToken ct)
    {
        var ranked = (await foods.SearchAsync(item.NameEn, 120, MatchThreshold, ct))
            .Select(c => (Food: c, Score: Rank(c, item.NameEn, item.State)))
            .ToList();
        if (!string.Equals(item.NameOriginal, item.NameEn, StringComparison.OrdinalIgnoreCase))
        {
            // Typed in Bulgarian: Bulgarian dishes and products, and USDA foods by their Bulgarian names, are
            // ranked by how well their Bulgarian name fits what the user wrote.
            ranked.AddRange((await foods.SearchAsync(item.NameOriginal, 120, MatchThreshold, ct))
                .Where(c => !string.IsNullOrEmpty(c.NameBg))
                .Select(c => (Food: c, Score: Rank(c, item.NameOriginal, item.State, bulgarian: true))));
        }

        // A stable sort keeps the first of equal scores first, as MaxBy did.
        return ranked.OrderByDescending(x => x.Score).ToList();
    }

    /// <summary>No database food: the parser's own numbers, marked Low.</summary>
    private static ConfirmItem Estimate(ParsedFoodItem item, List<LocalText> notes, string? matchModel)
    {
        notes.Add(new LocalText("No database match, so these numbers are the AI's estimate."));
        if (FoodMath.MacrosDisagree(item.Estimated))
        {
            notes.Add(new LocalText("The calories don't add up from the macros."));
        }

        return new ConfirmItem(Description(item), item.Grams, item.Estimated, null, null, Confidence.Low, notes, matchModel);
    }

    /// <summary>
    /// Trigram similarity ties often (many USDA names contain "chicken" and "breast"), so the ranking adds:
    /// the user's own foods first; USDA's main food word (before the first comma) being one the user wrote;
    /// how many of the user's words appear; the right raw or cooked state; and fewer niche variants.
    /// </summary>
    internal static double Rank(FoodMatch food, string query, FoodState state, bool bulgarian = false)
    {
        var name = (bulgarian ? food.NameBg ?? "" : food.Name).ToLowerInvariant();
        var cooked = (bulgarian ? CookedWordsBg : CookedWords).Any(name.Contains);
        var raw = name.Contains(bulgarian ? "суров" : "raw");
        var score = food.Score + (food.Own ? 0.15 : 0);

        var queryWords = Words(query).ToHashSet();
        if (queryWords.Count > 0 && Words(name).ToHashSet().SetEquals(queryWords))
        {
            score += 0.3; // exactly the food the user named, e.g. "Боб чорба" for "боб чорба"
        }

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
