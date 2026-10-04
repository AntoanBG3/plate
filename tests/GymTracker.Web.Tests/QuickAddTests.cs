using FluentAssertions;
using GymTracker.Core.Domain;
using GymTracker.Web.Features;

namespace GymTracker.Web.Tests;

[Collection(PostgresCollection.Name)]
public class QuickAddTests(PostgresFixture fixture)
{
    private static readonly DateOnly Day = new(2026, 10, 4);

    [Fact]
    public async Task A_quick_entry_counts_in_the_day_and_stays_out_of_recents()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());

        await s.FoodLog.AddQuickAsync(Day, Meal.Dinner, "Restaurant", 650, 40, 60, 25, default);

        var day = await s.FoodLog.DayAsync(Day, default);
        var entry = day.Entries.Single();
        (entry.Source, entry.Grams, entry.Kcal, entry.ProteinG, entry.FoodItemId).Should().Be((EntrySource.Quick, 0m, 650m, 40m, (int?)null));
        day.Totals.Kcal.Should().Be(650);
        (await s.FoodLog.RecentsAsync(Day, default)).Should().BeEmpty();
    }

    [Fact]
    public async Task Invalid_quick_entries_are_rejected()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        await s.Invoking(x => x.FoodLog.AddQuickAsync(Day, Meal.Snack, "x", 0, 0, 0, 0, default)).Should().ThrowAsync<ValidationException>();
        await s.Invoking(x => x.FoodLog.AddQuickAsync(Day, Meal.Snack, "x", 100, 600, 0, 0, default)).Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task Editing_a_quick_entry_changes_its_numbers_and_never_scales_by_grams()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        await s.FoodLog.AddQuickAsync(Day, Meal.Lunch, "Wrap", 500, 30, 50, 15, default);
        var id = (await s.FoodLog.DayAsync(Day, default)).Entries.Single().Id;

        await s.FoodLog.UpdateQuickAsync(id, "Big wrap", Meal.Dinner, 700, 35, 70, 25, default);
        await s.Invoking(x => x.FoodLog.UpdateAsync(id, "Big wrap", 200, Meal.Dinner, default)).Should().ThrowAsync<ValidationException>();

        s.Db.ChangeTracker.Clear();
        var entry = await s.FoodLog.GetAsync(id, default);
        (entry.Description, entry.Meal, entry.Kcal, entry.ProteinG, entry.CarbsG, entry.FatG, entry.Grams).Should().Be(("Big wrap", Meal.Dinner, 700m, 35m, 70m, 25m, 0m));
    }

    [Fact]
    public async Task Copying_a_meal_keeps_quick_entries_quick()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        await s.FoodLog.AddQuickAsync(Day, Meal.Breakfast, "Cafe", 300, 10, 40, 10, default);

        await s.FoodLog.CopyMealAsync(Day, Day.AddDays(1), Meal.Breakfast, default);

        (await s.FoodLog.DayAsync(Day.AddDays(1), default)).Entries.Single().Source.Should().Be(EntrySource.Quick);
    }
}
