using System.Text.Json.Nodes;
using FluentAssertions;
using GymTracker.Core.Analytics;
using GymTracker.Core.Domain;
using GymTracker.Core.Summary;
using GymTracker.Core.Text;
using GymTracker.Core.Time;
using GymTracker.Web.Data;
using GymTracker.Web.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GymTracker.Web.Tests;

/// <summary>A weekly summary writer that answers from a script and counts its calls.</summary>
public sealed class ScriptedWriter : IWeeklySummaryWriter
{
    public bool Available { get; set; } = true;
    public bool Fail { get; set; }
    public int Calls { get; private set; }
    public bool? LastBulgarian { get; private set; }
    public WeekFacts? LastFacts { get; private set; }

    public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(Available);

    public Task<WeeklySummaryResult> WriteAsync(WeekFacts facts, bool bulgarian, CancellationToken ct)
    {
        Calls++;
        LastBulgarian = bulgarian;
        LastFacts = facts;
        return Task.FromResult(Fail
            ? WeeklySummaryResult.Failed(new LocalText("The weekly summary isn't available right now."))
            : new WeeklySummaryResult(new WeeklySummaryText($"You weighed in {facts.WeighIns} times.", ["Weigh in every morning."], "Nice steady week."), "test/model", null));
    }
}

/// <summary>The weekly summary on Today (SPEC 6.5).</summary>
public class WeeklySummaryFlowTests(PostgresFixture db) : IDisposable, IClassFixture<PostgresFixture>
{
    private readonly ScriptedWriter writer = new();
    private AppFactory? app;

    private AppFactory App => app ??= new AppFactory(db, services: s => s.AddScoped<IWeeklySummaryWriter>(_ => writer));

    public void Dispose() => app?.Dispose();

    private static DateOnly LastMonday => IsoWeek.Of(UserClock.Today(DateTimeOffset.UtcNow, "Europe/Sofia")).Previous().Monday;

    private async Task<(HttpClient Client, string UserId)> UserWithWeighInsAsync(int weighIns)
    {
        var (client, email) = await App.SignedInClientAsync();
        await using var ctx = db.CreateContext(null);
        var id = await ctx.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        for (var i = 0; i < weighIns; i++)
        {
            ctx.Bodyweights.Add(new BodyweightEntry { UserId = id, Date = LastMonday.AddDays(i * 2), WeightKg = 80m - i * 0.2m });
        }

        await ctx.SaveChangesAsync();
        return (client, id);
    }

    private async Task<List<WeeklySummary>> StoredAsync(string userId)
    {
        await using var ctx = db.CreateContext(null);
        return await ctx.WeeklySummaries.IgnoreQueryFilters().AsNoTracking().Where(s => s.UserId == userId).ToListAsync();
    }

    [Fact]
    public async Task Today_loads_a_missing_summary_separately_then_shows_the_stored_one_without_the_model()
    {
        var (client, id) = await UserWithWeighInsAsync(3);

        var first = await client.GetStringAsync("/today");
        first.Should().Contain("hx-get=\"/today/weekly-summary\"");
        writer.Calls.Should().Be(0, "Today never waits for the model");

        var card = await client.GetStringAsync("/today/weekly-summary");
        card.Should().Contain("You weighed in 3 times.").And.Contain("Weigh in every morning.").And.Contain("Nice steady week.");
        writer.Calls.Should().Be(1);
        (await StoredAsync(id)).Should().ContainSingle(s => s.WeekStart == LastMonday && s.Model == "test/model");

        var again = await client.GetStringAsync("/today");
        again.Should().Contain("You weighed in 3 times.").And.NotContain("hx-get=\"/today/weekly-summary\"");
        await client.GetStringAsync("/today/weekly-summary");
        writer.Calls.Should().Be(1, "a stored summary is never written again");
    }

    [Fact]
    public async Task Last_week_is_judged_against_the_targets_captured_then_not_todays()
    {
        var (client, id) = await UserWithWeighInsAsync(3);
        var then = new GymTracker.Core.Nutrition.MacroTarget(80m, 100, 111, 50, 70, 150, 200, 30, 20, 30, false);
        await using (var ctx = db.CreateContext(null))
        {
            ctx.MaintenanceEstimates.Add(new MaintenanceEstimate { UserId = id, CalculatedOn = LastMonday, EstimatedTdeeKcal = 2400, TargetKcal = 2000, TargetProteinG = 111 });
            ctx.NutritionTargetDays.Add(new NutritionTargetDay
            {
                UserId = id, Date = LastMonday,
                Values = new GymTracker.Web.Features.Food.TargetDayValues(2400, new GymTracker.Core.Nutrition.GoalTarget(2000, 111, false, then), 80m, 1800).ToJson(),
            });
            await ctx.SaveChangesAsync();
        }

        await client.GetStringAsync("/today");
        await client.GetStringAsync("/today/weekly-summary");

        writer.LastFacts!.Targets.Should().Be(then);
    }

    [Fact]
    public async Task A_quiet_week_gets_no_summary()
    {
        var (client, _) = await UserWithWeighInsAsync(2);

        (await client.GetStringAsync("/today")).Should().NotContain("weekly-summary");
        (await client.GetStringAsync("/today/weekly-summary")).Should().BeEmpty();
        writer.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_failed_call_stores_nothing_and_the_next_visit_tries_again()
    {
        var (client, id) = await UserWithWeighInsAsync(4);
        writer.Fail = true;

        (await client.GetStringAsync("/today/weekly-summary")).Should().BeEmpty();
        (await StoredAsync(id)).Should().BeEmpty();

        writer.Fail = false;
        (await client.GetStringAsync("/today")).Should().Contain("hx-get=\"/today/weekly-summary\"");
        (await client.GetStringAsync("/today/weekly-summary")).Should().Contain("You weighed in 4 times.");
    }

    [Fact]
    public async Task The_switch_or_an_unavailable_model_stops_the_calls()
    {
        var (client, _) = await UserWithWeighInsAsync(3);
        await AppFactory.PostAsync(client, "/settings/weekly-summary", await AppFactory.TokenAsync(client, "/settings"), new() { ["enabled"] = "false" });

        (await client.GetStringAsync("/today")).Should().NotContain("weekly-summary");
        (await client.GetStringAsync("/today/weekly-summary")).Should().BeEmpty();

        await AppFactory.PostAsync(client, "/settings/weekly-summary", await AppFactory.TokenAsync(client, "/settings"), new() { ["enabled"] = "true" });
        writer.Available = false;
        (await client.GetStringAsync("/today")).Should().NotContain("weekly-summary");
        writer.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Switching_language_writes_it_again_in_the_new_language()
    {
        var (client, id) = await UserWithWeighInsAsync(3);
        await client.GetStringAsync("/today/weekly-summary");

        await using (var ctx = db.CreateContext(null))
        {
            await ctx.Profiles.IgnoreQueryFilters().Where(p => p.UserId == id).ExecuteUpdateAsync(u => u.SetProperty(p => p.Language, Language.Bulgarian));
        }

        (await client.GetStringAsync("/today")).Should().Contain("hx-get=\"/today/weekly-summary\"");
        await client.GetStringAsync("/today/weekly-summary");

        writer.LastBulgarian.Should().BeTrue();
        (await StoredAsync(id)).Should().ContainSingle(s => s.Language == Language.Bulgarian);
    }
}

public class WeeklySummaryWriterTests
{
    private static JsonNode Response(string json) =>
        new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["content"] = json } }) };

    [Fact]
    public void Reads_and_trims_the_models_answer()
    {
        var result = OpenRouterWeeklySummaryWriter.Read(Response("""
            {"summary":" Good week. ","tips":["Eat more protein.","",  "Sleep."],"motivation":"Keep it up."}
            """), "m");

        result.Text.Should().BeEquivalentTo(new WeeklySummaryText("Good week.", ["Eat more protein.", "Sleep."], "Keep it up."));
        result.Model.Should().Be("m");
    }

    [Fact]
    public void An_unreadable_answer_is_a_failure()
    {
        OpenRouterWeeklySummaryWriter.Read(Response("not json"), "m").Text.Should().BeNull();
        OpenRouterWeeklySummaryWriter.Read(Response("""{"summary":"","tips":[],"motivation":""}"""), "m").Error.Should().NotBeNull();
    }

    [Fact]
    public void The_facts_hold_numbers_only()
    {
        var facts = new WeekFacts(new DateOnly(2026, 9, 28), Goal.Cut, 0.5m, false, null, null, (82.4m, 82m), 5, 3, 51, ["Squat: 140 kg (was 137.5)"], ["Quads"]);

        var json = OpenRouterWeeklySummaryWriter.FactsJson(facts).ToJsonString();

        json.Should().Contain("\"week\":\"2026-09-28 to 2026-10-04\"").And.Contain("\"goal\":\"cut\"").And.Contain("\"finishedWorkouts\":3")
            .And.Contain("\"trendWeightEndKg\":82").And.Contain("Squat: 140 kg").And.Contain("\"food\":null");
    }
}
