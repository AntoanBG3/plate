using Plate.Core.Domain;
using Plate.Web.Tests;
using Microsoft.EntityFrameworkCore;
using FluentAssertions;

namespace Plate.E2E.Tests;

/// <summary>Progress charts draw what their legends list (Phase 11).</summary>
public class ChartTests(E2EFixture app) : IClassFixture<E2EFixture>
{
    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task A_target_with_one_day_so_far_shows_as_a_point(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.WithTargetsAsync(app);
        await phone.GoAsync("/today"); // stores today's estimate, the first day with a target
        // The user's day, as the app has it (the browser's UTC date can be a day off near midnight in Sofia).
        await phone.GoAsync("/food");
        var quick = await phone.Page.Locator("[data-sheet-open='quick-sheet']").First.GetAttributeAsync("hx-get");
        var date = System.Text.RegularExpressions.Regex.Match(quick!, @"date=([0-9-]+)").Groups[1].Value;
        await phone.PostAsync("/food/quick", new() { ["date"] = date, ["meal"] = "Lunch", ["kcal"] = "600", ["protein"] = "40" });
        await phone.GoAsync("/progress");
        await phone.Page.WaitForFunctionAsync("() => window.Chart && Chart.getChart(document.getElementById('protein-chart'))");

        var radii = await phone.Page.EvaluateAsync<double[]>("""
            () => ["calories-chart", "protein-chart"].map((id) => {
                const chart = Chart.getChart(document.getElementById(id));
                const meta = chart.getDatasetMeta(1); // Target, Aim
                const last = meta.data[meta.data.length - 1];
                return last.options.radius;
            })
            """);
        radii.Should().AllSatisfy(r => r.Should().BeGreaterThan(0, "a line needs two days; one day is drawn as a point"));
        phone.ShouldHaveNoErrors();
    }

    [Fact]
    public async Task Chart_uses_the_saved_minimum_for_yesterday()
    {
        await using var phone = await app.SignedInPhoneAsync(E2EFixture.Chromium);
        await phone.WithTargetsAsync(app);
        await phone.GoAsync("/progress");
        await using var system = app.Db.CreateContext(null);
        var uid = await system.Users.Where(u => u.Email == phone.Email).Select(u => u.Id).SingleAsync();
        await using var db = app.Db.CreateContext(uid);
        var now = await db.NutritionTargetDays.SingleAsync();
        var yesterday = now.Date.AddDays(-1);
        var values = System.Text.Json.Nodes.JsonNode.Parse(now.Values)!;
        values["macros"]!["proteinMinG"] = 95;
        db.NutritionTargetDays.Add(new() {UserId = uid, Date = yesterday, Values = values.ToJsonString()});
        db.FoodLog.Add(new() {UserId = uid, Date = yesterday, Description = "Stored day", Kcal = 200, CreatedAt = DateTimeOffset.UtcNow});
        await db.SaveChangesAsync();
        await phone.GoAsync("/progress");
        var plotted = await phone.Page.EvaluateAsync<int>("() => JSON.parse(document.getElementById('protein-data').textContent).minimum.at(-2)");
        plotted.Should().Be(95);
    }

    [Fact]
    public async Task Bulgarian_progress_keeps_the_custom_name()
    {
        await using var phone = await app.SignedInPhoneAsync(E2EFixture.Chromium);
        await phone.WithTargetsAsync(app, bulgarian: true);
        await using var system = app.Db.CreateContext(null);
        var uid = await system.Users.Where(u => u.Email == phone.Email).Select(u => u.Id).SingleAsync();
        await using var s = new UserScope(app.Db, uid);
        var custom = await s.Exercises.CreateAsync("Warm-up", Muscle.Chest, Equipment.Other, [], default);
        var session = await s.Sessions.StartAsync(null, default);
        var item = await s.Sessions.AddExerciseAsync(session.Id, custom.Id, default);
        await s.Sessions.LogSetAsync(new(Guid.NewGuid(), item, 20, 10, null, false, null), default);
        await s.Sessions.FinishAsync(session.Id, default);
        await phone.GoAsync("/progress");
        (await phone.Page.Locator("#exerciseId option:checked").TextContentAsync()).Should().Be("Warm-up");
    }
}
