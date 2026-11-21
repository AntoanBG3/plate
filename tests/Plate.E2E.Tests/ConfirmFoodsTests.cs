using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Plate.E2E.Tests;

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

        // The sheet opens already searching for the row's name. Typing waits for that answer: the tap's click sets the
        // field and starts that search, and text typed before the click has run would be replaced by it.
        await phone.Page.RunAndWaitForResponseAsync(() => row.Locator("[data-change-food]").TapAsync(), r => r.Url.Contains("/food/candidates"));
        var search = phone.Page.Locator("#change-food-sheet [data-change-search]");
        try
        {
            await phone.Page.RunAndWaitForResponseAsync(() => search.FillAsync("brown rice"), r => r.Url.Contains("/food/candidates") && r.Url.Contains("brown"));
        }
        catch (TimeoutException e)
        {
            // Seen on GitHub's runners in WebKit only (2026-11-15), never here: say what the page did instead.
            var searches = string.Join(", ", phone.Requests.Where(r => r.Contains("/food/candidates")));
            throw new TimeoutException($"No answer to the typed search. Field: \"{await search.InputValueAsync()}\". Searches sent: {searches}", e);
        }
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
        // Until the fade has run, however long the machine takes to start it: after a fixed 400 ms a GitHub runner
        // was still at 0.83 (2026-11-15).
        var amount = await row.Locator(".result-amount").ElementHandleAsync();
        await phone.Page.WaitForFunctionAsync("e => Number(getComputedStyle(e).opacity) < 0.6", amount, new() { Timeout = 10_000 });
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task A_counted_amount_follows_the_food_picked_for_its_row_until_the_grams_are_edited(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.GoAsync("/food/add?meal=Breakfast&tab=text");
        await phone.Page.Locator("textarea[name=text], input[name=text]").First.FillAsync("две закуски със сирене с лютеница от лидл");
        await phone.NavigateByAsync(() => phone.Page.Locator("form[action='/food/parse'] button[type=submit]").First.TapAsync());

        var row = phone.Page.Locator(".result").First;
        var grams = row.Locator("input[name$='Grams']");
        (await grams.InputValueAsync()).Should().Be("180", "two pieces of 90 g");
        (await row.Locator(".result-notes").TextContentAsync()).Should().Contain("estimated from the recipe");

        async Task PickAsync(string query, string piece)
        {
            await phone.Page.RunAndWaitForResponseAsync(() => row.Locator("[data-change-food]").TapAsync(), r => r.Url.Contains("/food/candidates"));
            var search = phone.Page.Locator("#change-food-sheet [data-change-search]");
            await phone.Page.RunAndWaitForResponseAsync(() => search.FillAsync(query), r => r.Url.Contains("/food/candidates") && r.Url.Contains(Uri.EscapeDataString(query.Split(' ')[0])));
            await phone.Page.WaitForFunctionAsync("() => !document.querySelector('#change-food-results.htmx-settling, #change-food-results.htmx-swapping')");
            var pick = phone.Page.Locator($"#change-food-results [data-candidate][data-piece='{piece}']").First;
            await pick.WaitForAsync(new() { Timeout = 5000 });
            await pick.TapAsync();
        }

        await PickAsync("кроасан с масло", "57");
        (await grams.InputValueAsync()).Should().Be("114", "two croissants of 57 g");
        var notes = await row.Locator(".result-notes").TextContentAsync();
        notes.Should().Contain("2 × a piece of 57 g").And.NotContain("estimated from the recipe", "the croissant's values are from its label");

        await grams.FillAsync("100");
        await PickAsync("закуска с гауда", "90");
        (await grams.InputValueAsync()).Should().Be("100", "grams the user typed are kept");
    }
}
