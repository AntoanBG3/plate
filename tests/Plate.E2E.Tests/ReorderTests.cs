using FluentAssertions;

namespace Plate.E2E.Tests;

/// <summary>Reorder mode: drag handles, and up and down buttons for anyone who can't drag (SPEC 7, Phase 11).</summary>
public class ReorderTests(E2EFixture app) : IClassFixture<E2EFixture>
{
    private static async Task<IEnumerable<string>> NamesAsync(Phone phone) =>
        (await phone.Page.Locator(".exercise .ex-name").AllTextContentsAsync()).Select(n => n.Trim());

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task Dragging_the_last_exercise_to_the_top_saves_the_new_order(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        var template = await phone.CreateTemplateAsync(app, "Upper", "Bench press", "Barbell row", "Dip");
        await phone.GoAsync($"/train/templates/{template}");
        await phone.Page.Locator(".topbar-trailing [data-menu-trigger]").TapAsync();
        await phone.NavigateByAsync(() => phone.Page.Locator(".topbar-trailing a[href$='/reorder']").TapAsync());

        var handles = phone.Page.Locator("[data-reorder] .drag-handle");
        var from = await Phone.BoxAsync(handles.Nth(2));
        var to = await Phone.BoxAsync(handles.Nth(0));
        await phone.Page.Mouse.MoveAsync(from.X + from.Width / 2, from.Y + from.Height / 2);
        await phone.Page.Mouse.DownAsync();
        for (var i = 1; i <= 10; i++)
        {
            var y = from.Y + from.Height / 2 + (to.Y - from.Y - 10) * i / 10;
            await phone.Page.Mouse.MoveAsync(from.X + from.Width / 2, y);
        }

        await phone.Page.Mouse.UpAsync();
        await phone.NavigateByAsync(() => phone.Page.Locator("[data-reorder] button[type=submit]").TapAsync());
        phone.Page.Url.Should().EndWith($"/train/templates/{template}");
        (await NamesAsync(phone)).Should().Equal("Dip", "Bench press", "Barbell row");
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task The_up_and_down_buttons_reorder_a_session(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        var template = await phone.CreateTemplateAsync(app, "Upper", "Bench press", "Barbell row", "Dip");
        var session = await phone.StartSessionAsync(template);
        await phone.Page.Locator(".exercise").First.Locator("[data-menu-trigger]").TapAsync();
        await phone.NavigateByAsync(() => phone.Page.Locator(".exercise").First.Locator("a[href$='/reorder']").TapAsync());

        await phone.Page.Locator("[data-reorder] li").Nth(0).Locator("[data-move='1']").TapAsync();
        await phone.Page.Locator("[data-reorder] li").Nth(1).Locator("[data-move='1']").TapAsync();
        await phone.NavigateByAsync(() => phone.Page.Locator("[data-reorder] button[type=submit]").TapAsync());

        phone.Page.Url.Should().EndWith($"/train/sessions/{session}");
        (await NamesAsync(phone)).Should().Equal("Barbell row", "Dip", "Bench press");
        phone.ShouldHaveNoErrors();
    }
}
