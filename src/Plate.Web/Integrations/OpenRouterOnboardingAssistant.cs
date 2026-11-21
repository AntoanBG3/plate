using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Plate.Core.Domain;
using Plate.Core.Nutrition;
using Plate.Core.Onboarding;
using Plate.Core.Text;

namespace Plate.Web.Integrations;

/// <summary>
/// The setup chat's model (SPEC 6.4): an OpenRouter chat completion with strict structured output. Each turn gets
/// the draft the server holds, so the model only reads the latest answers and asks for what is missing; the values
/// it proposes are checked against the profile limits by the caller.
/// </summary>
public class OpenRouterOnboardingAssistant(HttpClient http, OpenRouterModels catalog, ILogger<OpenRouterOnboardingAssistant> log)
{
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Only the latest messages are sent; the draft carries everything said before them.</summary>
    public const int HistoryLines = 20;

    public const int MaxQuickReplies = 4;

    private const string Instructions = """
        You are the setup assistant of Plate, a personal gym and nutrition tracker. You are chatting with a new user
        on their phone to fill in their profile, so the app can estimate their maintenance calories and set daily targets.

        Style:
        - Warm, friendly and brief: at most three short sentences per message. No lists, no markdown, no emojis.
        - Ask for one or two things per message, in this order: name, sex, age, height, current weight, daily activity, goal.
        - When you ask for something, say in a few words why the app needs it (for example "height and weight go into the calorie estimate").
        - Values under "Known" were already given or are on file. Confirm them briefly in one sentence instead of asking again.
        - Never give medical advice and never comment on the user's body, weight or goal.
        - If the user asks something unrelated, answer in one sentence and come back to the setup.

        Reading answers:
        - Put every value the user states in their latest message into "fields". Leave a field null when the latest message
          doesn't state it; don't repeat known values.
        - Convert units: height to cm, weight to kg (1 lb = 0.4536 kg, 1 in = 2.54 cm, 1 ft = 30.48 cm).
          If the user gives an age instead of a birth year, birthYear is the current year minus the age.
        - sex is "male" or "female": it picks the energy equation. If the user would rather not say, explain that the
          formula needs one of the two and that it can be changed later in Settings.
        - goal is "cut" (lose fat), "maintain" or "bulk" (gain muscle). For cut or bulk, ask how fast, as a percent of
          bodyweight per week (ratePercentPerWeek): a cut at most 1.0, a bulk at most 0.5. If they are unsure, suggest 0.5
          for a cut and 0.25 for a bulk. For maintain, leave ratePercentPerWeek null.
        - activityLevel: classify from the user's job, daily steps and exercise:
          {ACTIVITY}
          The levels add up all movement on top of everyday life. Ask for the job, the usual daily steps (or minutes of
          walking) and any training. Minutes of activity are more reliable than steps; use them when the user gives them.
          Three or four one-hour lifting sessions a week add about as much as half an hour of brisk walking a day, about
          half a level: count lifting together with walking, never as a level by itself. A desk job under about 8,000
          steps a day is inactive even with regular lifting; with about 10,000 steps or more plus regular lifting it is
          low active.
          If you are unsure between two levels, ask one follow-up question; if still unsure, pick the lower one.
          Tell the user the level you picked in plain words, and that they can change it later.
        - proteinGramsPerKg (null means Auto: the app sets it by goal, 1.8 g per kg or 2.2 on a cut) and
          startingMaintenanceKcal (only if they know their maintenance from past tracking) are optional. Don't ask about them, but fill them in if the user brings them up.
        - quickReplies: up to four short answers the user might tap for your question, in the reply language
          (for example "Male", "Female"; or "Lose fat", "Maintain", "Build muscle"). Empty when free text fits better.
        - When "Still missing" is empty, tell the user everything is ready, that they can check the summary below and
          tap Save, and that they can still tell you to change anything.
        """;

    private static readonly JsonObject Schema = JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "reply": { "type": "string" },
            "quickReplies": { "type": "array", "items": { "type": "string" } },
            "fields": {
              "type": "object",
              "properties": {
                "name": { "type": ["string", "null"] },
                "sex": { "anyOf": [{ "type": "string", "enum": ["male", "female"] }, { "type": "null" }] },
                "birthYear": { "type": ["integer", "null"] },
                "heightCm": { "type": ["number", "null"] },
                "weightKg": { "type": ["number", "null"] },
                "activityLevel": { "anyOf": [{ "type": "string", "enum": ["inactive", "low_active", "active", "very_active"] }, { "type": "null" }] },
                "goal": { "anyOf": [{ "type": "string", "enum": ["cut", "maintain", "bulk"] }, { "type": "null" }] },
                "ratePercentPerWeek": { "type": ["number", "null"] },
                "proteinGramsPerKg": { "type": ["number", "null"] },
                "startingMaintenanceKcal": { "type": ["integer", "null"] }
              },
              "required": ["name", "sex", "birthYear", "heightCm", "weightKg", "activityLevel", "goal", "ratePercentPerWeek", "proteinGramsPerKg", "startingMaintenanceKcal"],
              "additionalProperties": false
            }
          },
          "required": ["reply", "quickReplies", "fields"],
          "additionalProperties": false
        }
        """)!.AsObject();

    private static readonly string[] ActivitySlugs = ["inactive", "low_active", "active", "very_active"];

    /// <summary>Opens a chat for a made-up new user: null when the model answers, otherwise what went wrong.</summary>
    public async Task<LocalText?> TestAsync(string apiKey, string model, CancellationToken ct, string? effort = null)
    {
        var draft = new OnboardingDraft { Name = "Test" };
        var turn = await NextAsync(new OnboardingContext(draft, draft.Missing(), [], [], false, DateTime.UtcNow.Year), apiKey, model, ct, effort);
        return turn.Error;
    }

    public async Task<OnboardingTurn> NextAsync(OnboardingContext context, string apiKey, string model, CancellationToken ct, string? effort = null)
    {
        JsonObject? body = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(AttemptTimeout);
            try
            {
                body ??= await BodyAsync(context, model, effort, timeout.Token);
                using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions") { Content = JsonContent.Create(body) };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                using var response = await http.SendAsync(request, timeout.Token);
                if ((int)response.StatusCode >= 500 && attempt == 1)
                {
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    log.LogWarning("OpenRouter returned {Status} for {Model} in the setup chat: {Body}", (int)response.StatusCode, model, Truncate(await response.Content.ReadAsStringAsync(timeout.Token)));
                    return OnboardingTurn.Failed(response.StatusCode switch
                    {
                        HttpStatusCode.TooManyRequests => new LocalText("The assistant is busy. Try again in a minute, or use the form."),
                        HttpStatusCode.PaymentRequired => new LocalText("The assistant is out of credits. Use the form for now."),
                        _ => new LocalText("The assistant isn't available right now. Use the form instead."),
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
                return OnboardingTurn.Failed("The assistant took too long. Try again, or use the form.");
            }
            catch (HttpRequestException ex)
            {
                log.LogWarning(ex, "OpenRouter request failed in the setup chat");
                if (attempt == 2)
                {
                    return OnboardingTurn.Failed("The assistant is unreachable. Use the form instead.");
                }
            }
        }

        return OnboardingTurn.Failed("The assistant isn't available right now. Use the form instead.");
    }

    /// <summary>Same parameter rules as the food parser: temperature and effort only when the model takes them.</summary>
    private async Task<JsonObject> BodyAsync(OnboardingContext context, string model, string? effort, CancellationToken ct)
    {
        var info = await catalog.FindAsync(model, ct);
        var messages = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = SystemPrompt(context) });
        foreach (var line in context.History.TakeLast(HistoryLines))
        {
            messages.Add(new JsonObject { ["role"] = line.FromUser ? "user" : "assistant", ["content"] = line.Text });
        }

        if (context.History.Count == 0)
        {
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = "(The user has just opened the setup chat. Greet them by name if known and ask for what is missing.)" });
        }

        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages,
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject { ["name"] = "setup_turn", ["strict"] = true, ["schema"] = Schema.DeepClone() },
            },
            ["provider"] = new JsonObject { ["require_parameters"] = true },
        };
        if (info?.SupportsTemperature == true)
        {
            body["temperature"] = 0.5;
        }

        if (info?.EffortFor(effort) is { } chosen)
        {
            body["reasoning"] = new JsonObject { ["effort"] = chosen };
        }

        return body;
    }

    internal static string SystemPrompt(OnboardingContext context)
    {
        var activity = string.Join("\n  ", Enum.GetValues<ActivityLevel>().Select(l => $"{ActivitySlugs[(int)l]}: {ActivityLevels.Description(l)}"));
        var prompt = new StringBuilder(Instructions.Replace("{ACTIVITY}", activity));
        var year = context.CurrentYear;
        var d = context.Draft;
        prompt.AppendLine();
        prompt.AppendLine(context.Bulgarian
            ? "Reply in natural, friendly Bulgarian, using the informal \"ти\"."
            : "Reply in English.");
        prompt.AppendLine($"Current year: {year}.");
        prompt.AppendLine("Known: " + JsonSerializer.Serialize(new
        {
            name = d.Name,
            sex = d.Sex?.ToString().ToLowerInvariant(),
            birthYear = d.BirthYear,
            heightCm = d.HeightCm,
            weightKg = d.WeightKg,
            activityLevel = d.ActivityLevel is { } a ? ActivitySlugs[(int)a] : null,
            goal = d.Goal?.ToString().ToLowerInvariant(),
            ratePercentPerWeek = d.RatePercentPerWeek,
            proteinGramsPerKg = d.ProteinGramsPerKg,
            startingMaintenanceKcal = d.StartingMaintenanceKcal,
        }));
        prompt.AppendLine("Still missing: " + (context.Missing.Count == 0 ? "nothing" : string.Join(", ", context.Missing.Select(FieldName))));
        foreach (var r in context.Rejected)
        {
            prompt.AppendLine($"Refused from the last answer, ask again: {FieldName(r.Field)}: {r.Reason}");
        }

        prompt.Append(CultureInfo.InvariantCulture,
            $"Limits: age {ProfileLimits.MinAgeYears} to {ProfileLimits.MaxAgeYears} (born {ProfileLimits.MinBirthYear(year)} to {ProfileLimits.MaxBirthYear(year)}), ");
        prompt.Append(CultureInfo.InvariantCulture,
            $"height {ProfileLimits.MinHeightCm} to {ProfileLimits.MaxHeightCm} cm, weight {ProfileLimits.MinWeightKg} to {ProfileLimits.MaxWeightKg} kg, ");
        prompt.Append(CultureInfo.InvariantCulture,
            $"name up to {ProfileLimits.MaxNameLength} characters, protein {ProfileLimits.MinProteinGramsPerKg} to {ProfileLimits.MaxProteinGramsPerKg} g per kg, ");
        prompt.Append(CultureInfo.InvariantCulture,
            $"starting maintenance {ProfileLimits.MinMaintenanceKcal} to {ProfileLimits.MaxMaintenanceKcal} kcal. If an answer is outside these, say so kindly and ask again.");
        return prompt.ToString();
    }

    private static string FieldName(OnboardingField field) => field switch
    {
        OnboardingField.Name => "name",
        OnboardingField.Sex => "sex",
        OnboardingField.BirthYear => "birthYear (age)",
        OnboardingField.Height => "heightCm",
        OnboardingField.Weight => "weightKg",
        OnboardingField.ActivityLevel => "activityLevel",
        OnboardingField.Goal => "goal",
        OnboardingField.Rate => "ratePercentPerWeek",
        OnboardingField.Protein => "proteinGramsPerKg",
        _ => "startingMaintenanceKcal",
    };

    /// <summary>Pulls the JSON out of the first choice. Unknown or malformed values become null rather than failing the turn.</summary>
    internal static OnboardingTurn Read(JsonNode? response, string model)
    {
        var json = response?["choices"]?[0]?["message"]?["content"] is JsonValue content && content.TryGetValue<string>(out var c) ? c : null;
        JsonNode? parsed = null;
        try
        {
            parsed = string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json);
        }
        catch (JsonException)
        {
        }

        var reply = Str(parsed?["reply"]);
        if (reply is null)
        {
            return OnboardingTurn.Failed("The assistant returned something unreadable. Try again, or use the form.");
        }

        var f = parsed!["fields"];
        var fields = new OnboardingFields(
            Name: Str(f?["name"]),
            Sex: Str(f?["sex"]) switch { "male" => Sex.Male, "female" => Sex.Female, _ => null },
            BirthYear: Int(f?["birthYear"]),
            HeightCm: Num(f?["heightCm"]) is { } h ? (int)Math.Round(h, MidpointRounding.AwayFromZero) : null,
            WeightKg: Num(f?["weightKg"]),
            ActivityLevel: Array.IndexOf(ActivitySlugs, Str(f?["activityLevel"])) is var i and >= 0 ? (ActivityLevel)i : null,
            Goal: Str(f?["goal"]) switch { "cut" => Goal.Cut, "maintain" => Goal.Maintain, "bulk" => Goal.Bulk, _ => null },
            RatePercentPerWeek: Num(f?["ratePercentPerWeek"]),
            ProteinGramsPerKg: Num(f?["proteinGramsPerKg"]),
            StartingMaintenanceKcal: Int(f?["startingMaintenanceKcal"]));

        var quick = (parsed["quickReplies"] as JsonArray ?? [])
            .Select(Str)
            .OfType<string>()
            .Where(q => q.Length <= 40)
            .Distinct()
            .Take(MaxQuickReplies)
            .ToList();

        return new OnboardingTurn(reply, fields, quick, null, model);
    }

    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    private static decimal? Num(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) && Math.Abs(d) < 100_000 ? Math.Round((decimal)d, 2) : null;

    private static int? Int(JsonNode? node) => Num(node) is { } d ? (int)Math.Round(d, MidpointRounding.AwayFromZero) : null;

    private static string Truncate(string s) => s.Length > 500 ? s[..500] : s;
}

/// <summary>
/// The setup chat's model when a key is set and the onboarding model isn't off. Unavailable otherwise, and the
/// setup page then shows only the form.
/// </summary>
public class SwitchingOnboardingAssistant(OpenRouterSettings settings, IServiceProvider services) : IOnboardingAssistant
{
    public async Task<bool> IsAvailableAsync(CancellationToken ct) =>
        await settings.GetModelAsync(AiTask.Onboarding, ct) != AiTasks.Off && await settings.GetKeyAsync(ct) is not null;

    public async Task<OnboardingTurn> NextAsync(OnboardingContext context, CancellationToken ct)
    {
        var model = await settings.GetModelAsync(AiTask.Onboarding, ct);
        if (model == AiTasks.Off || await settings.GetKeyAsync(ct) is not { } key)
        {
            return OnboardingTurn.Failed("The assistant isn't available right now. Use the form instead.");
        }

        return await services.GetRequiredService<OpenRouterOnboardingAssistant>().NextAsync(context, key, model, ct, await settings.GetEffortAsync(AiTask.Onboarding, ct));
    }
}
