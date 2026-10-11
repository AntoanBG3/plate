using FluentAssertions;
using GymTracker.Core.Domain;
using GymTracker.Core.Time;

namespace GymTracker.E2E.Tests;

/// <summary>Phase 16: log it again faster (SPEC 5.10, 7 Food).</summary>
public class RepeatLoggingFlowTests(E2EFixture app) : IClassFixture<E2EFixture>
{
    private static string Today => UserClock.Today(DateTimeOffset.UtcNow, UserProfile.DefaultTimeZoneId).ToString("yyyy-MM-dd");

    private static string NewEan()
    {
        var digits = Enumerable.Range(0, 12).Select(i => i == 0 ? Random.Shared.Next(1, 10) : Random.Shared.Next(10)).ToArray();
        var sum = digits.Select((d, i) => i % 2 == 0 ? d : d * 3).Sum();
        return string.Concat(digits) + (10 - sum % 10) % 10;
    }

    /// <summary>A shared packaged product with a 40 g serving, as Open Food Facts would give it.</summary>
    private async Task<(string Code, string Name)> ProductAsync(bool unnamed = false)
    {
        var code = NewEan();
        var name = $"Oat bar {code[^5..]}";
        await using var db = app.Db.CreateContext(null);
        db.FoodItems.Add(new FoodItem { Name = name, Barcode = code, KcalPer100g = 400, ProteinPer100g = 10, CarbsPer100g = 60, FatPer100g = 12, DefaultServingGrams = 40, Source = FoodSource.OpenFoodFacts, Unnamed = unnamed });
        await db.SaveChangesAsync();
        return (code, name);
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task A_scanned_product_offers_the_amount_logged_last_time(string browser)
    {
        var (code, name) = await ProductAsync();
        await using var phone = await app.SignedInPhoneAsync(browser);

        await phone.GoAsync($"/food/barcode?date={Today}&meal=Lunch&code={code}");
        (await phone.Page.Locator("#grams").InputValueAsync()).Should().Be("40", "never logged: the serving");
        (await phone.Page.Locator("[data-last-time]").CountAsync()).Should().Be(0);
        await phone.Page.Locator("#grams").FillAsync("250");
        await phone.NavigateByAsync(() => phone.Page.Locator("form[action='/food/log-item'] button[type=submit]").First.TapAsync());

        await phone.GoAsync($"/food/barcode?date={Today}&meal=Lunch&code={code}");
        (await phone.Page.Locator("#grams").InputValueAsync()).Should().Be("250");
        (await phone.Page.Locator("[data-last-time]").TextContentAsync())!.Trim().Should().Be("Last time");
        (await phone.Page.Locator("[data-totals]").TextContentAsync()).Should().Contain("1000 kcal");

        await phone.GoAsync($"/food/add?date={Today}&meal=Lunch&tab=search&q={Uri.EscapeDataString(name)}");
        var row = phone.Page.Locator("#search-results form.portion", new() { HasText = name });
        (await row.Locator("input[name=grams]").InputValueAsync()).Should().Be("250");
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task Add_and_scan_another_logs_three_products_into_one_meal(string browser)
    {
        var products = new[] { await ProductAsync(), await ProductAsync(), await ProductAsync() };
        await using var phone = await app.SignedInPhoneAsync(browser);

        for (var i = 0; i < 2; i++)
        {
            await phone.GoAsync($"/food/barcode?date={Today}&meal=Lunch&code={products[i].Code}");
            var another = phone.Page.Locator("button[name=next][value=scan]");
            var box = await Phone.BoxAsync(another);
            box.Height.Should().BeGreaterThanOrEqualTo(44);
            (box.X + box.Width).Should().BeLessThanOrEqualTo(390);
            await phone.NavigateByAsync(() => another.TapAsync());

            phone.Page.Url.Should().Contain("tab=barcode").And.Contain("meal=Lunch").And.Contain($"date={Today}");
            (await phone.Page.Locator(".toast, .banner").AllInnerTextsAsync()).Should().Contain(t => t.Contains($"Added: {products[i].Name}"));
            (await phone.Page.Locator("[data-start]").IsVisibleAsync()).Should().BeTrue("the next scan is one tap away");
        }

        await phone.GoAsync($"/food/barcode?date={Today}&meal=Lunch&code={products[2].Code}");
        await phone.NavigateByAsync(() => phone.Page.Locator("form[action='/food/log-item'] button[type=submit]").First.TapAsync());

        var lunch = await phone.Page.Locator("#meal-lunch").InnerTextAsync();
        lunch.Should().Contain(products[0].Name).And.Contain(products[1].Name).And.Contain(products[2].Name);
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    public async Task Add_and_scan_another_names_an_unnamed_product(string browser)
    {
        var (code, _) = await ProductAsync(unnamed: true);
        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.GoAsync($"/food/barcode?date={Today}&meal=Lunch&code={code}");
        await phone.Page.Locator("#b-name").FillAsync("Granola square");

        await phone.NavigateByAsync(() => phone.Page.Locator("button[name=next][value=scan]").TapAsync());

        (await phone.Page.Locator(".toast, .banner").AllInnerTextsAsync()).Should().Contain(t => t.Contains("Added: Granola square"));
        await phone.GoAsync($"/food?date={Today}");
        (await phone.Page.Locator("#meal-lunch").InnerTextAsync()).Should().Contain("Granola square");
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    public async Task Recent_at_breakfast_lists_breakfast_foods_first(string browser)
    {
        var (_, porridge) = await ProductAsync();
        var (_, curry) = await ProductAsync();
        await using var phone = await app.SignedInPhoneAsync(browser);
        await using (var db = app.Db.CreateContext(null))
        {
            var ids = db.FoodItems.Where(f => f.Name == porridge || f.Name == curry).ToDictionary(f => f.Name, f => f.Id);
            for (var i = 0; i < 3; i++)
            {
                await phone.PostAsync("/food/log-item", new() { ["date"] = Today, ["meal"] = "Dinner", ["foodItemId"] = ids[curry].ToString(), ["grams"] = "300", ["source"] = "Search" });
            }

            await phone.PostAsync("/food/log-item", new() { ["date"] = Today, ["meal"] = "Breakfast", ["foodItemId"] = ids[porridge].ToString(), ["grams"] = "60", ["source"] = "Search" });
        }

        await phone.GoAsync($"/food/add?date={Today}&meal=Breakfast&tab=recent");
        (await phone.Page.Locator("form[action='/food/log-recent'] .row-title").First.TextContentAsync())!.Trim().Should().Be(porridge);
        phone.ShouldHaveNoErrors();
    }
}
