using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.E2E.Tests;

/// <summary>The confirmation list after typing what was eaten (SPEC 7 Food, Phase 11).</summary>
public class ConfirmFoodsTests(E2EFixture app) : IClassFixture<E2EFixture>
{
    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task A_row_can_be_switched_to_another_food_and_saves_with_it(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.GoAsync("/food/add?meal=Lunch&tab=text");
        await phone.Page.Locator("textarea[name=text], input[name=text]").First.FillAsync("150 g cooked rice");
        await phone.NavigateByAsync(() => phone.Page.Locator("form[action='/food/parse'] button[type=submit]").First.TapAsync());

        // The row is named after the database food it matched, with its state beside it.
        var row = phone.Page.Locator(".result").First;
        var name = row.Locator("textarea[name$='Description']");
        var matched = await name.InputValueAsync();
        matched.Should().NotBe("cooked rice");
        (await row.Locator("[data-using]").TextContentAsync()).Should().Be($"Database: {matched}");
        (await row.Locator("[data-status]").TextContentAsync()).Should().Be("cooked");

        await row.Locator("[data-change-food]").TapAsync();
        var search = phone.Page.Locator("#change-food-sheet [data-change-search]");
        // The sheet opens already searching for the row's name; wait for the answer to this search, not that one.
        await phone.Page.RunAndWaitForResponseAsync(() => search.FillAsync("brown rice"), r => r.Url.Contains("/food/candidates") && r.Url.Contains("brown"));
        await phone.Page.WaitForFunctionAsync("() => !document.querySelector('#change-food-results.htmx-settling, #change-food-results.htmx-swapping')");
        var first = phone.Page.Locator("#change-food-results [data-candidate]").First;
        await first.WaitForAsync(new() { Timeout = 5000 });
        var pickedId = int.Parse((await first.GetAttributeAsync("data-candidate"))!);
        var pickedName = await first.GetAttributeAsync("data-name");
        var pickedStatus = await first.GetAttributeAsync("data-status");
        await first.TapAsync();

        (await row.Locator("[data-using]").TextContentAsync()).Should().Be($"Database: {pickedName}");
        (await name.InputValueAsync()).Should().Be(pickedName, "the row is renamed after the food picked");
        (await row.Locator("[data-status]").IsHiddenAsync()).Should().Be(string.IsNullOrEmpty(pickedStatus));
        await phone.NavigateByAsync(() => phone.Page.Locator("form[action='/food/confirm'] button[type=submit]").Last.TapAsync());

        await using var db = app.Db.CreateContext(null);
        var saved = await db.FoodLog.IgnoreQueryFilters().OrderByDescending(e => e.Id).FirstAsync();
        saved.FoodItemId.Should().Be(pickedId);
        saved.Description.Should().Be(pickedName);
        saved.Status.Should().Be(string.IsNullOrEmpty(pickedStatus) ? null : pickedStatus);

        // The food log shows it as "name · 150 g" with the status chip.
        var title = phone.Page.Locator(".swipe-row .entry-title").Last;
        (await title.TextContentAsync()).Should().StartWith(pickedName).And.Contain("150 g");
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task An_unticked_row_fades(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.GoAsync("/food/add?meal=Lunch&tab=text");
        await phone.Page.Locator("textarea[name=text], input[name=text]").First.FillAsync("150 g cooked rice, a banana");
        await phone.NavigateByAsync(() => phone.Page.Locator("form[action='/food/parse'] button[type=submit]").First.TapAsync());

        var row = phone.Page.Locator(".result").Nth(1);
        await row.Locator(".check-input").UncheckAsync();
        await phone.Page.WaitForTimeoutAsync(400);
        var opacity = await row.Locator(".result-amount").EvaluateAsync<string>("e => getComputedStyle(e).opacity");
        double.Parse(opacity, System.Globalization.CultureInfo.InvariantCulture).Should().BeLessThan(0.6);
        phone.ShouldHaveNoErrors();
    }
}
