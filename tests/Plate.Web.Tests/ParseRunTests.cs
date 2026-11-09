using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Plate.Core.Nutrition;
using Plate.Web.Features.Feedback;
using Microsoft.EntityFrameworkCore;

namespace Plate.Web.Tests;

/// <summary>Every parse is recorded with what was suggested, and confirming records what was saved (SPEC 11).</summary>
public partial class ParseRunTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task A_parse_records_the_parsed_items_and_the_match_with_its_candidates()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());

        var outcome = await s.ParseRuns.ParseAsync("200g chicken breast raw, a banana", default);

        outcome.RunId.Should().NotBeNull();
        var run = await s.Db.ParseRuns.SingleAsync(r => r.Id == outcome.RunId);
        run.Text.Should().Be("200g chicken breast raw, a banana");
        run.AiModel.Should().Be("fake");
        run.Saved.Should().BeNull();
        var resolved = FeedbackJson.Read<List<ResolvedRow>>(run.Resolved)!;
        resolved.Should().HaveCount(2);
        resolved[0].FoodItemId.Should().Be(outcome.Items[0].FoodItemId);
        resolved[0].Match!.Top.Should().NotBeEmpty().And.HaveCountLessThanOrEqualTo(5);
        resolved[0].Match!.Top[0].Id.Should().Be(outcome.Items[0].FoodItemId!.Value, "the ranking's top candidate is the match");
        FeedbackJson.Read<List<ParsedFoodItem>>(run.Parsed)!.Select(p => p.Grams).Should().Equal(200m, 100m);
    }

    [Fact]
    public async Task A_matched_row_is_named_as_the_database_food_and_an_estimate_as_typed()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());

        var outcome = await s.ParseRuns.ParseAsync("200g chicken breast raw, 50g zzqx", default);

        var chicken = outcome.Items[0];
        chicken.Description.Should().Be(chicken.MatchedName).And.Contain("raw");
        chicken.Typed.Should().Be("chicken breast raw");
        chicken.State.Should().Be(FoodState.Raw);
        outcome.Items[1].FoodItemId.Should().BeNull();
        outcome.Items[1].Description.Should().Be("zzqx");
        s.Namer.Calls.Should().ContainSingle().Which.Should().ContainSingle("only matched rows are named")
            .Which.DatabaseName.Should().Be(chicken.MatchedName);
    }

    [Fact]
    public async Task A_fitting_short_name_replaces_the_database_name_and_a_misleading_one_does_not()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        s.Namer.Answer = _ => [new FoodName("Chicken breast", "Raw"), new FoodName("Bananas, boiled", "boiled")];

        var outcome = await s.ParseRuns.ParseAsync("200g chicken breast raw, a banana", default);

        outcome.Items[0].Description.Should().Be("Chicken breast");
        outcome.Items[0].Status.Should().Be("raw");
        outcome.Items[1].Description.Should().Be(outcome.Items[1].MatchedName, "a raw banana can't be named boiled");
        outcome.Items[1].Status.Should().BeNull("nor given the status boiled; the view then shows raw from the database name");
        var run = await s.Db.ParseRuns.SingleAsync(r => r.Id == outcome.RunId);
        var resolved = FeedbackJson.Read<List<ResolvedRow>>(run.Resolved)!;
        resolved[0].Description.Should().Be("Chicken breast");
        resolved[0].Status.Should().Be("raw");
        resolved[1].Proposed.Should().Be(new FoodName("Bananas, boiled", "boiled"), "a rejected answer is kept for feedback");
    }

    [Fact]
    public async Task Confirming_stores_what_was_saved_and_what_changed()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var outcome = await s.ParseRuns.ParseAsync("200g chicken breast raw, a banana", default);
        var chicken = outcome.Items[0];
        var banana = outcome.Items[1];

        var runId = await s.ParseRuns.ConfirmAsync(outcome.RunId, [
            new SavedItem(true, chicken.Description, 300, chicken.Per100, chicken.FoodItemId),
            new SavedItem(false, banana.Description, banana.Grams, banana.Per100, banana.FoodItemId),
        ], default);
        await s.Db.SaveChangesAsync();

        runId.Should().Be(outcome.RunId);
        var run = await s.Db.ParseRuns.SingleAsync(r => r.Id == runId);
        run.Corrections.Should().Be(ParseCorrection.GramsChanged | ParseCorrection.Removed);
        run.ConfirmedAt.Should().NotBeNull();
        FeedbackJson.Read<List<SavedRow>>(run.Saved)!.Select(r => r.Corrections).Should().Equal(ParseCorrection.GramsChanged, ParseCorrection.Removed);
    }

    [Fact]
    public async Task Another_users_run_or_none_is_ignored_without_failing()
    {
        await using var owner = new UserScope(fixture, await fixture.CreateUserAsync());
        var outcome = await owner.ParseRuns.ParseAsync("a banana", default);
        await using var other = new UserScope(fixture, await fixture.CreateUserAsync());

        (await other.ParseRuns.ConfirmAsync(outcome.RunId, [], default)).Should().BeNull();
        (await other.ParseRuns.ConfirmAsync(null, [], default)).Should().BeNull();
        (await other.ParseRuns.ConfirmAsync(int.MaxValue, [], default)).Should().BeNull();
    }

    [Fact]
    public async Task Saved_entries_link_to_their_run_through_the_web_flow()
    {
        using var app = new AppFactory(fixture);
        var (client, email) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(client, "/food/add");

        var confirm = await AppFactory.PostAsync(client, "/food/parse", token, new() { ["date"] = "2026-10-04", ["meal"] = "Lunch", ["text"] = "150g banana" });
        confirm.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await confirm.Content.ReadAsStringAsync();
        var runId = RunIdField().Match(html).Groups[1].Value;
        runId.Should().NotBeEmpty();

        // Submit the suggestion exactly as shown: nothing was corrected.
        var fields = new Dictionary<string, string> { ["date"] = "2026-10-04", ["meal"] = "Lunch", ["aiModel"] = "fake", ["parseRunId"] = runId, ["items[0].Include"] = "true" };
        foreach (var name in new[] { "Grams", "Kcal", "Protein", "Carbs", "Fat", "FoodItemId", "Status" })
        {
            fields[$"items[0].{name}"] = System.Net.WebUtility.HtmlDecode(Regex.Match(html, $"name=\"items\\[0\\]\\.{name}\" value=\"([^\"]*)\"").Groups[1].Value);
        }
        // The name is a growing text area, so its value is its content.
        fields["items[0].Description"] = System.Net.WebUtility.HtmlDecode(Regex.Match(html, "name=\"items\\[0\\]\\.Description\"[^>]*>([^<]*)</textarea>").Groups[1].Value);
        fields["items[0].Description"].Should().NotBeEmpty();

        AppFactory.IsRedirectTo(await AppFactory.PostAsync(client, "/food/confirm", token, fields), "/food").Should().BeTrue();

        await using var db = fixture.CreateContext(null);
        var entry = await db.FoodLog.IgnoreQueryFilters().SingleAsync(e => e.ParseRunId == int.Parse(runId));
        var run = await db.ParseRuns.IgnoreQueryFilters().SingleAsync(r => r.Id == entry.ParseRunId);
        run.ConfirmedAt.Should().NotBeNull();
        run.Corrections.Should().Be(ParseCorrection.None);
        entry.Description.Should().StartWith("Bananas", "the row was named after the database food");
        entry.Status.Should().Be("raw", "without a namer the status comes from the database name");
        email.Should().NotBeEmpty();
    }

    [GeneratedRegex("name=\"parseRunId\" value=\"(\\d+)\"")]
    private static partial Regex RunIdField();
}
