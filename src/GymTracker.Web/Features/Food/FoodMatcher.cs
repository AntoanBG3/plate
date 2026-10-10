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
/// <param name="Description">The row's name: the matched food's (or a short name for it), or what the user typed for an estimate.</param>
/// <param name="MatchedName">The matched food's database name in the account's language.</param>
/// <param name="MatchedNameEn">The matched food's English name when <paramref name="MatchedName"/> is Bulgarian.</param>
/// <param name="Typed">What the user wrote for this food, without the amount.</param>
/// <param name="State">Raw or cooked: from the matched food's names, or the parser's reading for an estimate.</param>
/// <param name="Status">
/// How the food is, shown beside its name ("boiled", "dried"): the namer's for a matched food, the parser's preparation for an
/// estimate. When null the view shows <paramref name="State"/>.
/// </param>
public record ConfirmItem(
    string Description,
    decimal Grams,
    Per100g Per100,
    int? FoodItemId,
    string? MatchedName,
    Confidence Confidence,
    IReadOnlyList<LocalText> Notes,
    string? MatchModel = null,
    string Typed = "",
    FoodState State = FoodState.NotApplicable,
    string? MatchedNameEn = null,
    string? Status = null);

/// <summary>How one item was matched, kept with the parse run for feedback (SPEC 11): the ranking's top candidates and the decider's answer.</summary>
/// <param name="DeciderModel">The decision model that answered for this parse, even where its answer was not used.</param>
public record MatchTrace(IReadOnlyList<TraceCandidate> Top, bool Asked, int? DeciderPick, decimal? DeciderConfidence, string? DeciderModel = null);

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
        "meat and skin", // chicken breast or thigh means the meat unless skin is said
        "plant-based", "vegetarian", "vegan", "meatless", "imitation", "substitute",
        "растителен", "вегетариан", "веган", "заместител",
        // The same niches in the Bulgarian names (the English ones above don't appear in them).
        "консерв", "замразен", "сушен", "на прах", "бебешк", "ресторант", "панир", "с кожа"];

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
                shortlists[n].Count > 0 && decisions is not null, decision?.FoodItemId, decision?.Confidence, decisions?.Model));
            if (decision is { FoodItemId: { } id } && shortlists[n].FirstOrDefault(c => c.Id == id) is { } pick)
            {
                var confidence = decision.Confidence >= JevHighConfidence ? Confidence.High : Confidence.Medium;
                result.Add(Matched(item, pick, Portion(item, pick, notes), confidence, notes, decisions!.Model));
            }
            else if (decision is { FoodItemId: null } && decision.Confidence >= NoneGate)
            {
                result.Add(Estimate(item, notes, decisions!.Model));
            }
            else if (ranked[n] is { } best)
            {
                // "Database" (High) only for a close match whose main food word is one the user wrote; otherwise the
                // confirmation marks it "Check this" (QA: a banana matched a strawberry-banana mousse as Database).
                var english = best.Food.NameBg is null || string.Equals(item.NameOriginal, item.NameEn, StringComparison.OrdinalIgnoreCase);
                var confidence = best.Score >= HighConfidence && HeadMatches(best.Food, english ? item.NameEn : item.NameOriginal, !english)
                    ? Confidence.High : Confidence.Medium;
                result.Add(Matched(item, best.Food, Portion(item, best.Food, notes), confidence, notes, null));
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
        var ranked = (await foods.SearchAsync(FoodWords(item.NameEn), 120, MatchThreshold, ct))
            .Where(c => !MatchesOnlyItsUse(c, item.NameEn))
            .Select(c => (Food: c, Score: Rank(c, item.NameEn, item.State)))
            .ToList();
        if (!string.Equals(item.NameOriginal, item.NameEn, StringComparison.OrdinalIgnoreCase))
        {
            // Typed in Bulgarian: Bulgarian dishes and products, and USDA foods by their Bulgarian names, are
            // ranked by how well their Bulgarian name fits what the user wrote.
            ranked.AddRange((await foods.SearchAsync(item.NameOriginal, 120, MatchThreshold, ct))
                .Where(c => !string.IsNullOrEmpty(c.NameBg) && !MatchesOnlyItsUse(c, item.NameOriginal, bulgarian: true))
                .Select(c => (Food: c, Score: Rank(c, item.NameOriginal, item.State, bulgarian: true))));
        }

        // A stable sort keeps the first of equal scores first, as MaxBy did.
        return ranked.OrderByDescending(x => x.Score).ToList();
    }

    /// <summary>
    /// The grams for an item: as parsed, or for a count ("a banana") the food's serving times the count, with a note.
    /// Foods without a serving keep the parser's 100 g each.
    /// </summary>
    private static decimal Portion(ParsedFoodItem item, FoodMatch food, List<LocalText> notes)
    {
        if (item.Units is not { } units)
        {
            return item.Grams;
        }

        if (food.DefaultServingGrams is { } serving and > 0)
        {
            notes.Add(new LocalText("{0} × a serving of {1} g", units, serving.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)));
            return serving * units;
        }

        notes.Add(new LocalText("assumed 100 g each"));
        return item.Grams;
    }

    /// <summary>The food's main word (before the first comma, in USDA's naming) is one the user wrote.</summary>
    internal static bool HeadMatches(FoodMatch food, string query, bool bulgarian = false)
    {
        var head = Head((bulgarian ? food.NameBg ?? "" : food.Name).ToLowerInvariant());
        var queryWords = Words(query).ToHashSet();
        return head.Count > 0 && head.Any(queryWords.Contains);
    }

    /// <summary>
    /// The main food words: before the first comma (USDA's naming) and before "for" or "за", which says what the
    /// food is used for ("Пилешко месо за дюнер" is meat, not a doner).
    /// </summary>
    private static List<string> Head(string name)
    {
        var words = Words(name.Split(',')[0]).ToList();
        var use = words.FindIndex(w => w is "for" or "за");
        return use > 0 ? words[..use] : words;
    }

    /// <summary>
    /// The user's words appear in the food's name only after "for" or "за": the food is something used for what they
    /// ate, not that food (feedback #4: a "голям дюнер" became the BIM doner meat).
    /// </summary>
    internal static bool MatchesOnlyItsUse(FoodMatch food, string query, bool bulgarian = false)
    {
        var words = Words((bulgarian ? food.NameBg ?? "" : food.Name).ToLowerInvariant()).ToList();
        var use = words.FindIndex(w => w is "for" or "за");
        if (use <= 0)
        {
            return false;
        }

        var queryWords = Words(query).ToHashSet();
        return !words[..use].Any(queryWords.Contains) && words[(use + 1)..].Any(queryWords.Contains);
    }

    /// <summary>A database food: the row takes its name, so the log says which food the numbers are (a namer may shorten it later).</summary>
    private static ConfirmItem Matched(ParsedFoodItem item, FoodMatch food, decimal grams, Confidence confidence, List<LocalText> notes, string? matchModel)
    {
        var name = food.DisplayName.Length > 200 ? food.DisplayName[..200] : food.DisplayName;
        return new ConfirmItem(name, grams, food.Per100, food.Id, food.DisplayName, confidence, notes, matchModel,
            Typed: Description(item), State: FoodStates.FromName(food.Name, food.NameBg),
            MatchedNameEn: food.DisplayName == food.Name ? null : food.Name);
    }

    /// <summary>No database food: the parser's own numbers, marked Low.</summary>
    private static ConfirmItem Estimate(ParsedFoodItem item, List<LocalText> notes, string? matchModel)
    {
        notes.Add(new LocalText("No database match, so these numbers are the AI's estimate."));
        if (FoodMath.MacrosDisagree(item.Estimated))
        {
            notes.Add(new LocalText("The calories don't add up from the macros."));
        }

        var typed = Description(item);
        return new ConfirmItem(typed, item.Grams, item.Estimated, null, null, Confidence.Low, notes, matchModel, Typed: typed, State: item.State,
            Status: FoodNaming.AcceptStatus(item.Preparation, item.State));
    }

    /// <summary>
    /// Trigram similarity ties often (many USDA names contain "chicken" and "breast"), so the ranking adds:
    /// the user's own foods first; USDA's main food word (before the first comma) being one the user wrote;
    /// how many of the user's words appear; the right raw or cooked state; and fewer niche variants.
    /// </summary>
    internal static double Rank(FoodMatch food, string query, FoodState state, bool bulgarian = false)
    {
        var name = (bulgarian ? food.NameBg ?? "" : food.Name).ToLowerInvariant();
        var nameState = FoodStates.FromName(name);
        var cooked = nameState == FoodState.Cooked;
        var raw = nameState == FoodState.Raw;
        var score = food.Score + (food.Own ? 0.15 : 0);

        var queryWords = Words(query).ToHashSet();
        if (queryWords.Count > 0 && Words(name).ToHashSet().SetEquals(queryWords))
        {
            score += 0.3; // exactly the food the user named, e.g. "Боб чорба" for "боб чорба"
        }

        var head = Head(name);
        if (head.Count > 0 && head.All(queryWords.Contains))
        {
            score += 0.2;
        }
        else if (head.Count > 0 && queryWords.Contains(head[0]))
        {
            score += 0.1;
        }

        // USDA writes brands in capitals ("UNCLE BENS"); a brand nobody wrote is not the plain food.
        if (!bulgarian && System.Text.RegularExpressions.Regex.IsMatch(food.Name, @"\b\p{Lu}{3,}\b")
            && !System.Text.RegularExpressions.Regex.Matches(food.Name, @"\b\p{Lu}{3,}\b").Any(m => query.Contains(m.Value, StringComparison.OrdinalIgnoreCase)))
        {
            score -= 0.15;
        }

        // A word in the main food name the user didn't write is a variety they didn't ask for: "wild rice" for "rice".
        score -= head.Count(w => !queryWords.Contains(w)) > 0 && head.Any(queryWords.Contains) ? 0.15 : 0;

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

    /// <summary>
    /// The text to look candidates up by: the food words without "cooked", "raw" and the like. With them, every cooked
    /// food in the database scored high on that one word and crowded the food itself out ("cooked rice" found cooked
    /// lamb before white rice). The state still counts in <see cref="Rank"/>.
    /// </summary>
    internal static string FoodWords(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var kept = words.Where(w => !CookedWords.Contains(w.ToLowerInvariant()) && !w.Equals("raw", StringComparison.OrdinalIgnoreCase)).ToList();
        return kept.Count == 0 ? text : string.Join(' ', kept);
    }

    /// <summary>Lower-case words with a plural "s" or "es" removed, so "bananas" matches "banana".</summary>
    private static IEnumerable<string> Words(string text) =>
        text.ToLowerInvariant()
            .Split([' ', ',', '(', ')', '-', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.EndsWith("oes") || w.EndsWith("ies") ? w[..^2] : w.EndsWith('s') && w.Length > 3 ? w[..^1] : w);

    private static string Description(ParsedFoodItem item)
    {
        // A model sometimes repeats the amount in the name; the grams are shown beside it, so it goes.
        var text = GymTracker.Core.Text.FoodText.StripAmount(item.NameOriginal.Length > 0 ? item.NameOriginal : item.NameEn);
        if (!string.IsNullOrEmpty(item.Preparation) && !text.Contains(item.Preparation, StringComparison.OrdinalIgnoreCase))
        {
            text = $"{text}, {item.Preparation}";
        }

        return text.Length > 200 ? text[..200] : text;
    }
}
