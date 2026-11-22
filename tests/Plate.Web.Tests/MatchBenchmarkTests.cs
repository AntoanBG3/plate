using FluentAssertions;
using Plate.Web.Cli;
using Plate.Web.Features.Feedback;

namespace Plate.Web.Tests;

/// <summary>The decision-model benchmark (tools/matchbench): its case file reads, and the ranking baseline is scored against the labels.</summary>
public class MatchBenchmarkTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly string Cases = Path.Combine(RepoPaths.Root, "tools", "matchbench", "cases.jsonl");

    [Fact]
    public void The_case_file_reads_and_every_pattern_is_a_valid_regex()
    {
        var cases = File.ReadLines(Cases).Where(l => l.Trim().Length > 0).Select(l => System.Text.Json.JsonSerializer.Deserialize<BenchCase>(l, FeedbackJson.Options)!).ToList();

        cases.Should().HaveCountGreaterThan(90);
        cases.Select(c => c.Id).Should().OnlyHaveUniqueItems();
        foreach (var item in cases.SelectMany(c => c.Items))
        {
            var check = () => item.Right("x", "y", 100);
            check.Should().NotThrow(item.NameOriginal);
        }
    }

    [Fact]
    public void Labels_take_either_name_and_respect_the_reject_pattern_and_energy()
    {
        var item = new BenchItem("cottage cheese", "извара", "na", 200, "извара|cottage", "плод|fruit", [50, 180], false);

        item.Right("Cheese, cottage, lowfat, 2% milkfat", "Котидж сирене, 2% масленост", 84).Should().BeTrue();
        item.Right("Извара", "Извара", 99).Should().BeTrue();
        item.Right("Cheese, cottage, with fruit", "Котидж сирене, с плодове", 97).Should().BeFalse("rejected");
        item.Right("Danish pastry, cheese", "Даниш сладкиш със сирене / извара", 374).Should().BeFalse("too much energy");
        new BenchItem("pad thai", "pad thai", "cooked", 300, null, null, null, true).Right("Pad thai", null, 150).Should().BeFalse("nothing is right");
    }

    [Fact]
    public async Task The_ranking_baseline_runs_against_the_catalogue_and_reports_per_category()
    {
        using var app = new AppFactory(fixture);
        var lines = string.Join('\n', File.ReadLines(Cases).Where(l => l.Contains("\"a banana\"") || l.Contains("\"pad thai\"")));
        // Required positive whose input retrieves nothing: declining it must count as a miss.
        lines += "\n" + """{"id":"missing","category":"generic","lang":"en","text":"xqzj","items":[{"nameEn":"xqzj","nameOriginal":"xqzj","state":"na","grams":100,"accept":"banana","noneOk":false}]}""";
        var output = new StringWriter();

        (await FeedbackCommand.RunAsync(app.Services, ["feedback", "matchbench", "--models", "off"], output, input: new StringReader(lines))).Should().Be(0);

        var text = output.ToString();
        text.Should().Contain("3 cases, 3 items (1 with no right food in the catalogue")
            .And.MatchRegex(@"off\s+\d+\.\d")
            .And.Contain("final by category")
            .And.Contain("1 repeat")
            .And.Contain("Selected correctly")
            .And.Contain("Required matches declined 1/2")
            .And.Contain("How rows were chosen")
            .And.Contain("missing/1: first acceptable rank - of 0; no candidates; estimate: no candidates; wrong");
    }
}
