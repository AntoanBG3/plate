using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using GymTracker.Core.Nutrition;

namespace GymTracker.Web.Integrations;

/// <summary>
/// Writes a short everyday name for each matched food from its database name, with the parser's model (SPEC 6.1).
/// USDA names run long ("Chicken, broiler or fryers, breast, skinless, boneless, meat only, raw"); the entry gets
/// "Chicken breast, skinless, raw". One call per parse, no retry: on any failure the database name stands.
/// </summary>
public class OpenRouterFoodNamer(HttpClient http, OpenRouterModels catalog, ILogger<OpenRouterFoodNamer> log)
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private const string Instructions = """
        You shorten nutrition database food names into names a person would write in a food diary.
        For each item you get the database name, and sometimes its English name for reference, plus what the user typed.
        - Write 2 to 6 words, at most 60 characters, in the language given below.
        - Keep what identifies the food and its nutrition, exactly as the database name says: the food, the cut or part,
          skin, fat level, and raw or cooked or the cooking method.
        - Drop what a person wouldn't say: "broiler or fryers", "meat only", "boneless", "enriched", "regular", USDA codes.
        - Never add anything the database name doesn't say. The user's text is only context: if it says "grilled" but
          the database name says "braised", the name says braised.
        - No amounts or weights.
        Return one name per item, with the item's index.
        """;

    private static readonly JsonObject Schema = JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "names": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "index": { "type": "integer" },
                  "name": { "type": "string" }
                },
                "required": ["index", "name"],
                "additionalProperties": false
              }
            }
          },
          "required": ["names"],
          "additionalProperties": false
        }
        """)!.AsObject();

    // Cyrillic as itself, not \u escapes: fewer tokens and easier for the model to read.
    private static readonly JsonSerializerOptions Readable = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public async Task<IReadOnlyList<string?>?> NameAsync(IReadOnlyList<NameRequest> items, string apiKey, string model, CancellationToken ct)
    {
        if (items.Count == 0)
        {
            return [];
        }

        var clock = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions") { Content = JsonContent.Create(await BodyAsync(items, model, ct)) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var response = await http.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                log.LogWarning("Food namer: OpenRouter returned {Status} for {Model}", (int)response.StatusCode, model);
                return null;
            }

            var node = await response.Content.ReadFromJsonAsync<JsonNode>(timeout.Token);
            var usage = node?["usage"] as JsonObject;
            log.LogInformation("Food namer {Model} named {Count} items in {Ms} ms, {Input} input and {Output} output tokens, ${Cost}",
                model, items.Count, clock.ElapsedMilliseconds, usage?["prompt_tokens"]?.ToString(), usage?["completion_tokens"]?.ToString(), usage?["cost"]?.ToString());
            return Read(node, items.Count);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("Food namer: {Model} took longer than {Seconds} s", model, Timeout.TotalSeconds);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            log.LogWarning(ex, "Food namer request failed");
            return null;
        }
    }

    private async Task<JsonObject> BodyAsync(IReadOnlyList<NameRequest> items, string model, CancellationToken ct)
    {
        var language = Features.Localization.Localizer.IsBulgarian ? "Bulgarian" : "English";
        var list = new JsonArray(items.Select((item, n) => (JsonNode)new JsonObject
        {
            ["index"] = n,
            ["databaseName"] = item.DatabaseName,
            ["englishName"] = item.DatabaseNameEn,
            ["userTyped"] = item.Typed,
        }).ToArray());
        var info = await catalog.FindAsync(model, ct);
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = Instructions + $"Language: {language}." },
                new JsonObject { ["role"] = "user", ["content"] = list.ToJsonString(Readable) }),
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject { ["name"] = "food_names", ["strict"] = true, ["schema"] = Schema.DeepClone() },
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

    /// <summary>One name per item in order; an index out of range or repeated is ignored. Null when nothing is readable.</summary>
    internal static IReadOnlyList<string?>? Read(JsonNode? response, int count)
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

        if (parsed?["names"] is not JsonArray names)
        {
            return null;
        }

        var result = new string?[count];
        foreach (var node in names)
        {
            if (node?["index"] is JsonValue i && i.TryGetValue<int>(out var index) && index >= 0 && index < count && result[index] is null
                && node["name"] is JsonValue v && v.TryGetValue<string>(out var name))
            {
                result[index] = name;
            }
        }

        return result;
    }
}
