using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using GymTracker.Core.Nutrition;
using GymTracker.Core.Text;

namespace GymTracker.Web.Integrations;

/// <summary>
/// Reads a nutrition label photo with an OpenRouter vision model and strict structured output (SPEC 6.6). The model
/// transcribes; LabelValues.Normalize does the arithmetic.
/// </summary>
public class OpenRouterLabelReader(HttpClient http, OpenRouterModels catalog, ILogger<OpenRouterLabelReader> log)
{
    /// <summary>Per attempt; settable so tests don't wait 20 seconds.</summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(20);
    private static readonly LocalText Unreadable = "Couldn't read the label. Enter the values by hand.";

    private const string Instructions = """
        You read the nutrition table on a food package from a photo, for a calorie tracker. Transcribe; never calculate,
        convert or estimate a number.

        - Use the column per 100 g, or per 100 ml for drinks. Only if the label has neither, use the per-serving column
          and give the serving weight in grams in serving_grams. basis says which column you used.
        - serving_grams: the portion weight when it is printed (for example "1 порция = 30 g"), else null.
        - A value that isn't printed or can't be read clearly is null. Never guess.
        - Read decimal commas as decimals ("12,5" is 12.5). "<0,5" or "<0.1" is the number after "<".
        - energy_kj and energy_kcal are the two energy figures; either may be missing.
        - Labels are usually Bulgarian: Енергийна стойност (energy), Мазнини (fat), от които наситени мастни киселини
          (saturated_fat), Въглехидрати (carbs), от които захари (sugars: NOT carbs, ignore), Фибри or Хранителни
          влакна (fiber), Белтъчини (protein), Сол (salt, ignore). Other EU languages use the same rows.
        - name and brand only when printed in this photo, as written; otherwise null.
        - is_label is false when the photo doesn't show a nutrition table.
        """;

    private static readonly JsonObject Schema = JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "is_label": { "type": "boolean" },
            "name": { "type": ["string", "null"] },
            "brand": { "type": ["string", "null"] },
            "basis": { "type": "string", "enum": ["per_100g", "per_100ml", "per_serving"] },
            "serving_grams": { "type": ["number", "null"] },
            "energy_kcal": { "type": ["number", "null"] },
            "energy_kj": { "type": ["number", "null"] },
            "protein": { "type": ["number", "null"] },
            "carbs": { "type": ["number", "null"] },
            "fat": { "type": ["number", "null"] },
            "saturated_fat": { "type": ["number", "null"] },
            "fiber": { "type": ["number", "null"] }
          },
          "required": ["is_label", "name", "brand", "basis", "serving_grams", "energy_kcal", "energy_kj", "protein", "carbs", "fat", "saturated_fat", "fiber"],
          "additionalProperties": false
        }
        """)!.AsObject();

    /// <summary>The energy printed on the bundled sample label (tools/labels/sample-label.html).</summary>
    public const decimal SampleKcal = 250;

    /// <summary>Reads the bundled sample label: null when the model reads its energy right, otherwise what went wrong.</summary>
    public async Task<LocalText?> TestAsync(string apiKey, string model, CancellationToken ct)
    {
        await using var stream = typeof(OpenRouterLabelReader).Assembly.GetManifestResourceStream("GymTracker.Web.Integrations.Assets.sample-label.jpg")
            ?? throw new InvalidOperationException("The sample label isn't embedded.");
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        var result = await ReadAsync(buffer.ToArray(), "image/jpeg", apiKey, model, ct);
        if (result.Reading is not { } reading)
        {
            return result.Error;
        }

        var kcal = LabelValues.Normalize(reading).Values?.Kcal;
        return kcal == SampleKcal
            ? null
            : new LocalText("The model misread the sample label (read {0} kcal, expected {1}).", kcal?.ToString(CultureInfo.InvariantCulture) ?? "nothing", SampleKcal);
    }

    public async Task<LabelReadResult> ReadAsync(byte[] image, string contentType, string apiKey, string model, CancellationToken ct)
    {
        var body = await BodyAsync(image, contentType, model, ct);
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(AttemptTimeout);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions") { Content = JsonContent.Create(body) };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                using var response = await http.SendAsync(request, timeout.Token);
                if ((int)response.StatusCode >= 500 && attempt == 1)
                {
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    log.LogWarning("OpenRouter returned {Status} for {Model} reading a label: {Body}", (int)response.StatusCode, model, Truncate(await response.Content.ReadAsStringAsync(ct)));
                    return LabelReadResult.Failed(Unreadable, model);
                }

                return Read(await response.Content.ReadFromJsonAsync<JsonNode>(timeout.Token), model);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // A timeout is not retried: the user is waiting with the form open (SPEC 6.6).
                return LabelReadResult.Failed(Unreadable, model);
            }
            catch (HttpRequestException ex)
            {
                log.LogWarning(ex, "OpenRouter request failed reading a label");
                if (attempt == 2)
                {
                    return LabelReadResult.Failed(Unreadable, model);
                }
            }
        }

        return LabelReadResult.Failed(Unreadable, model);
    }

    private async Task<JsonObject> BodyAsync(byte[] image, string contentType, string model, CancellationToken ct)
    {
        var info = await catalog.FindAsync(model, ct);
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = Instructions },
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray(
                        new JsonObject { ["type"] = "text", ["text"] = "Read this nutrition label." },
                        new JsonObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JsonObject { ["url"] = $"data:{contentType};base64,{Convert.ToBase64String(image)}" },
                        }),
                }),
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject { ["name"] = "nutrition_label", ["strict"] = true, ["schema"] = Schema.DeepClone() },
            },
            ["provider"] = new JsonObject { ["require_parameters"] = true },
        };
        if (info?.SupportsTemperature == true)
        {
            body["temperature"] = 0;
        }

        if (info?.LowestEffort is { } effort)
        {
            body["reasoning"] = new JsonObject { ["effort"] = effort };
        }

        return body;
    }

    internal static LabelReadResult Read(JsonNode? response, string model)
    {
        var json = response?["choices"]?[0]?["message"]?["content"] is JsonValue content && content.TryGetValue<string>(out var c) ? c : null;
        try
        {
            if (string.IsNullOrWhiteSpace(json) || JsonNode.Parse(json) is not JsonObject o || o["is_label"] is not JsonValue isLabel)
            {
                return LabelReadResult.Failed(Unreadable, model, json);
            }

            var basis = Str(o["basis"]) switch
            {
                "per_100ml" => LabelBasis.Per100ml,
                "per_serving" => LabelBasis.PerServing,
                _ => LabelBasis.Per100g,
            };
            return new LabelReadResult(
                new LabelReading(isLabel.GetValue<bool>(), Str(o["name"]), Str(o["brand"]), basis, Num(o["serving_grams"]),
                    Num(o["energy_kcal"]), Num(o["energy_kj"]), Num(o["protein"]), Num(o["carbs"]), Num(o["fat"]), Num(o["saturated_fat"]), Num(o["fiber"])),
                json, null, model);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return LabelReadResult.Failed(Unreadable, model, json);
        }
    }

    private static decimal? Num(JsonNode? node) => node is JsonValue v && v.TryGetValue<decimal>(out var d) ? d : null;

    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static string Truncate(string s) => s.Length > 500 ? s[..500] : s;
}

/// <summary>The label model when a key is set and it isn't off; otherwise label photos are hidden (SPEC 6.6).</summary>
public class SwitchingLabelReader(OpenRouterSettings settings, IServiceProvider services) : ILabelReader
{
    public async Task<bool> IsAvailableAsync(CancellationToken ct) =>
        await settings.GetModelAsync(AiTask.Label, ct) != AiTasks.Off && await settings.GetKeyAsync(ct) is not null;

    public async Task<LabelReadResult> ReadAsync(byte[] image, string contentType, CancellationToken ct)
    {
        var model = await settings.GetModelAsync(AiTask.Label, ct);
        if (model == AiTasks.Off || await settings.GetKeyAsync(ct) is not { } key)
        {
            // Named after the configured model, so the scan isn't grouped under a blank one (SPEC 11 accuracy).
            return LabelReadResult.Failed("Couldn't read the label. Enter the values by hand.", model);
        }

        return await services.GetRequiredService<OpenRouterLabelReader>().ReadAsync(image, contentType, key, model, ct);
    }
}
