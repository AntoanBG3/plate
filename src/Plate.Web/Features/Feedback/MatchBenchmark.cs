using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Plate.Core.Nutrition;
using Plate.Web.Data;
using Plate.Web.Features.Food;
using Plate.Web.Integrations;
using Microsoft.EntityFrameworkCore;

namespace Plate.Web.Features.Feedback;

/// <summary>One labelled entry of the matcher benchmark (tools/matchbench/cases.jsonl): the text and its parsed items.</summary>
public record BenchCase(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("lang")] string Lang,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("items")] IReadOnlyList<BenchItem> Items);

/// <summary>
/// A parsed item and which shared foods are right for it: a name matching <paramref name="Accept"/> (English or Bulgarian
/// name), not <paramref name="Reject"/>, with energy in <paramref name="Kcal"/>. No accept pattern means no food in the
/// catalogue is this item, so "no match" is right; <paramref name="NoneOk"/> makes "no match" right as well as a food.
/// </summary>
public record BenchItem(
    [property: JsonPropertyName("nameEn")] string NameEn,
    [property: JsonPropertyName("nameOriginal")] string NameOriginal,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("grams")] decimal Grams,
    [property: JsonPropertyName("accept")] string? Accept,
    [property: JsonPropertyName("reject")] string? Reject,
    [property: JsonPropertyName("kcal")] decimal[]? Kcal,
    [property: JsonPropertyName("noneOk")] bool NoneOk)
{
    public bool Right(string name, string? nameBg, decimal kcal) =>
        Accept is not null
        && (Regex.IsMatch(name, Accept, Options) || Regex.IsMatch(nameBg ?? "", Accept, Options))
        && (Reject is null || !(Regex.IsMatch(name, Reject, Options) || Regex.IsMatch(nameBg ?? "", Reject, Options)))
        && (Kcal is not [var min, var max] || (kcal >= min && kcal <= max));

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    public ParsedFoodItem Parsed() => new(NameEn, NameOriginal, Grams,
        State switch { "raw" => FoodState.Raw, "cooked" => FoodState.Cooked, _ => FoodState.NotApplicable },
        null, null, new Per100g(100, 5, 15, 3), AmountGiven: true, GramsStated: true);
}

/// <summary>What one model did with one item in one repetition.</summary>
/// <param name="Pick">The decider's raw answer: a food id, 0 for "none", null for no answer (timeout, error, or not asked).</param>
/// <param name="Final">The row's food after the matcher's floors, or null for an AI estimate.</param>
public record BenchOutcome(string Model, int Repeat, string CaseId, int Item, int? Pick, decimal? Confidence, bool PickRight, int? Final, bool FinalRight,
    bool FinalRightAt3s, decimal? KcalError, long Ms, bool Failed, int? Tokens, decimal? Cost, int? PoolSize, int? FirstAcceptableRank, string? RankingReason, string? Outcome = null);

/// <summary>
/// <c>feedback matchbench</c>: compares decision models on a labelled set of parsed items against the live catalogue.
/// Every model gets the same questions (the matcher's own shortlists), repeated to measure how stable its answers are.
/// Reports accuracy of the raw pick and of the row after Plate's confidence floors, "no match" handling, accuracy by
/// confidence, latency against the production 3-second limit, and cost. Spends OpenRouter credit (decision calls only).
/// </summary>
public static class MatchBenchmark
{
    public const string Usage = "Usage: feedback matchbench --models <id,id,...|off> [--repeat 3] [--timeout 10] [--json] < cases.jsonl";

    /// <summary>The production timeout; slower answers fall back to the ranking there.</summary>
    private const long ProductionMs = 3000;

    private const string BenchUser = "matchbench";

    public static async Task<int> RunAsync(IServiceProvider services, string[] args, TextWriter o, TextReader input, bool json, CancellationToken ct)
    {
        if (args.Contains("--help") || Option(args, "--models") is not { } list)
        {
            o.WriteLine(Usage);
            return args.Contains("--help") ? 0 : 2;
        }

        var models = list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(m => m != AiTasks.Off).Distinct().ToList();
        var repeats = int.TryParse(Option(args, "--repeat"), out var r) && r is > 0 and <= 10 ? r : 3;
        if (models.Count == 0) repeats = 1;
        var timeout = TimeSpan.FromSeconds(int.TryParse(Option(args, "--timeout"), out var t) && t is > 0 and <= 60 ? t : 10);
        var cases = new List<BenchCase>();
        while (input.ReadLine() is { } line)
        {
            if (line.Trim().Length > 0)
            {
                cases.Add(JsonSerializer.Deserialize<BenchCase>(line, FeedbackJson.Options)!);
            }
        }

        var key = await services.GetRequiredService<OpenRouterSettings>().GetKeyAsync(ct);
        if (key is null && models.Count > 0)
        {
            Console.Error.WriteLine("No OpenRouter key is set.");
            return 1;
        }

        var options = services.GetRequiredService<DbContextOptions<AppDbContext>>();
        var foodDatabase = services.GetRequiredService<IFoodDatabase>();

        // The ranking alone, with the shortlists every model will be asked about.
        Console.Error.WriteLine($"{cases.Count} cases, {cases.Sum(c => c.Items.Count)} items; ranking baseline first.");
        var baseline = await RunAsync(cases, "off", null, 1, options, foodDatabase, null, ct);
        var foods = baseline.Foods;
        var reachable = baseline.Reachable;
        var baselineRight = baseline.Outcomes.ToDictionary(x => (x.CaseId, x.Item), x => x.FinalRight);

        var outcomes = new ConcurrentBag<BenchOutcome>(baseline.Outcomes);
        await Task.WhenAll(models.Select(model => Task.Run(async () =>
        {
            var jev = services.GetRequiredService<JevFoodMatchDecider>();
            jev.Timeout = timeout;
            var run = await RunAsync(cases, model, new Asker(jev, key!, model), repeats, options, foodDatabase, baselineRight, ct);
            foreach (var x in run.Outcomes) outcomes.Add(x);
            foreach (var (id, f) in run.Foods) foods.TryAdd(id, f);
            Console.Error.WriteLine($"{model}: done.");
        }, ct)));

        var report = Report(cases, ["off", .. models], outcomes.ToList(), reachable);
        if (json)
        {
            // Shares with no items are NaN; JSON has no NaN, so they are written by name.
            o.WriteLine(JsonSerializer.Serialize(new { repeats, timeoutSeconds = timeout.TotalSeconds, summary = report, outcomes = outcomes.OrderBy(x => x.Model).ThenBy(x => x.Repeat).ThenBy(x => x.CaseId).ThenBy(x => x.Item) },
                new JsonSerializerOptions(FeedbackJson.Options) { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals }));
        }
        else
        {
            WriteReport(o, report, repeats, timeout, cases, outcomes.ToList(), foods, reachable);
        }

        return 0;
    }

    private sealed record RunResult(List<BenchOutcome> Outcomes, ConcurrentDictionary<int, (string Name, string? NameBg, decimal Kcal)> Foods, Dictionary<(string, int), bool> Reachable);

    private static async Task<RunResult> RunAsync(List<BenchCase> cases, string model, Asker? asker, int repeats, DbContextOptions<AppDbContext> options,
        IFoodDatabase foodDatabase, Dictionary<(string, int), bool>? baselineRight, CancellationToken ct)
    {
        var user = new FixedCurrentUser(BenchUser);
        await using var db = new AppDbContext(options, user);
        var service = new FoodItemService(db, user, foodDatabase);
        var foods = new ConcurrentDictionary<int, (string Name, string? NameBg, decimal Kcal)>();
        var reachable = new Dictionary<(string, int), bool>();
        var outcomes = new List<BenchOutcome>();
        for (var rep = 1; rep <= repeats; rep++)
        {
            foreach (var c in cases)
            {
                var recorder = new Recorder(asker);
                var ranking = new List<RankingInspection>();
                var rows = await new FoodMatcher(service, recorder).ResolveAsync(c.Text, c.Items.Select(i => i.Parsed()).ToList(), ct, inspectRanking: ranking);
                var ids = rows.Select(x => x.FoodItemId).Concat(recorder.Questions.SelectMany(q => q.Candidates.Select(x => (int?)x.Id))).OfType<int>().Distinct().Where(id => !foods.ContainsKey(id)).ToList();
                foreach (var f in await db.FoodItems.IgnoreQueryFilters().Where(f => ids.Contains(f.Id)).Select(f => new { f.Id, f.Name, f.NameBg, f.KcalPer100g }).ToListAsync(ct))
                {
                    foods[f.Id] = (f.Name, f.NameBg, f.KcalPer100g);
                }

                for (var n = 0; n < c.Items.Count; n++)
                {
                    var item = c.Items[n];
                    bool Right(int id) => foods.TryGetValue(id, out var f) && item.Right(f.Name, f.NameBg, f.Kcal);
                    var shortlist = recorder.Questions.ElementAtOrDefault(n)?.Candidates ?? [];
                    var canReach = shortlist.Any(x => Right(x.Id));
                    if (asker is null) reachable[(c.Id, n)] = canReach;
                    // "No match" is right when nothing in the catalogue is this item, or nothing right is shortlisted.
                    var noneRight = item.NoneOk || item.Accept is null || !canReach;
                    var decision = recorder.Decisions?.Items.ElementAtOrDefault(n);
                    int? pick = decision is null ? null : decision.FoodItemId ?? 0;
                    var pickRight = pick switch { null => false, 0 => noneRight, var id => Right(id.Value) };
                    var final = rows[n].FoodItemId;
                    var finalRight = final is { } fid ? Right(fid) : item.NoneOk || item.Accept is null;
                    var inspection = ranking.ElementAtOrDefault(n);
                    var firstRank = inspection?.Pool.Select((food, i) => new { Food = food, Rank = i + 1 })
                        .FirstOrDefault(x => item.Right(x.Food.Name, x.Food.NameBg, x.Food.KcalPer100g))?.Rank;
                    var slow = recorder.Ms > ProductionMs || recorder.Failed;
                    var at3s = asker is null || !slow ? finalRight : baselineRight!.GetValueOrDefault((c.Id, n));
                    outcomes.Add(new BenchOutcome(model, rep, c.Id, n, pick, decision?.Confidence, pickRight, final, finalRight, at3s,
                        KcalError(item, final, finalRight, shortlist, foods), recorder.Ms, recorder.Failed && asker is not null, recorder.Decisions?.InputTokens, recorder.Decisions?.Cost,
                        inspection?.Pool.Count, firstRank, inspection?.Reason, inspection?.Outcome));
                }
            }
        }

        return new RunResult(outcomes, foods, reachable);
    }

    /// <summary>
    /// How many kcal the row is off for its grams: 0 when right; for a wrong food, its energy against the right foods'
    /// median (or the middle of the label's range); null for an AI estimate or when no right energy is known.
    /// </summary>
    private static decimal? KcalError(BenchItem item, int? final, bool right, IReadOnlyList<MatchCandidate> shortlist,
        ConcurrentDictionary<int, (string Name, string? NameBg, decimal Kcal)> foods)
    {
        if (right)
        {
            return 0;
        }

        if (final is not { } id || !foods.TryGetValue(id, out var chosen))
        {
            return null;
        }

        var good = shortlist.Where(c => foods.TryGetValue(c.Id, out var f) && item.Right(f.Name, f.NameBg, f.Kcal)).Select(c => c.Per100.Kcal).OrderBy(k => k).ToList();
        decimal? gold = good.Count > 0 ? good[good.Count / 2] : item.Kcal is [var min, var max] ? (min + max) / 2 : null;
        return gold is { } g ? Math.Round(Math.Abs(chosen.Kcal - g) * item.Grams / 100, 1) : null;
    }

    /// <summary>Per model: accuracy, stability, calibration, latency and cost.</summary>
    public record ModelSummary(string Model, int Calls, double FinalAccuracy, double FinalAt3s, double PickAccuracy, double NoMatchRight, double Stability,
        double MeanKcalError, double P50Ms, double P95Ms, double MaxMs, double Over3s, int Failures, decimal Cost, double MeanTokens,
        IReadOnlyDictionary<string, double> ByCategory, IReadOnlyDictionary<string, double> ByLang, IReadOnlyList<CalibrationBucket> Calibration, SelectionMetrics Selection);

    public record SelectionMetrics(int Items, int Selected, int CorrectSelections, int Absent, int AbsentDeclined, int Required, int RequiredDeclined)
    {
        public double? Precision => Percent(CorrectSelections, Selected);
        public double? Coverage => Percent(Selected, Items);
        public double? AbsentRejection => Percent(AbsentDeclined, Absent);
        private static double? Percent(int n, int total) => total == 0 ? null : Math.Round(100.0 * n / total, 1);
    }

    public record CalibrationBucket(string Bucket, int Count, double Accuracy);

    private static List<ModelSummary> Report(List<BenchCase> cases, List<string> models, List<BenchOutcome> outcomes, Dictionary<(string, int), bool> reachable)
    {
        var items = cases.SelectMany(c => c.Items.Select((it, n) => (Case: c, Item: it, N: n))).ToList();
        var nomatch = items.Where(x => x.Item.Accept is null).Select(x => (x.Case.Id, x.N)).ToHashSet();
        var reach = items.Where(x => x.Item.Accept is not null && reachable.GetValueOrDefault((x.Case.Id, x.N))).Select(x => (x.Case.Id, x.N)).ToHashSet();
        var required = items.Where(x => x.Item.Accept is not null && !x.Item.NoneOk).Select(x => (x.Case.Id, x.N)).ToHashSet();
        var category = items.ToDictionary(x => (x.Case.Id, x.N), x => x.Case.Category);
        var lang = items.ToDictionary(x => (x.Case.Id, x.N), x => x.Case.Lang);
        var result = new List<ModelSummary>();
        foreach (var model in models)
        {
            var mine = outcomes.Where(x => x.Model == model).ToList();
            if (mine.Count == 0) continue;
            double Share(IEnumerable<BenchOutcome> xs, Func<BenchOutcome, bool> f) { var l = xs.ToList(); return l.Count == 0 ? double.NaN : Math.Round(100.0 * l.Count(f) / l.Count, 1); }
            var calls = mine.GroupBy(x => (x.Repeat, x.CaseId)).Select(g => g.First()).ToList();
            var ms = calls.Select(x => (double)x.Ms).OrderBy(x => x).ToList();
            var stable = mine.GroupBy(x => (x.CaseId, x.Item)).Where(g => reach.Contains(g.Key) || nomatch.Contains(g.Key))
                .Select(g => g.Select(x => x.Pick).Distinct().Count() == 1).ToList();
            var kcal = mine.Where(x => x.KcalError is not null).Select(x => (double)x.KcalError!.Value).ToList();
            var buckets = new (string, decimal, decimal)[] { ("<0.3", 0, 0.3m), ("0.3-0.7", 0.3m, 0.7m), ("0.7-0.95", 0.7m, 0.95m), ("≥0.95", 0.95m, 1.01m) };
            var calibration = buckets.Select(b =>
            {
                var xs = mine.Where(x => x.Confidence is { } c && c >= b.Item2 && c < b.Item3 && reach.Contains((x.CaseId, x.Item))).ToList();
                return new CalibrationBucket(b.Item1, xs.Count, xs.Count == 0 ? double.NaN : Math.Round(100.0 * xs.Count(x => x.PickRight) / xs.Count, 1));
            }).ToList();
            result.Add(new ModelSummary(model, calls.Count,
                Share(mine, x => x.FinalRight), Share(mine, x => x.FinalRightAt3s),
                model == "off" ? double.NaN : Share(mine.Where(x => reach.Contains((x.CaseId, x.Item))), x => x.PickRight),
                Share(mine.Where(x => nomatch.Contains((x.CaseId, x.Item))), x => x.FinalRight),
                model == "off" || stable.Count == 0 ? double.NaN : Math.Round(100.0 * stable.Count(s => s) / stable.Count, 1),
                kcal.Count == 0 ? 0 : Math.Round(kcal.Average(), 1),
                Percentile(ms, 0.5), Percentile(ms, 0.95), ms.Count == 0 ? 0 : ms[^1],
                model == "off" ? 0 : Math.Round(100.0 * calls.Count(x => x.Ms > ProductionMs || x.Failed) / Math.Max(1, calls.Count), 1),
                calls.Count(x => x.Failed), calls.Sum(x => x.Cost ?? 0),
                calls.Where(x => x.Tokens is not null).Select(x => (double)x.Tokens!.Value).DefaultIfEmpty(0).Average(),
                mine.GroupBy(x => category[(x.CaseId, x.Item)]).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => Share(g, x => x.FinalRight)),
                mine.GroupBy(x => lang[(x.CaseId, x.Item)]).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => Share(g, x => x.FinalRight)),
                calibration, new SelectionMetrics(mine.Count, mine.Count(x => x.Final.HasValue), mine.Count(x => x.Final.HasValue && x.FinalRight),
                    mine.Count(x => nomatch.Contains((x.CaseId, x.Item))), mine.Count(x => nomatch.Contains((x.CaseId, x.Item)) && x.Final is null),
                    mine.Count(x => required.Contains((x.CaseId, x.Item))), mine.Count(x => required.Contains((x.CaseId, x.Item)) && x.Final is null))));
        }

        return result;
    }

    private static double Percentile(List<double> sorted, double p) => sorted.Count == 0 ? 0 : sorted[(int)Math.Min(sorted.Count - 1, Math.Floor(p * sorted.Count))];

    private static void WriteReport(TextWriter o, List<ModelSummary> report, int repeats, TimeSpan timeout, List<BenchCase> cases, List<BenchOutcome> outcomes,
        ConcurrentDictionary<int, (string Name, string? NameBg, decimal Kcal)> foods, Dictionary<(string, int), bool> reachable)
    {
        var items = cases.Sum(c => c.Items.Count);
        o.WriteLine($"{cases.Count} cases, {items} items ({cases.SelectMany(c => c.Items).Count(i => i.Accept is null)} with no right food in the catalogue, "
            + $"{reachable.Count(r => !r.Value && cases.First(c => c.Id == r.Key.Item1).Items[r.Key.Item2].Accept is not null)} whose right food the decision shortlist misses), {repeats} repeats, {timeout.TotalSeconds:0} s timeout.");
        o.WriteLine();
        o.WriteLine($"{"model",-38} {"final",6} {"@3s",6} {"pick",6} {"none",6} {"stable",6} {"kcal",6} {"p50ms",6} {"p95ms",6} {">3s",5} {"fail",4} {"$",8}");
        foreach (var s in report)
        {
            o.WriteLine($"{s.Model,-38} {P(s.FinalAccuracy),6} {P(s.FinalAt3s),6} {P(s.PickAccuracy),6} {P(s.NoMatchRight),6} {P(s.Stability),6} {s.MeanKcalError,6:0.0} {s.P50Ms,6:0} {s.P95Ms,6:0} {s.Over3s,5:0.#} {s.Failures,4} {s.Cost,8:0.0000}");
        }

        o.WriteLine();
        o.WriteLine("final: item right after Plate's confidence floors; @3s: the same with answers slower than 3 s left to the ranking, as in production;");
        o.WriteLine("pick: the raw answer on items whose right food is shortlisted; none: right on items with no right food in the catalogue;");
        o.WriteLine("stable: same raw answer in every repeat; kcal: mean kcal off per item for its grams (wrong database picks only count); $: total cost.");
        o.WriteLine();
        foreach (var s in report)
        {
            var m = s.Selection;
            o.WriteLine($"{s.Model}: Selected correctly {m.CorrectSelections}/{m.Selected} ({P(m.Precision ?? double.NaN)}%); selected {m.Selected}/{m.Items} ({P(m.Coverage ?? double.NaN)}%); absent declined {m.AbsentDeclined}/{m.Absent} ({P(m.AbsentRejection ?? double.NaN)}%); Required matches declined {m.RequiredDeclined}/{m.Required}.");
        }
        o.WriteLine();
        o.WriteLine("How rows were chosen (path: items, right):");
        foreach (var s in report)
        {
            var paths = outcomes.Where(x => x.Model == s.Model && x.Outcome is not null).GroupBy(x => x.Outcome!).OrderByDescending(g => g.Count())
                .Select(g => $"{g.Key}: {g.Count()}, {g.Count(x => x.FinalRight)} right");
            o.WriteLine($"  {s.Model}: {string.Join("; ", paths)}");
        }

        o.WriteLine();
        o.WriteLine("Deterministic ranking by item (before any cooked-or-dry substitution):");
        foreach (var x in outcomes.Where(x => x.Model == "off").OrderBy(x => x.CaseId).ThenBy(x => x.Item))
        {
            o.WriteLine($"  {x.CaseId}/{x.Item + 1}: first acceptable rank {x.FirstAcceptableRank?.ToString() ?? "-"} of {x.PoolSize}; {x.RankingReason}; {x.Outcome}; {(x.FinalRight ? "right" : "wrong")}");
        }
        o.WriteLine();
        var categories = report.SelectMany(s => s.ByCategory.Keys).Distinct().OrderBy(x => x).ToList();
        o.WriteLine($"{"final by category",-38} " + string.Join(" ", categories.Select(c => $"{c,9}")) + "     bg     en");
        foreach (var s in report)
        {
            o.WriteLine($"{s.Model,-38} " + string.Join(" ", categories.Select(c => $"{P(s.ByCategory.GetValueOrDefault(c, double.NaN)),9}")) + $" {P(s.ByLang.GetValueOrDefault("bg", double.NaN)),6} {P(s.ByLang.GetValueOrDefault("en", double.NaN)),6}");
        }

        o.WriteLine();
        o.WriteLine($"{"pick accuracy by confidence",-38} " + string.Join(" ", report.First(s => s.Model != "off" || report.Count == 1).Calibration.Select(b => $"{b.Bucket,14}")));
        foreach (var s in report.Where(s => s.Model != "off"))
        {
            o.WriteLine($"{s.Model,-38} " + string.Join(" ", s.Calibration.Select(b => $"{(b.Count == 0 ? "-" : $"{P(b.Accuracy)} of {b.Count}"),14}")));
        }

        o.WriteLine();
        o.WriteLine("Items some model got wrong in the last repeat (model: chosen food):");
        var last = outcomes.GroupBy(x => x.Model).ToDictionary(g => g.Key, g => g.Where(x => x.Repeat == g.Max(y => y.Repeat)).ToDictionary(x => (x.CaseId, x.Item)));
        foreach (var c in cases)
        {
            for (var n = 0; n < c.Items.Count; n++)
            {
                var wrong = last.Where(m => m.Value.TryGetValue((c.Id, n), out var x) && !x.FinalRight).Select(m => (m.Key, m.Value[(c.Id, n)])).ToList();
                if (wrong.Count == 0) continue;
                o.WriteLine($"  {c.Id} [{c.Category}] \"{c.Items[n].NameOriginal}\" in \"{(c.Text.Length > 50 ? c.Text[..50] + "…" : c.Text)}\"");
                foreach (var (model, x) in wrong)
                {
                    var name = x.Final is { } id && foods.TryGetValue(id, out var f) ? $"{(f.NameBg ?? f.Name)} #{id} ({f.Kcal:0} kcal)" : "AI estimate";
                    o.WriteLine($"      {model}: {name}" + (x.Pick is { } p && p != x.Final ? $" (raw pick {(p == 0 ? "none" : "#" + p)} at {x.Confidence:0.00})" : ""));
                }
            }
        }
    }

    private static string P(double v) => double.IsNaN(v) ? "-" : v.ToString("0.0", CultureInfo.InvariantCulture);

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private sealed class Asker(JevFoodMatchDecider jev, string key, string model)
    {
        public Task<MatchDecisions?> AskAsync(string text, IReadOnlyList<MatchQuestion> questions, CancellationToken ct) => jev.DecideAsync(text, questions, key, model, ct);
    }

    /// <summary>Keeps the questions the matcher asked and the answer, with its time; without an asker it answers nothing (the ranking decides).</summary>
    private sealed class Recorder(Asker? asker) : IFoodMatchDecider
    {
        public IReadOnlyList<MatchQuestion> Questions { get; private set; } = [];
        public MatchDecisions? Decisions { get; private set; }
        public long Ms { get; private set; }
        public bool Failed { get; private set; }

        public async Task<MatchDecisions?> DecideAsync(string text, IReadOnlyList<MatchQuestion> questions, CancellationToken ct)
        {
            Questions = questions;
            if (asker is null) return null;
            var clock = Stopwatch.StartNew();
            Decisions = await asker.AskAsync(text, questions, ct);
            Ms = clock.ElapsedMilliseconds;
            Failed = Decisions is null;
            return Decisions;
        }
    }
}
