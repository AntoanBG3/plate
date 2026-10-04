using System.Globalization;
using System.Text.Json.Nodes;
using GymTracker.Core.Nutrition;

namespace GymTracker.Web.Integrations;

/// <summary>
/// Asks TypeSafe's Jev decision model, through OpenRouter's Decisions API, which shortlisted database food each
/// parsed item is (SPEC 6.1). One request per parse, one Choice question per item. The wording below is the one
/// measured in the Phase 0 evaluation; changing it means measuring again.
/// </summary>
public class JevFoodMatchDecider
{
    public const string Jev113 = "typesafe/jev-1.13";

    private const string Instructions =
        "Which database entry is the food described in foods.{0}? The diary_entry is what the user typed. " +
        "Pick the entry that is the same food in the same form: raw or cooked as the food's state says, and the " +
        "preparation if one is given. Pick none if no entry is this food, for example when the entries are only " +
        "a different food, a different form of it, or a dish that merely contains it.";

    private const string NoneKey = "none";
    private const string NoneOption = "None of these entries is this food.";

    /// <summary>The request: the text and every item as state, a Choice question for each item that has candidates.</summary>
    internal static JsonObject Body(string model, string text, IReadOnlyList<MatchQuestion> questions)
    {
        var foods = new JsonObject();
        var asked = new JsonObject();
        for (var n = 0; n < questions.Count; n++)
        {
            var item = questions[n].Item;
            foods[$"i{n}"] = new JsonObject
            {
                ["as_written"] = item.NameOriginal,
                ["english"] = item.NameEn,
                ["state"] = item.State switch { FoodState.Raw => "raw", FoodState.Cooked => "cooked", _ => "not applicable" },
                ["preparation"] = item.Preparation,
                ["grams"] = item.Grams,
            };
            if (questions[n].Candidates.Count == 0)
            {
                continue;
            }

            var criteria = new JsonObject();
            foreach (var c in questions[n].Candidates)
            {
                criteria[$"f{c.Id}"] = Describe(c);
            }

            criteria[NoneKey] = NoneOption;
            asked[$"i{n}"] = new JsonObject
            {
                ["type"] = "choice",
                ["instructions"] = string.Format(CultureInfo.InvariantCulture, Instructions, $"i{n}"),
                ["criteria"] = criteria,
            };
        }

        return new JsonObject
        {
            ["model"] = model,
            ["state"] = new JsonObject { ["diary_entry"] = text, ["foods"] = foods },
            ["questions"] = asked,
        };
    }

    /// <summary>
    /// One decision per question. An answer naming a food that was not offered for that item, or no answer at all,
    /// leaves the item undecided. Null when the response has no answers.
    /// </summary>
    internal static MatchDecisions? Read(JsonNode? response, IReadOnlyList<MatchQuestion> questions)
    {
        if (response?["answers"] is not JsonObject answers)
        {
            return null;
        }

        var items = new List<MatchDecision?>();
        for (var n = 0; n < questions.Count; n++)
        {
            items.Add(answers[$"i{n}"] is { } answer ? Decision(answer, questions[n]) : null);
        }

        return new MatchDecisions(items, response["model"] is JsonValue m && m.TryGetValue<string>(out var model) ? model : "");
    }

    private static MatchDecision? Decision(JsonNode answer, MatchQuestion question)
    {
        if (answer["choice"] is not JsonValue c || !c.TryGetValue<string>(out var choice)
            || answer["confidence"] is not JsonValue v || !v.TryGetValue<decimal>(out var confidence))
        {
            return null;
        }

        if (choice == NoneKey)
        {
            return new MatchDecision(null, confidence);
        }

        return choice.StartsWith('f') && int.TryParse(choice[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            && question.Candidates.Any(x => x.Id == id)
            ? new MatchDecision(id, confidence)
            : null;
    }

    private static string Describe(MatchCandidate c)
    {
        var bg = string.IsNullOrEmpty(c.NameBg) || c.NameBg == c.Name ? "" : $" / {c.NameBg}";
        var brand = string.IsNullOrEmpty(c.Brand) ? "" : $" ({c.Brand})";
        return string.Create(CultureInfo.InvariantCulture, $"{c.Name}{bg}{brand}: {c.Per100.Kcal:0} kcal, {c.Per100.Protein:0.#} g protein per 100 g");
    }
}
