using FluentAssertions;
using GymTracker.Core.Domain;
using GymTracker.Core.Nutrition;
using GymTracker.Web.Data.Seed;
using GymTracker.Web.Features.Food;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Tests;

/// <summary>Fiber and saturated fat from foods to entries, saved meals and the seeds (SPEC 4, 6.2, 6.3).</summary>
[Collection(PostgresCollection.Name)]
public class FiberTests(PostgresFixture fixture)
{
    private static readonly DateOnly Day = new(2026, 10, 5);

    [Fact]
    public async Task Entries_store_fiber_scaled_to_the_portion_and_unknown_stays_unknown()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        await s.FoodLog.AddAsync([
            new NewEntry(Day, Meal.Breakfast, "Oats", 50, new Per100g(380, 13, 60, 7, 10, 1.2m), null, EntrySource.Manual),
            new NewEntry(Day, Meal.Breakfast, "Mystery bar", 40, new Per100g(400, 10, 50, 15), null, EntrySource.Manual),
        ], default);

        var day = await s.FoodLog.DayAsync(Day, default);

        var oats = day.Entries.Single(e => e.Description == "Oats");
        (oats.FiberG, oats.SaturatedFatG).Should().Be((5m, 0.6m));
        day.Entries.Single(e => e.Description == "Mystery bar").FiberG.Should().BeNull();
        day.Totals.FiberG.Should().Be(5, "only the known value adds up");

        await s.FoodLog.UpdateAsync(oats.Id, "Oats", 100, Meal.Breakfast, default);
        (await s.FoodLog.GetAsync(oats.Id, default)).FiberG.Should().Be(10);
    }

    [Fact]
    public async Task Quick_entries_and_saved_meals_keep_fiber()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        await s.FoodLog.AddQuickAsync(Day, Meal.Lunch, "Salad bowl", 450, 20, 40, 18, 9, 4, default);
        await s.FoodLog.AddAsync([new NewEntry(Day, Meal.Lunch, "Lentils", 200, new Per100g(116, 9, 20, 0.4m, 8, 0.1m), null, EntrySource.Manual)], default);

        var saved = await s.SavedMeals.SaveFromMealAsync(Day, Meal.Lunch, "Lunch bowl", default);
        await s.SavedMeals.LogAsync(saved.Id, Day.AddDays(1), Meal.Lunch, [null, 100], default);

        var next = await s.FoodLog.DayAsync(Day.AddDays(1), default);
        next.Entries.Single(e => e.Description == "Salad bowl").FiberG.Should().Be(9);
        next.Entries.Single(e => e.Description == "Lentils").FiberG.Should().Be(8, "rescaled from 200 g to 100 g");
    }

    [Fact]
    public async Task The_shipped_seeds_carry_fiber_and_fill_databases_seeded_before_it()
    {
        await using var db = fixture.CreateContext(null);
        var banana = db.FoodItems.IgnoreQueryFilters().Where(f => f.Source == FoodSource.Usda && f.Name == "Bananas, raw");
        (await banana.Select(f => f.FiberPer100g).SingleAsync()).Should().Be(2.6m);

        // As on a database seeded before the columns existed.
        await db.FoodItems.IgnoreQueryFilters().Where(f => f.Source == FoodSource.Usda || f.Barcode != null)
            .ExecuteUpdateAsync(u => u.SetProperty(f => f.FiberPer100g, (decimal?)null).SetProperty(f => f.SaturatedFatPer100g, (decimal?)null));

        await UsdaSeed.SeedAsync(db, UsdaSeed.DefaultPath, default);
        await BgFoodSeed.SeedAsync(db, BgFoodSeed.DefaultPath, default);

        (await banana.Select(f => f.FiberPer100g).SingleAsync()).Should().Be(2.6m);
        (await db.FoodItems.IgnoreQueryFilters().Where(f => f.Barcode == "3800102751490").Select(f => f.SaturatedFatPer100g).SingleAsync())
            .Should().Be(1m, "Bulgarian products are filled by barcode");
    }

    [Fact]
    public async Task The_week_counts_only_this_users_logged_days()
    {
        await using var me = new UserScope(fixture, await fixture.CreateUserAsync());
        await using var other = new UserScope(fixture, await fixture.CreateUserAsync());
        foreach (var daysAgo in new[] { 1, 2, 3 })
        {
            await me.FoodLog.AddQuickAsync(Day.AddDays(-daysAgo), Meal.Lunch, "Bowl", 2000, 140, 200, 60, 30, 15, default);
            await other.FoodLog.AddQuickAsync(Day.AddDays(-daysAgo), Meal.Lunch, "Feast", 5000, 10, 500, 200, null, null, default);
        }

        await me.FoodLog.AddQuickAsync(Day, Meal.Lunch, "Today, not counted yet", 900, 50, 90, 30, null, null, default);

        var days = await me.FoodLog.WeekBeforeAsync(Day, default);

        days.Should().HaveCount(3);
        days.Should().OnlyContain(d => d.Totals.Kcal == 2000 && d.Quality.FiberG == 30);
    }
}
