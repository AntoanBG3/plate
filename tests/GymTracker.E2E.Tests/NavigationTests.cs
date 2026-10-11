using FluentAssertions;

namespace GymTracker.E2E.Tests;

/// <summary>Soft navigation: taps during a page transition, and keeping the user's place after a change (Phase 11).</summary>
public class NavigationTests(E2EFixture app) : IClassFixture<E2EFixture>
{
    private static async Task<bool> InViewAsync(Phone phone, string selector) =>
        await phone.Page.Locator(selector).First.EvaluateAsync<bool>(
            "e => { const r = e.getBoundingClientRect(); return r.top >= 0 && r.top < innerHeight - 80; }");

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task A_tap_during_a_page_transition_reaches_the_new_page(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.CreateTemplateAsync(app, "Upper", "Bench press");
        await phone.GoAsync("/today");

        await phone.TapAsync(".tabbar a[href='/train']");
        await phone.Page.WaitForURLAsync("**/train");
        // Straight away, while the slide is still running.
        await phone.Page.WaitForTimeoutAsync(60);
        await phone.NavigateByAsync(() => phone.Page.Locator("a[href='/train/history']").TapAsync(new() { Force = true }));
        phone.Page.Url.Should().EndWith("/train/history");
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task Removing_the_last_exercise_of_a_long_template_keeps_the_place(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        var template = await phone.CreateTemplateAsync(app, "Long", "Bench press", "Barbell row", "Overhead press", "Dumbbell row", "Lat pulldown", "Dip");
        await phone.GoAsync($"/train/templates/{template}");
        await phone.Page.EvaluateAsync("() => window.scrollTo(0, document.body.scrollHeight)");
        var before = await phone.Page.EvaluateAsync<double>("() => scrollY");
        before.Should().BeGreaterThan(500);

        var last = phone.Page.Locator(".exercise").Last;
        await last.Locator("[data-menu-trigger]").TapAsync();
        await phone.NavigateByAsync(() => last.Locator("form[action$='/remove'] button").TapAsync());

        (await phone.Page.EvaluateAsync<double>("() => scrollY")).Should().BeGreaterThan(before - 400, "the page stays where it was");
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task Swapping_a_late_exercise_comes_back_to_it(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        var template = await phone.CreateTemplateAsync(app, "Long", "Bench press", "Barbell row", "Overhead press", "Dumbbell row", "Lat pulldown");
        await phone.StartSessionAsync(template);
        var card = phone.Page.Locator(".exercise").Nth(4);
        var id = await card.GetAttributeAsync("id");
        await card.ScrollIntoViewIfNeededAsync();
        await card.Locator("[data-menu-trigger]").TapAsync();
        await phone.NavigateByAsync(() => card.Locator("a[href$='/swap']").TapAsync());

        await phone.NavigateByAsync(() => phone.Page.Locator("button.row-main").Filter(new() { HasText = "Chin-up" }).TapAsync());
        (await InViewAsync(phone, $"#{id}")).Should().BeTrue("the swapped exercise is shown, not the top of the page");
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task Quick_add_shows_the_meal_it_went_into(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.GoAsync("/food");
        var dinner = phone.Page.Locator("#meal-dinner");
        await dinner.ScrollIntoViewIfNeededAsync();
        await dinner.Locator("[data-sheet-open='quick-sheet']").TapAsync();
        await phone.Page.Locator("#quick-body input[name=kcal]").FillAsync("650");
        await phone.NavigateByAsync(() => phone.Page.Locator("#quick-body button[type=submit]").TapAsync());

        (await InViewAsync(phone, "#meal-dinner")).Should().BeTrue();
        (await dinner.Locator(".row-link").First.TextContentAsync()).Should().Contain("650");
        phone.ShouldHaveNoErrors();
    }
}
