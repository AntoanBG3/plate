using FluentAssertions;

namespace Plate.Web.Tests;

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
    public async Task Bulgarian_typed_in_Latin_letters_finds_its_Cyrillic_products()
    {
        // QA 2026-11-24 M4: the "Боза" products have Cyrillic names only.
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var results = await s.Foods.SearchForPickerAsync("boza", 20, default);
        results.Should().NotBeEmpty().And.Contain(r => r.Name.StartsWith("Боза", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("банан", "Банани, сурови")]
    [InlineData("мандарина", "Мандарини, сурови")]
    [InlineData("кисело мляко", "Кисело мляко")]
    [InlineData("кафе", "Кафе, варено / филтърно")]
    public async Task The_plain_food_comes_before_products_and_look_alikes(string query, string first)
    {
        // QA 2026-11-24 M5: "банан" listed "Банан нектар" first, "кафе" liqueurs and coffee beans, "мандарина" a
        // marinade, and "кисело мляко" twenty branded tubs before the plain food.
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var results = await s.Foods.SearchForPickerAsync(query, 20, default);
        results.First().NameBg.Should().Be(first);
    }

    [Fact]
    public async Task Plain_rice_comes_before_rice_crackers_and_flour()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var results = await s.Foods.SearchForPickerAsync("rice", 20, default);
        results.Take(5).Should().OnlyContain(r => r.Name.StartsWith("Rice,"));
    }

    [Fact]
    public async Task Espresso_comes_before_coffee_liqueur_and_a_marinade_after_the_mandarins()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var coffee = (await s.Foods.SearchForPickerAsync("кафе", 20, default)).Select(r => r.NameBg).ToList();
        coffee.IndexOf("Кафе еспресо").Should().BeGreaterThanOrEqualTo(0).And.BeLessThan(coffee.FindIndex(n => n!.StartsWith("Кафе ликьор")));

        var mandarins = (await s.Foods.SearchForPickerAsync("мандарина", 20, default)).Select(r => r.NameBg ?? r.Name).ToList();
        var marinade = mandarins.FindIndex(n => n.Contains("марина") && !n.Contains("мандарин"));
        if (marinade >= 0) marinade.Should().BeGreaterThan(mandarins.FindLastIndex(n => n.StartsWith("Мандарин")));
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
