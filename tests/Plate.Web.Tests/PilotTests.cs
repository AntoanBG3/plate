using System.Text.Json.Nodes;
using FluentAssertions;
using Plate.Core.Nutrition;
using Plate.Web.Cli;
using Plate.Web.Features.Feedback;
using Plate.Web.Integrations;
using Microsoft.Extensions.DependencyInjection;

namespace Plate.Web.Tests;

/// <summary>The weighed-meal pilot's file and its comparison with what Plate gives (reports/weighed-meal-pilot.md).</summary>
public class PilotTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Header = "meal,eaten_at,account,category,text,item,grams,kcal_100g,protein_100g,carbs_100g,fat_100g,source,uncertain,notes";

    [Fact]
    public void Rows_group_into_meals_and_every_problem_is_reported_by_line()
    {
        var (meals, errors) = PilotEvaluation.Read([
            Header,
            "example-1,,,counted,две закуски,pastry,180,233,7.6,23.3,11.9,label,,",
            "1,2026-11-22 08:15,,counted,\"Две закуски с лютеница и сирене от Лидл, айрян\",pastry,180,233,7.6,23.3,11.9,estimate,,",
            "1,,,,,ayran,250,\"37,5\",1.7,2.6,2,label,,",
            "2,,,soup,супа,soup,300,40,2,5,1,table,yes,",
            "3,noon,,staple,ориз,rice,abc,130,2.7,28,0.3,table,,",
        ], account: "me@example.test");

        meals.Select(m => m.Id).Should().Equal("1", "3");
        meals[0].Should().Match<WeighedMeal>(m => m.Text == "Две закуски с лютеница и сирене от Лидл, айрян" && m.Items.Count == 2 && m.Account == "me@example.test");
        meals[0].EatenAt.Should().Be(new DateTimeOffset(2026, 11, 22, 8, 15, 0, TimeSpan.FromHours(2)), "a local time is Sofia's");
        meals[0].Items[1].Per100.Kcal.Should().Be(37.5m, "a decimal comma is read");
        errors.Should().Equal(
            "Line 5: meal 2 needs a category: counted, staple, homemade, loose.",
            "Line 6: eaten_at \"noon\" isn't a time like 2026-11-22 13:30.",
            "Line 6: grams \"abc\" isn't a number from 0 to 3000.");
    }

    [Fact]
    public void The_template_in_the_repository_has_every_column_and_only_examples()
    {
        var template = File.ReadAllLines(Path.Combine(RepoPaths.Root, "reports", "weighed-meals-template.csv"));
        var (meals, errors) = PilotEvaluation.Read(template, "me@example.test");

        meals.Should().BeEmpty();
        errors.Should().Equal("No meals in the file (rows whose meal id starts with \"example\" are skipped).");
        template[0].Should().Be(Header);
    }

    [Fact]
    public async Task A_pilot_meal_is_replayed_as_the_account_and_compared_with_what_was_weighed()
    {
        var item = new JsonObject
        {
            ["nameEn"] = "savoury pastry with white cheese and lyutenitsa, Lidl", ["nameOriginal"] = "закуски с лютеница и сирене от Лидл", ["grams"] = 150,
            ["amountGiven"] = true, ["gramsStated"] = false, ["count"] = 2, ["countUnit"] = "piece", ["state"] = "na", ["preparation"] = null, ["assumption"] = null,
            ["kcalPer100g"] = 280, ["proteinPer100g"] = 9, ["carbsPer100g"] = 30, ["fatPer100g"] = 13, ["fiberPer100g"] = null, ["saturatedFatPer100g"] = null,
        };
        var completion = StubOpenRouter.Completion(new JsonObject { ["items"] = new JsonArray(item) }.ToJsonString());
        using var app = new AppFactory(fixture,
            settings: new Dictionary<string, string?> { ["OpenRouter:ApiKey"] = "sk-or-v1-test-0123456789abcdef" },
            services: s =>
            {
                s.AddHttpClient<OpenRouterModels>().ConfigurePrimaryHttpMessageHandler(() => new StubOpenRouter());
                s.AddHttpClient<OpenRouterFoodParser>().ConfigurePrimaryHttpMessageHandler(() => new StubOpenRouter(completion: completion));
            });
        var (_, email) = await app.SignedInClientAsync();
        var csv = new StringReader(string.Join('\n', Header,
            $"7,,{email},counted,Две закуски с лютеница и сирене от Лидл,pastry,170,233,7.6,23.3,11.9,estimate,,weighed 2 pieces"));
        var output = new StringWriter();

        (await FeedbackCommand.RunAsync(app.Services, ["feedback", "pilot", "--parser", "openai/gpt-x", "--matcher", "off"], output, input: csv)).Should().Be(0);

        var text = output.ToString();
        text.Should().Contain("1 meals (1 counted), 0 with uncertain items.")
            .And.Contain("weighed 396.1 kcal, Plate 419.4 (+23.3)", "Plate takes two 90 g pieces; the two weighed 170 g")
            .And.Contain("    Plate   180 g")
            .And.Contain("Overall: 1 meals, kcal off by 23.3 on average");
    }

    [Fact]
    public async Task Check_reads_the_file_and_calls_nothing()
    {
        using var app = new AppFactory(fixture);
        var (_, email) = await app.SignedInClientAsync();
        var output = new StringWriter();
        var csv = new StringReader(string.Join('\n', Header, $"1,,{email},staple,150 г сварен ориз,rice,150,130,2.7,28,0.3,table,,"));

        (await FeedbackCommand.RunAsync(app.Services, ["feedback", "pilot", "--check"], output, input: csv)).Should().Be(0);
        output.ToString().Should().Contain("1 meals (1 staple)");

        (await FeedbackCommand.RunAsync(app.Services, ["feedback", "pilot", "--check"], new StringWriter(),
            input: new StringReader(Header + "\n1,,nobody@example.test,staple,ориз,rice,150,130,2.7,28,0.3,table,,"))).Should().Be(2);
    }
}
