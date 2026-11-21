using System.Text.Json.Nodes;
using FluentAssertions;
using Plate.Core.Nutrition;
using Plate.Web.Features.Food;
using Plate.Web.Integrations;
using Microsoft.EntityFrameworkCore;

namespace Plate.Web.Tests;

/// <summary>
/// The amount a person gave reaches the matched food (SPEC 6.1 step 4): a count of pieces times the food's piece
/// weight, an explicit weight as written, and a fraction of a pack from the pack's weight. Run through the live
/// parser's response reader and the real matcher, and through the fallback parser.
/// </summary>
public class PortionTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string LidlPastry = "Закуска със сирене и лютеница, Пекарна Лидл";

    /// <summary>One item as the live parser returns it, read by its own response reader.</summary>
    private static ParsedFoodItem Live(string nameEn, string nameOriginal, decimal grams, bool amountGiven, bool gramsStated, decimal? count, string? countUnit, string? assumption = null)
    {
        var item = new JsonObject
        {
            ["nameEn"] = nameEn, ["nameOriginal"] = nameOriginal, ["grams"] = grams, ["amountGiven"] = amountGiven,
            ["gramsStated"] = gramsStated, ["count"] = count, ["countUnit"] = countUnit,
            ["state"] = "na", ["preparation"] = null, ["assumption"] = assumption,
            ["kcalPer100g"] = 280, ["proteinPer100g"] = 9, ["carbsPer100g"] = 30, ["fatPer100g"] = 13, ["fiberPer100g"] = null, ["saturatedFatPer100g"] = null,
        };
        var result = OpenRouterFoodParser.Read(JsonNode.Parse(StubOpenRouter.Completion(new JsonObject { ["items"] = new JsonArray(item) }.ToJsonString())), "openai/test");
        result.Error.Should().BeNull();
        return result.Items.Single();
    }

    [Fact]
    public async Task Two_Lidl_pastries_are_two_pieces_of_the_catalogue_weight()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var lidl = await s.Db.FoodItems.SingleAsync(f => f.NameBg == LidlPastry);
        var text = "Две закуски с лютеница и сирене от Лидл";
        var parsed = Live("savoury pastry with white cheese and lyutenitsa, Lidl", "закуски с лютеница и сирене от Лидл", 150, true, false, 2, "piece",
            "Приех около 75 г на закуска.");

        var row = (await s.Matcher.ResolveAsync(text, [parsed], default)).Single();

        row.FoodItemId.Should().Be(lidl.Id);
        row.Grams.Should().Be(180, "two pieces of 90 g");
        Math.Round(row.Grams * row.Per100.Kcal / 100).Should().Be(419);
        row.Notes.Select(n => n.ToString()).Should().Contain("2 × a piece of 90 g")
            .And.Contain("These values are estimated from the recipe, not read from a label.", "the catalogue's values for it are a recipe estimate")
            .And.NotContain("Приех около 75 г на закуска.", "the parser's guess no longer applies");
        row.Count.Should().Be(new CountedAmount(2, CountUnit.Piece), "kept so the grams can follow another food picked for the row");
    }

    [Fact]
    public async Task An_explicit_total_weight_wins_over_the_count()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var parsed = Live("savoury pastry with white cheese and lyutenitsa, Lidl", "pastries with white cheese and lyutenitsa from Lidl", 165, true, true, 2, "piece");

        var row = (await s.Matcher.ResolveAsync("Two of those Lidl pastries with white cheese and lyutenitsa, 165 g total", [parsed], default)).Single();

        row.Grams.Should().Be(165);
    }

    /// <summary>A shared packaged food as a barcode lookup caches it.</summary>
    private static Task<Core.Domain.FoodItem> ProductAsync(UserScope s, string name, decimal? serving, decimal? piece, decimal? pack) =>
        s.Foods.CacheAsync(new ProductInfo("", name, null, new Per100g(480, 6, 68, 20), serving, PieceGrams: piece, PackGrams: pack), default);

    [Fact]
    public async Task Two_biscuits_from_a_two_biscuit_serving_are_one_serving()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var tag = Guid.NewGuid().ToString("N")[..6];
        var biscuits = await ProductAsync(s, $"Kwizzle biscuits {tag}", serving: 30, piece: 15, pack: 200);

        var bg = (await s.Matcher.ResolveAsync($"Две бисквити Kwizzle {tag}", [Live($"Kwizzle biscuits {tag}", $"бисквити Kwizzle {tag}", 25, true, false, 2, "piece")], default)).Single();
        var en = (await s.Matcher.ResolveAsync($"two Kwizzle biscuits {tag}", [Live($"Kwizzle biscuits {tag}", $"Kwizzle biscuits {tag}", 25, true, false, 2, "piece")], default)).Single();

        bg.FoodItemId.Should().Be(biscuits.Id);
        bg.Grams.Should().Be(30, "the label's serving is two biscuits of 15 g");
        en.Grams.Should().Be(30);
        (await s.ParseRuns.ParseAsync($"2 Kwizzle biscuits {tag}", default)).Items.Single().Grams.Should().Be(30, "the fallback parser too");
    }

    [Fact]
    public async Task Half_a_tub_uses_a_known_pack_and_otherwise_leaves_the_grams_to_check()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var tag = Guid.NewGuid().ToString("N")[..6];
        await ProductAsync(s, $"Jogurtix Vereya {tag}", serving: 125, piece: null, pack: 400);
        await ProductAsync(s, $"Jogurtix Elena {tag}", serving: 125, piece: null, pack: null);

        var known = (await s.Matcher.ResolveAsync($"Половин кофичка кисело мляко Jogurtix Vereya {tag}",
            [Live($"Jogurtix Vereya {tag}", $"кисело мляко Jogurtix Vereya {tag}", 150, true, false, 0.5m, "pack")], default)).Single();
        var unknown = (await s.Matcher.ResolveAsync($"half a tub of Jogurtix Elena {tag}",
            [Live($"Jogurtix Elena {tag}", $"Jogurtix Elena {tag}", 150, true, false, 0.5m, "pack")], default)).Single();

        known.Grams.Should().Be(200);
        known.Notes.Select(n => n.ToString()).Should().Contain("0.5 × a pack of 400 g");
        unknown.Grams.Should().Be(150, "the usual serving is not the tub, so the parser's estimate stays for the user to edit");
        unknown.Notes.Select(n => n.Format).Should().Contain(Portions.NoPackNote);
    }

    [Fact]
    public async Task Without_an_amount_the_usual_amount_still_wins()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var tag = Guid.NewGuid().ToString("N")[..6];
        var biscuits = await ProductAsync(s, $"Kwizzle biscuits {tag}", serving: 30, piece: 15, pack: 200);
        s.Db.FoodLog.Add(new Core.Domain.FoodLogEntry
        {
            UserId = s.UserId, Date = DateOnly.FromDateTime(DateTime.UtcNow), Meal = Core.Domain.Meal.Snack, Description = "biscuits", Grams = 45,
            FoodItemId = biscuits.Id, CreatedAt = DateTimeOffset.UtcNow, Source = Core.Domain.EntrySource.Ai,
        });
        await s.Db.SaveChangesAsync();

        var row = (await s.Matcher.ResolveAsync($"a Kwizzle biscuit {tag}", [Live($"Kwizzle biscuits {tag}", $"Kwizzle biscuit {tag}", 12, false, false, 1, "piece")], default)).Single();

        row.Grams.Should().Be(45);
        row.Notes.Select(n => n.Format).Should().Contain("Your usual amount, {0} g").And.NotContain(Portions.PieceNote);
    }

    [Fact]
    public void The_count_is_stored_with_the_parsed_item_and_older_runs_still_read()
    {
        // ParseRun.Parsed and the exported cases keep parsed items as JSON: these names must not change.
        var item = new ParsedFoodItem("yogurt", "кисело мляко", 150, FoodState.NotApplicable, null, null, new Per100g(62, 3.3m, 4.2m, 3.6m), 0.5m, true, CountUnit.Pack);
        Plate.Web.Features.Feedback.FeedbackJson.Write(item).Should().Be(
            """{"nameEn":"yogurt","nameOriginal":"кисело мляко","grams":150,"state":"NotApplicable","estimated":{"kcal":62,"protein":3.3,"carbs":4.2,"fat":3.6},"units":0.5,"amountGiven":true,"countOf":"Pack","gramsStated":false}""");

        var old = Plate.Web.Features.Feedback.FeedbackJson.Read<ParsedFoodItem>(
            """{"nameEn":"eggs","nameOriginal":"2 eggs","grams":200,"state":"NotApplicable","estimated":{"kcal":100,"protein":5,"carbs":15,"fat":3},"units":2,"amountGiven":true}""")!;
        old.CountOf.Should().BeNull();
        old.GramsStated.Should().BeFalse();
        Portions.For(old, new UnitWeights(Piece: 55, null, null)).Grams.Should().Be(110, "a count stored without its unit is pieces");
    }

    [Fact]
    public async Task The_count_is_kept_with_the_stored_review_rows()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var outcome = await s.ParseRuns.ParseAsync("две закуски със сирене с лютеница от лидл", default);
        var run = await s.Db.ParseRuns.SingleAsync(r => r.Id == outcome.RunId);

        run.Resolved.Should().Contain("\"count\":{\"units\":2,\"of\":\"Piece\"}", "the review page is drawn again from these rows");
        Plate.Web.Features.Feedback.FeedbackJson.Read<List<Plate.Web.Features.Feedback.ResolvedRow>>(run.Resolved)!.Single().Count
            .Should().Be(new CountedAmount(2, CountUnit.Piece));
    }

    [Fact]
    public async Task The_fallback_parser_counts_pieces_too()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var lidl = await s.Db.FoodItems.SingleAsync(f => f.NameBg == LidlPastry);

        var outcome = await s.ParseRuns.ParseAsync("две закуски със сирене с лютеница от лидл", default);

        outcome.Items.Single().FoodItemId.Should().Be(lidl.Id);
        outcome.Items.Single().Grams.Should().Be(180);
    }
}
