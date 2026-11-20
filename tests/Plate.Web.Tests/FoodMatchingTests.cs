using System.Net;
using FluentAssertions;
using Plate.Core.Nutrition;
using Plate.Web.Features.Food;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Plate.Web.Tests;

public class FoodMatchingTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static ParsedFoodItem Parsed(string name, decimal grams, FoodState state) =>
        new(name, name, grams, state, null, null, new Per100g(999, 0, 0, 0));

    /// <summary>Rank's pick for raw chicken breast, and the matcher's shortlist for it.</summary>
    private static async Task<(int RankPick, IReadOnlyList<MatchCandidate> Shortlist)> ChickenAsync(UserScope s)
    {
        var rank = (await s.Matcher.ResolveAsync("200g chicken breast", [Parsed("chicken breast", 200, FoodState.Raw)], default)).Single();
        return (rank.FoodItemId!.Value, s.MatchDecider.Calls.Single()[0].Candidates);
    }

    [Fact]
    public async Task A_jev_pick_replaces_ranks_top_choice()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var (rankPick, shortlist) = await ChickenAsync(s);
        var other = shortlist.First(c => c.Id != rankPick);
        s.MatchDecider.Answer = q => StubMatchDecider.First(q, other.Id, 0.97m);

        var item = (await s.Matcher.ResolveAsync("200g chicken breast", [Parsed("chicken breast", 200, FoodState.Raw)], default)).Single();

        item.FoodItemId.Should().Be(other.Id);
        item.Per100.Should().Be(other.Per100);
        item.Confidence.Should().Be(Confidence.High);
        item.MatchModel.Should().Be(StubMatchDecider.Model);
        s.MatchDecider.LastText.Should().Be("200g chicken breast");
        var expected = FoodMatcher.Shortlist(await s.Foods.SearchAsync("chicken breast", 50, 0.3, default));
        shortlist.Select(c => c.Id).Should().Equal(expected.Select(c => c.Id), "the options are the plain trigram top 50, in that order");
        shortlist.Should().HaveCountLessThanOrEqualTo(50);
    }

    [Fact]
    public async Task The_users_own_food_at_the_top_of_the_ranking_stands()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var own = await s.Foods.CreateManualAsync("Chicken breast", null, new Per100g(110, 23, 0, 1.5m), null, share: false, default);
        var ranked = (await s.Matcher.ResolveAsync("200g chicken breast", [Parsed("chicken breast", 200, FoodState.Raw)], default)).Single();
        ranked.FoodItemId.Should().Be(own.Id, "precondition: Rank puts the user's own food first");
        s.MatchDecider.Calls.Clear();
        s.MatchDecider.Answer = q => new MatchDecisions([new MatchDecision(null, 0.99m), new MatchDecision(q[1].Candidates[0].Id, 0.97m)], StubMatchDecider.Model);

        var items = await s.Matcher.ResolveAsync("200g chicken breast and a banana", [Parsed("chicken breast", 200, FoodState.Raw), Parsed("banana", 120, FoodState.NotApplicable)], default);

        items[0].FoodItemId.Should().Be(own.Id);
        items[0].MatchModel.Should().BeNull("Rank decided");
        s.MatchDecider.Calls.Single()[0].Candidates.Should().BeEmpty("the own food's item is context only, not a question");
        items[1].MatchModel.Should().Be(StubMatchDecider.Model);
    }

    [Fact]
    public async Task Picks_below_the_high_cut_are_medium()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var (rankPick, _) = await ChickenAsync(s);
        s.MatchDecider.Answer = q => StubMatchDecider.First(q, rankPick, 0.6m);

        (await s.Matcher.ResolveAsync("chicken", [Parsed("chicken breast", 200, FoodState.Raw)], default)).Single().Confidence.Should().Be(Confidence.Medium);
    }

    [Fact]
    public async Task A_confident_none_uses_the_ai_estimate()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        s.MatchDecider.Answer = q => StubMatchDecider.First(q, null, 0.95m);

        var item = (await s.Matcher.ResolveAsync("halloumi", [new ParsedFoodItem("chicken breast", "chicken breast", 100, FoodState.Raw, null, null, new Per100g(320, 21, 2, 25))], default)).Single();

        item.FoodItemId.Should().BeNull();
        item.Confidence.Should().Be(Confidence.Low);
        item.Per100.Should().Be(new Per100g(320, 21, 2, 25));
        item.Notes.Should().Contain(n => n.Format.Contains("No database match"));
        item.MatchModel.Should().Be(StubMatchDecider.Model);
    }

    [Fact]
    public async Task An_unsure_none_falls_back_to_rank()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var (rankPick, _) = await ChickenAsync(s);
        s.MatchDecider.Answer = q => StubMatchDecider.First(q, null, 0.5m);

        var item = (await s.Matcher.ResolveAsync("chicken", [Parsed("chicken breast", 200, FoodState.Raw)], default)).Single();

        item.FoodItemId.Should().Be(rankPick);
        item.Confidence.Should().NotBe(Confidence.Low);
        item.MatchModel.Should().BeNull("Rank decided");
    }

    [Fact]
    public async Task A_pick_below_the_floor_uses_the_ai_estimate()
    {
        // Feedback #8 and #9: Lidl pastries became a breakfast pizza and tea biscuits on picks at 0.22 and 0.23.
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var (rankPick, _) = await ChickenAsync(s);
        s.MatchDecider.Answer = q => StubMatchDecider.First(q, rankPick, 0.22m);

        var item = (await s.Matcher.ResolveAsync("chicken", [new ParsedFoodItem("chicken breast", "chicken breast", 200, FoodState.Raw, null, null, new Per100g(120, 22, 0, 3))], default)).Single();

        item.FoodItemId.Should().BeNull();
        item.Confidence.Should().Be(Confidence.Low);
        item.Per100.Should().Be(new Per100g(120, 22, 0, 3));
        item.MatchModel.Should().Be(StubMatchDecider.Model);
    }

    [Fact]
    public async Task A_pick_at_the_floor_is_the_match()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var (rankPick, _) = await ChickenAsync(s);
        s.MatchDecider.Answer = q => StubMatchDecider.First(q, rankPick, FoodMatcher.PickFloor);

        (await s.Matcher.ResolveAsync("chicken", [Parsed("chicken breast", 200, FoodState.Raw)], default)).Single().FoodItemId.Should().Be(rankPick);
    }

    [Fact]
    public async Task An_unsure_none_with_a_weak_ranking_uses_the_ai_estimate()
    {
        // Feedback #7: a Lidl pastry became a SUBWAY steak sandwich, Rank's top pick at 0.52, after an unsure "none".
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var kiosk = new ParsedFoodItem("cheese pastry from the kiosk", "закуска от будката", 150, FoodState.NotApplicable, null, null, new Per100g(280, 9, 39, 9));
        var trace = new List<MatchTrace>();
        await s.Matcher.ResolveAsync("закуска от будката", [kiosk], default, trace);
        trace.Single().Top.Should().NotBeEmpty().And.Subject.First().Score.Should().BeLessThan(FoodMatcher.NoneFallbackFloor, "precondition: Rank has only a weak candidate");
        s.MatchDecider.Answer = q => StubMatchDecider.First(q, null, 0.3m);

        var item = (await s.Matcher.ResolveAsync("закуска от будката", [kiosk], default)).Single();

        item.FoodItemId.Should().BeNull();
        item.Confidence.Should().Be(Confidence.Low);
        item.MatchModel.Should().Be(StubMatchDecider.Model);
    }

    [Fact]
    public async Task A_food_named_only_in_what_a_product_is_for_is_not_that_product()
    {
        // Feedback #4: "голям дюнер" became the BIM "Пилешко месо за дюнер" as Database after Jev's unsure "none".
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        s.MatchDecider.Answer = q => StubMatchDecider.First(q, null, 0.86m);

        var item = (await s.Matcher.ResolveAsync("голям дюнер",
            [new ParsedFoodItem("doner kebab sandwich", "дюнер", 450, FoodState.Cooked, null, null, new Per100g(220, 10, 25, 9))], default)).Single();

        item.MatchedName.Should().NotContain("за дюнер", "meat for a doner is an ingredient, not the doner");
    }

    [Theory]
    [InlineData("Пилешко месо за дюнер", "дюнер", false)]
    [InlineData("Пилешко месо за дюнер", "пилешко месо", true)]
    [InlineData("Sauce for pasta", "pasta", false)]
    [InlineData("Chicken, breast", "chicken breast", true)]
    public void Words_after_for_are_not_the_main_food(string name, string query, bool matches)
    {
        var food = new FoodMatch(1, name, name, null, 100, 1, 1, 1, null, null, null, false, false, 1);

        FoodMatcher.HeadMatches(food, query, bulgarian: true).Should().Be(matches);
    }

    [Fact]
    public async Task Items_without_candidates_are_not_sent()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());

        await s.Matcher.ResolveAsync("xqzj vwkq", [Parsed("xqzj vwkq", 50, FoodState.NotApplicable)], default);
        s.MatchDecider.Calls.Should().BeEmpty();

        await s.Matcher.ResolveAsync("xqzj vwkq and a banana", [Parsed("xqzj vwkq", 50, FoodState.NotApplicable), Parsed("banana", 120, FoodState.NotApplicable)], default);
        s.MatchDecider.Calls.Should().ContainSingle().Which.Count(q => q.Candidates.Count > 0).Should().Be(1);
    }

    [Fact]
    public async Task The_confirm_page_shows_the_deciders_pick()
    {
        var stub = new StubMatchDecider();
        string? picked = null;
        stub.Answer = q =>
        {
            var last = q[0].Candidates[^1];
            picked = last.Name;
            return StubMatchDecider.First(q, last.Id, 0.97m);
        };
        using var app = new AppFactory(fixture, services: s => s.AddScoped<IFoodMatchDecider>(_ => stub));
        var (client, _) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(client, "/food/add");

        var response = await AppFactory.PostAsync(client, "/food/parse", token, new() { ["date"] = "2026-10-04", ["meal"] = "Lunch", ["text"] = "200g chicken breast" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await response.Content.ReadAsStringAsync();
        page.Should().Contain(WebUtility.HtmlEncode(picked!));
        page.Should().Contain($"name=\"items[0].MatchModel\" value=\"{StubMatchDecider.Model}\"");
    }

    [Fact]
    public async Task Confirmed_rows_keep_the_match_model()
    {
        using var app = new AppFactory(fixture);
        var (client, email) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(client, "/food/add");
        var tag = Guid.NewGuid().ToString("N")[..8];
        var fields = new Dictionary<string, string> { ["date"] = "2026-10-04", ["meal"] = "Lunch", ["aiModel"] = "openai/gpt-6-luna" };
        void Row(int n, string name, string? foodItemId, string? matchModel)
        {
            fields[$"items[{n}].Include"] = "true";
            fields[$"items[{n}].Description"] = $"{name} {tag}";
            fields[$"items[{n}].Grams"] = "100";
            fields[$"items[{n}].Kcal"] = "100";
            fields[$"items[{n}].Protein"] = "5";
            fields[$"items[{n}].Carbs"] = "10";
            fields[$"items[{n}].Fat"] = "3";
            fields[$"items[{n}].FoodItemId"] = foodItemId ?? "";
            if (matchModel is not null)
            {
                fields[$"items[{n}].MatchModel"] = matchModel;
            }
        }

        await using var db = fixture.CreateContext(null);
        var anyFood = await db.FoodItems.Where(f => f.OwnerUserId == null).Select(f => f.Id).FirstAsync();
        Row(0, "matched", anyFood.ToString(), StubMatchDecider.Model);
        Row(1, "estimate", null, StubMatchDecider.Model);
        Row(2, "ranked", anyFood.ToString(), null);
        Row(3, "long", null, new string('x', 150));

        AppFactory.IsRedirectTo(await AppFactory.PostAsync(client, "/food/confirm", token, fields), "/food?date=2026-10-04").Should().BeTrue();

        var saved = await db.FoodLog.IgnoreQueryFilters().Where(e => e.Description.EndsWith(tag)).ToDictionaryAsync(e => e.Description.Split(' ')[0], e => e.MatchModel);
        saved.Should().BeEquivalentTo(new Dictionary<string, string?>
        {
            ["matched"] = StubMatchDecider.Model,
            ["estimate"] = StubMatchDecider.Model,
            ["ranked"] = null,
            ["long"] = new string('x', 100),
        });
    }
}
