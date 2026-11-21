namespace Plate.Core.Nutrition;

/// <summary>A packaged product from an external food database such as Open Food Facts.</summary>
/// <param name="Unnamed">The database had no name, so <paramref name="Name"/> is made up from the brand and quantity.</param>
/// <param name="PieceGrams">One piece, when the serving is given as a count of pieces ("2 biscuits (30 g)").</param>
/// <param name="PackGrams">The pack's net weight, when given in grams.</param>
public record ProductInfo(string Barcode, string Name, string? Brand, Per100g Per100, decimal? ServingGrams, bool Unnamed = false, decimal? PieceGrams = null, decimal? PackGrams = null);

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
    /// <summary>
    /// The digits of a typed or scanned code, with a UPC-E code (8 digits, the short form on small packs) expanded to
    /// the UPC-A it stands for, as the food databases store it. An 8-digit code that is a valid EAN-8 stays one.
    /// </summary>
    public static string Normalize(string? code)
    {
        var digits = new string((code ?? "").Where(char.IsAsciiDigit).ToArray());
        if (digits.Length == 8 && !IsValid(digits) && digits[0] is '0' or '1' && ExpandUpcE(digits) is { } upcA && IsValid(upcA))
        {
            return upcA;
        }

        return digits;
    }

    /// <summary>UPC-E to UPC-A: the zeros the short form leaves out go back, by the rule its sixth digit sets.</summary>
    private static string? ExpandUpcE(string e)
    {
        var (n, d, check) = (e[0], e[1..7], e[7]);
        var body = d[5] switch
        {
            '0' or '1' or '2' => $"{d[0]}{d[1]}{d[5]}0000{d[2]}{d[3]}{d[4]}",
            '3' => $"{d[0]}{d[1]}{d[2]}00000{d[3]}{d[4]}",
            '4' => $"{d[0]}{d[1]}{d[2]}{d[3]}00000{d[4]}",
            _ => $"{d[0]}{d[1]}{d[2]}{d[3]}{d[4]}0000{d[5]}",
        };
        return $"{n}{body}{check}";
    }

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
