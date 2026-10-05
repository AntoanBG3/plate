using FluentAssertions;
using GymTracker.Web.Features.Food;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace GymTracker.Web.Tests;

/// <summary>The built-in parser with database ranking only (no AI key), on the QA pass's cases (Phase 11).</summary>
[Collection(PostgresCollection.Name)]
public class FallbackMatchTests(PostgresFixture fixture, ITestOutputHelper output)
{
    private async Task<(string Name, decimal Grams, Confidence Confidence)> MatchAsync(string text)
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var (_, items, _) = await s.ParseRuns.ParseAsync(text, default);
        var item = items.Single();
        var name = item.FoodItemId is { } id ? await s.Db.FoodItems.Where(f => f.Id == id).Select(f => f.Name).SingleAsync() : "(none)";
        output.WriteLine($"{text} -> {name}, {item.Grams} g, {item.Confidence}");
        return (name, item.Grams, item.Confidence);
    }

    [Fact]
    public async Task A_banana_is_a_raw_banana()
    {
        var (name, grams, confidence) = await MatchAsync("a banana");
        name.Should().Be("Bananas, raw");
        grams.Should().Be(100, "this food has no serving size, so 100 g is assumed and said");
    }

    [Fact]
    public async Task Cooked_rice_is_plain_rice_not_wild_rice()
    {
        // White and brown are both plain rice here; the QA case was "Wild rice", a variety nobody asked for.
        var (name, grams, _) = await MatchAsync("150 g cooked rice");
        name.Should().StartWith("Rice,").And.Contain("cooked");
        grams.Should().Be(150);
    }

    [Fact]
    public async Task Chicken_breast_is_the_meat_without_skin()
    {
        var (name, _, _) = await MatchAsync("200 g chicken breast");
        name.Should().Contain("breast").And.NotContain("meat and skin");
    }
}
