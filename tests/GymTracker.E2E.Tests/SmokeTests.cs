using FluentAssertions;

namespace GymTracker.E2E.Tests;

public class SmokeTests(E2EFixture app) : IClassFixture<E2EFixture>
{
    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task Every_tab_opens_at_phone_width_without_console_errors(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.GoAsync("/today");

        foreach (var tab in new[] { "train", "food", "progress", "settings", "today" })
        {
            await phone.NavigateByAsync(() => phone.TapAsync($".tabbar a[href='/{tab}']"));
            phone.Page.Url.Should().EndWith("/" + tab);
        }

        phone.ShouldHaveNoErrors();
    }
}
