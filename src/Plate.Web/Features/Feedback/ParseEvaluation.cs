using System.Globalization;
using Plate.Core.Nutrition;
using Plate.Web.Data;
using Plate.Web.Features.Food;
using Plate.Web.Integrations;
using Microsoft.EntityFrameworkCore;

namespace Plate.Web.Features.Feedback;

/// <param name="ParserModel">Null for the model picked in the admin area.</param>
/// <param name="MatcherModel">"off", a decision model id, or null for the one picked in the admin area.</param>
/// <param name="History">Replay with each account's usual foods and last amounts as they were at the parse, as the app would have; off measures the parser and matcher alone.</param>
public record EvalRequest(DateTimeOffset Since, bool FlaggedOnly, int Limit, string? ParserModel, string? MatcherModel, bool History = true);

/// <param name="Known">False for a saved item whose food the case doesn't record.</param>
public record EvalFresh(string Description, decimal Grams, int? FoodItemId, string? MatchedName, bool Known = true);

/// <param name="Saved">What the user saved, item by item, to set beside <paramref name="Fresh"/>.</param>
/// <param name="SavedTotals">The saved meal's energy and macros; null when the case lacks the saved values.</param>
/// <param name="FreshTotals">The same for what the parser and matcher give now.</param>
public record EvalCaseResult(int RunId, string Text, AgreementResult Score, int RepeatedWrongFoods, string? Error, IReadOnlyList<EvalFresh> Fresh,
    IReadOnlyList<EvalFresh>? Saved = null, MealNutrients? SavedTotals = null, MealNutrients? FreshTotals = null);

public record EvalReport(string ParserModel, string MatcherModel, AgreementResult Total, int RepeatedWrongFoods, IReadOnlyList<EvalCaseResult> Cases, bool History = true)
{
    public static string Percent(int n, int of) => of == 0 ? "-" : $"{100.0 * n / of:0}%";

    /// <summary>Per-meal energy and macro differences from the saved log, over the cases that record the saved values.</summary>
    public NutrientAgreement Nutrients => NutrientAgreement.Summarize(Cases.Where(c => c.FreshTotals is not null).Select(c => (c.SavedTotals, c.FreshTotals!)));
}

/// <summary>
/// Re-runs the live parser and matcher on confirmed parses and scores agreement with the saved log: the foods, grams and
/// nutrients the user saved, not what was first suggested (SPEC 11). A user can accept a bad suggestion, so this is not
/// accuracy; weighed meals are the stronger reference. It spends OpenRouter credit, so it only runs by hand: <c>feedback eval</c> or the admin area's Eval page,
/// never the tests. Each case runs as its own user, so their saved foods are in the matching as they were.
/// </summary>
public interface IParseEvaluator
{
    /// <param name="onCase">Called after each case with the result and the number done out of the total.</param>
    Task<EvalReport> RunAsync(EvalRequest request, Action<EvalCaseResult, int, int>? onCase, CancellationToken ct);
}

public class ParseEvaluator(
    OpenRouterSettings settings,
    FeedbackAdmin admin,
    OpenRouterFoodParser parser,
    JevFoodMatchDecider jev,
    DbContextOptions<AppDbContext> options,
    IFoodDatabase foodDatabase,
    AppDbContext rootDb) : IParseEvaluator
{
    /// <summary>The key and the parser and matcher models a run uses: the ones asked for, else those picked in the admin area.</summary>
    public async Task<(string Key, string Parser, string Matcher)> ModelsAsync(string? parserModel, string? matcherModel, CancellationToken ct)
    {
        var key = await settings.GetKeyAsync(ct) ?? throw new ValidationException("No OpenRouter key is set.");
        var parser = parserModel ?? await settings.GetModelAsync(AiTask.Parser, ct);
        var matcher = matcherModel is { } m
            ? m.Equals("jev", StringComparison.OrdinalIgnoreCase) ? JevFoodMatchDecider.Jev113 : m.ToLowerInvariant()
            : await settings.GetModelAsync(AiTask.Matcher, ct);
        return (key, parser, matcher);
    }

    /// <summary>
    /// Parses and matches <paramref name="text"/> as <paramref name="userId"/>, with that account's history as it was at
    /// <paramref name="asOf"/> unless <paramref name="history"/> is off. Nothing is saved.
    /// </summary>
    public async Task<(FoodParseResult Parsed, IReadOnlyList<ConfirmItem> Items)> ReplayAsync(string userId, string text, DateTimeOffset asOf, bool history,
        (string Key, string Parser, string Matcher) models, CancellationToken ct)
    {
        var user = new FixedCurrentUser(userId);
        await using var db = new AppDbContext(options, user);
        var foods = new FoodItemService(db, user, foodDatabase);
        IFoodMatchDecider decider = models.Matcher == AiTasks.Off ? new NoDecider() : new KeyedDecider(jev, models.Key, models.Matcher);
        // As the app would have matched it then: the account's usual foods and last amounts from before the parse.
        var matcher = new FoodMatcher(foods, decider, history ? new HistoryAsOf(db, user, asOf) : null);
        var parsed = await parser.ParseAsync(text, models.Key, models.Parser, ct);
        return (parsed, parsed.Items.Count == 0 ? [] : await matcher.ResolveAsync(text, parsed.Items, ct));
    }

    public async Task<EvalReport> RunAsync(EvalRequest request, Action<EvalCaseResult, int, int>? onCase, CancellationToken ct)
    {
        var models = await ModelsAsync(request.ParserModel, request.MatcherModel, ct);
        var (parserModel, matcherModel) = (models.Parser, models.Matcher);

        var cases = (await admin.EvalCasesAsync(request.Since, request.FlaggedOnly, ct)).TakeLast(request.Limit).ToList();
        var total = AgreementResult.Zero;
        var repeats = 0;
        var results = new List<EvalCaseResult>();
        foreach (var c in cases)
        {
            var run = await rootDb.ParseRuns.IgnoreQueryFilters().Where(r => r.Id == c.RunId).Select(r => new { r.UserId, r.CreatedAt }).SingleAsync(ct);
            var (parsed, fresh) = await ReplayAsync(run.UserId, c.Text, run.CreatedAt, request.History, models, ct);
            var result = Score(c, fresh, parsed.Error?.ToString());
            repeats += result.RepeatedWrongFoods;
            total += result.Score;
            results.Add(result);
            onCase?.Invoke(result, results.Count, cases.Count);
        }

        return new EvalReport(parserModel, matcherModel, total, repeats, results, request.History);
    }

    /// <summary>
    /// Scores one replay against the saved log: each saved item expects the food it was saved as, a rejected suggestion
    /// picked again counts as a repeat, an entry logged right after the confirm can back up a new item, and the meal's
    /// energy and macros are compared with the saved grams and values.
    /// </summary>
    public static EvalCaseResult Score(EvalCase c, IReadOnlyList<ConfirmItem> fresh, string? error = null)
    {
        var expected = c.Items.Select(i => i.Expectation()).ToList();
        var score = ParseAgreement.Replay(expected,
            fresh.Select(f => new SuggestedItem(f.Description, f.Grams, f.Per100, f.FoodItemId, f.MatchedNameEn ?? f.MatchedName)).ToList(),
            c.Later?.Select(l => l.Entry()).ToList());
        var saved = c.Items.Zip(expected, (i, e) => new EvalFresh(
                (e.Included ? "" : "(unticked) ") + (i.Saved?.Description ?? i.Parsed.NameOriginal), e.Grams,
                e.FoodKnown ? e.FoodItemId : null, e.FoodKnown ? i.Saved?.FoodName ?? i.FoodName : null, e.FoodKnown))
            .ToList();
        return new EvalCaseResult(c.RunId, c.Text, score.Agreement, score.RepeatedRejections, error,
            fresh.Select(f => new EvalFresh(f.Description, f.Grams, f.FoodItemId, f.MatchedName)).ToList(),
            [.. saved, .. (c.Later ?? []).Select(l => new EvalFresh("(logged after) " + l.Description, l.Grams, l.FoodItemId, l.FoodName))],
            score.Saved, score.Fresh);
    }

    private sealed class NoDecider : IFoodMatchDecider
    {
        public Task<bool> IsEnabledAsync(CancellationToken ct) => Task.FromResult(false);

        public Task<MatchDecisions?> DecideAsync(string text, IReadOnlyList<MatchQuestion> questions, CancellationToken ct) => Task.FromResult<MatchDecisions?>(null);
    }

    private sealed class KeyedDecider(JevFoodMatchDecider jev, string key, string model) : IFoodMatchDecider
    {
        public Task<MatchDecisions?> DecideAsync(string text, IReadOnlyList<MatchQuestion> questions, CancellationToken ct) =>
            jev.DecideAsync(text, questions, key, model, ct);
    }
}

/// <summary><c>feedback eval</c>: the evaluation with text or JSON output.</summary>
public static class ParseEvaluation
{
    public const string Usage = "Usage: feedback eval [--since 365d] [--flagged] [--limit 20] [--parser <model>] [--matcher off|jev|<decision model id>] [--no-history] [--json]";

    private static readonly string[] Valued = ["--since", "--limit", "--parser", "--matcher"];
    private static readonly string[] Flags = ["--flagged", "--json", "--no-history"];

    /// <param name="args">The command's arguments, starting with "eval".</param>
    public static async Task<int> RunAsync(IServiceProvider services, string[] args, TextWriter o, bool json, CancellationToken ct)
    {
        // Every run spends OpenRouter credit, so a misspelt option or a request for help must not start one.
        for (var n = 1; n < args.Length; n++)
        {
            if (args[n] is "--help" or "-h" or "help")
            {
                o.WriteLine(Usage);
                o.WriteLine("Re-runs the live parser and matcher on saved parses and scores agreement with the saved log: the foods, grams and nutrients users saved. Each run spends OpenRouter credit.");
                return 0;
            }

            if (Valued.Contains(args[n]) && n + 1 < args.Length)
            {
                n++;
            }
            else if (!Flags.Contains(args[n]))
            {
                Console.Error.WriteLine(Valued.Contains(args[n]) ? $"{args[n]} needs a value; nothing was run." : $"Unknown option \"{args[n]}\" for eval; nothing was run.");
                Console.Error.WriteLine(Usage);
                return 2;
            }
        }

        var request = new EvalRequest(
            TimeWindow.Since(Option(args, "--since"), TimeSpan.FromDays(365), DateTimeOffset.UtcNow)!.Value,
            args.Contains("--flagged"),
            int.TryParse(Option(args, "--limit"), CultureInfo.InvariantCulture, out var l) && l > 0 ? l : 20,
            Option(args, "--parser"),
            Option(args, "--matcher"),
            History: !args.Contains("--no-history"));
        if (await services.GetRequiredService<OpenRouterSettings>().GetKeyAsync(ct) is null)
        {
            Console.Error.WriteLine("No OpenRouter key is set, so there is nothing to evaluate against.");
            return 1;
        }

        var started = false;
        var report = await services.GetRequiredService<IParseEvaluator>().RunAsync(request, (c, done, total) =>
        {
            if (json)
            {
                return;
            }

            if (!started)
            {
                started = true;
                o.WriteLine($"Evaluating {total} parses.");
            }

            o.WriteLine(CaseText(c));
        }, ct);

        if (json)
        {
            Cli.FeedbackCommand.WriteJson(o, new { report.ParserModel, report.MatcherModel, history = report.History, total = report.Total, repeatedWrongFoods = report.RepeatedWrongFoods, nutrients = report.Nutrients, cases = report.Cases });
        }
        else
        {
            o.WriteLine($"Parser {report.ParserModel}, matcher {report.MatcherModel}, {(report.History ? "with each account's history as it was" : "without history")}.");
            o.WriteLine(TotalLine(report));
            o.WriteLine("Cost is not reported here; check the OpenRouter activity page.");
        }

        return 0;
    }

    public static string CaseLine(EvalCaseResult c) =>
        $"run {c.RunId,-5} food {c.Score.FoodAgreed}/{c.Score.Expected}, grams {c.Score.GramsAgreed}/{c.Score.Expected}, extra {c.Score.Extra}"
        + (c.Score.Unknown > 0 ? $", saved food not recorded {c.Score.Unknown}" : "")
        + (c.Score.LoggedLater > 0 ? $", logged after {c.Score.LoggedLater}" : "")
        + (c.RepeatedWrongFoods > 0 ? $", picked a rejected food {c.RepeatedWrongFoods}x" : "")
        + (c.SavedTotals is { } s && c.FreshTotals is { } f ? $", kcal {Num(s.Kcal)} saved, {Num(f.Kcal)} now" : "")
        + $"  \"{Clip(c.Text)}\""
        + (c.Error is { } error ? $"  [{error}]" : "");

    public static string TotalLine(EvalReport r)
    {
        var line = $"Agreement with the saved log: food {r.Total.FoodAgreed}/{r.Total.Expected} ({EvalReport.Percent(r.Total.FoodAgreed, r.Total.Expected)}), grams {r.Total.GramsAgreed}/{r.Total.Expected} ({EvalReport.Percent(r.Total.GramsAgreed, r.Total.Expected)}), extra items {r.Total.Extra}, rejected foods picked again {r.RepeatedWrongFoods}"
            + (r.Total.LoggedLater > 0 ? $", found in entries logged right after {r.Total.LoggedLater}" : "")
            + (r.Total.Unknown > 0 ? $", saved food not recorded for {r.Total.Unknown} items." : ".");
        var n = r.Nutrients;
        return n.Meals == 0 ? line
            : line + $" Per meal over {n.Meals} meals: kcal off by {Num(n.MeanAbsKcal)} on average (median {Num(n.MedianAbsKcal)}, bias {(n.MeanSignedKcal >= 0 ? "+" : "")}{Num(n.MeanSignedKcal)}), protein {Num(n.MeanAbsProtein)} g, carbs {Num(n.MeanAbsCarbs)} g, fat {Num(n.MeanAbsFat)} g.";
    }

    private static string Num(decimal value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>
    /// The case line, and for a case that didn't fully agree, what the user saved beside what the parser and matcher
    /// give now, item by item.
    /// </summary>
    public static string CaseText(EvalCaseResult c)
    {
        var s = c.Score;
        if (s.FoodAgreed == s.Expected && s.GramsAgreed == s.Expected && s.Extra == 0 && c.RepeatedWrongFoods == 0)
        {
            return CaseLine(c);
        }

        var lines = new List<string> { CaseLine(c) };
        lines.AddRange((c.Saved ?? []).Select(i => "    saved " + ItemText(i)));
        lines.AddRange(c.Fresh.Select(i => "    now   " + ItemText(i)));
        return string.Join('\n', lines);
    }

    private static string ItemText(EvalFresh i) =>
        $"{i.Grams.ToString("0.#", CultureInfo.InvariantCulture)} g {i.Description} → "
        + (!i.Known ? "food not recorded" : i.FoodItemId is { } id ? $"{i.MatchedName} #{id}" : "AI estimate");

    private static string Clip(string text) => text.Length > 60 ? text[..60] + "…" : text;

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
