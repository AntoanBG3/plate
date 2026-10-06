using GymTracker.Core.Domain;
using GymTracker.Core.Nutrition;
using GymTracker.Core.Onboarding;
using GymTracker.Core.Summary;

namespace GymTracker.E2E.Tests;

/// <summary>
/// Stand-ins for the OpenRouter models on the fixture's second app (E2EFixture.AiBaseUrl), so the pages that only
/// show with a key (the setup chat, the weekly summary, AI parsing) can be driven in the browser.
/// </summary>
public sealed class StubFoodParser : IFoodParser
{
    // One food the database has and one it can't have, so the confirmation shows a match and an AI estimate.
    public Task<FoodParseResult> ParseAsync(string text, CancellationToken ct) => Task.FromResult(new FoodParseResult(
    [
        new ParsedFoodItem("Banana", "banana", 120, FoodState.Raw, null, null, new Per100g(89, 1.1m, 23, 0.3m)),
        new ParsedFoodItem("Zorblax qwimbly", "zorblax qwimbly", 300, FoodState.NotApplicable, null, null, new Per100g(110, 6, 14, 3)),
    ], null, "test/parser"));
}

/// <summary>Learns everything from the first message, as a chatty model would from "I'm a 30-year-old man...".</summary>
public sealed class StubOnboardingAssistant : IOnboardingAssistant
{
    public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

    public Task<OnboardingTurn> NextAsync(OnboardingContext context, CancellationToken ct) => Task.FromResult(new OnboardingTurn(
        "Thanks, that's everything I need. Check the card below and save.",
        new OnboardingFields(Sex: Sex.Male, BirthYear: 1996, HeightCm: 180, WeightKg: 80, ActivityLevel: ActivityLevel.LowActive, Goal: Goal.Maintain),
        ["Looks right"], null, "test/onboarding"));
}

public sealed class StubWeeklySummaryWriter : IWeeklySummaryWriter
{
    public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

    public Task<WeeklySummaryResult> WriteAsync(WeekFacts facts, bool bulgarian, CancellationToken ct) => Task.FromResult(new WeeklySummaryResult(
        new WeeklySummaryText($"You weighed in {facts.WeighIns} times last week.", ["Weigh in every morning."], "Nice steady week."), "test/summary", null));
}
