namespace GymTracker.Core.Nutrition;

/// <summary>A packaged product from an external food database such as Open Food Facts.</summary>
public record ProductInfo(string Barcode, string Name, string? Brand, Per100g Per100, decimal? ServingGrams);

public enum LookupStatus
{
    Found,
    NotFound,
    NoNutrition,
    RateLimited,
    Unavailable,
}

public record ProductLookup(LookupStatus Status, ProductInfo? Product)
{
    public static ProductLookup Of(LookupStatus status) => new(status, null);
}

public record ProductSearch(LookupStatus Status, IReadOnlyList<ProductInfo> Products);

public interface IFoodDatabase
{
    Task<ProductLookup> GetByBarcodeAsync(string barcode, CancellationToken ct);

    Task<ProductSearch> SearchAsync(string query, CancellationToken ct);
}

public static class Barcodes
{
    /// <summary>EAN-8, UPC-A (12), EAN-13 or GTIN-14 digits with a valid check digit.</summary>
    public static bool IsValid(string? code)
    {
        if (code is null || code.Length is not (8 or 12 or 13 or 14) || !code.All(char.IsAsciiDigit))
        {
            return false;
        }

        var sum = 0;
        for (var i = 0; i < code.Length - 1; i++)
        {
            // Weights alternate 3,1 starting from the digit next to the check digit.
            var weight = (code.Length - 1 - i) % 2 == 1 ? 3 : 1;
            sum += (code[i] - '0') * weight;
        }

        return (10 - sum % 10) % 10 == code[^1] - '0';
    }
}
