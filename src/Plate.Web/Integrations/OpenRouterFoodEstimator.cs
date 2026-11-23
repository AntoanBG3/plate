using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Plate.Core.Nutrition;
using Plate.Core.Text;

namespace Plate.Web.Integrations;

/// <summary>
/// Estimates values per 100 g for the foods no database food matched, with a model of its own (SPEC 6.1 step 6). Unlike
/// the parser, which guesses every food's values before matching, it sees the closest database foods and reasons about
/// one food at a time. One call per parse, no retry: on any failure the parser's own guesses stand.
/// </summary>
public class OpenRouterFoodEstimator(HttpClient http, OpenRouterModels catalog, ILogger<OpenRouterFoodEstimator> log)
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(12);

    private const string Instructions = """
        You estimate nutrition values for foods that aren't in a nutrition database.
        You get everything the user wrote for the meal, and for each food: what the user called it, an English name, the
        portion in grams, whether it is raw or cooked, how it was prepared, and the closest foods the database has.
        - Give values per 100 g of the food as eaten: kcal, protein, carbs, fat, fiber and saturated fat, in grams.
        - Think of the usual recipe or product. A Bulgarian name means a food as made or sold in Bulgaria; a shop or
          brand means that shop's or brand's product.
        - The database foods were judged not to be this food. Use one only as a reference when it is a close relative
          (the same dish from another shop, another filling or flavour, the same food prepared another way); ignore the rest.
        - kcal must agree with 4 kcal per gram of protein and carbs and 9 per gram of fat, within 10%.
        - Use null for fiber or saturated fat only when you can't tell at all.
        Return one entry per food, with the food's index.
        """;

    private static readonly JsonObject Schema = JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "estimates": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "index": { "type": "integer" },
                  "kcal": { "type": "number" },
                  "protein": { "type": "number" },
                  "carbs": { "type": "number" },
                  "fat": { "type": "number" },
                  "fiber": { "type": ["number", "null"] },
                  "saturatedFat": { "type": ["number", "null"] }
                },
                "required": ["index", "kcal", "protein", "carbs", "fat", "fiber", "saturatedFat"],
                "additionalProperties": false
              }
            }
          },
          "required": ["estimates"],
          "additionalProperties": false
        }
        """)!.AsObject();

    // Cyrillic as itself, not \u escapes: fewer tokens and easier for the model to read.
    private static readonly JsonSerializerOptions Readable = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Estimates a Lidl pastry with the given model: null when it answers with possible values, otherwise why not.</summary>
    public async Task<LocalText?> TestAsync(string apiKey, string model, CancellationToken ct, string? effort = null)
    {
        var sample = new EstimateRequest("закуска с лютеница и сирене от Лидл", "pastry with lyutenitsa and cheese from Lidl", 90, FoodState.Cooked, "baked", []);
        var answer = await EstimateAsync("закуска с лютеница и сирене от Лидл", [sample], apiKey, model, ct, effort);
        return FoodEstimating.Accept(answer?.Values.FirstOrDefault()) is null
            ? new LocalText("The model gave no usable estimate for a sample food.")
            : null;
    }

    public async Task<FoodEstimates?> EstimateAsync(string text, IReadOnlyList<EstimateRequest> items, string apiKey, string model, CancellationToken ct, string? effort = null)
    {
        if (items.Count == 0)
        {
            return new FoodEstimates([], model);
        }

        var clock = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            var body = await BodyAsync(text, items, model, effort, timeout.Token);
            using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions") { Content = JsonContent.Create(body) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var response = await http.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                log.LogWarning("Food estimator: OpenRouter returned {Status} for {Model}", (int)response.StatusCode, model);
                return null;
            }

            var node = await response.Content.ReadFromJsonAsync<JsonNode>(timeout.Token);
            var usage = node?["usage"] as JsonObject;
            log.LogInformation("Food estimator {Model} estimated {Count} items in {Ms} ms, {Input} input and {Output} output tokens, ${Cost}",
                model, items.Count, clock.ElapsedMilliseconds, usage?["prompt_tokens"]?.ToString(), usage?["completion_tokens"]?.ToString(), usage?["cost"]?.ToString());
            return Read(node, items.Count) is { } values ? new FoodEstimates(values, model) : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("Food estimator: {Model} took longer than {Seconds} s", model, Timeout.TotalSeconds);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            log.LogWarning(ex, "Food estimator request failed");
            return null;
        }
    }

    private async Task<JsonObject> BodyAsync(string text, IReadOnlyList<EstimateRequest> items, string model, string? effort, CancellationToken ct)
    {
        var foods = new JsonArray(items.Select((item, n) => (JsonNode)new JsonObject
        {
            ["index"] = n,
            ["userTyped"] = item.Typed,
            ["englishName"] = item.NameEn,
            ["grams"] = item.Grams,
            ["state"] = item.State switch { FoodState.Raw => "raw", FoodState.Cooked => "cooked", _ => null },
            ["preparation"] = item.Preparation,
            ["closestDatabaseFoods"] = new JsonArray(item.Nearby.Select(f => (JsonNode)new JsonObject
            {
                ["name"] = f.Name,
                ["kcal"] = f.Per100.Kcal,
                ["protein"] = f.Per100.Protein,
                ["carbs"] = f.Per100.Carbs,
                ["fat"] = f.Per100.Fat,
            }).ToArray()),
        }).ToArray());
        var input = new JsonObject { ["userWrote"] = text, ["foods"] = foods };
        var info = await catalog.FindAsync(model, ct);
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = Instructions },
                new JsonObject { ["role"] = "user", ["content"] = input.ToJsonString(Readable) }),
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject { ["name"] = "food_estimates", ["strict"] = true, ["schema"] = Schema.DeepClone() },
            },
            ["provider"] = new JsonObject { ["require_parameters"] = true },
        };
        if (info?.SupportsTemperature == true)
        {
            body["temperature"] = 0.2;
        }

        if (info?.EffortFor(effort) is { } chosen)
        {
            body["reasoning"] = new JsonObject { ["effort"] = chosen };
        }

        return body;
    }

    /// <summary>One answer per item in order; an index out of range or repeated is ignored. Null when nothing is readable.</summary>
    internal static IReadOnlyList<Per100g?>? Read(JsonNode? response, int count)
    {
        var json = response?["choices"]?[0]?["message"]?["content"] is JsonValue content && content.TryGetValue<string>(out var c) ? c : null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (parsed?["estimates"] is not JsonArray estimates)
        {
            return null;
        }

        var result = new Per100g?[count];
        foreach (var node in estimates)
        {
            if (node?["index"] is JsonValue i && i.TryGetValue<int>(out var index) && index >= 0 && index < count && result[index] is null
                && Num(node["kcal"]) is { } kcal && Num(node["protein"]) is { } protein && Num(node["carbs"]) is { } carbs && Num(node["fat"]) is { } fat)
            {
                result[index] = new Per100g(R(kcal), R(protein), R(carbs), R(fat), Num(node["fiber"]) is { } fiber ? R(fiber) : null,
                    Num(node["saturatedFat"]) is { } saturated ? R(saturated) : null);
            }
        }

        return result;
    }

    private static decimal? Num(JsonNode? node) => node is JsonValue v && v.TryGetValue<decimal>(out var d) ? d : null;

    private static decimal R(decimal value) => Math.Round(value, 1);
}
