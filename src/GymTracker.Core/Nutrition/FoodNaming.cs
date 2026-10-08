using GymTracker.Core.Text;

namespace GymTracker.Core.Nutrition;

/// <summary>One matched food to name.</summary>
/// <param name="Typed">What the user wrote for it, as context only.</param>
/// <param name="DatabaseName">The food's name in the account's language.</param>
/// <param name="DatabaseNameEn">The food's English name when <paramref name="DatabaseName"/> is another language.</param>
/// <param name="State">Raw or cooked as the database names say; the name must not contradict it.</param>
public record NameRequest(string Typed, string DatabaseName, string? DatabaseNameEn, FoodState State);

/// <summary>A namer's answer for one food, unchecked.</summary>
/// <param name="Name">The short name, without the status.</param>
/// <param name="Status">How the food is, as its database name says ("raw", "boiled", "dried", "canned"); null when it says nothing.</param>
public record FoodName(string? Name, string? Status);

/// <summary>Writes a short everyday name and a status for each matched food, from its database name (SPEC 6.1).</summary>
public interface IFoodNamer
{
    /// <summary>One answer per request, in order (null where none came back); null when naming failed or is unavailable.</summary>
    Task<IReadOnlyList<FoodName?>?> NameAsync(IReadOnlyList<NameRequest> items, CancellationToken ct);
}

public static class FoodNaming
{
    public const int MaxLength = 60;
    public const int MaxStatusLength = 30;

    /// <summary>
    /// The proposed name, tidied, or null when it can't stand in for the database name: empty, longer than
    /// <see cref="MaxLength"/>, starting with an amount, or raw where the food is cooked (or the other way round).
    /// A name that leaves the state out is fine; the chip beside it shows the state.
    /// </summary>
    public static string? Accept(string? proposed, FoodState databaseState)
    {
        if (string.IsNullOrWhiteSpace(proposed))
        {
            return null;
        }

        var name = Tidy(proposed);
        return name.Length > MaxLength || FoodText.StripAmount(name) != name || Contradicts(name, databaseState) ? null : name;
    }

    /// <summary>
    /// The proposed status, tidied, or null when it can't be shown beside the name: empty, longer than
    /// <see cref="MaxStatusLength"/> or three words, with a number in it, or raw where the food is cooked (or the other way round).
    /// </summary>
    public static string? AcceptStatus(string? proposed, FoodState databaseState)
    {
        if (string.IsNullOrWhiteSpace(proposed))
        {
            return null;
        }

        var status = Tidy(proposed).ToLowerInvariant();
        return status.Length > MaxStatusLength || status.Split(' ').Length > 3 || status.Any(char.IsDigit) || Contradicts(status, databaseState)
            ? null
            : status;
    }

    private static string Tidy(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static bool Contradicts(string text, FoodState databaseState)
    {
        var state = FoodStates.FromName(text);
        return (state, databaseState) is (FoodState.Raw, FoodState.Cooked) or (FoodState.Cooked, FoodState.Raw)
            // A state the database name doesn't say is something the model added.
            || (state != FoodState.NotApplicable && databaseState == FoodState.NotApplicable);
    }
}
