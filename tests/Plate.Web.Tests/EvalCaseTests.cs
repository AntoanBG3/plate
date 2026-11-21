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

    // A case as exported before the saved row was kept (the start of Fixtures/parse-corrections.sample.jsonl).
    private const string OldCase = """
        {"runId":0,"text":"200g chicken breast raw, a banana","aiModel":"fake","items":[{"index":0,"parsed":{"nameEn":"chicken breast raw","nameOriginal":"200g chicken breast raw","grams":200,"state":"Raw","estimated":{"kcal":100,"protein":5,"carbs":15,"fat":3}},"verdict":"accepted","foodItemId":2698,"foodName":"Chicken, breast, meat and skin, raw","foodSource":"Usda","savedGrams":200,"reasons":"None"},{"index":1,"parsed":{"nameEn":"a banana","nameOriginal":"a banana","grams":100,"state":"NotApplicable","estimated":{"kcal":100,"protein":5,"carbs":15,"fat":3}},"verdict":"wrong-food","foodItemId":10051,"foodName":"Mousse","foodSource":"OpenFoodFacts","reasons":"WrongFood"},{"index":2,"parsed":{"nameEn":"rice","nameOriginal":"ориз","grams":150,"state":"Cooked","estimated":{"kcal":130,"protein":2.7,"carbs":28,"fat":0.3}},"verdict":"corrected","foodItemId":5,"foodName":"Rice","foodSource":"Usda","savedGrams":180,"reasons":"None"}]}
        """;

    [Fact]
    public void An_older_case_still_reads_and_says_what_it_does_not_know()
    {
        var c = JsonSerializer.Deserialize<EvalCase>(OldCase, FeedbackJson.Options)!;

        c.Items.Select(i => i.Saved).Should().AllSatisfy(saved => saved.Should().BeNull());
        c.Items[0].Expectation().Should().Be(new ReplayExpectation(true, true, 2698, "", 200, null), "accepted means saved as suggested");
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
        item.Expectation().Should().Be(new ReplayExpectation(true, true, 5, "Ориз", 180, new Per100g(130, 2.7m, 28, 0.3m)));
        item.SavedFood.Should().Be(("Rice", FoodSource.Usda));
    }
}
