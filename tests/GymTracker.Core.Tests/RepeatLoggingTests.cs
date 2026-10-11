using FluentAssertions;
using GymTracker.Core.Domain;
using GymTracker.Core.Nutrition;

namespace GymTracker.Core.Tests;

public class RepeatLoggingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 11);

    [Fact]
    public void The_latest_entry_wins_over_a_more_frequent_older_one()
    {
        LastAmount.Pick([
            new AmountUse(200, Now.AddDays(-10)),
            new AmountUse(200, Now.AddDays(-9)),
            new AmountUse(200, Now.AddDays(-8)),
            new AmountUse(250, Now.AddDays(-1)),
        ], Now).Should().Be(250);
    }

    [Fact]
    public void Quick_entries_and_old_entries_are_ignored()
    {
        LastAmount.Pick([new AmountUse(0, Now.AddHours(-1)), new AmountUse(150, Now.AddDays(-2))], Now).Should().Be(150);
        LastAmount.Pick([new AmountUse(150, Now.AddDays(-91))], Now).Should().BeNull();
        LastAmount.Pick([new AmountUse(150, Now.AddDays(-90))], Now).Should().Be(150);
        LastAmount.Pick([], Now).Should().BeNull();
    }

    private static int _id;

    private static IEnumerable<RecentUse> Uses(int? food, string name, Meal meal, int times, int daysAgo = 1) =>
        Enumerable.Range(0, times).Select(i =>
            new RecentUse(++_id, food, name, meal, Today.AddDays(-daysAgo), Now.AddDays(-daysAgo).AddMinutes(i), 100));

    [Fact]
    public void Foods_eaten_at_the_meal_come_first()
    {
        var uses = Uses(1, "Oats", Meal.Breakfast, 5).Concat(Uses(1, "Oats", Meal.Dinner, 1))
            .Concat(Uses(2, "Rice", Meal.Dinner, 3)).ToList();
        var oats = uses.Where(u => u.FoodItemId == 1).MaxBy(u => u.CreatedAt)!.EntryId; // the latest breakfast entry

        var breakfast = RecentOrder.For(uses, Meal.Breakfast, Today);
        var dinner = RecentOrder.For(uses, Meal.Dinner, Today);

        breakfast[0].Should().Be(new RecentGroup(oats, 6));
        dinner.Select(g => g.Times).Should().Equal(3, 6);
    }

    [Fact]
    public void Ties_fall_to_the_total_then_the_latest_entry()
    {
        var uses = Uses(1, "Apple", Meal.Lunch, 2, daysAgo: 5)
            .Concat(Uses(2, "Pear", Meal.Lunch, 2, daysAgo: 1))
            .Concat(Uses(3, "Plum", Meal.Lunch, 2, daysAgo: 3)).Concat(Uses(3, "Plum", Meal.Dinner, 1, daysAgo: 3)).ToList();

        var order = RecentOrder.For(uses, Meal.Breakfast, Today).Select(g => uses.Single(u => u.EntryId == g.LastEntryId).Description);

        order.Should().Equal("Plum", "Pear", "Apple");
    }

    [Fact]
    public void Recent_keeps_30_days_skips_quick_entries_and_shows_20()
    {
        var uses = Uses(1, "Old", Meal.Lunch, 1, daysAgo: 31)
            .Concat(Uses(2, "Edge", Meal.Lunch, 1, daysAgo: 30))
            .Append(new RecentUse(++_id, null, "Wrap", Meal.Lunch, Today, Now, 0))
            .Concat(Enumerable.Range(10, 25).SelectMany(n => Uses(n, $"Food {n}", Meal.Lunch, 1)))
            .ToList();

        var order = RecentOrder.For(uses, Meal.Lunch, Today);

        order.Should().HaveCount(20);
        var names = RecentOrder.For(uses.Where(u => u.Description is "Old" or "Edge" or "Wrap"), Meal.Lunch, Today)
            .Select(g => uses.Single(u => u.EntryId == g.LastEntryId).Description);
        names.Should().Equal("Edge");
    }

    [Fact]
    public void Entries_group_by_food_and_name_ignoring_case()
    {
        var uses = Uses(1, "Skyr", Meal.Lunch, 1).Concat(Uses(1, "skyr", Meal.Lunch, 1)).Concat(Uses(1, "Skyr vanilla", Meal.Lunch, 1)).ToList();

        RecentOrder.For(uses, Meal.Lunch, Today).Select(g => g.Times).Should().BeEquivalentTo([2, 1]);
    }

    [Theory]
    [InlineData("200г извара", "извара")]
    [InlineData("Извара", "извара")]
    [InlineData("извара  ", "извара")]
    [InlineData("2 бр. банани", "банани")]
    [InlineData("½ avocado", "avocado")]
    [InlineData("1/2 avocado", "avocado")]
    [InlineData("1,5 кг ориз", "ориз")]
    [InlineData("извара 200 g", "извара")]
    [InlineData("200g, cottage cheese", "cottage cheese")]
    [InlineData("мляко 2%", "мляко 2%")]
    [InlineData("7days croissant", "7days croissant")]
    [InlineData("pão  de   queijo", "pão de queijo")]
    public void Typed_names_give_one_key_without_their_amount(string typed, string key) =>
        UsualFoods.Key(typed).Should().Be(key);

    [Theory]
    [InlineData("200 g")]
    [InlineData("  ")]
    [InlineData(null)]
    [InlineData("...")]
    public void An_amount_alone_gives_no_key(string? typed) =>
        UsualFoods.Key(typed).Should().BeNull();

    [Fact]
    public void A_key_is_at_most_100_characters() =>
        UsualFoods.Key(new string('a', 150))!.Length.Should().Be(UsualFoods.MaxLength);
}
