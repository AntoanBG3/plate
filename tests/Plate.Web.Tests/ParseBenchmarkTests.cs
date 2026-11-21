using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Plate.Core.Nutrition;
using Plate.Web.Cli;
using Plate.Web.Features.Feedback;
using Plate.Web.Features.Food;
using Plate.Web.Integrations;
using Microsoft.Extensions.DependencyInjection;

namespace Plate.Web.Tests;

/// <summary>The parser benchmark (tools/parsebench): its labels read, a parse is scored against them, and a run goes end to end.</summary>
public class ParseBenchmarkTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly string File = Path.Combine(RepoPaths.Root, "tools", "parsebench", "cases.jsonl");

    private static List<ParseCase> Cases() => System.IO.File.ReadLines(File).Where(l => l.Trim().Length > 0).Select(l => JsonSerializer.Deserialize<ParseCase>(l, FeedbackJson.Options)!).ToList();

    private static ParsedFoodItem Item(string en, string original, decimal grams, decimal? units = null, CountUnit? of = null, bool stated = false, bool given = true, FoodState state = FoodState.NotApplicable) =>
        new(en, original, grams, state, null, null, new Per100g(100, 5, 10, 3), units, given, of, stated);

    [Fact]
    public void The_case_file_reads_with_valid_patterns()
    {
        var cases = Cases();

        cases.Should().HaveCountGreaterThan(55);
        cases.Select(c => c.Id).Should().OnlyHaveUniqueItems();
        foreach (var e in cases.SelectMany(c => c.Items))
        {
            e.Invoking(x => x.Matches(Item("x", "y", 1))).Should().NotThrow(e.Match);
            e.ReferenceGrams.Should().BeGreaterThan(0, e.Match);
        }
    }

    [Fact]
    public void A_parse_is_scored_item_by_item_against_the_labels()
    {
        var c = Cases().Single(x => x.Text == "хляб с масло и сирене");
        // Bread right, butter way over its range, cheese missing, a jam nobody wrote.
        FoodParseResult parsed = new([Item("bread", "хляб", 60, given: false), Item("butter", "масло", 50, given: false), Item("jam", "сладко", 20, given: false)], null, "m");
        ConfirmItem Row(decimal grams, decimal kcal) => new("x", grams, new Per100g(kcal, 1, 1, 1), 1, "x", Confidence.High, []);

        var x = ParseBenchmark.Score("m", 1, c, parsed, [Row(60, 265), Row(50, 717)], 900, null);

        x.Expected.Should().Be(3);
        x.Found.Should().Be(2);
        x.Extra.Should().Be(1);
        x.GramsRight.Should().Be(1);
        x.AllRight.Should().BeFalse();
        x.PlateKcal.Should().Be(517.5m);
        x.Problems.Should().Contain(p => p.Contains("missed")).And.Contain(p => p.StartsWith("масло: 50 g")).And.Contain("extra сладко");
    }

    [Fact]
    public void Counts_flags_and_states_are_checked_where_the_label_sets_them()
    {
        var two = Cases().Single(x => x.Text == "две закуски с лютеница и сирене от лидл");
        var ok = ParseBenchmark.Score("m", 1, two, new([Item("savoury pastry", "закуски с лютеница и сирене от лидл", 180, 2, CountUnit.Piece)], null, "m"), [], 1, null);
        var wrong = ParseBenchmark.Score("m", 1, two, new([Item("savoury pastry", "закуски с лютеница и сирене от лидл", 180, stated: true)], null, "m"), [], 1, null);

        ok.AllRight.Should().BeTrue();
        wrong.ChecksRight.Should().BeLessThan(wrong.Checks);
        wrong.Problems.Should().Contain(p => p.Contains("count none")).And.Contain(p => p.Contains("gramsStated True"));
    }

    [Fact]
    public async Task A_run_parses_matches_and_reports_each_model()
    {
        var item = new JsonObject
        {
            ["nameEn"] = "banana", ["nameOriginal"] = "banana", ["grams"] = 120, ["amountGiven"] = false, ["gramsStated"] = false, ["count"] = 1, ["countUnit"] = "piece",
            ["state"] = "na", ["preparation"] = null, ["assumption"] = null, ["kcalPer100g"] = 89, ["proteinPer100g"] = 1.1, ["carbsPer100g"] = 23, ["fatPer100g"] = 0.3,
            ["fiberPer100g"] = null, ["saturatedFatPer100g"] = null,
        };
        var completion = new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = new JsonObject { ["items"] = new JsonArray(item) }.ToJsonString() } }),
            ["usage"] = new JsonObject { ["prompt_tokens"] = 900, ["completion_tokens"] = 120, ["cost"] = 0.00021 },
        }.ToJsonString();
        using var app = new AppFactory(fixture,
            settings: new Dictionary<string, string?> { ["OpenRouter:ApiKey"] = "sk-or-v1-test-0123456789abcdef" },
            services: s =>
            {
                s.AddHttpClient<OpenRouterModels>().ConfigurePrimaryHttpMessageHandler(() => new StubOpenRouter());
                s.AddHttpClient<OpenRouterFoodParser>().ConfigurePrimaryHttpMessageHandler(() => new StubOpenRouter(completion: completion));
            });
        var lines = string.Join('\n', System.IO.File.ReadLines(File).Where(l => l.Contains("\"a banana\"")));
        var output = new StringWriter();

        (await FeedbackCommand.RunAsync(app.Services, ["feedback", "parsebench", "--models", "openai/gpt-x,openai/gpt-x@low", "--repeat", "1", "--matcher", "off"], output, input: new StringReader(lines)))
            .Should().Be(0);

        var text = output.ToString();
        text.Should().Contain("1 texts, 1 expected items, 1 repeats")
            .And.MatchRegex(@"openai/gpt-x\s+100\.0\s+100\.0")
            .And.Contain("openai/gpt-x@low")
            .And.Contain("0.000210");
    }
}
