using System.Text.Json;
using FluentAssertions;
using Plate.Core.Domain;
using Plate.Web.Features.Feedback;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace Plate.Web.Tests;

/// <summary>
/// Real parses replayed against the database ranking (SPEC 11). The cases come from
/// <c>feedback parses --export-eval</c> on wizard, reviewed before committing, in Fixtures/parse-corrections*.jsonl.
/// An item must still get the food the user saved for it (older cases record that only for accepted items), and an
/// item rated "wrong food" must not get the rejected food again.
/// Failures that are known and not yet fixed are listed in Fixtures/parse-corrections.known-failures.txt
/// (GYM_WRITE_KNOWN=1 rewrites it), so only new regressions fail the test. Only shared seed foods are checked,
/// found by name, since ids differ between databases and the users' own foods aren't here.
/// </summary>
public class ParseRegressionTests(PostgresFixture fixture, ITestOutputHelper output) : IClassFixture<PostgresFixture>
{
    private static readonly string Fixtures = Path.Combine(RepoPaths.Root, "tests", "Plate.Web.Tests", "Fixtures");
    private static readonly string KnownFile = Path.Combine(Fixtures, "parse-corrections.known-failures.txt");
    private static readonly FoodSource[] SeedSources = [FoodSource.Usda, FoodSource.Reference, FoodSource.OpenFoodFacts];

    [Fact]
    public async Task Accepted_matches_hold_and_rejected_foods_are_not_picked_again()
    {
        var cases = Directory.Exists(Fixtures)
            ? Directory.EnumerateFiles(Fixtures, "parse-corrections*.jsonl")
                .SelectMany(File.ReadLines)
                .Where(l => l.Trim().Length > 0)
                .Select(l => JsonSerializer.Deserialize<EvalCase>(l, FeedbackJson.Options)!)
                .ToList()
            : [];
        var known = File.Exists(KnownFile)
            ? File.ReadLines(KnownFile).Select(l => l.Split('#')[0].Trim()).Where(l => l.Length > 0).ToHashSet()
            : [];

        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var failures = new List<string>();
        int checkedItems = 0, skipped = 0;
        foreach (var c in cases)
        {
            var fresh = await s.Matcher.ResolveAsync(c.Text, c.Items.Select(i => i.Parsed).ToList(), default);
            foreach (var item in c.Items)
            {
                var rejected = item.RejectedFoodItemId is null ? null : await SeedFoodAsync(s, item.FoodName, item.FoodSource);
                var savedFood = item.SavedFood is { } sf ? await SeedFoodAsync(s, sf.Name, sf.Source) : null;
                if (savedFood == rejected)
                {
                    savedFood = null; // kept after rating it wrong: no reference
                }

                if (rejected is null && savedFood is null)
                {
                    skipped++;
                    continue;
                }

                checkedItems++;
                var got = fresh[item.Index];
                var problems = new List<string>();
                if (savedFood is not null && got.FoodItemId != savedFood)
                {
                    problems.Add($"expected {item.SavedFood!.Value.Name}");
                }

                if (rejected is not null && got.FoodItemId == rejected)
                {
                    problems.Add($"expected not {item.FoodName}");
                }

                if (problems.Count > 0)
                {
                    failures.Add($"{c.RunId}:{item.Index}");
                    output.WriteLine($"{c.RunId}:{item.Index} {item.Verdict} \"{item.Parsed.NameOriginal}\" {string.Join(" and ", problems)}, got {got.MatchedName ?? "an AI estimate"}");
                }
            }
        }

        if (Environment.GetEnvironmentVariable("GYM_WRITE_KNOWN") == "1")
        {
            Directory.CreateDirectory(Fixtures);
            File.WriteAllLines(KnownFile, ["# run:item of parse regression cases that fail today; remove a line once it passes.", .. failures]);
        }

        output.WriteLine($"{cases.Count} cases, {checkedItems} items checked, {skipped} skipped (not a seed food), {failures.Count} failing ({failures.Count(known.Contains)} known).");
        foreach (var fixedNow in known.Except(failures))
        {
            output.WriteLine($"{fixedNow} passes now; remove it from {Path.GetFileName(KnownFile)}.");
        }

        failures.Where(f => !known.Contains(f)).Should().BeEmpty("these parse cases regressed; see the test output");
    }

    /// <summary>A shared seed food by name and source, or null for any other food.</summary>
    private static async Task<int?> SeedFoodAsync(UserScope s, string? name, FoodSource? source) =>
        name is null || source is not { } src || !SeedSources.Contains(src)
            ? null
            : await s.Db.FoodItems.Where(f => f.OwnerUserId == null && f.Name == name && f.Source == src).Select(f => (int?)f.Id).FirstOrDefaultAsync();
}
