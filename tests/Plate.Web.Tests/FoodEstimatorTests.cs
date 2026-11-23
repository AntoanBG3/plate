using System.Net;
using FluentAssertions;
using Plate.Core.Nutrition;
using Plate.Web.Data;
using Plate.Web.Features.Feedback;
using Plate.Web.Integrations;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Plate.Web.Tests;

/// <summary>Foods with no database match get their values from the estimator model, not the parser (SPEC 6.1 step 6).</summary>
public class FoodEstimatorTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Text = "200g chicken breast raw, 50g zzqx";
    private static readonly Per100g Kebab = new(250, 12, 22, 12.5m, 1.5m, 4);

    [Fact]
    public async Task An_unmatched_row_gets_the_estimators_values_and_matched_rows_are_not_sent()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        s.Estimator.Answer = _ => new FoodEstimates([Kebab], "test/estimator");

        var outcome = await s.ParseRuns.ParseAsync(Text, default);

        var request = s.Estimator.Calls.Should().ContainSingle().Which.Should().ContainSingle("only the unmatched row is estimated").Which;
        request.Typed.Should().Be("zzqx");
        request.Grams.Should().Be(50);
        outcome.Items[0].FoodItemId.Should().NotBeNull();
        outcome.Items[0].EstimateModel.Should().BeNull();
        outcome.Items[1].Per100.Should().Be(Kebab);
        outcome.Items[1].Grams.Should().Be(50, "the estimator gives values, not grams");
        outcome.Items[1].EstimateModel.Should().Be("test/estimator");

        var run = await s.Db.ParseRuns.SingleAsync(r => r.Id == outcome.RunId);
        var resolved = FeedbackJson.Read<List<ResolvedRow>>(run.Resolved)!;
        resolved[1].Per100.Should().Be(Kebab);
        resolved[1].EstimateModel.Should().Be("test/estimator");
        resolved[0].EstimateModel.Should().BeNull();
    }

    [Fact]
    public async Task The_closest_database_foods_go_with_the_request()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());

        await s.ParseRuns.ParseAsync("100g chicken pie zzqx", default);

        var request = s.Estimator.Calls.Should().ContainSingle().Which.Single();
        request.Nearby.Should().NotBeEmpty().And.HaveCountLessThanOrEqualTo(5);
        request.Nearby.Should().OnlyContain(f => f.Per100.Kcal >= 0 && f.Name.Length > 0);
    }

    [Fact]
    public async Task No_answer_or_impossible_values_keep_the_parsers_values()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var parsers = (await s.ParseRuns.ParseAsync(Text, default)).Items[1].Per100;

        s.Estimator.Answer = _ => new FoodEstimates([new Per100g(2000, 10, 10, 10)], "test/estimator");
        var impossible = (await s.ParseRuns.ParseAsync(Text, default)).Items[1];
        s.Estimator.Answer = _ => new FoodEstimates([null], "test/estimator");
        var missing = (await s.ParseRuns.ParseAsync(Text, default)).Items[1];

        s.Estimator.Calls.Should().HaveCount(3);
        impossible.Per100.Should().Be(parsers);
        impossible.EstimateModel.Should().BeNull();
        missing.Per100.Should().Be(parsers);
        missing.EstimateModel.Should().BeNull();
    }

    [Fact]
    public async Task The_macros_note_follows_the_estimators_values()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        const string note = "The calories don't add up from the macros.";

        s.Estimator.Answer = _ => new FoodEstimates([new Per100g(500, 5, 5, 5)], "test/estimator");
        var off = (await s.ParseRuns.ParseAsync(Text, default)).Items[1];
        s.Estimator.Answer = _ => new FoodEstimates([Kebab], "test/estimator");
        var fits = (await s.ParseRuns.ParseAsync(Text, default)).Items[1];

        off.Notes.Select(n => n.Format).Should().Contain(note);
        fits.Notes.Select(n => n.Format).Should().NotContain(note).And.Contain("No database match, so these numbers are the AI's estimate.");
    }

    [Fact]
    public void Reads_the_persisted_estimate_model()
    {
        const string json = """
            {"description":"Голям дюнер","per100":{"kcal":250},"confidence":"Low","notes":[],"estimateModel":"google/gemini-4-carbon"}
            """;

        FeedbackJson.Read<ResolvedRow>(json)!.EstimateModel.Should().Be("google/gemini-4-carbon");
    }

    private static OpenRouterSettings Store(UserScope s, string key = "", string model = "") =>
        new(s.Db, new FixedCurrentUser(s.UserId), new EphemeralDataProtectionProvider(),
            Options.Create(new OpenRouterOptions { ApiKey = key, Model = model }), new StubOpenRouter().Catalog(),
            TimeProvider.System, NullLogger<OpenRouterSettings>.Instance);

    [Fact]
    public async Task The_estimator_defaults_to_the_parsers_model_and_off_makes_no_call()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        await s.Db.AppSettings.Where(x => x.Name.StartsWith("OpenRouter:")).ExecuteDeleteAsync();
        try
        {
            var store = Store(s, "sk-or-v1-estimator-test-0123456789", "openai/gpt-x");
            (await store.GetModelAsync(AiTask.Estimator, default)).Should().Be("openai/gpt-x");
            (await store.ListModelsAsync(AiTask.Estimator, default))!.First().Id.Should().Be(AiTasks.Off);

            await store.SetModelAsync(AiTask.Estimator, AiTasks.Off, default);
            // No services at all: asking for the OpenRouter client would throw.
            var estimator = new SwitchingFoodEstimator(store, new ServiceCollection().BuildServiceProvider());
            (await estimator.EstimateAsync("zzqx", [new EstimateRequest("zzqx", "zzqx", 50, FoodState.NotApplicable, null, [])], default)).Should().BeNull();
        }
        finally
        {
            await s.Db.AppSettings.Where(x => x.Name.StartsWith("OpenRouter:")).ExecuteDeleteAsync();
        }
    }
}

public class OpenRouterFoodEstimatorTests
{
    private static readonly EstimateRequest[] Two =
    [
        new("голям дюнер", "large doner kebab", 400, FoodState.Cooked, null, [new NearbyFood("Дюнер с пилешко", new Per100g(215, 11, 20, 10))]),
        new("закуска с лютеница", "pastry with lyutenitsa", 90, FoodState.Cooked, "baked", []),
    ];

    [Fact]
    public async Task Sends_each_food_with_its_nearby_foods_and_a_strict_schema()
    {
        var stub = new StubOpenRouter(completion: StubOpenRouter.Completion("""
            {"estimates":[{"index":0,"kcal":230,"protein":11.5,"carbs":21,"fat":11,"fiber":1.4,"saturatedFat":3.2},
                          {"index":1,"kcal":330,"protein":8,"carbs":38,"fat":16,"fiber":null,"saturatedFat":null}]}
            """));

        var answer = await stub.Estimator().EstimateAsync("голям дюнер и закуска с лютеница", Two, "sk-or-v1-key", "openai/gpt-x", default);

        answer!.Model.Should().Be("openai/gpt-x");
        answer.Values.Should().Equal(new Per100g(230, 11.5m, 21, 11, 1.4m, 3.2m), new Per100g(330, 8, 38, 16));
        stub.Calls.Should().Be(1);
        var body = stub.SentBody!;
        body["response_format"]!["json_schema"]!["strict"]!.GetValue<bool>().Should().BeTrue();
        var sent = body["messages"]![1]!["content"]!.GetValue<string>();
        sent.Should().Contain("голям дюнер и закуска с лютеница").And.Contain("Дюнер с пилешко").And.Contain("215");
    }

    [Fact]
    public async Task Answers_follow_their_index_and_incomplete_ones_are_null()
    {
        var stub = new StubOpenRouter(completion: StubOpenRouter.Completion("""
            {"estimates":[{"index":1,"kcal":330,"protein":8,"carbs":38,"fat":16,"fiber":null,"saturatedFat":null},
                          {"index":1,"kcal":1,"protein":1,"carbs":1,"fat":1,"fiber":null,"saturatedFat":null},
                          {"index":0,"kcal":230,"protein":null,"carbs":21,"fat":11,"fiber":null,"saturatedFat":null},
                          {"index":7,"kcal":230,"protein":1,"carbs":21,"fat":11,"fiber":null,"saturatedFat":null}]}
            """));

        (await stub.Estimator().EstimateAsync("x", Two, "k", "openai/gpt-x", default))!.Values.Should().Equal(null, new Per100g(330, 8, 38, 16));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "{}")]
    [InlineData(HttpStatusCode.OK, "not json")]
    public async Task A_failure_gives_no_answer_and_no_retry(HttpStatusCode status, string content)
    {
        var stub = new StubOpenRouter(status, StubOpenRouter.Completion(content));

        (await stub.Estimator().EstimateAsync("x", Two, "k", "openai/gpt-x", default)).Should().BeNull();
        stub.Calls.Should().Be(1);
    }

    [Fact]
    public async Task A_slow_answer_gives_no_answer()
    {
        var stub = new StubOpenRouter(completion: StubOpenRouter.Completion("""{"estimates":[]}""")) { Delay = OpenRouterFoodEstimator.Timeout + TimeSpan.FromSeconds(1) };

        (await stub.Estimator().EstimateAsync("x", Two, "k", "openai/gpt-x", default)).Should().BeNull();
    }
}
