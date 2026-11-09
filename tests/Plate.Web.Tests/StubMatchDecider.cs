using Plate.Core.Nutrition;

namespace Plate.Web.Tests;

/// <summary>A scripted matcher: answers with whatever <see cref="Answer"/> returns (no decision by default) and records the calls.</summary>
public sealed class StubMatchDecider : IFoodMatchDecider
{
    public const string Model = "typesafe/jev-1.13-20260917";

    public Func<IReadOnlyList<MatchQuestion>, MatchDecisions?> Answer { get; set; } = _ => null;

    public List<IReadOnlyList<MatchQuestion>> Calls { get; } = [];

    public string? LastText { get; private set; }

    public Task<MatchDecisions?> DecideAsync(string text, IReadOnlyList<MatchQuestion> questions, CancellationToken ct)
    {
        Calls.Add(questions);
        LastText = text;
        return Task.FromResult(Answer(questions));
    }

    /// <summary>Answers the first question with <paramref name="pick"/> (a candidate id, or null for none).</summary>
    public static MatchDecisions First(IReadOnlyList<MatchQuestion> questions, int? pick, decimal confidence) =>
        new([new MatchDecision(pick, confidence), .. questions.Skip(1).Select(_ => (MatchDecision?)null)], Model);
}
