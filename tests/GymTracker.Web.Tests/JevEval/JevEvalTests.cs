using System.Diagnostics;
using FluentAssertions;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using GymTracker.Core.Domain;
using GymTracker.Core.Nutrition;
using GymTracker.Web.Data;
using GymTracker.Web.Features.Food;
using GymTracker.Web.Integrations;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace GymTracker.Web.Tests.JevEval;

/// <summary>Runs only when GYM_JEV_EVAL has the given value, because it calls OpenRouter with a real key.</summary>
public sealed class JevEvalFactAttribute : FactAttribute
{
    public JevEvalFactAttribute(string mode)
    {
        if (Environment.GetEnvironmentVariable("GYM_JEV_EVAL") != mode)
        {
            Skip = $"Calls OpenRouter; runs only with GYM_JEV_EVAL={mode} (see JevEval/README.md).";
        }
    }
}

public record EvalItem(
    string NameEn,
    string NameOriginal,
    decimal Grams,
    FoodState State,
    string? Preparation,
    string? Assumption,
    Per100g Estimated,
    List<string>? Expected,
    string? Note)
{
    public ParsedFoodItem Parsed => new(NameEn, NameOriginal, Grams, State, Preparation, Assumption, Estimated);
}

public record EvalEntry(string Id, string Text, string? ParsedBy, List<EvalItem>? Items)
{
    public bool Bulgarian => Text.Any(c => c is >= 'а' and <= 'я' or >= 'А' and <= 'Я');
}

public record EvalSet(List<EvalEntry> Entries);

/// <summary>
/// Phase 0 of docs/superpowers/plans/2026-10-04-jev-food-matching.md: compares Rank's top pick with Jev choosing
/// from Rank's top 5 and from the plain trigram top 50, on a labelled set of real parser output.
/// </summary>
public class JevEvalTests(ITestOutputHelper output)
{
    private const string JevModel = "typesafe/jev-1.13";
    private const int RankShortlist = 5;
    private const int TrigramShortlist = 50;
    private const double RankThreshold = 0.5; // FoodMatcher.MatchThreshold
    private const double TrigramThreshold = 0.3;

    private const string Instructions =
        "Which database entry is the food described in foods.{0}? The diary_entry is what the user typed. " +
        "Pick the entry that is the same food in the same form: raw or cooked as the food's state says, and the " +
        "preparation if one is given. Pick none if no entry is this food, for example when the entries are only " +
        "a different food, a different form of it, or a dish that merely contains it.";

    private const string NoneOption = "None of these entries is this food.";

    /// <summary>Gated rule (Phase 0b): a "none" counts only at this confidence; below it Rank's top pick is used.</summary>
    private const double NoneGate = 0.9;

    private static readonly string Dir = Path.Combine(RepoPaths.Root, "tests", "GymTracker.Web.Tests", "JevEval");

    /// <summary>jev-eval-set (round 1) or jev-eval-set-2 (the held-out round); GYM_JEV_EVAL_SET picks one.</summary>
    private static readonly string SetName = Env("GYM_JEV_EVAL_SET", "jev-eval-set");
    private static readonly string SetPath = Path.Combine(Dir, SetName + ".json");
    private static readonly string OutDir = Path.Combine(Dir, "out", SetName);
    private static readonly string ResultsPath = Path.Combine(RepoPaths.Root, "docs", "superpowers", "plans",
        $"2026-10-04-jev-food-matching-results{SetName["jev-eval-set".Length..]}.md");

    private static string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    [JevEvalFact("prepare")]
    public async Task Prepare()
    {
        var set = Load();
        var reparse = Environment.GetEnvironmentVariable("GYM_JEV_EVAL_REPARSE") == "1";
        var model = Environment.GetEnvironmentVariable("OPENROUTER_MODEL") is { Length: > 0 } m ? m : OpenRouterOptions.DefaultModel;
        var api = OpenRouterHttp("v1/");
        var parser = new OpenRouterFoodParser(api, new OpenRouterModels(api, new MemoryCache(new MemoryCacheOptions()), NullLogger<OpenRouterModels>.Instance),
            NullLogger<OpenRouterFoodParser>.Instance);

        for (var i = 0; i < set.Entries.Count; i++)
        {
            var entry = set.Entries[i];
            if (entry.Items is not null && !reparse)
            {
                continue;
            }

            var result = await parser.ParseAsync(entry.Text, Key(), model, default);
            result.Items.Should().NotBeEmpty($"the parser should read \"{entry.Text}\" ({result.Error})");
            set.Entries[i] = entry with
            {
                ParsedBy = result.Model,
                Items = result.Items.Select(p => new EvalItem(p.NameEn, p.NameOriginal, p.Grams, p.State, p.Preparation, p.Assumption, p.Estimated, null, null)).ToList(),
            };
            Save(set);
        }

        await using var db = Db();
        var foods = Foods(db);
        var report = new StringBuilder("# Candidates for labelling\n\n");
        foreach (var entry in set.Entries)
        {
            report.Append($"## {entry.Id}: {entry.Text}\n\n");
            foreach (var (item, n) in entry.Items!.Select((x, n) => (x, n)))
            {
                var rank = await RankShortlistAsync(foods, item.Parsed, 10);
                var trigram = await TrigramShortlistAsync(foods, item.Parsed);
                var keys = await KeysAsync(db, rank.Concat(trigram).Select(c => c.Id));
                report.Append($"### {entry.Id}.{n}: {item.NameEn} / {item.NameOriginal}, {item.State}, {item.Preparation ?? "-"}, {item.Grams} g\n\n");
                report.Append($"Label: {(item.Expected is null ? "(none yet)" : string.Join(" OR ", item.Expected))}\n\nRank top 10:\n\n");
                report.AppendJoin("", rank.Select((c, k) => $"{k + 1}. `{keys[c.Id]}` {c.Per100.Kcal:0} kcal\n"));
                report.Append("\nTrigram top 50:\n\n");
                report.AppendJoin("", trigram.Select((c, k) => $"{k + 1}. `{keys[c.Id]}` {c.Score:0.00}, {c.Per100.Kcal:0} kcal\n"));
                report.Append('\n');
            }
        }

        Directory.CreateDirectory(OutDir);
        await File.WriteAllTextAsync(Path.Combine(OutDir, "candidates.md"), report.ToString());
        output.WriteLine($"Parsed {set.Entries.Sum(e => e.Items!.Count)} items; candidates in {Path.Combine(OutDir, "candidates.md")}");
    }

    [JevEvalFact("score")]
    public async Task Score()
    {
        var set = Load();
        set.Entries.Should().OnlyContain(e => e.Items != null && e.Items.All(i => i.Expected != null), "every item needs a label before scoring");

        await using var db = Db();
        var foods = Foods(db);
        var matcher = new FoodMatcher(foods);
        var jev = OpenRouterHttp("alpha/");
        var approaches = Env("GYM_JEV_EVAL_APPROACHES", "b,c").Split(',');
        var runs = int.Parse(Env("GYM_JEV_EVAL_RUNS", "1"), CultureInfo.InvariantCulture);
        Directory.CreateDirectory(OutDir);

        var rows = new List<Row>();
        var calls = new List<Call>();
        foreach (var entry in set.Entries)
        {
            var items = entry.Items!;
            var rankLists = new List<List<FoodMatch>>();
            var trigramLists = new List<List<FoodMatch>>();
            foreach (var item in items)
            {
                rankLists.Add(await RankShortlistAsync(foods, item.Parsed, RankShortlist));
                trigramLists.Add(await TrigramShortlistAsync(foods, item.Parsed));
            }

            var picks = new Dictionary<string, List<Pick?>>();
            foreach (var approach in approaches)
            {
                for (var run = 1; run <= runs; run++)
                {
                    foreach (var reversed in new[] { false, true })
                    {
                        var key = Key(approach, reversed, run);
                        var (answers, call) = await AskJevAsync(jev, entry, approach == "b" ? rankLists : trigramLists, reversed, key);
                        picks[key] = answers;
                        calls.Add(call);
                    }
                }
            }

            for (var n = 0; n < items.Count; n++)
            {
                var item = items[n];
                var expected = await ResolveLabelAsync(db, item.Expected!);
                var today = (await matcher.ResolveAsync([item.Parsed], default)).Single().FoodItemId;
                rows.Add(new Row(
                    $"{entry.Id}.{n}", entry.Bulgarian, item, expected, today,
                    rankLists[n].FirstOrDefault()?.Id,
                    rankLists[n].Select(c => c.Id).ToList(),
                    trigramLists[n].Select(c => c.Id).ToList(),
                    picks.ToDictionary(p => p.Key, p => p.Value[n])));
            }
        }

        var names = await KeysAsync(db, rows.SelectMany(r => r.Expected.Concat(r.Picks.Values.Where(p => p?.Id is not null).Select(p => p!.Id!.Value)).Append(r.Today ?? 0)));
        var report = Report(rows, calls, names, approaches, runs);
        await File.WriteAllTextAsync(ResultsPath, report);
        await File.WriteAllTextAsync(Path.Combine(OutDir, "rows.json"), JsonSerializer.Serialize(rows, Json));
        output.WriteLine(report);
    }

    // ---- Candidate lists ------------------------------------------------------------------------------------

    /// <summary>The candidates FoodMatcher.ResolveAsync ranks, best first, deduplicated across the two searches.</summary>
    private static async Task<List<FoodMatch>> RankShortlistAsync(FoodItemService foods, ParsedFoodItem item, int take)
    {
        var ranked = (await foods.SearchAsync(item.NameEn, 120, RankThreshold, default))
            .Select(c => (Food: c, Score: FoodMatcher.Rank(c, item.NameEn, item.State)))
            .ToList();
        if (!string.Equals(item.NameOriginal, item.NameEn, StringComparison.OrdinalIgnoreCase))
        {
            ranked.AddRange((await foods.SearchAsync(item.NameOriginal, 120, RankThreshold, default))
                .Where(c => !string.IsNullOrEmpty(c.NameBg))
                .Select(c => (Food: c, Score: FoodMatcher.Rank(c, item.NameOriginal, item.State, bulgarian: true))));
        }

        return ranked
            .GroupBy(x => x.Food.Id)
            .Select(g => g.MaxBy(x => x.Score))
            .OrderByDescending(x => x.Score)
            .Take(take)
            .Select(x => x.Food)
            .ToList();
    }

    /// <summary>Plain trigram similarity on the English and the original name, with no ranking adjustments.</summary>
    private static async Task<List<FoodMatch>> TrigramShortlistAsync(FoodItemService foods, ParsedFoodItem item)
    {
        var found = await foods.SearchAsync(item.NameEn, TrigramShortlist, TrigramThreshold, default);
        if (!string.Equals(item.NameOriginal, item.NameEn, StringComparison.OrdinalIgnoreCase))
        {
            found.AddRange(await foods.SearchAsync(item.NameOriginal, TrigramShortlist, TrigramThreshold, default));
        }

        return found
            .GroupBy(c => c.Id)
            .Select(g => g.MaxBy(c => c.Score)!)
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Name.Length)
            .Take(TrigramShortlist)
            .ToList();
    }

    // ---- Jev ------------------------------------------------------------------------------------------------

    public record Pick(int? Id, double Confidence, double NoneProbability);

    public record Call(string Entry, string Approach, bool Bulgarian, int Questions, int Options, long Ms, int InputTokens, decimal Cost, string? Model, string? Error);

    private static async Task<(List<Pick?> Answers, Call Call)> AskJevAsync(HttpClient http, EvalEntry entry, List<List<FoodMatch>> lists, bool reversed, string approach)
    {
        var items = entry.Items!;
        var foodsState = new JsonObject();
        var questions = new JsonObject();
        for (var n = 0; n < items.Count; n++)
        {
            var item = items[n];
            foodsState[$"i{n}"] = new JsonObject
            {
                ["as_written"] = item.NameOriginal,
                ["english"] = item.NameEn,
                ["state"] = item.State switch { FoodState.Raw => "raw", FoodState.Cooked => "cooked", _ => "not applicable" },
                ["preparation"] = item.Preparation,
                ["grams"] = item.Grams,
            };
            if (lists[n].Count == 0)
            {
                continue;
            }

            var criteria = new JsonObject();
            foreach (var c in reversed ? Enumerable.Reverse(lists[n]) : lists[n])
            {
                criteria[$"f{c.Id}"] = Describe(c);
            }

            criteria["none"] = NoneOption;
            questions[$"i{n}"] = new JsonObject
            {
                ["type"] = "choice",
                ["instructions"] = string.Format(CultureInfo.InvariantCulture, Instructions, $"i{n}"),
                ["criteria"] = criteria,
            };
        }

        var answers = items.Select(_ => (Pick?)null).ToList();
        var options = lists.Sum(l => l.Count == 0 ? 0 : l.Count + 1);
        if (questions.Count == 0)
        {
            return (answers, new Call(entry.Id, approach, entry.Bulgarian, 0, 0, 0, 0, 0, null, null));
        }

        var body = new JsonObject
        {
            ["model"] = JevModel,
            ["state"] = new JsonObject { ["diary_entry"] = entry.Text, ["foods"] = foodsState },
            ["questions"] = questions,
        };

        var clock = Stopwatch.StartNew();
        using var request = new HttpRequestMessage(HttpMethod.Post, "decisions") { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Key());
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        clock.Stop();
        await File.WriteAllTextAsync(Path.Combine(OutDir, $"{entry.Id}-{approach.Replace('#', '-')}.json"),
            $"{{\"request\":{body.ToJsonString(Json)},\"status\":{(int)response.StatusCode},\"response\":{(text.TrimStart().StartsWith('{') ? text : JsonSerializer.Serialize(text))}}}");

        if (!response.IsSuccessStatusCode)
        {
            return (answers, new Call(entry.Id, approach, entry.Bulgarian, questions.Count, options, clock.ElapsedMilliseconds, 0, 0, null, $"HTTP {(int)response.StatusCode}"));
        }

        var json = JsonNode.Parse(text)!;
        for (var n = 0; n < items.Count; n++)
        {
            if (json["answers"]?[$"i{n}"] is not { } a)
            {
                continue;
            }

            var choice = a["choice"]?.GetValue<string>();
            var id = choice is not null && choice.StartsWith('f') && int.TryParse(choice[1..], out var parsed) && lists[n].Any(c => c.Id == parsed) ? parsed : (int?)null;
            answers[n] = new Pick(id, a["confidence"]?.GetValue<double>() ?? 0, a["probabilities"]?["none"]?.GetValue<double>() ?? 0);
        }

        return (answers, new Call(entry.Id, approach, entry.Bulgarian, questions.Count, options, clock.ElapsedMilliseconds,
            json["usage"]?["input_tokens"]?.GetValue<int>() ?? 0, json["usage"]?["cost"]?.GetValue<decimal>() ?? 0, json["model"]?.GetValue<string>(), null));
    }

    private static string Describe(FoodMatch c)
    {
        var bg = string.IsNullOrEmpty(c.NameBg) || c.NameBg == c.Name ? "" : $" / {c.NameBg}";
        var brand = string.IsNullOrEmpty(c.Brand) ? "" : $" ({c.Brand})";
        return string.Create(CultureInfo.InvariantCulture, $"{c.Name}{bg}{brand}: {c.KcalPer100g:0} kcal, {c.ProteinPer100g:0.#} g protein per 100 g");
    }

    // ---- Scoring --------------------------------------------------------------------------------------------

    public record Row(string Id, bool Bulgarian, EvalItem Item, List<int> Expected, int? Today, int? RankTop, List<int> RankIds, List<int> TrigramIds, Dictionary<string, Pick?> Picks)
    {
        public bool IsNone => Expected.Count == 0;

        public bool Correct(int? id) => IsNone ? id is null : id is { } x && Expected.Contains(x);
    }

    private static string Key(string approach, bool reversed, int run) => $"{approach}{(reversed ? "-reversed" : "")}#{run}";

    /// <summary>Jev's answer as given; an undecided item falls back to Rank's top pick.</summary>
    private static int? Plain(Row r, string key) => r.Picks[key] is { } p ? p.Id : r.Today;

    /// <summary>Jev's food pick, or its "none" at NoneGate confidence or more; anything else is Rank's top pick.</summary>
    private static int? Gated(Row r, string key) => r.Picks[key] is { } p ? p.Id ?? (p.Confidence >= NoneGate ? null : r.Today) : r.Today;

    private static string Raw(Row r, string key) => r.Picks[key] is { } p ? p.Id?.ToString(CultureInfo.InvariantCulture) ?? "none" : "undecided";

    private record Rule(string Label, Func<Row, string, int?> Decide);

    private static readonly Rule[] Rules = [new("plain", Plain), new("gated", Gated)];

    private static string Report(List<Row> rows, List<Call> calls, Dictionary<int, string> names, string[] approaches, int runs)
    {
        var bg = rows.Where(r => r.Bulgarian).ToList();
        var en = rows.Where(r => !r.Bulgarian).ToList();
        var nones = rows.Where(r => r.IsNone).ToList();
        var matchable = rows.Where(r => !r.IsNone).ToList();
        List<string> Keys(string a, bool reversed) => Enumerable.Range(1, runs).Select(n => Key(a, reversed, n)).ToList();

        var s = new StringBuilder("# Jev food matching: results\n\n");
        s.Append(CultureInfo.InvariantCulture, $"Set `{SetName}`, run {DateTime.Now:yyyy-MM-dd HH:mm}. {rows.Count} items from {rows.Select(r => r.Id.Split('.')[0]).Distinct().Count()} entries; ")
            .Append(CultureInfo.InvariantCulture, $"{bg.Count} Bulgarian, {en.Count} English; {nones.Count} labelled none. ")
            .Append(CultureInfo.InvariantCulture, $"Jev answered as {string.Join(", ", calls.Select(c => c.Model).Where(m => m is not null).Distinct())}; ")
            .Append(CultureInfo.InvariantCulture, $"{runs} run(s) per order. \"Gated\": a none counts only at confidence {NoneGate:0.0} or more, otherwise Rank's top pick. ")
            .Append("With several runs a cell shows the mean and, in brackets, the lowest and highest run.\n\n");

        // Correct items per run for one rule, as "n/m (p%)" for one run or "mean/m (min-max)" for several.
        string Cell(List<Row> subset, IEnumerable<Func<Row, int?>> decides, Func<Row, int?, bool> ok)
        {
            var counts = decides.Select(d => subset.Count(r => ok(r, d(r)))).ToList();
            if (subset.Count == 0)
            {
                return "-";
            }

            return counts.Count == 1
                ? string.Create(CultureInfo.InvariantCulture, $"{counts[0]}/{subset.Count} ({100.0 * counts[0] / subset.Count:0}%)")
                : string.Create(CultureInfo.InvariantCulture, $"{counts.Average():0.#}/{subset.Count} ({counts.Min()}–{counts.Max()})");
        }

        var lines = new List<(string Label, List<Func<Row, int?>> Decides)> { ("a) Rank top-1 (today)", [r => r.Today]) };
        foreach (var a in approaches)
        {
            foreach (var rule in Rules)
            {
                foreach (var reversed in new[] { false, true })
                {
                    lines.Add(($"{a} {rule.Label}, {(reversed ? "reversed" : "listed")}", Keys(a, reversed).Select(k => (Func<Row, int?>)(r => rule.Decide(r, k))).ToList()));
                }
            }
        }

        s.Append("## Accuracy\n\n| Approach | All | Bulgarian | English | None caught | Matchable wrongly rejected |\n|---|---|---|---|---|---|\n");
        foreach (var (label, decides) in lines)
        {
            s.Append(CultureInfo.InvariantCulture, $"| {label} | {Cell(rows, decides, (r, id) => r.Correct(id))} | {Cell(bg, decides, (r, id) => r.Correct(id))} | ")
                .Append(CultureInfo.InvariantCulture, $"{Cell(en, decides, (r, id) => r.Correct(id))} | {Cell(nones, decides, (_, id) => id is null)} | {Cell(matchable, decides, (_, id) => id is null)} |\n");
        }

        // The go / no-go bars from the plan, per listed-order run.
        var rankBg = bg.Count(r => r.Correct(r.Today));
        var rankEn = en.Count(r => r.Correct(r.Today));
        s.Append("\n## Go / no-go bars (listed order, per run)\n\n");
        foreach (var a in approaches)
        {
            foreach (var rule in Rules)
            {
                var all = Keys(a, false).Concat(Keys(a, true)).ToList();
                var stable = rows.Count(r => all.Select(k => rule.Decide(r, k)).Distinct().Count() == 1);
                var maxMs = calls.Where(c => c.Approach.StartsWith(a + "#") || c.Approach.StartsWith(a + "-reversed#")).Select(c => c.Ms).DefaultIfEmpty(0).Max();
                s.Append(CultureInfo.InvariantCulture, $"**{a} {rule.Label}.** Same final decision in all {all.Count} runs: {stable}/{rows.Count} ({100.0 * stable / rows.Count:0}%) {Mark(stable >= 0.9 * rows.Count)}; ")
                    .Append(CultureInfo.InvariantCulture, $"max latency {maxMs} ms {Mark(maxMs < 2000)}.\n\n| Bar | {string.Join(" | ", Enumerable.Range(1, runs).Select(n => $"run {n}"))} |\n|---|{string.Concat(Enumerable.Repeat("---|", runs))}\n");
                var bars = new (string Label, Func<string, string> Value)[]
                {
                    ("Bulgarian: at least +3 items and +10 points over Rank (" + rankBg + "/" + bg.Count + ")", k =>
                    {
                        var n = bg.Count(r => r.Correct(rule.Decide(r, k)));
                        return string.Create(CultureInfo.InvariantCulture, $"{n - rankBg:+0;-0;0} items, {100.0 * (n - rankBg) / Math.Max(1, bg.Count):+0;-0;0} pts {Mark(n - rankBg >= 3 && 100.0 * (n - rankBg) / bg.Count >= 10)}");
                    }),
                    ("English: at most 1 item below Rank (" + rankEn + "/" + en.Count + ")", k =>
                    {
                        var n = en.Count(r => r.Correct(rule.Decide(r, k)));
                        return string.Create(CultureInfo.InvariantCulture, $"{n - rankEn:+0;-0;0} {Mark(n >= rankEn - 1)}");
                    }),
                    ("None caught: at least half", k =>
                    {
                        var n = nones.Count(r => rule.Decide(r, k) is null);
                        return $"{n}/{nones.Count} {Mark(2 * n >= nones.Count)}";
                    }),
                    ("Matchable rejected: at most 1 in 20", k =>
                    {
                        var n = matchable.Count(r => rule.Decide(r, k) is null);
                        return $"{n}/{matchable.Count} {Mark(20 * n <= matchable.Count)}";
                    }),
                };
                foreach (var (label, value) in bars)
                {
                    s.Append($"| {label} | {string.Join(" | ", Keys(a, false).Select(value))} |\n");
                }

                s.Append('\n');
            }
        }

        s.Append("## Shortlist recall (matchable items with a correct food in the list)\n\n| List | All | Bulgarian | English |\n|---|---|---|---|\n");
        foreach (var (label, ids) in new (string, Func<Row, IEnumerable<int>>)[]
                 {
                     ("Rank top 1", r => r.RankIds.Take(1)), ("Rank top 5", r => r.RankIds),
                     ("Trigram top 30", r => r.TrigramIds.Take(30)), ("Trigram top 40", r => r.TrigramIds.Take(40)), ("Trigram top 50", r => r.TrigramIds),
                 })
        {
            Func<Row, int?> hit = r => ids(r).Any(r.Expected.Contains) ? 1 : null;
            s.Append(CultureInfo.InvariantCulture, $"| {label} | {Cell(matchable, [hit], (_, x) => x is not null)} | ")
                .Append(CultureInfo.InvariantCulture, $"{Cell(matchable.Where(r => r.Bulgarian).ToList(), [hit], (_, x) => x is not null)} | {Cell(matchable.Where(r => !r.Bulgarian).ToList(), [hit], (_, x) => x is not null)} |\n");
        }

        s.Append("\n## Agreement (items whose raw Jev answer is the same in every run of the group)\n\n");
        foreach (var a in approaches)
        {
            string Same(List<string> keys) => runs * 2 == 0 ? "-" : Pct(rows, r => keys.Select(k => Raw(r, k)).Distinct().Count() == 1);
            s.Append($"- {a}: listed runs {Same(Keys(a, false))}, reversed runs {Same(Keys(a, true))}, all runs {Same(Keys(a, false).Concat(Keys(a, true)).ToList())}\n");
        }

        s.Append("\n## Latency and cost per parse (one call per entry)\n\n| Calls | Median ms | Max ms | Mean input tokens | Mean cost | Max cost | Errors |\n|---|---|---|---|---|---|---|\n");
        foreach (var group in calls.Where(c => c.Questions > 0).GroupBy(c => c.Approach.Split('#')[0]))
        {
            var ok = group.Where(c => c.Error is null).ToList();
            var ms = ok.Select(c => c.Ms).Order().ToList();
            s.Append(CultureInfo.InvariantCulture, $"| {group.Key} | {(ms.Count == 0 ? 0 : ms[ms.Count / 2])} | {(ms.Count == 0 ? 0 : ms[^1])} | {(ok.Count == 0 ? 0 : ok.Average(c => c.InputTokens)):0} | ")
                .Append(CultureInfo.InvariantCulture, $"${(ok.Count == 0 ? 0 : ok.Average(c => c.Cost)):0.000000} | ${(ok.Count == 0 ? 0 : ok.Max(c => c.Cost)):0.000000} | {group.Count(c => c.Error is not null)} |\n");
        }

        s.Append($"\n## Accuracy by Jev confidence (raw answer, all listed runs; correct / answers)\n\n| Confidence | {string.Join(" | ", approaches)} |\n|---|{string.Concat(approaches.Select(_ => "---|"))}\n");
        foreach (var (lo, hi) in new[] { (0.0, 0.5), (0.5, 0.7), (0.7, 0.85), (0.85, 0.9), (0.9, 0.95), (0.95, 1.01) })
        {
            string Bucket(string a)
            {
                var answers = Keys(a, false).SelectMany(k => rows.Where(r => r.Picks[k] is { } p && p.Confidence >= lo && p.Confidence < hi).Select(r => (r, r.Picks[k]!))).ToList();
                return $"{answers.Count(x => x.r.Correct(x.Item2.Id))} / {answers.Count}";
            }

            s.Append(CultureInfo.InvariantCulture, $"| {lo:0.00} to {Math.Min(hi, 1):0.00} | {string.Join(" | ", approaches.Select(Bucket))} |\n");
        }

        s.Append("\n## Per item\n\n✓ correct, ✗ wrong; the name is what was picked (— means no match). Jev columns are run 1, listed order, with its confidence; \"runs\" is how many of all runs gave the most common raw answer.\n\n")
            .Append($"| Item | Text | Label | a | {string.Join(" | ", approaches.Select(a => $"{a} plain | {a} gated | {a} runs"))} |\n|---|---|---|---|{string.Concat(approaches.Select(_ => "---|---|---|"))}\n");
        foreach (var r in rows)
        {
            string Cell1(int? id) => $"{(r.Correct(id) ? "✓" : "✗")} {(id is { } x ? names.GetValueOrDefault(x, x.ToString(CultureInfo.InvariantCulture)) : "—")}";
            string Conf(string k) => r.Picks[k] is { } p ? string.Create(CultureInfo.InvariantCulture, $" ({p.Confidence:0.00})") : " (undecided)";
            string Runs(string a)
            {
                var all = Keys(a, false).Concat(Keys(a, true)).Select(k => Raw(r, k)).ToList();
                return $"{all.GroupBy(x => x).Max(g => g.Count())}/{all.Count}";
            }

            var label = r.IsNone ? "none" : names.GetValueOrDefault(r.Expected[0]) + (r.Expected.Count > 1 ? $" (+{r.Expected.Count - 1})" : "");
            s.Append(CultureInfo.InvariantCulture, $"| {r.Id} | {r.Item.NameOriginal} | {label} | {Cell1(r.Today)} | ")
                .Append(string.Join(" | ", approaches.Select(a => $"{Cell1(Plain(r, Key(a, false, 1)))}{Conf(Key(a, false, 1))} | {Cell1(Gated(r, Key(a, false, 1)))} | {Runs(a)}")))
                .Append(" |\n");
        }

        return s.ToString();
    }

    private static string Mark(bool ok) => ok ? "✓" : "✗";

    private static string Pct<T>(IEnumerable<T> items, Func<T, bool> ok)
    {
        var list = items.ToList();
        return list.Count == 0 ? "-" : string.Create(CultureInfo.InvariantCulture, $"{list.Count(ok)}/{list.Count} ({100.0 * list.Count(ok) / list.Count:0}%)");
    }

    // ---- Labels and plumbing --------------------------------------------------------------------------------

    /// <summary>
    /// Labels name foods in a way that survives a reseed: usda:&lt;Name&gt;, bg:&lt;NameBg&gt; (Bulgarian reference
    /// foods), off:&lt;Barcode&gt;. ["none"] means no food in the database is correct; resolves to no ids.
    /// </summary>
    private static async Task<List<int>> ResolveLabelAsync(AppDbContext db, List<string> labels)
    {
        if (labels is ["none"])
        {
            return [];
        }

        var ids = new List<int>();
        foreach (var label in labels)
        {
            var (kind, value) = (label[..label.IndexOf(':')], label[(label.IndexOf(':') + 1)..]);
            var shared = db.FoodItems.Where(f => f.OwnerUserId == null);
            var found = kind switch
            {
                "usda" => await shared.Where(f => f.Source == FoodSource.Usda && f.Name == value).Select(f => f.Id).ToListAsync(),
                "bg" => await shared.Where(f => f.Source == FoodSource.Reference && f.NameBg == value).Select(f => f.Id).ToListAsync(),
                "off" => await shared.Where(f => f.Barcode == value).Select(f => f.Id).ToListAsync(),
                _ => throw new InvalidOperationException($"Unknown label kind in {label}"),
            };
            found.Should().NotBeEmpty($"label {label} must name a food in the database");
            ids.AddRange(found);
        }

        return ids;
    }

    private static async Task<Dictionary<int, string>> KeysAsync(AppDbContext db, IEnumerable<int> ids)
    {
        var wanted = ids.Distinct().ToList();
        var foods = await db.FoodItems.Where(f => wanted.Contains(f.Id)).Select(f => new { f.Id, f.Source, f.Name, f.NameBg, f.Barcode, f.Brand }).ToListAsync();
        return foods.ToDictionary(f => f.Id, f => f.Source switch
        {
            FoodSource.Usda => $"usda:{f.Name}",
            FoodSource.Reference => $"bg:{f.NameBg}",
            _ when f.Barcode is not null => $"off:{f.Barcode} {f.Name}{(f.Brand is null ? "" : $" ({f.Brand})")}",
            _ => $"{f.Source}:{f.Name}",
        });
    }

    private static readonly IServiceProvider IdentityServices = new ServiceCollection()
        .Configure<IdentityOptions>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3)
        .BuildServiceProvider();

    /// <summary>The dev database (USDA, Bulgarian foods and products seeded), as a user with no foods of their own.</summary>
    private static AppDbContext Db()
    {
        var connection = Environment.GetEnvironmentVariable("GYM_EVAL_DB") is { Length: > 0 } c
            ? c
            : "Host=localhost;Port=5432;Database=gym;Username=gym;Password=gym-dev-only";
        return new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).UseApplicationServiceProvider(IdentityServices).Options,
            new FixedCurrentUser(EvalUser));
    }

    private static readonly string EvalUser = Guid.NewGuid().ToString();

    private static FoodItemService Foods(AppDbContext db) => new(db, new FixedCurrentUser(EvalUser), new FakeFoodDatabase());

    private static HttpClient OpenRouterHttp(string path)
    {
        var http = new HttpClient { BaseAddress = new Uri("https://openrouter.ai/api/" + path), Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.Add("HTTP-Referer", "https://gym.peychev.uk");
        http.DefaultRequestHeaders.Add("X-Title", "GymTracker");
        return http;
    }

    /// <summary>OPENROUTER_API_KEY, else ~/.config/gymtracker/openrouter-key. Never printed.</summary>
    private static string Key()
    {
        if (Environment.GetEnvironmentVariable("OPENROUTER_API_KEY") is { Length: > 0 } key)
        {
            return key.Trim();
        }

        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "gymtracker", "openrouter-key");
        return File.Exists(path) ? File.ReadAllText(path).Trim() : throw new InvalidOperationException("No OpenRouter key: set OPENROUTER_API_KEY or ~/.config/gymtracker/openrouter-key.");
    }

    private static EvalSet Load() => JsonSerializer.Deserialize<EvalSet>(File.ReadAllText(SetPath), Json)!;

    private static void Save(EvalSet set) => File.WriteAllText(SetPath, JsonSerializer.Serialize(set, Json) + "\n");
}
