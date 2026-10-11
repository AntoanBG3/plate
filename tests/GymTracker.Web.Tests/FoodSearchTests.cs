using FluentAssertions;

namespace GymTracker.Web.Tests;

/// <summary>The Search tab: plain foods first, each food once (SPEC 7 Food, Phase 11).</summary>
public class FoodSearchTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Theory]
    [InlineData("banana", "Bananas, raw")]
    [InlineData("Banana", "Bananas, raw")]
    [InlineData("rice", "Rice")]
    [InlineData("oats", "Oats")]
    public async Task A_food_whose_name_starts_with_the_search_comes_first(string query, string startsWith)
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var results = await s.Foods.SearchForPickerAsync(query, 20, default);
        results.First().Name.Should().StartWith(startsWith);
    }

    [Fact]
    public async Task The_same_food_from_two_sources_is_listed_once()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var results = await s.Foods.SearchForPickerAsync("banana", 20, default);
        results.Count(r => r.Name == "Bananas, raw").Should().Be(1);
    }

    [Fact]
    public async Task The_search_tab_lists_your_foods_and_the_database_apart()
    {
        await using var app = new AppFactory(fixture);
        var (client, _) = await app.SignedInClientAsync();
        var html = await client.GetStringAsync("/food/add?date=2026-09-28&meal=Lunch&tab=search&q=banana");
        html.Should().Contain("Food database").And.NotContain("Saved foods");
        html.Should().Contain("id=\"search-results\"").And.Contain("hx-trigger=\"input changed delay:300ms");
    }
}
