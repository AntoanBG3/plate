using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Plate.Core.Text;
using Microsoft.Extensions.Caching.Memory;

namespace Plate.Web.Integrations;

/// <summary>A model from OpenRouter's catalog: a chat model that can return structured JSON, or a decision model.</summary>
/// <param name="Provider">The part of the catalog name before the colon, e.g. "Google".</param>
/// <param name="LowestEffort">The lowest reasoning effort the model accepts, or null when it has no effort setting.</param>
/// <param name="AcceptsImages">The model takes images as input, so it can read a label photo (SPEC 6.6).</param>
/// <param name="Rank">Place in OpenRouter's list by tokens processed last week, most used first; null when unknown.</param>
/// <param name="Created">When OpenRouter added the model; null when unknown.</param>
/// <param name="Expires">The day OpenRouter retires the model, when one is announced.</param>
/// <param name="Efforts">The reasoning efforts the model accepts, highest first ("none" left out when reasoning is mandatory); empty without an effort setting.</param>
public record OpenRouterModel(
    string Id,
    string Provider,
    string Name,
    decimal PromptPerMillion,
    decimal CompletionPerMillion,
    bool SupportsTemperature,
    string? LowestEffort,
    bool AcceptsImages = false,
    int? Rank = null,
    DateTimeOffset? Created = null,
    DateOnly? Expires = null,
    IReadOnlyList<string>? Efforts = null)
{
    /// <summary>
    /// The effort to send: the one picked for the task when this model accepts it, else the model's lowest (SPEC 6.1).
    /// A pick another model doesn't take is not sent, since OpenRouter would then skip every provider for the request.
    /// </summary>
    public string? EffortFor(string? picked) =>
        picked is not null && (Efforts ?? []).Contains(picked) ? picked : LowestEffort;

    public LocalText Price => PromptPerMillion == 0 && CompletionPerMillion == 0
        ? new LocalText("Free")
        : new LocalText("${0} in, ${1} out per million tokens", Money(PromptPerMillion), Money(CompletionPerMillion));

    private static string Money(decimal d) => d.ToString(d < 0.1m && d != 0 ? "0.000" : "0.00", CultureInfo.InvariantCulture);
}

/// <summary>
/// OpenRouter's public model lists, each cached for an hour: chat models (GET models, most used last week first),
/// narrowed to text models with structured outputs, and decision models (GET models?output_modalities=decisions).
/// A failed refresh reuses the last good list for three minutes. With no saved list, the result is null.
/// </summary>
public class OpenRouterModels(HttpClient http, IMemoryCache cache, ILogger<OpenRouterModels> log)
{
    public Task<IReadOnlyList<OpenRouterModel>?> ListAsync(CancellationToken ct) =>
        LoadAsync("models?sort=top-weekly", "openrouter-models", Parse, ct);

    /// <summary>Decision models for the food matcher (SPEC 6.1); aliases such as ~typesafe/jev-latest are left out.</summary>
    public Task<IReadOnlyList<OpenRouterModel>?> ListDecisionsAsync(CancellationToken ct) =>
        LoadAsync("models?output_modalities=decisions", "openrouter-decision-models", ParseDecisions, ct);

    private async Task<IReadOnlyList<OpenRouterModel>?> LoadAsync(string url, string key, Func<JsonNode?, List<OpenRouterModel>> parse, CancellationToken ct)
    {
        var lastGoodKey = key + "-last-good";
        var backoffKey = key + "-refresh-backoff";
        if (cache.TryGetValue(key, out IReadOnlyList<OpenRouterModel>? fresh)
            || cache.TryGetValue(backoffKey, out fresh))
        {
            return fresh;
        }

        try
        {
            var models = parse(await http.GetFromJsonAsync<JsonNode>(url, ct));
            if (models.Count > 0)
            {
                cache.Set(key, models, TimeSpan.FromHours(1));
                cache.Set(lastGoodKey, models);
                return models;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException || ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "Could not load the OpenRouter model list {Url}", url);
        }

        var lastGood = cache.Get<IReadOnlyList<OpenRouterModel>>(lastGoodKey);
        if (lastGood is not null)
        {
            // Use a separate entry so a failed refresh cannot replace a concurrent successful refresh.
            cache.Set(backoffKey, lastGood, TimeSpan.FromMinutes(3));
        }
        return lastGood;
    }

    public async Task<OpenRouterModel?> FindAsync(string id, CancellationToken ct) =>
        (await ListAsync(ct))?.FirstOrDefault(m => m.Id == id);

    internal static List<OpenRouterModel> Parse(JsonNode? response)
    {
        var models = new List<OpenRouterModel>();
        var rank = -1;
        foreach (var m in response?["data"]?.AsArray() ?? [])
        {
            rank++;
            var id = Str(m?["id"]);
            var parameters = Strings(m?["supported_parameters"]);
            var output = Strings(m?["architecture"]?["output_modalities"]);
            var input = Strings(m?["architecture"]?["input_modalities"]);
            // Batch variants are asynchronous; openrouter/* routers pick a different model per request.
            if (id is null || id.EndsWith(":batch", StringComparison.Ordinal) || id.StartsWith("openrouter/", StringComparison.Ordinal)
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

            var (provider, name) = Names(m, id);
            models.Add(new OpenRouterModel(
                id,
                provider,
                name,
                prompt,
                completion,
                parameters.Contains("temperature"),
                parameters.Contains("reasoning") ? LowestEffort(m?["reasoning"]) : null,
                input.Contains("image"),
                rank,
                Created(m),
                Expires(m),
                parameters.Contains("reasoning") ? EffortsOf(m?["reasoning"]) : []));
        }

        return models.OrderBy(m => m.Provider, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.PromptPerMillion + m.CompletionPerMillion).ThenBy(m => m.Name).ToList();
    }

    /// <summary>Decision models priced per input token, newest first within each provider.</summary>
    internal static List<OpenRouterModel> ParseDecisions(JsonNode? response)
    {
        var models = new List<OpenRouterModel>();
        foreach (var m in response?["data"]?.AsArray() ?? [])
        {
            var id = Str(m?["id"]);
            // An alias (~provider/model-latest) can move to a new version under the matcher's thresholds.
            if (id is null || id.StartsWith('~') || !Strings(m?["architecture"]?["output_modalities"]).Contains("decisions"))
            {
                continue;
            }

            var prompt = Price(m?["pricing"]?["prompt"]);
            if (prompt < 0)
            {
                continue;
            }

            var (provider, name) = Names(m, id);
            models.Add(new OpenRouterModel(id, provider, name, prompt, 0, false, null,
                Strings(m?["architecture"]?["input_modalities"]).Contains("image"), null, Created(m), Expires(m)));
        }

        return models.OrderBy(m => m.Provider, StringComparer.OrdinalIgnoreCase).ThenByDescending(m => m.Created).ThenBy(m => m.Name).ToList();
    }

    /// <summary>"Google: Gemini 3.5 Flash" is provider "Google", name "Gemini 3.5 Flash".</summary>
    private static (string Provider, string Name) Names(JsonNode? m, string id)
    {
        var name = Str(m?["name"]) ?? id;
        var colon = name.IndexOf(": ", StringComparison.Ordinal);
        return colon > 0 ? (name[..colon], name[(colon + 2)..]) : (id.Split('/')[0].TrimStart('~'), name);
    }

    private static DateTimeOffset? Created(JsonNode? m) =>
        m?["created"] is JsonValue v && v.TryGetValue<long>(out var seconds) && seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;

    private static DateOnly? Expires(JsonNode? m) =>
        DateOnly.TryParseExact(Str(m?["expiration_date"]), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    /// <summary>The efforts are listed highest first; "none" is not allowed when reasoning is mandatory.</summary>
    private static string? LowestEffort(JsonNode? reasoning) => EffortsOf(reasoning) is { Count: > 0 } efforts ? efforts[^1] : null;

    /// <summary>The supported efforts as OpenRouter lists them, highest first, without "none" when reasoning is mandatory.</summary>
    private static List<string> EffortsOf(JsonNode? reasoning)
    {
        var efforts = Strings(reasoning?["supported_efforts"]);
        if (reasoning?["mandatory"] is JsonValue v && v.TryGetValue<bool>(out var mandatory) && mandatory)
        {
            efforts.Remove("none");
        }

        return efforts;
    }

    private static decimal Price(JsonNode? perToken) =>
        decimal.TryParse(Str(perToken), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? Math.Round(p * 1_000_000m, 4) : -1;

    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static List<string> Strings(JsonNode? node) =>
        node is JsonArray a ? a.Select(Str).OfType<string>().ToList() : [];
}
