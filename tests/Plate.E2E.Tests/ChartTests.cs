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
}
