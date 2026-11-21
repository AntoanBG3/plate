using Plate.Core.Nutrition;
using Plate.Core.Text;

namespace Plate.Web.Features.Food;

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
/// <param name="Usual">Matched through the account's usual food for these words (SPEC 6.1 step 1).</param>
/// <param name="Count">
/// The count the text gave instead of a weight, kept so the row's grams can follow when another food is picked for it.
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
    string? Status = null,
    bool Usual = false,
    CountedAmount? Count = null);

/// <summary>A count the text gave ("две", "half a"), of pieces, packs or servings. Stored with the run's rows (ResolvedRow), so the names are fixed.</summary>
public record CountedAmount(
    [property: System.Text.Json.Serialization.JsonPropertyName("units")] decimal Units,
    [property: System.Text.Json.Serialization.JsonPropertyName("of")] CountUnit Of);

/// <summary>How one item was matched, kept with the parse run for feedback (SPEC 11): the ranking's top candidates and the decider's answer.</summary>
/// <param name="DeciderModel">The decision model that answered for this parse, even where its answer was not used.</param>
public record MatchTrace(IReadOnlyList<TraceCandidate> Top, bool Asked, int? DeciderPick, decimal? DeciderConfidence, string? DeciderModel = null);

public record TraceCandidate(int Id, string Name, double Score, bool Own);

/// <summary>
/// Turns parsed foods into database foods: the model reads the text, the database supplies the numbers (SPEC 6.1).
/// A decider (Jev) chooses from a trigram shortlist when one is set up; its "none" counts only when it is sure or
/// Rank's top pick is weak, and a pick it is barely sure of leaves a strong top pick of Rank's standing and is no
/// match otherwise. Anything it doesn't decide falls back to Rank's top pick. Without <paramref name="repeat"/> (the evaluation), no usual
/// foods or last amounts are used, so the parser and matcher are measured alone.
/// </summary>
public class FoodMatcher(FoodItemService foods, IFoodMatchDecider decider, RepeatLoggingService? repeat = null)
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

    /// <summary>
    /// A pick below this is a guess: the item gets the AI estimate instead (feedback #7-#10: Lidl pastries matched a
    /// breakfast pizza and tea biscuits at 0.22 and 0.23; the lowest pick users kept was 0.34).
    /// </summary>
    internal const decimal PickFloor = 0.3m;

    /// <summary>
    /// After an unsure "none", Rank's top pick stands only from this score; below it the AI estimate is the honest answer
    /// (feedback #7: a SUBWAY steak sandwich at 0.52 for a Lidl pastry; the lowest fallback users kept was 1.2).
    /// </summary>
    internal const double NoneFallbackFloor = 0.8;

    /// <summary>
    /// After a pick below <see cref="PickFloor"/>, Rank's top pick stands only from this score. The decider spreads its
    /// confidence over near-identical rows (five "Лютеница" rows ranked at 1.70 drew a pick at 0.14), while the Lidl
    /// misses ranked at 0.52 to 0.86; the lowest fallback users kept was 1.2.
    /// </summary>
    internal const double UnsurePickFallbackFloor = 1.2;

    private static readonly string[] CookedWords = ["cooked", "boiled", "roasted", "grilled", "baked", "fried", "broiled", "steamed", "stewed", "braised"];

    // USDA entries that are rarely what someone means by a plain food name.
    private static readonly string[] Niche = ["babyfood", "restaurant", "fast food", "fast foods", "school lunch", "infant", "breaded", "with ", "mix", "flour", "juice", "chips", "glutinous", "dehydrated", "dried", "powder", "canned", "frozen",
        ", skin,", "skin only", "peel", // the skin of a potato or fruit, eaten on its own
        "meat and skin", // chicken breast or thigh means the meat unless skin is said
        "plant-based", "vegetarian", "vegan", "meatless", "imitation", "substitute",
        "растителен", "вегетариан", "веган", "заместител",
        // The same niches in the Bulgarian names (the English ones above don't appear in them).
        "консерв", "замразен", "сушен", "на прах", "бебешк", "ресторант", "панир", "с кожа"];

    private sealed record Candidates(ParsedFoodItem Item, FoodMatch? Usual, List<(FoodMatch Food, double Score)> Ranked, List<FoodMatch> Shortlist)
    {
        public (FoodMatch Food, double Score)? Best => Ranked.Count == 0 ? null : Ranked[0];
    }

    /// <param name="trace">When given, receives one <see cref="MatchTrace"/> per item.</param>
    public async Task<List<ConfirmItem>> ResolveAsync(string text, IEnumerable<ParsedFoodItem> parsed, CancellationToken ct, List<MatchTrace>? trace = null)
    {
        var items = parsed.ToList();
        var usual = repeat is null ? [] : await repeat.UsualMatchesAsync(items, ct);
        var askModel = items.Count > 0 && await decider.IsEnabledAsync(ct);
        var candidates = new List<Candidates>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            candidates.Add(await FindCandidatesAsync(items[i], usual.GetValueOrDefault(i), trace is not null, askModel, ct));
        }

        // Every item is context for the model, including foods already decided locally.
        var decisions = candidates.Any(c => c.Shortlist.Count > 0)
            ? await decider.DecideAsync(text, candidates.Select(c => new MatchQuestion(c.Item, c.Shortlist.Select(Candidate).ToList())).ToList(), ct)
            : null;
        var result = new List<ConfirmItem>(items.Count);
        for (var i = 0; i < candidates.Count; i++)
        {
            var row = candidates[i];
            var decision = row.Shortlist.Count > 0 ? decisions?.Items.ElementAtOrDefault(i) : null;
            trace?.Add(new MatchTrace(
                row.Ranked.Select(t => new TraceCandidate(t.Food.Id, t.Food.DisplayName, Math.Round(t.Score, 3), t.Food.Own)).ToList(),
                row.Shortlist.Count > 0 && decisions is not null, decision?.FoodItemId, decision?.Confidence, decisions?.Model));
            result.Add(Choose(row, decision, decisions?.Model));
        }
        if (repeat is not null) await UseLastAmountsAsync(items, result, ct);
        return result;
    }

    private async Task<Candidates> FindCandidatesAsync(ParsedFoodItem item, FoodMatch? usual, bool trace, bool askModel, CancellationToken ct)
    {
        var ranked = usual is null || trace ? await RankAsync(item, trace ? TraceSize : 1, ct) : [];
        if (!askModel || usual is not null || ranked.Count > 0 && ranked[0].Food.Own)
            return new(item, usual, ranked, []);

        var found = await foods.SearchAsync(item.NameEn, ShortlistSize, ShortlistThreshold, ct);
        if (!string.Equals(item.NameOriginal, item.NameEn, StringComparison.OrdinalIgnoreCase))
            found.AddRange(await foods.SearchAsync(item.NameOriginal, ShortlistSize, ShortlistThreshold, ct));
        return new(item, null, ranked, Shortlist(found, Typed(item)));
    }

    private static ConfirmItem Choose(Candidates candidates, MatchDecision? decision, string? model)
    {
        var item = candidates.Item;
        var notes = new List<LocalText>();
        if (!string.IsNullOrEmpty(item.Assumption))
        {
            notes.Add(new LocalText(item.Assumption));
        }

        if (candidates.Usual is { } mine)
        {
            return Matched(item, mine, Portion(item, mine, notes), Confidence.High, notes, null) with { Usual = true };
        }

        if (decision is { FoodItemId: not null } && decision.Confidence < PickFloor && candidates.Best is not { Score: >= UnsurePickFallbackFloor })
        {
            return Estimate(item, notes, model);
        }
        if (decision is { FoodItemId: { } id } && decision.Confidence >= PickFloor && candidates.Shortlist.FirstOrDefault(c => c.Id == id) is { } pick)
        {
            var confidence = decision.Confidence >= JevHighConfidence ? Confidence.High : Confidence.Medium;
            return Matched(item, pick, Portion(item, pick, notes), confidence, notes, model);
        }
        if (decision is { FoodItemId: null } && (decision.Confidence >= NoneGate || candidates.Best is not { Score: >= NoneFallbackFloor }))
        {
            return Estimate(item, notes, model);
        }
        if (candidates.Best is { } best)
        {
            // "Database" (High) only for a close match whose main food word is one the user wrote; otherwise the
            // confirmation marks it "Check this" (QA: a banana matched a strawberry-banana mousse as Database).
            var english = best.Food.NameBg is null || string.Equals(item.NameOriginal, item.NameEn, StringComparison.OrdinalIgnoreCase);
            var confidence = best.Score >= HighConfidence && HeadMatches(best.Food, english ? item.NameEn : item.NameOriginal, !english)
                ? Confidence.High : Confidence.Medium;
            return Matched(item, best.Food, Portion(item, best.Food, notes), confidence, notes, null);
        }
        return Estimate(item, notes, null);
    }


    private const string EstimatedNote = "These values are estimated from the recipe, not read from a label.";

    /// <summary>
    /// SPEC 6.1 step 4: when the text gave no amount, a matched food takes the account's last amount, and the note
    /// about the guessed amount gives way to "Your usual amount". An amount the user gave is never replaced.
    /// </summary>
    private async Task UseLastAmountsAsync(List<ParsedFoodItem> items, List<ConfirmItem> result, CancellationToken ct)
    {
        var open = Enumerable.Range(0, result.Count).Where(n => !items[n].AmountGiven && result[n].FoodItemId is not null).ToList();
        if (open.Count == 0)
        {
            return;
        }

        var last = await repeat!.LastAmountsAsync(open.Select(n => result[n].FoodItemId!.Value).ToList(), ct);
        foreach (var n in open)
        {
            if (last.TryGetValue(result[n].FoodItemId!.Value, out var grams))
            {
                var notes = result[n].Notes
                    .Where(note => note.Format != items[n].Assumption && !Portions.Notes.Contains(note.Format))
                    .Append(new LocalText("Your usual amount, {0} g", grams.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)))
                    .ToList();
                result[n] = result[n] with { Grams = grams, Notes = notes };
            }
        }
    }

    /// <summary>
    /// The decider's options: the plain trigram matches of the English and the original name, merged by food with
    /// the better similarity, best first. This order is the one measured in the evaluation; Jev leans to the first.
    /// </summary>
    /// <param name="typed">What the user wrote for the item; a brand in it keeps that brand's products apart (see <see cref="Collapse{T}"/>).</param>
    internal static List<FoodMatch> Shortlist(IEnumerable<FoodMatch> found, string typed = "") =>
        Collapse(found
                .GroupBy(c => c.Id)
                .Select(g => g.MaxBy(c => c.Score)!)
                .OrderByDescending(c => c.Score)
                .ThenBy(c => c.Name.Length)
                .ToList(), c => c, (_, food) => food, typed)
            .Take(ShortlistSize)
            .ToList();

    /// <summary>
    /// Same-named foods as one option. Products from many makers share a plain name (six "Лютеница" rows), and offered
    /// side by side they split the decider's confidence (parse run 22: a pick at 0.14) and leave Rank's pick to whichever
    /// brand sorts first. Unless the user named one of their brands, products without an English name of their own that
    /// share a Bulgarian name go in as one food: the generic food of that name when there is exactly one, otherwise the
    /// product with the middle energy. Foods with English names of their own stay apart (two USDA foods can share a
    /// translation), and so do the account's own foods and shared ones. Nothing is deleted, and a barcode still finds its
    /// product. Each group takes its best member's place; a food listed twice keeps its first place.
    /// </summary>
    internal static List<T> Collapse<T>(IReadOnlyList<T> items, Func<T, FoodMatch> food, Func<T, FoodMatch, T> replace, string typed)
    {
        var seen = new HashSet<int>();
        var unique = items.Where(i => seen.Add(food(i).Id)).ToList();
        var words = " " + Key(typed) + " ";
        var groups = unique.Select(food)
            .Where(Mergeable)
            .GroupBy(f => Key(f.NameBg!))
            .Where(g =>
            {
                var products = g.Count(IsProduct);
                return (products > 1 || (products == 1 && g.Count() == 2))
                    && !g.Any(f => f.Brand is { } brand && Key(brand) is { Length: >= 2 } b && words.Contains(" " + b + " ", StringComparison.Ordinal));
            })
            .ToDictionary(g => g.Key, g => Representative(g.ToList()));

        var result = new List<T>();
        var placed = new HashSet<string>();
        foreach (var i in unique)
        {
            var f = food(i);
            if (!Mergeable(f) || !groups.TryGetValue(Key(f.NameBg!), out var representative) || (!IsProduct(f) && f.Id != representative.Id))
            {
                result.Add(i);
            }
            else if (placed.Add(Key(f.NameBg!)))
            {
                result.Add(f.Id == representative.Id ? i : replace(i, representative));
            }
        }

        return result;

        static bool Mergeable(FoodMatch f) => !f.Own && !f.ByOther && !string.IsNullOrWhiteSpace(f.NameBg);

        // A product's name is its Bulgarian name; a generic or USDA food has an English one besides.
        static bool IsProduct(FoodMatch f) => string.Equals(f.Name, f.NameBg, StringComparison.OrdinalIgnoreCase);

        static FoodMatch Representative(List<FoodMatch> group)
        {
            var named = group.Where(f => !IsProduct(f)).ToList();
            if (named.Count == 1)
            {
                return named[0];
            }

            var products = group.Where(IsProduct).OrderBy(f => f.KcalPer100g).ThenBy(f => f.Id).ToList();
            return products[(products.Count - 1) / 2];
        }

        static string Key(string s) =>
            string.Join(' ', s.ToLowerInvariant().Split(s.Where(c => !char.IsLetterOrDigit(c)).Distinct().ToArray(), StringSplitOptions.RemoveEmptyEntries));
    }

    private static MatchCandidate Candidate(FoodMatch f) => new(f.Id, f.Name, f.NameBg, f.Brand, f.Per100);

    /// <summary>Rank's candidates, best first, across the English search and, for text in another language, the original-name search.</summary>
    private async Task<List<(FoodMatch Food, double Score)>> RankAsync(ParsedFoodItem item, int take, CancellationToken ct)
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

        // A stable sort keeps the first of equal scores first, as MaxBy did. Same-named products are grouped before
        // taking, so Rank's pick is the group's representative, not whichever brand sorted first.
        return Collapse(ranked.OrderByDescending(x => x.Score).ToList(), x => x.Food, (x, food) => (food, x.Score), Typed(item))
            .Take(take).ToList();
    }

    private static string Typed(ParsedFoodItem item) => item.NameOriginal + " " + item.NameEn;

    /// <summary>The grams for an item on this food (<see cref="Portions.For"/>), with a note on how they were reached.</summary>
    private static decimal Portion(ParsedFoodItem item, FoodMatch food, List<LocalText> notes)
    {
        var portion = Portions.For(item, food.Weights);
        if (portion.Basis == PortionBasis.Count)
        {
            // The parser's assumption was about the grams it guessed, which the food's own weight now replaces.
            notes.RemoveAll(n => n.Format == item.Assumption);
        }

        if (portion.Note is { } note)
        {
            notes.Add(note);
        }

        return portion.Grams;
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
        if (food.ValuesEstimated)
        {
            notes.Add(new LocalText(EstimatedNote));
        }

        return new ConfirmItem(name, grams, food.Per100, food.Id, food.DisplayName, confidence, notes, matchModel,
            Typed: Description(item), State: FoodStates.FromName(food.Name, food.NameBg),
            MatchedNameEn: food.DisplayName == food.Name ? null : food.Name, Count: Counted(item));
    }

    private static CountedAmount? Counted(ParsedFoodItem item) =>
        !item.GramsStated && item.Units is { } units && units > 0 ? new CountedAmount(units, item.CountOf ?? CountUnit.Piece) : null;

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
            Status: FoodNaming.AcceptStatus(item.Preparation, item.State), Count: Counted(item));
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
        var text = Plate.Core.Text.FoodText.StripAmount(item.NameOriginal.Length > 0 ? item.NameOriginal : item.NameEn);
        if (!string.IsNullOrEmpty(item.Preparation) && !text.Contains(item.Preparation, StringComparison.OrdinalIgnoreCase))
        {
            text = $"{text}, {item.Preparation}";
        }

        return text.Length > 200 ? text[..200] : text;
    }
}
