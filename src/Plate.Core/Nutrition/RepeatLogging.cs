using System.Text.RegularExpressions;
using Plate.Core.Domain;

namespace Plate.Core.Nutrition;

/// <summary>One past entry of a food: how much, and when it was logged (SPEC 5.10).</summary>
public record AmountUse(decimal Grams, DateTimeOffset CreatedAt);

/// <summary>The amount this account logged last time for a food (SPEC 5.10).</summary>
public static class LastAmount
{
    public static readonly TimeSpan Window = TimeSpan.FromDays(90);

    /// <summary>
    /// The grams of the most recent entry in the window, or null. Quick entries (0 g) don't count. The most recent wins
    /// over the most frequent, since an amount changes when a pack size or a habit does.
    /// </summary>
    public static decimal? Pick(IEnumerable<AmountUse> uses, DateTimeOffset now) =>
        uses.Where(u => u.Grams > 0 && u.CreatedAt >= now - Window)
            .OrderByDescending(u => u.CreatedAt)
            .Select(u => (decimal?)u.Grams)
            .FirstOrDefault();
}

/// <summary>One food log entry as the Recent tab sees it.</summary>
public record RecentUse(int EntryId, int? FoodItemId, string Description, Meal Meal, DateOnly Date, DateTimeOffset CreatedAt, decimal Grams);

/// <summary>One Recent row: the entry it is drawn from (the latest of its group) and how often it was logged.</summary>
public record RecentGroup(int LastEntryId, int Times);

/// <summary>The Recent tab's foods for a meal (SPEC 5.10): what the account eats at that meal first.</summary>
public static class RecentOrder
{
    public const int Days = 30;
    public const int Count = 20;

    public static IReadOnlyList<RecentGroup> For(IEnumerable<RecentUse> uses, Meal meal, DateOnly today)
    {
        var since = today.AddDays(-Days);
        return uses
            .Where(u => u.Date >= since && u.Grams > 0)
            .GroupBy(u => (u.FoodItemId, Key: u.Description.ToLowerInvariant()))
            .Select(g =>
            {
                var last = g.OrderByDescending(u => u.Date).ThenByDescending(u => u.CreatedAt).First();
                return (Last: last, AtMeal: g.Count(u => u.Meal == meal), Times: g.Count());
            })
            .OrderByDescending(x => x.AtMeal)
            .ThenByDescending(x => x.Times)
            .ThenByDescending(x => x.Last.Date)
            .ThenByDescending(x => x.Last.CreatedAt)
            .ThenBy(x => x.Last.Description, StringComparer.Ordinal)
            .Take(Count)
            .Select(x => new RecentGroup(x.Last.EntryId, x.Times))
            .ToList();
    }
}

/// <summary>The key a usual food is stored under: what the user typed for a food, without its amount (SPEC 5.10).</summary>
public static partial class UsualFoods
{
    public const int MaxLength = 100;

    private static readonly HashSet<string> Units = ["g", "гр", "г", "kg", "кг", "ml", "мл", "l", "л", "бр"];

    public static string? Key(string? typed)
    {
        if (string.IsNullOrWhiteSpace(typed))
        {
            return null;
        }

        var tokens = typed.ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(TrimEdges)
            .Where(t => t.Length > 0)
            .ToList();

        // "200 g извара", "2 бр. банани": an amount, then maybe its unit.
        while (tokens.Count > 0 && IsAmount(tokens[0]))
        {
            var number = IsNumber(tokens[0]);
            tokens.RemoveAt(0);
            if (number && tokens.Count > 0 && Units.Contains(tokens[0]))
            {
                tokens.RemoveAt(0);
            }
        }

        // "извара 200 g", "извара 200г".
        while (tokens.Count > 0)
        {
            if (IsAmount(tokens[^1]))
            {
                tokens.RemoveAt(tokens.Count - 1);
            }
            else if (tokens.Count > 1 && Units.Contains(tokens[^1]) && IsNumber(tokens[^2]))
            {
                tokens.RemoveRange(tokens.Count - 2, 2);
            }
            else
            {
                break;
            }
        }

        var key = string.Join(' ', tokens);
        return key.Length == 0 ? null : key.Length > MaxLength ? key[..MaxLength] : key;
    }

    /// <summary>Punctuation at a word's edges goes; inside a word ("7-up", "1/2") and a percent sign ("2%") stay.</summary>
    private static string TrimEdges(string token)
    {
        int start = 0, end = token.Length;
        while (start < end && IsEdge(token[start]))
        {
            start++;
        }

        while (end > start && IsEdge(token[end - 1]))
        {
            end--;
        }

        return token[start..end];
    }

    private static bool IsEdge(char c) => c != '%' && (char.IsPunctuation(c) || char.IsSymbol(c));

    private static bool IsNumber(string token) => Number().IsMatch(token);

    private static bool IsAmount(string token) => IsNumber(token) || NumberWithUnit().IsMatch(token);

    [GeneratedRegex(@"^(\d+([.,]\d+)?|\d+/\d+|[½¼¾⅓⅔])$")]
    private static partial Regex Number();

    [GeneratedRegex(@"^(\d+([.,]\d+)?|\d+/\d+)(g|гр|г|kg|кг|ml|мл|l|л|бр)$")]
    private static partial Regex NumberWithUnit();
}
