using System.Text.Json;
using FluentAssertions;
using Plate.Core.Domain;
using Plate.Core.Nutrition;
using Plate.Web.Features.Admin;
using Plate.Web.Features.Feedback;
using Plate.Web.Features.Food;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Plate.Web.Tests;

/// <summary>
/// Exported regression cases and how a replay is scored against them (SPEC 11): the reference is the saved log, so a
/// corrected item expects the food the user saved, not the one first suggested.
/// </summary>
public class EvalCaseTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static FeedbackAdmin Admin(UserScope s) => new(s.Db, TimeProvider.System, new AdminAuditLog(s.Db, new HttpContextAccessor(), TimeProvider.System));

    private static ConfirmItem Fresh(FoodItem food, decimal grams) =>
        new(food.Name, grams, new Per100g(food.KcalPer100g, food.ProteinPer100g, food.CarbsPer100g, food.FatPer100g), food.Id, food.Name, Confidence.High, []);

    private static async Task<FoodItem> OtherSeedFoodAsync(UserScope s, int? not) =>
        await s.Db.FoodItems.Where(f => f.OwnerUserId == null && f.Source == FoodSource.Usda && f.Id != not && f.KcalPer100g > 0).OrderBy(f => f.Id).FirstAsync();

    [Fact]
    public async Task A_suggestion_corrected_to_another_food_expects_the_saved_food()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var outcome = await s.ParseRuns.ParseAsync("200g chicken breast raw", default);
        var a = await s.Db.FoodItems.SingleAsync(f => f.Id == outcome.Items[0].FoodItemId);
        var b = await OtherSeedFoodAsync(s, a.Id);
        await s.ParseRuns.ConfirmAsync(outcome.RunId, [new SavedItem(true, b.Name, 200, Fresh(b, 200).Per100, b.Id)], default);
        await s.Db.SaveChangesAsync();

        var c = (await Admin(s).EvalCasesAsync(DateTimeOffset.UtcNow.AddHours(-1), false, default)).Single(x => x.RunId == outcome.RunId);

        c.Items[0].Verdict.Should().Be(EvalVerdict.Corrected);
        c.Items[0].FoodItemId.Should().Be(a.Id, "the suggestion is kept");
        c.Items[0].Saved!.FoodItemId.Should().Be(b.Id);
        c.Items[0].Saved!.FoodName.Should().Be(b.Name);
        ParseEvaluator.Score(c, [Fresh(b, 200)]).Score.Should().Be(new AgreementResult(1, 1, 1, 0));
        ParseEvaluator.Score(c, [Fresh(a, 200)]).Score.Should().Be(new AgreementResult(1, 0, 0, 1), "replaying the corrected suggestion earns nothing");
    }

    [Fact]
    public async Task A_rejected_suggestion_a_removed_item_and_a_saved_estimate_keep_their_own_evidence()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var outcome = await s.ParseRuns.ParseAsync("200g chicken breast raw, a banana, 90g qqzxw", default);
        var a = await s.Db.FoodItems.SingleAsync(f => f.Id == outcome.Items[0].FoodItemId);
        var banana = outcome.Items[1];
        var estimate = outcome.Items[2];
        estimate.FoodItemId.Should().BeNull("nothing in the database is called that");
        var b = await OtherSeedFoodAsync(s, a.Id);
        await s.ParseRuns.ConfirmAsync(outcome.RunId, [
            new SavedItem(true, b.Name, 150, Fresh(b, 150).Per100, b.Id),
            new SavedItem(false, banana.Description, banana.Grams, banana.Per100, banana.FoodItemId),
            new SavedItem(true, estimate.Description, 90, estimate.Per100, null),
        ], default);
        s.Db.Feedback.Add(new Core.Domain.Feedback
        {
            ClientId = Guid.NewGuid(), UserId = s.UserId, CreatedAt = DateTimeOffset.UtcNow, Source = FeedbackSource.ParseRating,
            Rating = FeedbackRating.Down, Reasons = ParseReason.WrongFood, ParseRunId = outcome.RunId, ParseItemIndex = 0,
        });
        await s.Db.SaveChangesAsync();

        var c = (await Admin(s).EvalCasesAsync(DateTimeOffset.UtcNow.AddHours(-1), false, default)).Single(x => x.RunId == outcome.RunId);

        c.Items.Select(i => i.Verdict).Should().Equal(EvalVerdict.WrongFood, EvalVerdict.Removed, EvalVerdict.Accepted);
        c.Items[0].RejectedFoodItemId.Should().Be(a.Id);
        c.Items[0].Saved!.FoodItemId.Should().Be(b.Id);
        c.Items[1].Saved!.Included.Should().BeFalse();
        c.Items[2].Saved!.FoodItemId.Should().BeNull();
        c.Items[2].Saved!.Description.Should().Be(estimate.Description);

        var right = ParseEvaluator.Score(c, [Fresh(b, 150), estimate with { Grams = 90 }]);
        right.Score.Should().Be(new AgreementResult(2, 2, 2, 0));
        right.RepeatedWrongFoods.Should().Be(0);
        right.SavedTotals.Should().Be(right.FreshTotals, "the same foods and grams as saved");
        right.SavedTotals!.Kcal.Should().Be(Math.Round(1.5m * b.KcalPer100g + 0.9m * estimate.Per100.Kcal, 1));

        var again = ParseEvaluator.Score(c, [Fresh(a, 150), estimate with { Grams = 90 }]);
        again.Score.FoodAgreed.Should().Be(1, "only the estimate agrees");
        again.RepeatedWrongFoods.Should().Be(1);
        ParseEvaluation.CaseText(again).Should().Contain("picked a rejected food 1x").And.Contain($"    saved 150 g {b.Name} → {b.Name} #{b.Id}")
            .And.Contain("(unticked) ");
    }

    [Fact]
    public async Task Entries_logged_right_after_the_confirm_go_with_the_case_and_back_up_the_replay()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var outcome = await s.ParseRuns.ParseAsync("200g chicken breast raw, 90g qqzxw", default);
        var chicken = outcome.Items[0];
        var b = await OtherSeedFoodAsync(s, chicken.FoodItemId);
        // The second row was wrong: unticked, and the right food logged by hand a minute later.
        await s.ParseRuns.ConfirmAsync(outcome.RunId, [
            new SavedItem(true, chicken.Description, 200, chicken.Per100, chicken.FoodItemId),
            new SavedItem(false, outcome.Items[1].Description, 90, outcome.Items[1].Per100, null),
        ], default);
        await s.Db.SaveChangesAsync();
        var confirmed = (await s.Db.ParseRuns.SingleAsync(r => r.Id == outcome.RunId)).ConfirmedAt!.Value;
        Core.Domain.FoodLogEntry Entry(string name, int? food, decimal grams, TimeSpan after, int? run = null, bool deleted = false) => new()
        {
            UserId = s.UserId, Date = DateOnly.FromDateTime(DateTime.UtcNow), Meal = Core.Domain.Meal.Lunch, Description = name, Grams = grams,
            Kcal = grams * b.KcalPer100g / 100, ProteinG = grams * b.ProteinPer100g / 100, CarbsG = grams * b.CarbsPer100g / 100, FatG = grams * b.FatPer100g / 100,
            FoodItemId = food, CreatedAt = confirmed + after, Source = Core.Domain.EntrySource.Search, ParseRunId = run, DeletedAt = deleted ? DateTimeOffset.UtcNow : null,
        };
        s.Db.FoodLog.AddRange(
            Entry(b.Name, b.Id, 90, TimeSpan.FromMinutes(1)),
            Entry("Saved with the run", chicken.FoodItemId, 200, TimeSpan.Zero, run: outcome.RunId),
            Entry("Deleted again", b.Id, 50, TimeSpan.FromMinutes(2), deleted: true),
            Entry("Next meal", b.Id, 70, TimeSpan.FromMinutes(45)));
        await s.Db.SaveChangesAsync();

        var c = (await Admin(s).EvalCasesAsync(DateTimeOffset.UtcNow.AddHours(-1), false, default)).Single(x => x.RunId == outcome.RunId);

        c.Later.Should().ContainSingle().Which.Should().Match<EvalLater>(l => l.FoodItemId == b.Id && l.Grams == 90 && l.FoodName == b.Name);
        c.Later![0].Per100.Kcal.Should().BeApproximately(b.KcalPer100g, 0.01m);
        var score = ParseEvaluator.Score(c, [chicken, Fresh(b, 90)]);
        score.Score.Should().Be(new AgreementResult(1, 1, 1, 0, 0, LoggedLater: 1), "the replay found the food the user logged by hand");
        ParseEvaluation.CaseText(score).Should().Contain("logged after 1");
    }

    [Fact]
    public async Task History_for_a_replay_is_what_the_account_knew_at_the_parse()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var foods = await s.Db.FoodItems.Where(f => f.OwnerUserId == null && f.Source == FoodSource.Usda && f.KcalPer100g > 0).OrderBy(f => f.Id).Take(2).ToListAsync();
        var (before, after) = (foods[0], foods[1]);
        var asOf = DateTimeOffset.UtcNow.AddDays(-10);

        async Task ConfirmAsync(string text, Core.Domain.FoodItem food, DateTimeOffset at)
        {
            var o = await s.ParseRuns.ParseAsync(text, default);
            SavedItem[] saved = [new SavedItem(true, food.Name, 100, Fresh(food, 100).Per100, food.Id)];
            await s.ParseRuns.ConfirmAsync(o.RunId, saved, default);
            await s.Db.SaveChangesAsync();
            await s.ParseRuns.RememberAsync(o.RunId, saved, default);
            await s.Db.ParseRuns.Where(r => r.Id == o.RunId).ExecuteUpdateAsync(u => u.SetProperty(r => r.ConfirmedAt, at).SetProperty(r => r.CreatedAt, at));
        }

        await ConfirmAsync("извара qx", before, asOf.AddDays(-3));
        await ConfirmAsync("извара qx", after, asOf.AddDays(2)); // learned after the meal
        await ConfirmAsync("кашкавал qx", before, asOf.AddDays(-200)); // forgotten by then
        s.Db.FoodLog.AddRange(
            new Core.Domain.FoodLogEntry { UserId = s.UserId, Description = "x", Grams = 250, FoodItemId = before.Id, CreatedAt = asOf.AddDays(-1), Source = Core.Domain.EntrySource.Ai },
            new Core.Domain.FoodLogEntry { UserId = s.UserId, Description = "x", Grams = 400, FoodItemId = before.Id, CreatedAt = asOf.AddDays(1), Source = Core.Domain.EntrySource.Ai });
        await s.Db.SaveChangesAsync();

        var history = new HistoryAsOf(s.Db, new Plate.Web.Data.FixedCurrentUser(s.UserId), asOf);
        var item = new ParsedFoodItem("cottage cheese", "извара qx", 100, Core.Nutrition.FoodState.NotApplicable, null, null, new Per100g(100, 10, 3, 4), AmountGiven: false);
        var forgotten = item with { NameOriginal = "кашкавал qx" };

        var usual = await history.UsualMatchesAsync([item, forgotten], default);
        usual.Should().ContainKey(0).WhoseValue.Id.Should().Be(before.Id, "the save after the meal is not known yet");
        usual.Should().NotContainKey(1, "words unused for 180 days were forgotten");
        (await history.LastAmountsAsync([before.Id], default)).Should().Equal(new Dictionary<int, decimal> { [before.Id] = 250 });
        (await s.Repeat.UsualMatchesAsync([item], default))[0].Id.Should().Be(after.Id, "the live service knows the latest save");
    }

    [Fact]
    public async Task A_meal_typed_again_soon_after_supersedes_the_earlier_run()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var first = await s.ParseRuns.ParseAsync("200g chicken breast raw, 90g qqzxw", default);
        await s.ParseRuns.ConfirmAsync(first.RunId, [
            new SavedItem(true, first.Items[0].Description, 200, first.Items[0].Per100, first.Items[0].FoodItemId),
            new SavedItem(false, first.Items[1].Description, 90, first.Items[1].Per100, null),
        ], default);
        await s.Db.SaveChangesAsync();
        var again = await s.ParseRuns.ParseAsync("200g chicken breast raw", default);
        await s.ParseRuns.ConfirmAsync(again.RunId, [new SavedItem(true, again.Items[0].Description, 200, again.Items[0].Per100, again.Items[0].FoodItemId)], default);
        await s.Db.SaveChangesAsync();

        var cases = (await Admin(s).EvalCasesAsync(DateTimeOffset.UtcNow.AddHours(-1), false, default)).Where(c => c.RunId == first.RunId || c.RunId == again.RunId).ToList();

        cases.Single(c => c.RunId == first.RunId).SupersededBy.Should().Be(again.RunId);
        cases.Single(c => c.RunId == again.RunId).SupersededBy.Should().BeNull();
    }

    [Fact]
    public async Task A_miss_says_whether_the_saved_food_was_ranked()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var outcome = await s.ParseRuns.ParseAsync("200g chicken breast raw", default);
        var a = await s.Db.FoodItems.SingleAsync(f => f.Id == outcome.Items[0].FoodItemId);
        var b = await OtherSeedFoodAsync(s, a.Id);
        await s.ParseRuns.ConfirmAsync(outcome.RunId, [new SavedItem(true, b.Name, 200, Fresh(b, 200).Per100, b.Id)], default);
        await s.Db.SaveChangesAsync();
        var c = (await Admin(s).EvalCasesAsync(DateTimeOffset.UtcNow.AddHours(-1), false, default)).Single(x => x.RunId == outcome.RunId);

        var ranked = ParseEvaluator.Score(c, [Fresh(a, 200)], trace: [new MatchTrace([new(a.Id, a.Name, 1.4, false), new(b.Id, b.Name, 1.1, false)], true, a.Id, 0.62m)]);
        var unranked = ParseEvaluator.Score(c, [Fresh(a, 200)], trace: [new MatchTrace([new(a.Id, a.Name, 1.4, false)], false, null, null)]);

        ranked.Lost.Should().ContainSingle().Which.Should().Be($"{b.Name}: #{b.Id} ranked 2 for that item, but {a.Name} #{a.Id} was chosen (the decider picked #{a.Id} at 0.62)");
        unranked.Lost.Should().ContainSingle().Which.Should().Contain("was not among any item's top 1 ranked candidates");
        ParseEvaluation.CaseText(ranked).Should().Contain("    lost  ");
    }

    // A case as exported before the saved row was kept (the start of Fixtures/parse-corrections.sample.jsonl).
    private const string OldCase = """
        {"runId":0,"text":"200g chicken breast raw, a banana","aiModel":"fake","items":[{"index":0,"parsed":{"nameEn":"chicken breast raw","nameOriginal":"200g chicken breast raw","grams":200,"state":"Raw","estimated":{"kcal":100,"protein":5,"carbs":15,"fat":3}},"verdict":"accepted","foodItemId":2698,"foodName":"Chicken, breast, meat and skin, raw","foodSource":"Usda","savedGrams":200,"reasons":"None"},{"index":1,"parsed":{"nameEn":"a banana","nameOriginal":"a banana","grams":100,"state":"NotApplicable","estimated":{"kcal":100,"protein":5,"carbs":15,"fat":3}},"verdict":"wrong-food","foodItemId":10051,"foodName":"Mousse","foodSource":"OpenFoodFacts","reasons":"WrongFood"},{"index":2,"parsed":{"nameEn":"rice","nameOriginal":"ориз","grams":150,"state":"Cooked","estimated":{"kcal":130,"protein":2.7,"carbs":28,"fat":0.3}},"verdict":"corrected","foodItemId":5,"foodName":"Rice","foodSource":"Usda","savedGrams":180,"reasons":"None"}]}
        """;

    [Fact]
    public void An_older_case_still_reads_and_says_what_it_does_not_know()
    {
        var c = JsonSerializer.Deserialize<EvalCase>(OldCase, FeedbackJson.Options)!;

        c.Items.Select(i => i.Saved).Should().AllSatisfy(saved => saved.Should().BeNull());
        c.Items[0].Expectation().Should().Be(new ReplayExpectation(true, true, 2698, "", 200, null, FoodName: "Chicken, breast, meat and skin, raw"), "accepted means saved as suggested");
        c.Items[0].SavedFood.Should().Be(("Chicken, breast, meat and skin, raw", FoodSource.Usda));
        c.Items[1].Expectation().Should().Be(new ReplayExpectation(false, false, null, "", 100, null, 10051), "no saved grams: it was unticked");
        c.Items[2].Expectation().Should().Be(new ReplayExpectation(true, false, null, "", 180, null), "the corrected food was not recorded");
        c.Items[2].SavedFood.Should().BeNull();

        var score = ParseEvaluator.Score(c, [Fresh(new FoodItem { Id = 2698, Name = "x", KcalPer100g = 100 }, 200), Fresh(new FoodItem { Id = 77, Name = "y" }, 150)]);
        score.Score.Should().Be(new AgreementResult(1, 1, 1, 0, 1));
        score.SavedTotals.Should().BeNull("older cases have no saved values");
    }

    [Fact]
    public void The_saved_row_is_written_with_fixed_names()
    {
        var saved = new EvalSaved(true, "Ориз", 180, new Per100g(130, 2.7m, 28, 0.3m), 5, "Rice", FoodSource.Usda);

        JsonSerializer.Serialize(saved, FeedbackJson.Options).Should().Be(
            """{"included":true,"description":"Ориз","grams":180,"per100":{"kcal":130,"protein":2.7,"carbs":28,"fat":0.3},"foodItemId":5,"foodName":"Rice","foodSource":"Usda"}""");
        var item = JsonSerializer.Deserialize<EvalItem>(
            """{"index":2,"parsed":{"nameEn":"rice","nameOriginal":"ориз","grams":150,"state":"Cooked","estimated":{"kcal":130,"protein":2.7,"carbs":28,"fat":0.3}},"verdict":"corrected","foodItemId":9,"foodName":"Pilaf","foodSource":"Reference","savedGrams":180,"reasons":"None","saved":{"included":true,"description":"Ориз","grams":180,"per100":{"kcal":130,"protein":2.7,"carbs":28,"fat":0.3},"foodItemId":5,"foodName":"Rice","foodSource":"Usda"}}""",
            FeedbackJson.Options)!;
        item.Saved.Should().Be(saved);
        item.Expectation().Should().Be(new ReplayExpectation(true, true, 5, "Ориз", 180, new Per100g(130, 2.7m, 28, 0.3m), FoodName: "Rice"));
        item.SavedFood.Should().Be(("Rice", FoodSource.Usda));
    }
}
