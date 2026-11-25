using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Plate.Core.Domain;

namespace Plate.E2E.Tests;

/// <summary>Numbers follow an amount as it is typed, before Save (QA 2026-11-24 L6, SPEC 7 Food).</summary>
public class LiveNumbersTests(E2EFixture app) : IClassFixture<E2EFixture>
{
    private static string Today => Plate.Core.Time.UserClock.Today(DateTimeOffset.UtcNow, UserProfile.DefaultTimeZoneId).ToString("yyyy-MM-dd");

    /// <summary>A shared product at 400 kcal and 10 g protein per 100 g, logged at 200 g for lunch.</summary>
    private async Task<(int EntryId, string Name)> LoggedAsync(Phone phone)
    {
        var name = $"Live bar {Guid.NewGuid():N}"[..17];
        int foodId;
        await using (var db = app.Db.CreateContext(null))
        {
            var food = new FoodItem { Name = name, KcalPer100g = 400, ProteinPer100g = 10, CarbsPer100g = 60, FatPer100g = 12, Source = FoodSource.OpenFoodFacts };
            db.FoodItems.Add(food);
            await db.SaveChangesAsync();
            foodId = food.Id;
        }

        await phone.PostAsync("/food/log-item", new() { ["date"] = Today, ["meal"] = "Lunch", ["foodItemId"] = foodId.ToString(), ["source"] = "Search", ["grams"] = "200" });
        await using var check = app.Db.CreateContext(null);
        return (await check.FoodLog.IgnoreQueryFilters().Where(e => e.FoodItemId == foodId).Select(e => e.Id).SingleAsync(), name);
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task An_entrys_numbers_follow_its_amount_before_save(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        var (id, _) = await LoggedAsync(phone);

        await phone.GoAsync($"/food/entries/{id}");
        var stats = phone.Page.Locator("[data-entry-stats] [data-stat]");
        (await stats.Nth(0).TextContentAsync()).Should().Be("800");
        await phone.Page.Locator("#grams").FillAsync("100");

        (await stats.Nth(0).TextContentAsync()).Should().Be("400");
        (await stats.Nth(1).TextContentAsync()).Should().Be("10");
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task A_saved_meals_items_and_total_follow_their_grams(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.GoAsync("/food");
        await LoggedAsync(phone);
        var meal = $"Live meal {Guid.NewGuid():N}"[..18];
        await phone.PostAsync("/food/saved-meals", new() { ["date"] = Today, ["meal"] = "Lunch", ["name"] = meal });

        await phone.GoAsync($"/food/add?date={Today}&meal=Dinner&tab=recent");
        await phone.Page.Locator("button.row-link", new() { HasText = meal }).TapAsync();
        var item = phone.Page.Locator("#food-sheet-body [data-per100]").First;
        await item.WaitForAsync();
        await item.Locator("input[name=grams]").FillAsync("100");

        (await item.Locator("[data-totals]").TextContentAsync()).Should().Contain("400 kcal");
        (await phone.Page.Locator("#food-sheet-body [data-meal-total]").TextContentAsync()).Should().Contain("400 kcal").And.Contain("10.0 g protein");
        phone.ShouldHaveNoErrors();
    }
}
