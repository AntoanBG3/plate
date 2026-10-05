using FluentAssertions;

namespace GymTracker.E2E.Tests;

/// <summary>Layout at phone widths (390 and 320 px): things that must never overlap or overflow (Phase 11).</summary>
[Collection(E2ECollection.Name)]
public class LayoutTests(E2EFixture app)
{
    private const string LongName = "Upper A — heavy pressing and rowing day with extra arm work";

    private static async Task TopbarFitsAsync(Phone phone, string where)
    {
        // Both keep their place in the bar whether or not the title is showing yet (it fades in once scrolled).
        await phone.Page.EvaluateAsync("() => window.scrollTo(0, 400)");
        await phone.AnimationsDoneAsync(".topbar");
        var back = await Phone.BoxAsync(phone.Page.Locator(".topbar-back"));
        var title = await Phone.BoxAsync(phone.Page.Locator(".topbar-title"));
        Phone.Overlap(back, title).Should().BeFalse($"the back label and the title overlap on {where}");
        var width = phone.Page.ViewportSize!.Width;
        (back.X + back.Width).Should().BeLessThanOrEqualTo(width);
        (title.X + title.Width).Should().BeLessThanOrEqualTo(width);
    }

    [Theory]
    [InlineData(E2EFixture.Chromium, 390)]
    [InlineData(E2EFixture.Chromium, 320)]
    [InlineData(E2EFixture.WebKit, 390)]
    [InlineData(E2EFixture.WebKit, 320)]
    public async Task A_long_title_never_runs_into_the_back_label(string browser, int width)
    {
        await using var phone = await app.SignedInPhoneAsync(browser, width);
        var template = await phone.CreateTemplateAsync(app, LongName, "Bench press", "Barbell row", "Overhead press");
        await phone.GoAsync($"/train/templates/{template}");
        await TopbarFitsAsync(phone, "the template editor");

        var session = await phone.StartSessionAsync(template);
        await phone.GoAsync(new Uri(await phone.PostAsync($"/train/sessions/{session}/finish")).PathAndQuery);
        await TopbarFitsAsync(phone, "the summary");
        phone.ShouldHaveNoErrors();
    }
}
