using FluentAssertions;
using Plate.Core.Domain;
using Plate.Core.Time;

namespace Plate.E2E.Tests;

/// <summary>Phase 18: a recipe built in the editor, then logged from Recent and by typing its name (SPEC 5.12, 7 Food).</summary>
public class RecipeFlowTests(E2EFixture app) : IClassFixture<E2EFixture>
{
    private static string Today => UserClock.Today(DateTimeOffset.UtcNow, UserProfile.DefaultTimeZoneId).ToString("yyyy-MM-dd");

    [Theory]
    [InlineData(E2EFixture.Chromium, false)]
    [InlineData(E2EFixture.WebKit, false)]
    [InlineData(E2EFixture.Chromium, true)]
    public async Task A_recipe_is_built_from_typed_and_found_ingredients_and_logged_by_weight(string browser, bool bulgarian)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.WithTargetsAsync(app, bulgarian);
        string T(string en, string bg) => bulgarian ? bg : en;
        var name = "Lentil stew " + Guid.NewGuid().ToString("N")[..4];

        await phone.GoAsync($"/food/add?date={Today}&meal=Dinner&tab=recent");
        await phone.NavigateByAsync(() => phone.Page.Locator("a[href^='/food/recipes/new']").TapAsync());
        await phone.Page.Locator("#r-name").FillAsync(name);

        // Typed ingredients become Check foods rows.
        await phone.Page.Locator("#r-text").FillAsync("300 g lentils, 200 g carrots");
        await phone.Page.RunAndWaitForResponseAsync(() => phone.Page.Locator("[data-recipe-parse]").TapAsync(), r => r.Url.Contains("/food/recipes/parse"));
        var rows = phone.Page.Locator("#recipe-rows .result");
        await rows.Nth(1).WaitForAsync();
        (await rows.CountAsync()).Should().Be(2);
        (await phone.Page.Locator("#r-text").InputValueAsync()).Should().BeEmpty("the typed words are now rows");

        // A food found by search is added at its serving or 100 g; the search clears.
        await phone.Page.Locator("[data-module=recipe-editor] input[name=q]").FillAsync("olive oil");
        var result = phone.Page.Locator("#recipe-search-results [data-recipe-add]").First;
        await result.WaitForAsync();
        await phone.Page.RunAndWaitForResponseAsync(() => result.TapAsync(), r => r.Url.Contains("/food/recipes/ingredient"));
        await rows.Nth(2).WaitForAsync();
        (await phone.Page.Locator("#recipe-search-results [data-recipe-add]").CountAsync()).Should().Be(0);
        await rows.Nth(2).Locator("input[name$='.Grams']").FillAsync("20");

        await phone.Page.Locator("#r-yield").FillAsync("1200");
        var summary = await phone.Page.Locator("[data-recipe-summary]").TextContentAsync();
        summary.Should().MatchRegex(T(@"^\d+ kcal and [\d.]+ g protein per 100 g, \d+ kcal in all$", @"^\d+ ккал и [\d.]+ г протеин на 100 г, общо \d+ ккал$"));
        (await phone.Page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth")).Should().BeTrue("nothing scrolls sideways at 390 px");

        await phone.NavigateByAsync(() => phone.Page.Locator("button[type=submit]", new() { HasText = T("Save recipe", "Запази рецептата") }).TapAsync());
        new Uri(phone.Page.Url).Query.Should().Contain("tab=recent");
        var recipe = phone.Page.Locator("[data-recipes] form.portion", new() { HasText = name });
        (await recipe.TextContentAsync()).Should().Contain(T("3 ingredients", "3 съставки"));
        var kcalPer100 = int.Parse(System.Text.RegularExpressions.Regex.Match(summary!, @"^\d+").Value);
        // The Bulgarian text keeps the number and unit together with a non-breaking space.
        (await recipe.TextContentAsync()).Should().MatchRegex($@"\b{kcalPer100}\s{T("kcal", "ккал")}", "the saved values are those previewed");

        // Logged from Recent by weight.
        await recipe.Locator("input[name=grams]").FillAsync("350");
        await phone.NavigateByAsync(() => recipe.Locator("button[type=submit]").TapAsync());
        new Uri(phone.Page.Url).AbsolutePath.Should().Be("/food");
        (await phone.Page.Locator("main").InnerTextAsync()).Should().Contain(name).And.Contain("350");

        // And it is the account's own food when typed.
        await phone.GoAsync($"/food/add?date={Today}&meal=Dinner&tab=text");
        await phone.Page.Locator("textarea[name=text], input[name=text]").First.FillAsync($"350 g {name.ToLowerInvariant()}");
        await phone.NavigateByAsync(() => phone.Page.Locator("form[action='/food/parse'] button[type=submit]").First.TapAsync());
        (await phone.Page.Locator(".result [data-using]").First.TextContentAsync()).Should().Contain(name);
        (await phone.Page.Locator(".result [data-state-choice]").CountAsync()).Should().Be(0);
        phone.ShouldHaveNoErrors();
    }
}
