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

    // Settings is a hub of rows that each open one page (SPEC 7); every page fits a phone in both languages.
    [Theory]
    [InlineData(E2EFixture.Chromium, 390, false)]
    [InlineData(E2EFixture.Chromium, 320, true)]
    [InlineData(E2EFixture.WebKit, 390, true)]
    [InlineData(E2EFixture.WebKit, 320, false)]
    public async Task Settings_pages_open_from_the_hub_and_fit_the_phone(string browser, int width, bool bulgarian)
    {
        await using var phone = await app.SignedInPhoneAsync(browser, width);
        await phone.WithTargetsAsync(app, bulgarian);
        await phone.GoAsync("/settings");

        var rows = await phone.Page.EvaluateAsync<double[]>("() => [...document.querySelectorAll('main .row-link')].map((r) => r.getBoundingClientRect().height)");
        rows.Should().NotBeEmpty().And.OnlyContain(h => h >= 44, "every row is a full touch target");

        foreach (var page in new[] { "profile", "goal", "training", "general", "security" })
        {
            await phone.NavigateByAsync(() => phone.Page.Locator($"main a[href='/settings/{page}']").TapAsync());
            var overflow = await phone.Page.EvaluateAsync<double>("() => document.documentElement.scrollWidth - window.innerWidth");
            overflow.Should().BeLessThanOrEqualTo(0, $"/settings/{page} does not scroll sideways");
            await TopbarFitsAsync(phone, $"/settings/{page}");
            await phone.NavigateByAsync(() => phone.Page.Locator(".topbar-back").TapAsync());
            new Uri(phone.Page.Url).AbsolutePath.Should().Be("/settings");
        }

        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium, 320, false)]
    [InlineData(E2EFixture.Chromium, 320, true)]
    [InlineData(E2EFixture.WebKit, 320, false)]
    [InlineData(E2EFixture.WebKit, 320, true)]
    [InlineData(E2EFixture.WebKit, 390, true)]
    public async Task The_macro_meters_line_up(string browser, int width, bool bulgarian)
    {
        await using var phone = await app.SignedInPhoneAsync(browser, width);
        await phone.WithTargetsAsync(app, bulgarian);
        await phone.GoAsync("/food");

        var meters = phone.Page.Locator(".macro-grid:not(.quality-grid) .meter");
        (await meters.CountAsync()).Should().Be(3);
        var tops = new List<float>();
        for (var i = 0; i < 3; i++)
        {
            tops.Add((await Phone.BoxAsync(meters.Nth(i))).Y);
        }

        (tops.Max() - tops.Min()).Should().BeLessThan(2, "the protein, carbs and fat meters sit on one line");
        phone.ShouldHaveNoErrors();
    }

    /// <summary>Distance between the middle of the composer's first text line and the middle of its send button.</summary>
    private static Task<double> ComposerOffsetAsync(Phone phone) => phone.Page.EvaluateAsync<double>("""
        () => {
          const ta = document.querySelector('.composer textarea');
          const btn = document.querySelector('.composer .send-btn');
          const s = getComputedStyle(ta);
          const r = ta.getBoundingClientRect();
          const lineMid = r.top + parseFloat(s.paddingTop) + parseFloat(s.lineHeight) / 2;
          const b = btn.getBoundingClientRect();
          return Math.abs(lineMid - (b.top + b.height / 2));
        }
        """);

    [Theory]
    [InlineData(E2EFixture.Chromium, false)]
    [InlineData(E2EFixture.Chromium, true)]
    [InlineData(E2EFixture.WebKit, false)]
    [InlineData(E2EFixture.WebKit, true)]
    public async Task The_composer_text_sits_level_with_the_send_button(string browser, bool bulgarian)
    {
        var (email, password) = await app.App.CreateUserAsync(onboarded: false);
        await using var phone = await app.PhoneAsync(browser, ai: true);
        await phone.SignInAsync(email, password);
        phone.Email = email;
        await phone.WithTargetsAsync(app, bulgarian);

        await phone.GoAsync("/welcome");
        await phone.NavigateByAsync(() => phone.Page.Locator("form[action='/welcome/start'] button[type=submit]").TapAsync());
        (await ComposerOffsetAsync(phone)).Should().BeLessThanOrEqualTo(1, "in the setup chat");

        await phone.NavigateByAsync(() => phone.Page.Locator("form[action='/welcome/skip'] button, a[href*='/welcome/skip']").First.TapAsync());
        await phone.GoAsync("/food/add?tab=text");
        (await ComposerOffsetAsync(phone)).Should().BeLessThanOrEqualTo(1, "in the food composer");
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task Decimals_show_with_a_dot_under_a_Bulgarian_locale_and_a_typed_comma_is_kept_right(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser, locale: "bg-BG");
        var template = await phone.CreateTemplateAsync(app, "Upper", "Bench press");
        await phone.StartSessionAsync(template);
        await phone.LogSetAsync(phone.Page.Locator(".rows.working .set-row").First, "62,5", "10");
        await phone.Page.Locator(".set-row.done").WaitForAsync();

        (await phone.Page.Locator(".set-row.done input[name=weightKg]").InputValueAsync()).Should().Be("62.5");
        (await phone.Page.Locator(".set-row.done input[name=weightKg]").GetAttributeAsync("type")).Should().Be("text",
            "a number input shows the device's decimal comma, unlike the rest of the app");
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium, 320)]
    [InlineData(E2EFixture.Chromium, 390)]
    [InlineData(E2EFixture.WebKit, 320)]
    [InlineData(E2EFixture.WebKit, 390)]
    public async Task Placeholders_fit_their_fields_in_Bulgarian(string browser, int width)
    {
        await using var phone = await app.SignedInPhoneAsync(browser, width);
        await phone.WithTargetsAsync(app, bulgarian: true);
        var template = await phone.CreateTemplateAsync(app, "Горна част", "Bench press");

        foreach (var path in new[] { "/settings/goal", "/settings/security", $"/train/templates/{template}", "/today" })
        {
            await phone.GoAsync(path);
            // Text width of each visible placeholder against the input's content box.
            var clipped = await phone.Page.EvaluateAsync<string[]>("""
                () => {
                    const ctx = document.createElement("canvas").getContext("2d");
                    return [...document.querySelectorAll(".field-row input[placeholder]")]
                        .filter((i) => i.offsetParent && !i.value)
                        .filter((i) => {
                            const s = getComputedStyle(i);
                            ctx.font = `${s.fontWeight} ${s.fontSize} ${s.fontFamily}`;
                            const room = i.clientWidth - parseFloat(s.paddingLeft) - parseFloat(s.paddingRight);
                            return ctx.measureText(i.placeholder).width > room + 1;
                        })
                        .map((i) => `${i.name}: ${i.placeholder}`);
                }
                """);
            clipped.Should().BeEmpty($"every placeholder on {path} is readable in full");
        }

        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium, 320)]
    [InlineData(E2EFixture.WebKit, 320)]
    [InlineData(E2EFixture.WebKit, 390)]
    public async Task Segment_labels_fit_in_Bulgarian(string browser, int width)
    {
        var (email, password) = await app.App.CreateUserAsync(onboarded: false);
        await using var phone = await app.PhoneAsync(browser, width);
        await phone.SignInAsync(email, password);
        phone.Email = email;
        await phone.WithTargetsAsync(app, bulgarian: true);

        foreach (var path in new[] { "/welcome", "/settings/goal", "/settings/general", "/food/add", "/progress" })
        {
            await phone.GoAsync(path);
            // A label wider than its segment, or a segment taller than two lines, is cut off or crowds the control.
            var bad = await phone.Page.EvaluateAsync<string[]>("""
                () => [...document.querySelectorAll(".segmented .segment")]
                    .filter((s) => s.offsetParent)
                    .filter((s) => s.scrollWidth > s.clientWidth + 1 || s.getBoundingClientRect().height > 64)
                    .map((s) => s.textContent.trim())
                """);
            bad.Should().BeEmpty($"every segment label on {path} fits");
        }
    }
}
