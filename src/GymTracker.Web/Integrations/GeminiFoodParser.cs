using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using GymTracker.Core.Nutrition;
using Microsoft.Extensions.Options;

namespace GymTracker.Web.Integrations;

public class GeminiOptions
{
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gemini-3.5-flash-lite";
    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/";

    /// <summary>MINIMAL keeps parsing fast and cheap; empty sends no thinking setting.</summary>
    public string ThinkingLevel { get; set; } = "MINIMAL";
}

/// <summary>
/// Reads foods and portions from free text with Gemini's generateContent and structured JSON output
/// (SPEC 6.1). The model's nutrition numbers are only a fallback; the caller looks foods up in the database.
/// </summary>
public class GeminiFoodParser(HttpClient http, IOptions<GeminiOptions> options, ILogger<GeminiFoodParser> log) : IFoodParser
{
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(15);

    private const string Instructions = """
        You turn a short food diary entry into a list of foods with portion sizes.
        The text is in English or Bulgarian. Return one item per distinct food.
        - nameEn: a plain English name as a nutrition database would list it, e.g. "chicken breast", "white rice", "banana".
        - nameOriginal: the food as the user wrote it.
        - grams: the edible weight. Estimate vague amounts ("a banana", "a plate of rice", "една филия") and explain in assumption.
        - state: "raw" or "cooked" for meat, fish, rice, pasta, grains and legumes; otherwise "na".
          Assume raw weight for meat and fish and cooked weight for rice and pasta unless the text says otherwise.
        - preparation: e.g. "grilled", "fried in oil", or null.
        - assumption: a short note on anything you assumed, or null.
        - kcalPer100g, proteinPer100g, carbsPer100g, fatPer100g: standard nutrition database values for that food and state.
        Never invent foods that are not in the text. If the text contains no food, return an empty list.
        """;

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
                  "grams": { "type": "number", "minimum": 0 },
                  "state": { "type": "string", "enum": ["raw", "cooked", "na"] },
                  "preparation": { "type": ["string", "null"] },
                  "assumption": { "type": ["string", "null"] },
                  "kcalPer100g": { "type": "number", "minimum": 0 },
                  "proteinPer100g": { "type": "number", "minimum": 0 },
                  "carbsPer100g": { "type": "number", "minimum": 0 },
                  "fatPer100g": { "type": "number", "minimum": 0 }
                },
                "required": ["nameEn", "nameOriginal", "grams", "state", "kcalPer100g", "proteinPer100g", "carbsPer100g", "fatPer100g"]
              }
            }
          },
          "required": ["items"]
        }
        """)!.AsObject();

    public async Task<FoodParseResult> ParseAsync(string text, CancellationToken ct)
    {
        var o = options.Value;
        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = Instructions }) },
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = text }),
            }),
            ["generationConfig"] = GenerationConfig(o),
        };

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(AttemptTimeout);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"v1beta/models/{Uri.EscapeDataString(o.Model)}:generateContent")
                {
                    Content = JsonContent.Create(body),
                };
                request.Headers.Add("x-goog-api-key", o.ApiKey);

                using var response = await http.SendAsync(request, timeout.Token);
                if ((int)response.StatusCode >= 500 && attempt == 1)
                {
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    log.LogWarning("Gemini returned {Status}: {Body}", (int)response.StatusCode, Truncate(await response.Content.ReadAsStringAsync(ct)));
                    return FoodParseResult.Failed(response.StatusCode == HttpStatusCode.TooManyRequests
                        ? "The food parser is busy. Try again in a minute, or enter the food by hand."
                        : "The food parser could not read that. Enter the food by hand.");
                }

                return Read(await response.Content.ReadFromJsonAsync<JsonNode>(timeout.Token), o.Model);
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
                log.LogWarning(ex, "Gemini request failed");
                if (attempt == 2)
                {
                    return FoodParseResult.Failed("The food parser is unreachable. Enter the food by hand.");
                }
            }
        }

        return FoodParseResult.Failed("The food parser is unavailable. Enter the food by hand.");
    }

    private static JsonObject GenerationConfig(GeminiOptions o)
    {
        var config = new JsonObject
        {
            ["responseMimeType"] = "application/json",
            ["responseJsonSchema"] = Schema.DeepClone(),
            ["temperature"] = 0.2,
        };
        if (!string.IsNullOrEmpty(o.ThinkingLevel))
        {
            config["thinkingConfig"] = new JsonObject { ["thinkingLevel"] = o.ThinkingLevel };
        }

        return config;
    }

    /// <summary>Pulls the JSON text out of the first candidate and validates each item.</summary>
    internal static FoodParseResult Read(JsonNode? response, string model)
    {
        var json = response?["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.GetValue<string>();
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

            var item = new ParsedFoodItem(
                Str(node["nameEn"]) ?? "",
                Str(node["nameOriginal"]) ?? Str(node["nameEn"]) ?? "",
                Num(node["grams"]),
                Str(node["state"]) switch { "raw" => FoodState.Raw, "cooked" => FoodState.Cooked, _ => FoodState.NotApplicable },
                Str(node["preparation"]),
                Str(node["assumption"]),
                new Per100g(Num(node["kcalPer100g"]), Num(node["proteinPer100g"]), Num(node["carbsPer100g"]), Num(node["fatPer100g"])));

            if (item.NameEn.Length == 0 || FoodMath.Validate(item.Grams, item.Estimated) is not null)
            {
                rejected++;
                continue;
            }

            items.Add(item);
        }

        var error = rejected > 0 ? $"Skipped {rejected} item(s) with impossible amounts or values." : null;
        if (items.Count == 0)
        {
            return FoodParseResult.Failed(error ?? "No food found in that text.");
        }

        return new FoodParseResult(items, error, model);
    }

    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    private static decimal Num(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<double>(out var d) ? Math.Round((decimal)d, 2) : -1;

    private static string Truncate(string s) => s.Length > 500 ? s[..500] : s;
}
