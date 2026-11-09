using FluentAssertions;
using Microsoft.Playwright;

namespace Plate.E2E.Tests;

/// <summary>The template editor saves each field on change, with errors next to the field (SPEC 7, Phase 11).</summary>
public class TemplateEditorTests(E2EFixture app) : IClassFixture<E2EFixture>
{
    private static ILocator Card(IPage page, int n = 0) => page.Locator(".exercise").Nth(n);

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task A_changed_field_saves_without_a_button(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        var template = await phone.CreateTemplateAsync(app, "Upper", "Bench press");
        await phone.GoAsync($"/train/templates/{template}");
        (await Card(phone.Page).GetByText("Save changes").CountAsync()).Should().Be(0);

        await Card(phone.Page).Locator("input[name=workingSets]").FillAsync("4");
        await phone.Page.Locator(".large-title").TapAsync();
        await Card(phone.Page).Locator("form.is-saved").WaitForAsync(new() { Timeout = 5000 });

        await phone.GoAsync($"/train/templates/{template}");
        (await Card(phone.Page).Locator("input[name=workingSets]").InputValueAsync()).Should().Be("4");
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task Renaming_saves_on_change_and_updates_the_titles(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        var template = await phone.CreateTemplateAsync(app, "Upper", "Bench press");
        await phone.GoAsync($"/train/templates/{template}");

        await phone.Page.Locator("#name").FillAsync("Upper B");
        await phone.Page.Locator(".large-title").TapAsync();
        await phone.Page.Locator("form.is-saved #name").WaitForAsync(new() { Timeout = 5000 });
        (await phone.Page.Locator(".large-title").TextContentAsync()).Should().Be("Upper B");

        await phone.GoAsync($"/train/templates/{template}");
        (await phone.Page.Locator("#name").InputValueAsync()).Should().Be("Upper B");
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task A_field_still_being_typed_is_saved_when_leaving(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        var template = await phone.CreateTemplateAsync(app, "Upper", "Bench press");
        await phone.GoAsync($"/train/templates/{template}");

        await Card(phone.Page).Locator("input[name=restSeconds]").FillAsync("90");
        await phone.NavigateByAsync(() => phone.TapAsync(".topbar-back"));
        await phone.GoAsync($"/train/templates/{template}");
        (await Card(phone.Page).Locator("input[name=restSeconds]").InputValueAsync()).Should().Be("90");
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task An_invalid_rep_range_is_explained_on_the_card_and_keeps_what_was_typed(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        var template = await phone.CreateTemplateAsync(app, "Upper", "Bench press");
        await phone.GoAsync($"/train/templates/{template}");

        await Card(phone.Page).Locator("input[name=repMin]").FillAsync("12");
        await Card(phone.Page).Locator("input[name=repMax]").FillAsync("8");
        await phone.Page.Locator(".large-title").TapAsync();
        var error = Card(phone.Page).Locator(".field-error");
        await error.WaitForAsync(new() { Timeout = 5000 });
        (await error.TextContentAsync()).Should().Contain("Rep range");
        (await Card(phone.Page).Locator("input[name=repMin]").InputValueAsync()).Should().Be("12");
        (await Card(phone.Page).Locator("input[name=repMax]").InputValueAsync()).Should().Be("8");
        (await phone.Page.Locator("#main > .banner.is-error").CountAsync()).Should().Be(0, "the error belongs to the card, not the page top");

        await phone.GoAsync($"/train/templates/{template}");
        (await Card(phone.Page).Locator("input[name=repMin]").InputValueAsync()).Should().Be("8", "nothing invalid was saved");
        phone.ShouldHaveNoErrors();
    }
}
