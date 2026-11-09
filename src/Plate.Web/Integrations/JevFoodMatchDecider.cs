using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Plate.Core.Nutrition;
using Plate.Core.Text;

namespace Plate.Web.Integrations;

/// <summary>
/// Asks TypeSafe's Jev decision model, through OpenRouter's Decisions API, which shortlisted database food each
/// parsed item is (SPEC 6.1). One request per parse, one Choice question per item. The wording below is the one
/// measured in the Phase 0 evaluation; changing it means measuring again.
/// </summary>
public class JevFoodMatchDecider(HttpClient http, ILogger<JevFoodMatchDecider> log)
{
    public const string Jev113 = "typesafe/jev-1.13";

    /// <summary>Per call; Phase 0 measured at most about 1 s. No retry: Rank's pick is the fallback.</summary>
    internal TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(3);

    private static readonly MatchQuestion TestQuestion = new(
        new ParsedFoodItem("banana", "banana", 120, FoodState.NotApplicable, null, null, new Per100g(89, 1.1m, 23, 0.3m)),
        [new MatchCandidate(1, "Bananas, raw", null, null, new Per100g(89, 1.1m, 23, 0.3m)), new MatchCandidate(2, "Pepper, banana, raw", null, null, new Per100g(27, 1.7m, 5.4m, 0.5m))]);

    /// <summary>One decision per question, or null on any failure; logging food never waits on or fails because of Jev.</summary>
    public async Task<MatchDecisions?> DecideAsync(string text, IReadOnlyList<MatchQuestion> questions, string apiKey, string model, CancellationToken ct) =>
        (await CallAsync(text, questions, apiKey, model, ct)).Decisions;

    /// <summary>Checks a key and model with one question ("a banana"). Null when it works, otherwise what went wrong.</summary>
    public async Task<LocalText?> TestAsync(string apiKey, string model, CancellationToken ct)
    {
        var (decisions, error) = await CallAsync("a banana", [TestQuestion], apiKey, model, ct);
        return error ?? (decisions?.Items[0]?.FoodItemId == 1 ? null : new LocalText("The food matcher answered, but did not pick the banana for \"a banana\"."));
    }

    private async Task<(MatchDecisions? Decisions, LocalText? Error)> CallAsync(string text, IReadOnlyList<MatchQuestion> questions, string apiKey, string model, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        var clock = Stopwatch.StartNew();
        try
        {
            // Relative to the OpenRouter base URL (…/api/v1/), so tests can point it at a stub.
            using var request = new HttpRequestMessage(HttpMethod.Post, "../alpha/decisions") { Content = JsonContent.Create(Body(model, text, questions)) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var response = await http.SendAsync(request, timeout.Token);
            var json = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                log.LogWarning("Jev returned {Status} for {Model}: {Body}", (int)response.StatusCode, model, json.Length > 500 ? json[..500] : json);
                return (null, response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new LocalText("OpenRouter rejected the API key ({0}).", (int)response.StatusCode),
                    HttpStatusCode.PaymentRequired => new LocalText("The OpenRouter account is out of credits."),
                    _ => new LocalText("The food matcher can't be reached right now ({0}).", (int)response.StatusCode),
                });
            }

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(json);
            }
            catch (JsonException)
            {
                node = null;
            }

            if (Read(node, questions) is not { } decisions)
            {
                log.LogWarning("Jev returned no answers for {Model}", model);
                return (null, new LocalText("The food matcher returned something unreadable."));
            }

            var usage = (node as JsonObject)?["usage"] as JsonObject;
            log.LogInformation("Jev {Model} decided {Count} items in {Ms} ms, {Tokens} input tokens, ${Cost}",
                decisions.Model, questions.Count, clock.ElapsedMilliseconds, usage?["input_tokens"]?.ToString(), usage?["cost"]?.ToString());
            return (decisions, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("Jev took longer than {Timeout} for {Model}", Timeout, model);
            return (null, new LocalText("The food matcher took too long."));
        }
        catch (HttpRequestException ex)
        {
            log.LogWarning(ex, "Jev request failed");
            return (null, new LocalText("The food matcher can't be reached right now ({0}).", ex.StatusCode is { } s ? (int)s : 0));
        }
    }

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
        // Shapes are checked before indexing: indexing a JSON array or value by name throws.
        if (response is not JsonObject root || root["answers"] is not JsonObject answers)
        {
            return null;
        }

        var items = new List<MatchDecision?>();
        for (var n = 0; n < questions.Count; n++)
        {
            items.Add(answers[$"i{n}"] is JsonObject answer ? Decision(answer, questions[n]) : null);
        }

        return new MatchDecisions(items, root["model"] is JsonValue m && m.TryGetValue<string>(out var model) ? model : "");
    }

    /// <summary>An item without candidates was never asked, so any answer for it is ignored.</summary>
    private static MatchDecision? Decision(JsonObject answer, MatchQuestion question)
    {
        if (question.Candidates.Count == 0 || answer["choice"] is not JsonValue c || !c.TryGetValue<string>(out var choice)
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
