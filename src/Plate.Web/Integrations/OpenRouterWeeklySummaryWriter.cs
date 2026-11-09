using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Plate.Core.Domain;
using Plate.Core.Summary;
using Plate.Core.Text;

namespace Plate.Web.Integrations;

/// <summary>
/// Writes the weekly summary (SPEC 6.5): an OpenRouter chat completion with strict structured output, sent only the
/// week's numbers. Request shape, timeout and retry follow the setup chat.
/// </summary>
public class OpenRouterWeeklySummaryWriter(HttpClient http, OpenRouterModels catalog, ILogger<OpenRouterWeeklySummaryWriter> log)
{
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(20);

    private const string Instructions = """
        You write a short weekly check-in for a user of Plate, a personal gym and nutrition tracker. You get last
        week's numbers as JSON and return a summary, tips and one line of encouragement, shown on their home screen.

        - summary: two or three plain sentences on how the week went against their goal, naming the most telling
          numbers (for example "you logged 6 days and averaged 2,050 kcal against 2,060"). Never invent a number.
        - tips: up to three specific things to do next week, each one short sentence, drawn from the weakest numbers:
          protein days under the minimum (for example add a protein food at breakfast), fat under its floor, fiber under
          its minimum, few logged or weighed days, no or fewer workouts. If everything is on track, one tip to keep it up.
        - motivation: one short, warm line. Specific beats generic; no clichés.
        - Leave out anything that is null or missing in the facts.
        - Describe weight changes neutrally against the goal and rate. Never praise or blame the body, appearance or
          weight itself, and never give medical advice.
        - No markdown, no emojis, no lists inside strings.
        """;

    private static readonly JsonObject Schema = JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "summary": { "type": "string" },
            "tips": { "type": "array", "items": { "type": "string" } },
            "motivation": { "type": "string" }
          },
          "required": ["summary", "tips", "motivation"],
          "additionalProperties": false
        }
        """)!.AsObject();

    /// <summary>Writes a summary for a made-up week: null when the model answers, otherwise what went wrong.</summary>
    public async Task<LocalText?> TestAsync(string apiKey, string model, CancellationToken ct)
    {
        var monday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-7);
        var facts = new WeekFacts(monday, Goal.Maintain, 0, false, null, null, (80m, 79.8m), 5, 3, 48, ["Bench press: 102.5 kg (was 100)"], ["Chest", "Quads"]);
        return (await WriteAsync(facts, false, apiKey, model, ct)).Error;
    }

    public async Task<WeeklySummaryResult> WriteAsync(WeekFacts facts, bool bulgarian, string apiKey, string model, CancellationToken ct)
    {
        JsonObject? body = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(AttemptTimeout);
            try
            {
                body ??= await BodyAsync(facts, bulgarian, model, timeout.Token);
                using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions") { Content = JsonContent.Create(body) };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                using var response = await http.SendAsync(request, timeout.Token);
                if ((int)response.StatusCode >= 500 && attempt == 1)
                {
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    log.LogWarning("OpenRouter returned {Status} for {Model} writing a weekly summary: {Body}", (int)response.StatusCode, model, Truncate(await response.Content.ReadAsStringAsync(timeout.Token)));
                    return WeeklySummaryResult.Failed("The weekly summary isn't available right now.");
                }

                return Read(await response.Content.ReadFromJsonAsync<JsonNode>(timeout.Token), model);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && attempt == 1)
            {
                // Timed out: one retry.
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return WeeklySummaryResult.Failed("The weekly summary isn't available right now.");
            }
            catch (HttpRequestException ex)
            {
                log.LogWarning(ex, "OpenRouter request failed writing a weekly summary");
                if (attempt == 2)
                {
                    return WeeklySummaryResult.Failed("The weekly summary isn't available right now.");
                }
            }
        }

        return WeeklySummaryResult.Failed("The weekly summary isn't available right now.");
    }

    private async Task<JsonObject> BodyAsync(WeekFacts facts, bool bulgarian, string model, CancellationToken ct)
    {
        var info = await catalog.FindAsync(model, ct);
        var language = bulgarian ? "Write in natural, friendly Bulgarian, using the informal \"ти\"." : "Write in English.";
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = Instructions + "\n" + language },
                new JsonObject { ["role"] = "user", ["content"] = FactsJson(facts).ToJsonString() }),
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject { ["name"] = "weekly_summary", ["strict"] = true, ["schema"] = Schema.DeepClone() },
            },
            ["provider"] = new JsonObject { ["require_parameters"] = true },
        };
        if (info?.SupportsTemperature == true)
        {
            body["temperature"] = 0.6;
        }

        if (info?.LowestEffort is { } effort)
        {
            body["reasoning"] = new JsonObject { ["effort"] = effort };
        }

        return body;
    }

    /// <summary>The facts as the model sees them: plain numbers with units in the names, nulls for what is unknown.</summary>
    internal static JsonObject FactsJson(WeekFacts f)
    {
        var food = f.Food;
        var t = f.Targets;
        return new JsonObject
        {
            ["week"] = $"{f.Monday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} to {f.Sunday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}",
            ["goal"] = f.Goal.ToString().ToLowerInvariant(),
            ["targetRatePercentOfWeightPerWeek"] = f.Goal == Goal.Maintain ? null : f.RatePercentPerWeek,
            ["dietBreakAtMaintenance"] = f.DietBreak,
            ["food"] = food is null ? null : new JsonObject
            {
                ["loggedDays"] = food.LoggedDays,
                ["averageKcal"] = food.Kcal,
                ["averageProteinG"] = food.ProteinG,
                ["daysProteinReachedMinimum"] = food.ProteinMinDays,
                ["averageFatG"] = food.FatG,
                ["daysFatAtLeastFloor"] = food.FatFloorDays,
                ["averageCarbsG"] = food.CarbsG,
                ["averageFiberG"] = food.FiberG,
                ["averageSaturatedFatG"] = food.SaturatedFatG,
            },
            ["targets"] = t is null ? null : new JsonObject
            {
                ["proteinMinimumG"] = t.ProteinMinG,
                ["proteinAimG"] = t.ProteinAimG,
                ["fatG"] = $"{t.FatMinG}-{t.FatMaxG}",
                ["carbsG"] = $"{t.CarbsMinG}-{t.CarbsMaxG}",
                ["fiberMinimumG"] = t.FiberMinG,
                ["saturatedFatMaximumG"] = t.SaturatedFatMaxG,
                ["proteinPerMainMealG"] = t.MealProteinG,
            },
            ["body"] = new JsonObject
            {
                ["weighIns"] = f.WeighIns,
                ["trendWeightStartKg"] = f.TrendKg?.Start,
                ["trendWeightEndKg"] = f.TrendKg?.End,
            },
            ["training"] = new JsonObject
            {
                ["finishedWorkouts"] = f.Sessions,
                ["workingSets"] = f.WorkingSets,
                ["newRecords"] = new JsonArray(f.Records.Select(r => (JsonNode)r).ToArray()),
                ["mostTrainedMuscles"] = new JsonArray(f.TopMuscles.Select(m => (JsonNode)m).ToArray()),
            },
        };
    }

    internal static WeeklySummaryResult Read(JsonNode? response, string model)
    {
        var json = response?["choices"]?[0]?["message"]?["content"] is JsonValue content && content.TryGetValue<string>(out var c) ? c : null;
        JsonNode? parsed = null;
        try
        {
            parsed = string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json);
        }
        catch (System.Text.Json.JsonException)
        {
        }

        var text = WeeklySummaryText.Clean(
            Str(parsed?["summary"]),
            (parsed?["tips"] as JsonArray ?? []).Select(Str),
            Str(parsed?["motivation"]));
        return text is null
            ? WeeklySummaryResult.Failed("The weekly summary model returned something unreadable.")
            : new WeeklySummaryResult(text, model, null);
    }

    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static string Truncate(string s) => s.Length > 500 ? s[..500] : s;
}

/// <summary>The weekly model when a key is set and it isn't off; otherwise there is no weekly summary.</summary>
public class SwitchingWeeklySummaryWriter(OpenRouterSettings settings, IServiceProvider services) : IWeeklySummaryWriter
{
    public async Task<bool> IsAvailableAsync(CancellationToken ct) =>
        await settings.GetModelAsync(AiTask.Weekly, ct) != AiTasks.Off && await settings.GetKeyAsync(ct) is not null;

    public async Task<WeeklySummaryResult> WriteAsync(WeekFacts facts, bool bulgarian, CancellationToken ct)
    {
        var model = await settings.GetModelAsync(AiTask.Weekly, ct);
        if (model == AiTasks.Off || await settings.GetKeyAsync(ct) is not { } key)
        {
            return WeeklySummaryResult.Failed("The weekly summary isn't available right now.");
        }

        return await services.GetRequiredService<OpenRouterWeeklySummaryWriter>().WriteAsync(facts, bulgarian, key, model, ct);
    }
}
