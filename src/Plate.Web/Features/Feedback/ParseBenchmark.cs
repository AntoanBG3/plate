using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Plate.Core.Nutrition;
using Plate.Web.Features.Food;
using Plate.Web.Integrations;

namespace Plate.Web.Features.Feedback;

/// <summary>One labelled entry of the parser benchmark (tools/parsebench/cases.jsonl): the text and the items it should give.</summary>
public record ParseCase(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("lang")] string Lang,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("items")] IReadOnlyList<ExpectedItem> Items);

/// <summary>
/// An item the text should give. <c>match</c> finds it among the parsed items by its English or original name. <c>grams</c>
/// is a number for an amount the text states (right within 3% or 3 g) or [min, max] for an estimate. <c>stated</c>,
/// <c>given</c> and <c>count</c> are the expected gramsStated, amountGiven and count, or "any" when the text is ambiguous;
/// a null count means none. <c>kcal100</c> is a reference energy for the meal's kcal; an optional item may be missing.
/// </summary>
public record ExpectedItem(
    [property: JsonPropertyName("match")] string Match,
    [property: JsonPropertyName("grams")] JsonElement Grams,
    [property: JsonPropertyName("stated")] JsonElement Stated,
    [property: JsonPropertyName("given")] JsonElement Given,
    [property: JsonPropertyName("count")] JsonElement Count,
    [property: JsonPropertyName("unit")] string? Unit,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("kcal100")] decimal Kcal100,
    [property: JsonPropertyName("optional")] bool Optional = false)
{
    public bool Matches(ParsedFoodItem p) =>
        Regex.IsMatch(p.NameEn, Match, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        || Regex.IsMatch(p.NameOriginal, Match, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The reference grams: the stated amount, or the middle of the range.</summary>
    public decimal ReferenceGrams => Grams.ValueKind == JsonValueKind.Number ? Grams.GetDecimal() : (Grams[0].GetDecimal() + Grams[1].GetDecimal()) / 2;

    public bool GramsRight(decimal grams) => Grams.ValueKind == JsonValueKind.Number
        ? Math.Abs(grams - Grams.GetDecimal()) <= Math.Max(3, Grams.GetDecimal() * 0.03m)
        : grams >= Grams[0].GetDecimal() && grams <= Grams[1].GetDecimal();

    /// <summary>Null when not scored ("any").</summary>
    public bool? CountRight(ParsedFoodItem p) => Count.ValueKind switch
    {
        JsonValueKind.String => null,
        JsonValueKind.Null or JsonValueKind.Undefined => p.Units is null,
        _ => p.Units is { } u && Math.Abs(u - Count.GetDecimal()) < 0.01m
            && (Unit is null || string.Equals((p.CountOf ?? CountUnit.Piece).ToString(), Unit, StringComparison.OrdinalIgnoreCase)),
    };

    public static bool? Flag(JsonElement expected, bool actual) =>
        expected.ValueKind is JsonValueKind.True or JsonValueKind.False ? expected.GetBoolean() == actual : null;

    public bool? StateRight(ParsedFoodItem p) => State is null ? null
        : string.Equals(State, p.State switch { FoodState.Raw => "raw", FoodState.Cooked => "cooked", _ => "na" }, StringComparison.Ordinal);
}

/// <summary>One model on one case in one repetition.</summary>
public record ParseBenchOutcome(string Model, int Repeat, string CaseId, int Expected, int Found, int Extra, int Checks, int ChecksRight,
    int GramsChecks, int GramsRight, bool AllRight, decimal ReferenceKcal, decimal PlateKcal, long Ms, bool Failed, ParseUsage? Usage, decimal? Cost,
    IReadOnlyList<string> Problems);

/// <summary>
/// <c>feedback parsebench</c>: compares parser models on labelled meal texts. Each text goes through the live parser with
/// the model, then Plate's matcher and portions as they run in the app (no account history), and is scored on the items
/// it should give (missed, extra), their amounts (stated weights exact, estimates in range, counts and units, the
/// stated and given flags, raw or cooked) and the meal's kcal against a reference. Also latency, failures, tokens and cost.
/// Models are ids, or id@effort to set the reasoning effort (lowest when not given). Spends OpenRouter credit.
/// </summary>
public static class ParseBenchmark
{
    public const string Usage = "Usage: feedback parsebench --models <id[@effort],...> [--repeat 2] [--matcher off|<decision model id>] [--json] < tools/parsebench/cases.jsonl";

    private const string BenchUser = "parsebench";

    /// <summary>A parse slower than this counts as slow; the app's parser gives up at 15 s and retries once.</summary>
    private const long SlowMs = 10_000;

    /// <summary>A model with this many slow or failed calls, more than half of its calls so far, is dropped.</summary>
    private const int GiveUpAfter = 5;

    public static async Task<int> RunAsync(IServiceProvider services, string[] args, TextWriter o, TextReader input, bool json, CancellationToken ct)
    {
        if (args.Contains("--help") || Option(args, "--models") is not { } list)
        {
            o.WriteLine(Usage);
            return args.Contains("--help") ? 0 : 2;
        }

        var specs = list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
        var repeats = int.TryParse(Option(args, "--repeat"), out var r) && r is > 0 and <= 5 ? r : 2;
        var cases = new List<ParseCase>();
        while (input.ReadLine() is { } line)
        {
            if (line.Trim().Length > 0)
            {
                cases.Add(JsonSerializer.Deserialize<ParseCase>(line, FeedbackJson.Options)!);
            }
        }

        var evaluator = services.GetRequiredService<ParseEvaluator>();
        var catalog = services.GetRequiredService<OpenRouterModels>();
        var outcomes = new ConcurrentBag<ParseBenchOutcome>();
        var dropped = new ConcurrentDictionary<string, string>();
        await Task.WhenAll(specs.Select(spec => Task.Run(async () =>
        {
            var (model, effort) = spec.Split('@') is [var m, var e] ? (m, e) : (spec, AiTasks.LowestEffort);
            using var scope = services.CreateScope();
            var replay = scope.ServiceProvider.GetRequiredService<ParseEvaluator>();
            var models = await replay.ModelsAsync(model, Option(args, "--matcher"), ct);
            var info = await catalog.FindAsync(model, ct);
            var mine = new List<ParseBenchOutcome>();
            for (var rep = 1; rep <= repeats; rep++)
            {
                foreach (var c in cases)
                {
                    var clock = Stopwatch.StartNew();
                    var (parsed, rows) = await replay.ReplayAsync(BenchUser, c.Text, DateTimeOffset.UtcNow, false, models, ct, effort: effort);
                    var outcome = Score(spec, rep, c, parsed, rows, clock.ElapsedMilliseconds, info);
                    outcomes.Add(outcome);
                    mine.Add(outcome);
                    // A parse slower than this falls back to manual entry in the app; a model that keeps doing it is no candidate.
                    var slow = mine.Count(x => x.Ms > SlowMs || x.Failed);
                    if (slow >= GiveUpAfter && slow * 2 > mine.Count)
                    {
                        dropped[spec] = $"dropped after {mine.Count} calls: {slow} slower than {SlowMs / 1000} s or failed";
                        Console.Error.WriteLine($"{spec}: {dropped[spec]}.");
                        return;
                    }
                }
            }

            // Printed as each model finishes, so a run stopped early keeps what it has.
            Console.Error.WriteLine("done " + SummaryRow(spec, mine, repeats));
        }, ct)));

        var all = outcomes.ToList();
        if (json)
        {
            o.WriteLine(JsonSerializer.Serialize(new { repeats, outcomes = all.OrderBy(x => x.Model).ThenBy(x => x.Repeat).ThenBy(x => x.CaseId) },
                new JsonSerializerOptions(FeedbackJson.Options) { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals }));
            return 0;
        }

        Write(o, specs, cases, all, repeats);
        foreach (var (spec, why) in dropped)
        {
            o.WriteLine($"{spec}: {why}; its row above covers only those calls.");
        }

        return 0;
    }

    /// <summary>Pairs the parsed items with the expected ones by name, then checks each pair and the meal's kcal.</summary>
    public static ParseBenchOutcome Score(string model, int repeat, ParseCase c, FoodParseResult parsed, IReadOnlyList<ConfirmItem> rows, long ms, OpenRouterModel? info)
    {
        var items = parsed.Items.ToList();
        var used = new bool[items.Count];
        int expected = 0, found = 0, checks = 0, right = 0, gramsChecks = 0, gramsRight = 0;
        var problems = new List<string>();
        foreach (var e in c.Items)
        {
            if (!e.Optional) expected++;
            var n = Enumerable.Range(0, items.Count).FirstOrDefault(i => !used[i] && e.Matches(items[i]), -1);
            if (n < 0)
            {
                if (!e.Optional) problems.Add($"missed /{e.Match}/");
                continue;
            }

            used[n] = true;
            if (e.Optional) continue;
            found++;
            var p = items[n];
            void Check(bool? ok, string what)
            {
                if (ok is not { } v) return;
                checks++;
                if (v) right++; else problems.Add($"{p.NameOriginal}: {what}");
            }

            gramsChecks++;
            if (e.GramsRight(p.Grams)) gramsRight++; else problems.Add($"{p.NameOriginal}: {p.Grams:0.#} g, expected {e.Grams}");
            Check(e.CountRight(p), $"count {(p.Units is { } u ? $"{u:0.##} {p.CountOf ?? CountUnit.Piece}" : "none")}");
            Check(ExpectedItem.Flag(e.Stated, p.GramsStated), $"gramsStated {p.GramsStated}");
            Check(ExpectedItem.Flag(e.Given, p.AmountGiven), $"amountGiven {p.AmountGiven}");
            Check(e.StateRight(p), $"state {p.State}");
        }

        var extra = used.Count(u => !u);
        if (extra > 0) problems.Add("extra " + string.Join(", ", items.Where((_, i) => !used[i]).Select(p => p.NameOriginal)));
        var reference = Math.Round(c.Items.Where(e => !e.Optional).Sum(e => e.ReferenceGrams * e.Kcal100 / 100), 1);
        var plate = Math.Round(rows.Sum(x => x.Grams * x.Per100.Kcal / 100), 1);
        var failed = parsed.Error is not null && items.Count == 0 && c.Items.Any(e => !e.Optional);
        var usage = parsed.Usage;
        var cost = usage?.Cost ?? (usage is null || info is null ? null : (usage.InputTokens * info.PromptPerMillion + usage.OutputTokens * info.CompletionPerMillion) / 1_000_000m);
        return new ParseBenchOutcome(model, repeat, c.Id, expected, found, extra, checks, right, gramsChecks, gramsRight,
            found == expected && extra == 0 && right == checks && gramsRight == gramsChecks, reference, plate, ms, failed, usage, cost, problems);
    }

    private static void Write(TextWriter o, List<string> specs, List<ParseCase> cases, List<ParseBenchOutcome> all, int repeats)
    {
        o.WriteLine($"{cases.Count} texts, {cases.Sum(c => c.Items.Count(i => !i.Optional))} expected items, {repeats} repeats; matcher and portions as in the app, no account history.");
        o.WriteLine();
        o.WriteLine($"{"model",-44} {"right",6} {"found",6} {"extra",6} {"grams",6} {"flags",6} {"kcal",6} {"med",5} {"bias",6} {"p50ms",6} {"p95ms",6} {"fail",4} {"$/call",9} {"out tok",7}");
        foreach (var spec in specs)
        {
            var xs = all.Where(x => x.Model == spec).ToList();
            if (xs.Count > 0) o.WriteLine(SummaryRow(spec, xs, repeats));
        }

        o.WriteLine();
        o.WriteLine("right: text parsed fully right (every item found, none extra, every amount and flag right); found: expected items found; extra: items per run");
        o.WriteLine("nobody expected; grams: amounts right (stated within 3%, estimates in range); flags: count, unit, stated, given and raw or cooked right;");
        o.WriteLine("kcal / med / bias: the meal's kcal after matching against the reference, mean and median absolute and mean signed; $/call from OpenRouter.");
        o.WriteLine();
        var categories = cases.Select(c => c.Category).Distinct().OrderBy(x => x).ToList();
        o.WriteLine($"{"texts fully right by category",-44} " + string.Join(" ", categories.Select(c => $"{c,8}")));
        foreach (var spec in specs)
        {
            var xs = all.Where(x => x.Model == spec).ToList();
            o.WriteLine($"{spec,-44} " + string.Join(" ", categories.Select(cat =>
            {
                var ids = cases.Where(c => c.Category == cat).Select(c => c.Id).ToHashSet();
                var inCat = xs.Where(x => ids.Contains(x.CaseId)).ToList();
                return $"{Pct(inCat.Count(x => x.AllRight), inCat.Count),8}";
            })));
        }

        o.WriteLine();
        o.WriteLine("Problems in the last repeat, by text:");
        foreach (var c in cases)
        {
            var bad = all.Where(x => x.CaseId == c.Id && x.Repeat == repeats && x.Problems.Count > 0).OrderBy(x => x.Model).ToList();
            if (bad.Count == 0) continue;
            o.WriteLine($"  {c.Id} [{c.Category}] \"{(c.Text.Length > 60 ? c.Text[..60].Replace('\n', ' ') + "…" : c.Text.Replace('\n', ' '))}\"");
            foreach (var x in bad)
            {
                o.WriteLine($"      {x.Model}: {string.Join("; ", x.Problems)}");
            }
        }
    }

    /// <summary>One model's row of the summary table.</summary>
    private static string SummaryRow(string spec, List<ParseBenchOutcome> xs, int repeats)
    {
        var errors = xs.Where(x => x.ReferenceKcal > 0 || x.PlateKcal > 0).Select(x => x.PlateKcal - x.ReferenceKcal).ToList();
        var abs = errors.Select(Math.Abs).OrderBy(x => x).ToList();
        var ms = xs.Select(x => (double)x.Ms).OrderBy(x => x).ToList();
        var runs = Math.Max(1, xs.Select(x => x.Repeat).Distinct().Count());
        return $"{spec,-44} {Pct(xs.Count(x => x.AllRight), xs.Count),6} {Pct(xs.Sum(x => x.Found), xs.Sum(x => x.Expected)),6} {(double)xs.Sum(x => x.Extra) / runs,6:0.#} "
            + $"{Pct(xs.Sum(x => x.GramsRight), xs.Sum(x => x.GramsChecks)),6} {Pct(xs.Sum(x => x.ChecksRight), xs.Sum(x => x.Checks)),6} "
            + $"{(abs.Count == 0 ? 0 : abs.Average()),6:0} {(abs.Count == 0 ? 0 : abs[abs.Count / 2]),5:0} {(errors.Count == 0 ? 0 : errors.Average()),6:+0;-0;0} "
            + $"{(ms.Count == 0 ? 0 : ms[ms.Count / 2]),6:0} {(ms.Count == 0 ? 0 : ms[(int)Math.Min(ms.Count - 1, ms.Count * 0.95)]),6:0} {xs.Count(x => x.Failed),4} "
            + $"{(xs.Any(x => x.Cost is not null) ? xs.Where(x => x.Cost is not null).Average(x => x.Cost!.Value) : 0),9:0.000000} {xs.Where(x => x.Usage is not null).Select(x => (double)x.Usage!.OutputTokens).DefaultIfEmpty(0).Average(),7:0}";
    }

    private static string Pct(int n, int of) => of == 0 ? "-" : (100.0 * n / of).ToString("0.0", CultureInfo.InvariantCulture);

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
