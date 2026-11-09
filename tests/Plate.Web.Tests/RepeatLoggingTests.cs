using FluentAssertions;
using Plate.Core.Domain;
using Plate.Core.Nutrition;
using Plate.Web.Features.Feedback;
using Plate.Web.Features.Food;
using Plate.Web.Features.Undo;
using Microsoft.EntityFrameworkCore;

namespace Plate.Web.Tests;

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

    [Theory]
    [InlineData("200г извара", true)]
    [InlineData("извара", false)]
    [InlineData("a banana", false)]
    [InlineData("една баница", false)]
    [InlineData("2 eggs", true)]
    [InlineData("две яйца", true)]
    public async Task The_fake_parser_marks_typed_amounts_and_counts(string text, bool given) =>
        (await new Plate.Web.Integrations.FakeFoodParser().ParseAsync(text, default)).Items.Single().AmountGiven.Should().Be(given);

    /// <summary>Parses, saves the first row as <paramref name="foodItemId"/> and remembers it, as the Confirm action does.</summary>
    private static async Task ConfirmAsync(UserScope s, string text, int foodItemId, bool include = true)
    {
        var outcome = await s.ParseRuns.ParseAsync(text, default);
        var row = outcome.Items[0];
        var saved = outcome.Items.Select((r, n) => new SavedItem(n == 0 ? include : false, r.Description, r.Grams, r.Per100, n == 0 ? foodItemId : r.FoodItemId)).ToList();
        var runId = await s.ParseRuns.ConfirmAsync(outcome.RunId, saved, default);
        if (include)
        {
            await s.FoodLog.AddAsync([new NewEntry(Today, Meal.Lunch, row.Description, row.Grams, row.Per100, foodItemId, EntrySource.Ai, ParseRunId: runId)], default);
        }

        await s.ParseRuns.RememberAsync(runId, saved, default);
    }

    /// <summary>A shared "извара" food that the ranking does not pick first, so only a remembered choice can explain it.</summary>
    private static async Task<int> HandPickedAsync(UserScope s)
    {
        var ranked = (await s.ParseRuns.ParseAsync("200г извара", default)).Items[0].FoodItemId;
        return (await s.Foods.SearchAsync("извара", 20, 0.3, default)).First(f => f.Id != ranked && !f.Own).Id;
    }

    [Fact]
    public async Task A_confirmed_food_is_proposed_again_for_the_same_words_with_its_amount()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var picked = await HandPickedAsync(s);
        await ConfirmAsync(s, "200г извара", picked);
        s.MatchDecider.Calls.Clear();

        var outcome = await s.ParseRuns.ParseAsync("Извара and banana", default);
        var item = outcome.Items[0];

        item.FoodItemId.Should().Be(picked);
        item.Usual.Should().BeTrue();
        item.Confidence.Should().Be(Confidence.High);
        item.Grams.Should().Be(200);
        item.Notes.Should().ContainSingle(n => n.Format == "Your usual amount, {0} g");
        s.MatchDecider.Calls.Single()[0].Candidates.Should().BeEmpty("a usual food is context for the decider, not a question");
        var run = await s.Db.ParseRuns.SingleAsync(r => r.Id == outcome.RunId);
        Plate.Web.Features.Feedback.FeedbackJson.Read<List<Plate.Web.Features.Feedback.ResolvedRow>>(run.Resolved)![0].Usual.Should().BeTrue();
    }

    [Fact]
    public async Task A_typed_amount_is_kept()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var picked = await HandPickedAsync(s);
        await ConfirmAsync(s, "200г извара", picked);

        var item = (await s.ParseRuns.ParseAsync("300г извара", default)).Items.Single();

        (item.FoodItemId, item.Grams, item.Usual).Should().Be(((int?)picked, 300m, true));
        item.Notes.Should().NotContain(n => n.Format == "Your usual amount, {0} g");
    }

    [Fact]
    public async Task Picking_another_food_once_moves_the_words_to_it()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var first = await HandPickedAsync(s);
        var second = (await s.Foods.SearchAsync("извара", 20, 0.3, default)).First(f => f.Id != first && !f.Own).Id;
        await ConfirmAsync(s, "200г извара", first);
        await ConfirmAsync(s, "извара", second);

        (await s.ParseRuns.ParseAsync("извара", default)).Items.Single().FoodItemId.Should().Be(second);
        (await s.Db.UsualFoods.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task An_unticked_row_writes_nothing()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        await ConfirmAsync(s, "200г извара", await HandPickedAsync(s), include: false);

        (await s.Db.UsualFoods.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_same_words_twice_in_one_parse_write_one_row()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var a = await SharedFoodAsync(s, "banana");
        var b = (await s.Foods.SearchAsync("apple", 5, 0.3, default)).First(f => f.Id != a).Id;

        await s.Repeat.RememberAsync([("200г банан", a), ("банан", b)], default);

        (await s.Db.UsualFoods.SingleAsync()).FoodItemId.Should().Be(b, "the later row wins");
    }

    [Fact]
    public async Task Remembering_twice_keeps_one_row()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var food = await SharedFoodAsync(s, "banana");
        await s.Repeat.RememberAsync([("банан", food)], default);
        await using var again = new UserScope(fixture, s.UserId);

        await again.Repeat.RememberAsync([("банан", food)], default);

        (await s.Db.UsualFoods.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_usual_food_pointing_at_a_food_this_account_cannot_see_is_ignored()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        await using var other = new UserScope(fixture, await fixture.CreateUserAsync());
        var foreign = await other.Foods.CreateManualAsync("Извара от село", null, new Per100g(120, 14, 3, 6), null, share: false, default);
        s.Db.UsualFoods.Add(new UsualFood { UserId = s.UserId, Phrase = "извара", FoodItemId = foreign.Id, LastUsedAt = DateTimeOffset.UtcNow });
        await s.Db.SaveChangesAsync();

        var item = (await s.ParseRuns.ParseAsync("извара", default)).Items.Single();

        item.FoodItemId.Should().NotBe(foreign.Id);
        item.Usual.Should().BeFalse();
    }

    [Fact]
    public async Task Deleting_the_food_removes_its_usual_food()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var mine = await s.Foods.CreateManualAsync("Извара Верея", null, new Per100g(98, 11, 3.4m, 4.3m), null, share: false, default);
        await s.Repeat.RememberAsync([("извара", mine.Id)], default);

        s.Db.FoodItems.Remove(mine);
        await s.Db.SaveChangesAsync();

        (await s.Db.UsualFoods.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Without_a_run_nothing_is_remembered()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        await s.ParseRuns.RememberAsync(null, [new SavedItem(true, "Banana", 120, new Per100g(89, 1, 23, 0), await SharedFoodAsync(s, "banana"))], default);

        (await s.Db.UsualFoods.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_run_that_cannot_be_read_does_not_fail_the_confirm()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var food = await SharedFoodAsync(s, "banana");
        var runId = (await s.ParseRuns.ParseAsync("банан", default)).RunId!.Value;
        // jsonb rejects invalid JSON, so the run holds valid JSON of the wrong shape: numbers where items belong.
        await s.Db.Database.ExecuteSqlAsync($"""UPDATE "ParseRuns" SET "Parsed" = '[1]'::jsonb WHERE "Id" = {runId}""");
        await using var confirm = new UserScope(fixture, s.UserId);

        await confirm.ParseRuns.RememberAsync(runId, [new SavedItem(true, "Banana", 120, new Per100g(89, 1, 23, 0), food)], default);

        (await s.Db.UsualFoods.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(FoodState.Cooked, false)]
    [InlineData(FoodState.Raw, true)]
    [InlineData(FoodState.NotApplicable, true)]
    public async Task A_usual_food_is_skipped_when_its_raw_or_cooked_state_contradicts_the_item(FoodState state, bool used)
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var raw = (await s.Foods.SearchAsync("chicken breast boneless skinless raw", 10, 0.3, default))
            .First(f => FoodStates.FromName(f.Name, f.NameBg) == FoodState.Raw);
        const string name = "пилешко филе";
        await s.Repeat.RememberAsync([(name, raw.Id)], default);

        var matches = await s.Repeat.UsualMatchesAsync([new ParsedFoodItem(name, name, 200, state, null, null, new Per100g(100, 5, 15, 3))], default);

        if (used)
        {
            matches.Should().ContainSingle().Which.Value.Id.Should().Be(raw.Id);
        }
        else
        {
            matches.Should().BeEmpty("\"пилешко филе на скара\" must not become the raw fillet confirmed for \"пилешко филе\"");
        }
    }
}
