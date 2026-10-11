using FluentAssertions;
using GymTracker.Core.Domain;
using GymTracker.Core.Nutrition;
using GymTracker.Web.Features.Food;
using GymTracker.Web.Features.Undo;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Tests;

/// <summary>SPEC 5.10 and 6.1: last amounts, the Recent order and usual foods, against a real database.</summary>
public class RepeatLoggingTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private static async Task<int> SharedFoodAsync(UserScope s, string query) =>
        (await s.Foods.SearchAsync(query, 1, 0.3, default)).Single().Id;

    [Fact]
    public async Task Usual_foods_unused_for_180_days_are_purged()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var food = await SharedFoodAsync(s, "banana");
        var now = DateTimeOffset.UtcNow;
        s.Db.UsualFoods.AddRange(
            new UsualFood { UserId = s.UserId, Phrase = "old", FoodItemId = food, LastUsedAt = now.AddDays(-181) },
            new UsualFood { UserId = s.UserId, Phrase = "new", FoodItemId = food, LastUsedAt = now.AddDays(-179) });
        await s.Db.SaveChangesAsync();

        await SoftDeletePurge.PurgeFeedbackAsync(s.Db, now, default);

        (await s.Db.UsualFoods.Select(u => u.Phrase).ToListAsync()).Should().Equal("new");
    }

    [Fact]
    public async Task A_phrase_is_unique_per_account_only()
    {
        await using var a = new UserScope(fixture, await fixture.CreateUserAsync());
        await using var b = new UserScope(fixture, await fixture.CreateUserAsync());
        var food = await SharedFoodAsync(a, "banana");
        a.Db.UsualFoods.Add(new UsualFood { UserId = a.UserId, Phrase = "банан", FoodItemId = food, LastUsedAt = DateTimeOffset.UtcNow });
        b.Db.UsualFoods.Add(new UsualFood { UserId = b.UserId, Phrase = "банан", FoodItemId = food, LastUsedAt = DateTimeOffset.UtcNow });
        await a.Db.SaveChangesAsync();
        await b.Db.SaveChangesAsync();

        (await a.Db.UsualFoods.CountAsync()).Should().Be(1, "each account sees only its own");
        a.Db.UsualFoods.Add(new UsualFood { UserId = a.UserId, Phrase = "банан", FoodItemId = food, LastUsedAt = DateTimeOffset.UtcNow });
        await FluentActions.Awaiting(() => a.Db.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>();
    }

    private static NewEntry Entry(int food, decimal grams, Meal meal = Meal.Lunch, string name = "Banana") =>
        new(Today, meal, name, grams, new Per100g(89, 1.1m, 23, 0.3m), food, EntrySource.Search);

    [Fact]
    public async Task Last_amounts_come_from_this_accounts_entries_only()
    {
        await using var a = new UserScope(fixture, await fixture.CreateUserAsync());
        await using var b = new UserScope(fixture, await fixture.CreateUserAsync());
        var food = await SharedFoodAsync(a, "banana");
        await b.FoodLog.AddAsync([Entry(food, 300)], default);

        (await a.Repeat.LastAmountsAsync([food], default)).Should().BeEmpty();

        await a.FoodLog.AddAsync([Entry(food, 120)], default);
        await a.FoodLog.AddAsync([Entry(food, 150)], default);
        (await a.Repeat.LastAmountsAsync([food], default)).Should().Equal(new Dictionary<int, decimal> { [food] = 150 });
    }

    [Fact]
    public async Task Recent_lists_what_this_account_eats_at_the_meal_first()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var banana = await SharedFoodAsync(s, "banana");
        var apple = (await s.Foods.SearchAsync("apple", 5, 0.3, default)).First(f => f.Id != banana).Id;
        for (var i = 0; i < 3; i++)
        {
            await s.FoodLog.AddAsync([Entry(apple, 150, Meal.Dinner, "Apple")], default);
        }

        await s.FoodLog.AddAsync([Entry(banana, 120, Meal.Breakfast)], default);

        (await s.FoodLog.RecentsAsync(Today, Meal.Breakfast, default)).Select(r => r.FoodItemId).Should().Equal(banana, apple);
        (await s.FoodLog.RecentsAsync(Today, Meal.Dinner, default)).Select(r => r.FoodItemId).Should().Equal(apple, banana);
        (await s.FoodLog.RecentsAsync(Today, Meal.Breakfast, default))[0].LastGrams.Should().Be(120);
    }

    [Fact]
    public async Task Recent_ignores_other_accounts()
    {
        await using var a = new UserScope(fixture, await fixture.CreateUserAsync());
        await using var b = new UserScope(fixture, await fixture.CreateUserAsync());
        await b.FoodLog.AddAsync([Entry(await SharedFoodAsync(b, "banana"), 120, Meal.Breakfast)], default);

        (await a.FoodLog.RecentsAsync(Today, Meal.Breakfast, default)).Should().BeEmpty();
    }
}
