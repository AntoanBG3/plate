using System.Net;
using System.Text.Json.Nodes;
using GymTracker.Core.Nutrition;

namespace GymTracker.Web.Integrations;

public class OpenFoodFactsOptions
{
    public string BaseUrl { get; set; } = "https://world.openfoodfacts.org/";

    /// <summary>Open Food Facts asks every app to identify itself with a contact address.</summary>
    public string ContactEmail { get; set; } = "";
}

/// <summary>
/// Open Food Facts lookups (SPEC 6.2): product by barcode through API v3, and text search. The API allows
/// 15 product reads and 10 searches per minute per IP, shared by both users, so callers cache every hit and
/// only search when the user asks.
/// </summary>
public class OpenFoodFactsClient(HttpClient http, ILogger<OpenFoodFactsClient> log) : IFoodDatabase
{
    private const string Fields = "code,product_name,product_name_en,generic_name,generic_name_en,brands,quantity,nutriments,serving_quantity";

    public async Task<ProductLookup> GetByBarcodeAsync(string barcode, CancellationToken ct)
    {
        var (status, json) = await GetAsync($"api/v3/product/{Uri.EscapeDataString(barcode)}?fields={Fields}", ct);
        if (status != LookupStatus.Found)
        {
            return ProductLookup.Of(status);
        }

        var product = json?["product"];
        if (product is null || json?["status"]?.GetValue<string>() == "failure")
        {
            return ProductLookup.Of(LookupStatus.NotFound);
        }

        var info = ReadProduct(product, barcode);
        return info is null ? ProductLookup.Of(LookupStatus.NoNutrition) : new ProductLookup(LookupStatus.Found, info);
    }

    public async Task<ProductSearch> SearchAsync(string query, CancellationToken ct)
    {
        var url = $"cgi/search.pl?search_terms={Uri.EscapeDataString(query)}&search_simple=1&action=process&json=1&page_size=15&fields={Fields}";
        var (status, json) = await GetAsync(url, ct);
        if (status != LookupStatus.Found)
        {
            return new ProductSearch(status, []);
        }

        var products = (json?["products"]?.AsArray() ?? [])
            .Select(p => p is null ? null : ReadProduct(p, p["code"]?.ToString() ?? ""))
            .OfType<ProductInfo>()
            .ToList();
        return new ProductSearch(LookupStatus.Found, products);
    }

    private async Task<(LookupStatus, JsonNode?)> GetAsync(string url, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(url, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return (LookupStatus.NotFound, null);
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return (LookupStatus.RateLimited, null);
            }

            if (!response.IsSuccessStatusCode)
            {
                log.LogWarning("Open Food Facts returned {Status} for {Url}", (int)response.StatusCode, url);
                return (LookupStatus.Unavailable, null);
            }

            return (LookupStatus.Found, JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning(ex, "Open Food Facts request failed for {Url}", url);
            return (LookupStatus.Unavailable, null);
        }
    }

    /// <summary>Null when the product lacks the energy, protein, carb and fat values needed to log it.</summary>
    internal static ProductInfo? ReadProduct(JsonNode product, string barcode)
    {
        var n = product["nutriments"];
        var kcal = Num(n?["energy-kcal_100g"]);
        if (kcal is null && Num(n?["energy-kj_100g"] ?? n?["energy_100g"]) is { } kj)
        {
            kcal = Math.Round(kj / 4.184m, 1);
        }

        var protein = Num(n?["proteins_100g"]);
        var carbs = Num(n?["carbohydrates_100g"]);
        var fat = Num(n?["fat_100g"]);
        if (kcal is null || protein is null || carbs is null || fat is null)
        {
            return null;
        }

        var name = new[] { "product_name", "product_name_en", "generic_name", "generic_name_en" }
            .Select(f => product[f]?.ToString().Trim())
            .FirstOrDefault(v => !string.IsNullOrEmpty(v));
        var brand = product["brands"]?.ToString().Split(',')[0].Trim();
        brand = string.IsNullOrEmpty(brand) ? null : brand;

        // No name in any field (feedback #5, a Monster can): the brand and quantity stand in until a user names it.
        var stand = string.Join(' ', new[] { brand, product["quantity"]?.ToString().Trim() }.Where(v => !string.IsNullOrEmpty(v)));
        return new ProductInfo(
            barcode,
            name ?? (brand is null ? $"Product {barcode}" : stand),
            brand,
            new Per100g(kcal.Value, protein.Value, carbs.Value, fat.Value, Optional(n?["fiber_100g"], 100), Optional(n?["saturated-fat_100g"], fat.Value + 0.5m)),
            Num(product["serving_quantity"]) is { } s and > 0 ? s : null,
            Unnamed: name is null);
    }

    /// <summary>A value that is not required to log the product; dropped when it is negative or above <paramref name="max"/>.</summary>
    private static decimal? Optional(JsonNode? node, decimal max) => Num(node) is { } v && v >= 0 && v <= max ? v : null;

    private static decimal? Num(JsonNode? node)
    {
        if (node is not JsonValue v)
        {
            return null;
        }

        if (v.TryGetValue<double>(out var d))
        {
            return Math.Round((decimal)d, 2);
        }

        return v.TryGetValue<string>(out var s)
            && decimal.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }
}
