using FluentAssertions;

namespace GymTracker.E2E.Tests;

/// <summary>Colours, focus and motion that apply across the app (Phase 11).</summary>
public class VisualSystemTests(E2EFixture app) : IClassFixture<E2EFixture>
{
    // Relative luminance of an rgb() or rgba() colour composited over an opaque background colour.
    private const string Luminance = """
        (fg, bg) => {
            const parse = (c) => c.match(/[\d.]+/g).map(Number);
            const [r, g, b, a = 1] = parse(fg);
            const [R, G, B] = parse(bg);
            const mix = [r * a + R * (1 - a), g * a + G * (1 - a), b * a + B * (1 - a)];
            const lin = (v) => { v /= 255; return v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4; };
            return 0.2126 * lin(mix[0]) + 0.7152 * lin(mix[1]) + 0.0722 * lin(mix[2]);
        }
        """;

    private static async Task<(double Thumb, double Track)> SegmentLuminanceAsync(Phone phone)
    {
        var values = await phone.Page.EvaluateAsync<double[]>($$"""
            () => {
                const luminance = {{Luminance}};
                const card = getComputedStyle(document.querySelector(".list")).backgroundColor;
                const control = document.querySelector(".list .segmented");
                const track = luminance(getComputedStyle(control).backgroundColor, card);
                const thumbColor = getComputedStyle(control.querySelector(".segment:has(:checked)")).backgroundColor;
                return [luminance(thumbColor, card), track];
            }
            """);
        return (values[0], values[1]);
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task The_selected_segment_stands_out_from_its_track_in_a_card(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.GoAsync("/settings/general");
        var (thumb, track) = await SegmentLuminanceAsync(phone);
        thumb.Should().BeGreaterThan(track * 1.5, "in dark mode the selected segment must be lighter than the track");
        phone.ShouldHaveNoErrors();
    }

    // The 44 px minimum for field-row inputs used to stretch the switch to 51 x 44 with its knob stuck at the top.
    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task A_switch_in_a_field_row_keeps_its_size(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.GoAsync("/settings/general");
        var box = await phone.Page.Locator(".field-row .switch").First.BoundingBoxAsync();
        box!.Width.Should().Be(51);
        box.Height.Should().Be(31);
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task Typed_values_read_brighter_than_placeholders(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.GoAsync("/settings/profile");
        var colors = await phone.Page.EvaluateAsync<string[]>("""
            () => {
                const filled = document.querySelector(".field-row input[name=heightCm], .field-row input[type=email], .field-row input[name=displayName]");
                const style = getComputedStyle(document.documentElement);
                return [getComputedStyle(filled).color, style.getPropertyValue("--label").trim()];
            }
            """);
        var label = await phone.Page.EvaluateAsync<string>("c => { const d = document.createElement('div'); d.style.color = c; document.body.append(d); const v = getComputedStyle(d).color; d.remove(); return v; }", colors[1]);
        colors[0].Should().Be(label, "an entered value is shown in the label colour, not the placeholder's grey");
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task A_missing_field_is_scrolled_into_view_below_the_top_bar(string browser)
    {
        var (email, password) = await app.App.CreateUserAsync(onboarded: false);
        await using var phone = await app.PhoneAsync(browser);
        await phone.SignInAsync(email, password);
        await phone.GoAsync("/welcome");
        await phone.Page.EvaluateAsync("() => window.scrollTo(0, document.body.scrollHeight)");
        await phone.Page.Locator("form button[type=submit]").Last.TapAsync();
        await phone.Page.WaitForTimeoutAsync(400);

        // The browser scrolls to the first field still missing; it must not end up under the top bar.
        var box = await phone.Page.EvaluateAsync<double[]>("""
            () => [document.querySelector("input:invalid").closest(".segmented, .field-row, .choice-row").getBoundingClientRect().top,
                   document.querySelector(".topbar").getBoundingClientRect().bottom]
            """);
        box[0].Should().BeGreaterThanOrEqualTo(box[1], "the field with the error is not hidden under the top bar");
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task Switching_theme_is_instant_without_a_crossfade(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.GoAsync("/settings/general");
        await phone.Page.EvaluateAsync("""
            () => {
                window.__transitions = 0;
                const start = document.startViewTransition && document.startViewTransition.bind(document);
                if (start) {
                    document.startViewTransition = (cb) => { window.__transitions++; return start(cb); };
                }
            }
            """);
        await phone.NavigateByAsync(() => phone.Page.Locator("label.segment:has(input[value=Light])").TapAsync());
        (await phone.Page.EvaluateAsync<string>("() => document.documentElement.dataset.theme")).Should().Be("light");
        (await phone.Page.EvaluateAsync<int>("() => window.__transitions")).Should().Be(0);

        // Light mode keeps empty meter tracks visible on white.
        await phone.WithTargetsAsync(app);
        await phone.GoAsync("/food");
        var alpha = await phone.Page.Locator(".meter").First.EvaluateAsync<double>(
            "e => { const m = getComputedStyle(e).backgroundColor.match(/[\\d.]+/g).map(Number); return m.length > 3 ? m[3] : 1; }");
        alpha.Should().BeGreaterThanOrEqualTo(0.2);
        phone.ShouldHaveNoErrors();
    }
}
