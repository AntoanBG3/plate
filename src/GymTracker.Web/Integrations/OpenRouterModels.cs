using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using GymTracker.Core.Text;
using Microsoft.Extensions.Caching.Memory;

namespace GymTracker.Web.Integrations;

/// <summary>A model from OpenRouter's catalog that can return structured JSON, so it can parse food.</summary>
/// <param name="Provider">The part of the catalog name before the colon, e.g. "Google".</param>
/// <param name="LowestEffort">The lowest reasoning effort the model accepts, or null when it has no effort setting.</param>
public record OpenRouterModel(
    string Id,
    string Provider,
    string Name,
    decimal PromptPerMillion,
    decimal CompletionPerMillion,
    bool SupportsTemperature,
    string? LowestEffort)
{
    public LocalText Price => PromptPerMillion == 0 && CompletionPerMillion == 0
        ? new LocalText("Free")
        : new LocalText("${0} in, ${1} out per million tokens", Money(PromptPerMillion), Money(CompletionPerMillion));

    private static string Money(decimal d) => d.ToString(d < 0.1m && d != 0 ? "0.000" : "0.00", CultureInfo.InvariantCulture);
}

/// <summary>
/// OpenRouter's public model list (GET models), narrowed to text models with structured outputs and cached for an
/// hour. When OpenRouter can't be reached the last good list is used; with no list at all the result is null.
/// </summary>
public class OpenRouterModels(HttpClient http, IMemoryCache cache, ILogger<OpenRouterModels> log)
{
    private const string FreshKey = "openrouter-models";
    private const string LastGoodKey = "openrouter-models-last-good";

    public async Task<IReadOnlyList<OpenRouterModel>?> ListAsync(CancellationToken ct)
    {
        if (cache.TryGetValue(FreshKey, out IReadOnlyList<OpenRouterModel>? fresh))
        {
            return fresh;
        }

        try
        {
            var models = Parse(await http.GetFromJsonAsync<JsonNode>("models", ct));
            if (models.Count > 0)
            {
                cache.Set(FreshKey, models, TimeSpan.FromHours(1));
                cache.Set(LastGoodKey, models);
                return models;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException || ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "Could not load the OpenRouter model list");
        }

        return cache.Get<IReadOnlyList<OpenRouterModel>>(LastGoodKey);
    }

    public async Task<OpenRouterModel?> FindAsync(string id, CancellationToken ct) =>
        (await ListAsync(ct))?.FirstOrDefault(m => m.Id == id);

    internal static List<OpenRouterModel> Parse(JsonNode? response)
    {
        var models = new List<OpenRouterModel>();
        foreach (var m in response?["data"]?.AsArray() ?? [])
        {
            var id = Str(m?["id"]);
            var parameters = Strings(m?["supported_parameters"]);
            var output = Strings(m?["architecture"]?["output_modalities"]);
            if (id is null || id.EndsWith(":batch", StringComparison.Ordinal)
                || !parameters.Contains("structured_outputs") || !parameters.Contains("response_format")
                || output is not ["text"])
            {
                continue;
            }

            var prompt = Price(m?["pricing"]?["prompt"]);
            var completion = Price(m?["pricing"]?["completion"]);
            if (prompt < 0 || completion < 0)
            {
                continue; // routers such as openrouter/auto have no fixed price
            }

            var name = Str(m?["name"]) ?? id;
            var colon = name.IndexOf(": ", StringComparison.Ordinal);
            models.Add(new OpenRouterModel(
                id,
                colon > 0 ? name[..colon] : id.Split('/')[0].TrimStart('~'),
                colon > 0 ? name[(colon + 2)..] : name,
                prompt,
                completion,
                parameters.Contains("temperature"),
                parameters.Contains("reasoning") ? LowestEffort(m?["reasoning"]) : null));
        }

        return models.OrderBy(m => m.Provider, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.PromptPerMillion + m.CompletionPerMillion).ThenBy(m => m.Name).ToList();
    }

    /// <summary>The efforts are listed highest first; "none" is not allowed when reasoning is mandatory.</summary>
    private static string? LowestEffort(JsonNode? reasoning)
    {
        var efforts = Strings(reasoning?["supported_efforts"]);
        if (reasoning?["mandatory"] is JsonValue v && v.TryGetValue<bool>(out var mandatory) && mandatory)
        {
            efforts.Remove("none");
        }

        return efforts.Count > 0 ? efforts[^1] : null;
    }

    private static decimal Price(JsonNode? perToken) =>
        decimal.TryParse(Str(perToken), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? Math.Round(p * 1_000_000m, 4) : -1;

    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static List<string> Strings(JsonNode? node) =>
        node is JsonArray a ? a.Select(Str).OfType<string>().ToList() : [];
}
