using System.Net;
using FluentAssertions;
using GymTracker.Core.Nutrition;
using GymTracker.Web.Features.Food;
using Microsoft.Extensions.DependencyInjection;

namespace GymTracker.Web.Tests;

[Collection(PostgresCollection.Name)]
public class FoodMatchingTests(PostgresFixture fixture)
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
        (await response.Content.ReadAsStringAsync()).Should().Contain(WebUtility.HtmlEncode(picked!));
    }
}
