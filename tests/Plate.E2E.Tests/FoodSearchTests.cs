using FluentAssertions;

namespace Plate.E2E.Tests;

/// <summary>Food search shows results as you type (SPEC 7 Food, Phase 11).</summary>
public class FoodSearchTests(E2EFixture app) : IClassFixture<E2EFixture>
{
    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task Typing_shows_results_without_pressing_enter(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.GoAsync("/food/add?meal=Lunch&tab=search");
        await phone.Page.Locator("input[name=q]").PressSequentiallyAsync("banana", new() { Delay = 40 });

        var first = phone.Page.Locator("#search-results .row-title").First;
        await first.WaitForAsync(new() { Timeout = 5000 });
        (await first.TextContentAsync())!.Trim().Should().Be("Bananas, raw");
        phone.Page.Url.Should().Contain("q=banana", "the address follows, so Back returns to the same results");
        phone.ShouldHaveNoErrors();
    }
}
