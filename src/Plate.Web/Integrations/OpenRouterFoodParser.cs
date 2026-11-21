using Plate.Core.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Plate.Core.Nutrition;

namespace Plate.Web.Integrations;

public class OpenRouterOptions
{
    public const string DefaultModel = "openai/gpt-6-luna";

    public string ApiKey { get; set; } = "";

    /// <summary>Used until a model is picked in Settings.</summary>
    public string Model { get; set; } = DefaultModel;

    public string BaseUrl { get; set; } = "https://openrouter.ai/api/v1/";
}

/// <summary>
/// Reads foods and portions from free text with an OpenRouter chat completion and strict structured JSON output
/// (SPEC 6.1). The model's nutrition numbers are only a fallback; the caller looks foods up in the database.
/// </summary>
public class OpenRouterFoodParser(HttpClient http, OpenRouterModels catalog, ILogger<OpenRouterFoodParser> log)
{
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(15);

    private const string Instructions = """
        You turn a short food diary entry into a list of foods with portion sizes.
        The text is in English or Bulgarian. Return one item per distinct food.
        A baked good, pastry or ready-made product named by its filling or topping is one food, not its ingredients:
        "закуска с лютеница и сирене", "баница със сирене", "кроасан с шоколад", "pizza with ham". Keep the shop or brand
        the user named in nameOriginal ("закуска с лютеница и сирене от Lidl"). Foods the user put together themselves
        ("хляб с масло и сирене", "rice with chicken") are still one item per food.
        - nameEn: a plain English name as a nutrition database would list it, e.g. "chicken breast, skinless", "white rice", "banana".
          Include what changes the nutrition: skinless for poultry unless the text mentions skin, fat level for dairy.
        - nameOriginal: the food's name as the user wrote it, in their language, without the amount or unit
          ("пилешко филе", not "200 г пилешко филе").
        - grams: the edible weight. Estimate vague amounts ("a banana", "a plate of rice", "една филия") and explain in assumption.
        - amountGiven: true when the text gives this food's amount as a weight, volume, count or fraction ("200 g", "2 eggs",
          "half a pack", "две филии"); false when it gives no amount ("извара", "some rice", "a banana": an article alone is not an amount).
        - gramsStated: true when the text gives this food's weight or volume ("200 g", "165 g total", "1 литър"), so grams is
          that amount; false when grams is your estimate. A stated weight wins over any count.
        - count and countUnit: when the text counts this food instead of weighing it, how many and of what: "piece" for items
          ("две закуски" 2, "2 eggs" 2, "a banana" 1, "една филия" 1), "pack" for a whole package, tub, bottle or can
          ("половин кофичка кисело мляко" 0.5, "a can of tuna" 1), "serving" for a serving or portion ("2 servings" 2).
          Fractions are decimals (half 0.5). Both null when the text gives a weight or volume or no count ("200 г", "some rice").
          Still estimate grams for the count; the database's piece or pack weight replaces it when it has one.
        - state: "raw" or "cooked" for meat, fish, rice, pasta, grains and legumes; otherwise "na".
          Assume raw weight for meat and fish and cooked weight for rice and pasta unless the text says otherwise.
        - preparation: how it was cooked, only if the text says so, in the language of the text
          (e.g. "на скара", "grilled", "пържено в олио"); otherwise null.
        - assumption: a short note on anything you assumed, or null.
        - kcalPer100g, proteinPer100g, carbsPer100g, fatPer100g: standard nutrition database values for that food and state.
        - fiberPer100g, saturatedFatPer100g: the same, or null when you don't know them.
        Never invent foods that are not in the text. If the text contains no food, return an empty list.
        """;

    // The user reads the assumption on the confirmation list, so it is written in their interface language.
    private static string AssumptionLanguage() =>
        Features.Localization.Localizer.IsBulgarian
            ? "Write assumption in natural Bulgarian, as a person would say it (\"приех една средна банана, 120 г\")."
            : "Write assumption in English.";

    private static readonly JsonObject Schema = JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "items": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "nameEn": { "type": "string" },
                  "nameOriginal": { "type": "string" },
                  "grams": { "type": "number" },
                  "amountGiven": { "type": "boolean" },
                  "gramsStated": { "type": "boolean" },
                  "count": { "type": ["number", "null"] },
                  "countUnit": { "type": ["string", "null"], "enum": ["piece", "pack", "serving", null] },
                  "state": { "type": "string", "enum": ["raw", "cooked", "na"] },
                  "preparation": { "type": ["string", "null"] },
                  "assumption": { "type": ["string", "null"] },
                  "kcalPer100g": { "type": "number" },
                  "proteinPer100g": { "type": "number" },
                  "carbsPer100g": { "type": "number" },
                  "fatPer100g": { "type": "number" },
                  "fiberPer100g": { "type": ["number", "null"] },
                  "saturatedFatPer100g": { "type": ["number", "null"] }
                },
                "required": ["nameEn", "nameOriginal", "grams", "amountGiven", "gramsStated", "count", "countUnit", "state", "preparation", "assumption", "kcalPer100g", "proteinPer100g", "carbsPer100g", "fatPer100g", "fiberPer100g", "saturatedFatPer100g"],
                "additionalProperties": false
              }
            }
          },
          "required": ["items"],
          "additionalProperties": false
        }
        """)!.AsObject();

    /// <summary>Checks a key and model with a one-line request. Returns null when it works, otherwise what went wrong.</summary>
    public async Task<LocalText?> TestAsync(string apiKey, string model, CancellationToken ct)
    {
        var result = await ParseAsync("1 banana", apiKey, model, ct);
        return result.Items.Count > 0 ? null : result.Error ?? new LocalText("The model returned no food for \"1 banana\".");
    }

    public async Task<FoodParseResult> ParseAsync(string text, string apiKey, string model, CancellationToken ct)
    {
        JsonObject? body = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(AttemptTimeout);
            try
            {
                body ??= await BodyAsync(text, model, timeout.Token);
                using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions") { Content = JsonContent.Create(body) };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                using var response = await http.SendAsync(request, timeout.Token);
                if ((int)response.StatusCode >= 500 && attempt == 1)
                {
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    log.LogWarning("OpenRouter returned {Status} for {Model}: {Body}", (int)response.StatusCode, model, Truncate(await response.Content.ReadAsStringAsync(timeout.Token)));
                    return FoodParseResult.Failed(response.StatusCode switch
                    {
                        HttpStatusCode.TooManyRequests => new LocalText("The food parser is busy. Try again in a minute, or enter the food by hand."),
                        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                            new LocalText("OpenRouter rejected the API key ({0}). Check it in Settings, or enter the food by hand.", (int)response.StatusCode),
                        HttpStatusCode.PaymentRequired => new LocalText("The OpenRouter account is out of credits. Top it up, or enter the food by hand."),
                        HttpStatusCode.NotFound => new LocalText("The model {0} can't parse food right now. Pick another one in Settings, or enter the food by hand.", model),
                        _ => new LocalText("The food parser could not read that. Enter the food by hand."),
                    });
                }

                return Read(await response.Content.ReadFromJsonAsync<JsonNode>(timeout.Token), model);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && attempt == 1)
            {
                // Timed out: one retry.
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return FoodParseResult.Failed("The food parser took too long. Try again, or enter the food by hand.");
            }
            catch (HttpRequestException ex)
            {
                log.LogWarning(ex, "OpenRouter request failed");
                if (attempt == 2)
                {
                    return FoodParseResult.Failed("The food parser is unreachable. Enter the food by hand.");
                }
            }
        }

        return FoodParseResult.Failed("The food parser is unavailable. Enter the food by hand.");
    }

    /// <summary>
    /// require_parameters makes OpenRouter use only providers that honour every parameter sent, so temperature and
    /// reasoning effort are sent only when the catalog says the model takes them.
    /// </summary>
    private async Task<JsonObject> BodyAsync(string text, string model, CancellationToken ct)
    {
        var info = await catalog.FindAsync(model, ct);
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = Instructions + AssumptionLanguage() },
                new JsonObject { ["role"] = "user", ["content"] = text }),
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject { ["name"] = "food_items", ["strict"] = true, ["schema"] = Schema.DeepClone() },
            },
            ["provider"] = new JsonObject { ["require_parameters"] = true },
        };
        if (info?.SupportsTemperature == true)
        {
            body["temperature"] = 0.2;
        }

        if (info?.LowestEffort is { } effort)
        {
            body["reasoning"] = new JsonObject { ["effort"] = effort };
        }

        return body;
    }

    /// <summary>Pulls the JSON text out of the first choice and validates each item.</summary>
    internal static FoodParseResult Read(JsonNode? response, string model)
    {
        var json = response?["choices"]?[0]?["message"]?["content"] is JsonValue content && content.TryGetValue<string>(out var c) ? c : null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return FoodParseResult.Failed("The food parser returned nothing. Enter the food by hand.");
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return FoodParseResult.Failed("The food parser returned something unreadable. Enter the food by hand.");
        }

        var items = new List<ParsedFoodItem>();
        var rejected = 0;
        foreach (var node in parsed?["items"]?.AsArray() ?? [])
        {
            if (node is null)
            {
                continue;
            }

            var stated = node["gramsStated"] is JsonValue s && s.TryGetValue<bool>(out var st) && st;
            // A count is read only without a stated weight, and only when it says what it counts.
            var unit = stated ? null : Str(node["countUnit"]) switch { "piece" => CountUnit.Piece, "pack" => CountUnit.Pack, "serving" => CountUnit.Serving, _ => (CountUnit?)null };
            var count = unit is null ? null : Num(node["count"]) is var n && n is > 0 and <= 100 ? n : (decimal?)null;
            var item = new ParsedFoodItem(
                Str(node["nameEn"]) ?? "",
                Str(node["nameOriginal"]) ?? Str(node["nameEn"]) ?? "",
                Num(node["grams"]),
                Str(node["state"]) switch { "raw" => FoodState.Raw, "cooked" => FoodState.Cooked, _ => FoodState.NotApplicable },
                Str(node["preparation"]),
                Str(node["assumption"]),
                Estimate(node),
                Units: count,
                AmountGiven: stated || (node["amountGiven"] is JsonValue given && given.TryGetValue<bool>(out var g) ? g : true),
                CountOf: count is null ? null : unit,
                GramsStated: stated);

            if (item.NameEn.Length == 0 || FoodMath.Validate(item.Grams, item.Estimated) is not null)
            {
                rejected++;
                continue;
            }

            items.Add(item);
        }

        var error = rejected > 0
            ? LocalText.Count(rejected, "Skipped 1 item with impossible amounts or values.", "Skipped {0} items with impossible amounts or values.")
            : null;
        if (items.Count == 0)
        {
            return FoodParseResult.Failed(error ?? "No food found in that text.");
        }

        return new FoodParseResult(items, error, model);
    }

    /// <summary>
    /// The model's per-100 g values. Fiber and saturated fat are optional: an impossible one is dropped as unknown
    /// rather than rejecting the food.
    /// </summary>
    private static Per100g Estimate(JsonNode node)
    {
        var fat = Num(node["fatPer100g"]);
        var fiber = Num(node["fiberPer100g"]) is var f && f is >= 0 and <= 100 ? f : (decimal?)null;
        var saturated = Num(node["saturatedFatPer100g"]) is var sf && sf >= 0 && sf <= fat + 0.5m ? sf : (decimal?)null;
        return new Per100g(Num(node["kcalPer100g"]), Num(node["proteinPer100g"]), Num(node["carbsPer100g"]), fat, fiber, saturated);
    }

    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    private static decimal Num(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<double>(out var d) ? Math.Round((decimal)d, 2) : -1;

    private static string Truncate(string s) => s.Length > 500 ? s[..500] : s;
}
