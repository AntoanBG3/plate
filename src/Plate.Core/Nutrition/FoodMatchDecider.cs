namespace Plate.Core.Nutrition;

/// <summary>A database food offered to the decider for one parsed item.</summary>
public record MatchCandidate(int Id, string Name, string? NameBg, string? Brand, Per100g Per100);

/// <summary>One parsed item and the database foods it might be.</summary>
public record MatchQuestion(ParsedFoodItem Item, IReadOnlyList<MatchCandidate> Candidates);

/// <param name="FoodItemId">The chosen candidate, or null when the decider said none of them is this food.</param>
/// <param name="Confidence">How sure the decider was, 0 to 1.</param>
public record MatchDecision(int? FoodItemId, decimal Confidence);

/// <param name="Items">One entry per question, in the same order; null where that item was not decided.</param>
/// <param name="Model">The model version that answered.</param>
public record MatchDecisions(IReadOnlyList<MatchDecision?> Items, string Model);

/// <summary>Decides which database food each parsed item is, from a shortlist, or that none fits (SPEC 6.1).</summary>
public interface IFoodMatchDecider
{
    /// <summary>Null when no decision could be made (matcher off, no key, timeout, error); the caller then uses its own ranking.</summary>
    Task<MatchDecisions?> DecideAsync(string text, IReadOnlyList<MatchQuestion> questions, CancellationToken ct);
}
