using System.Globalization;
using GymTracker.Core.Nutrition;
using GymTracker.Web.Data;
using GymTracker.Web.Features.Food;
using GymTracker.Web.Integrations;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Features.Feedback;

/// <param name="ParserModel">Null for the model picked in the admin area.</param>
/// <param name="MatcherModel">"off", a decision model id, or null for the one picked in the admin area.</param>
public record EvalRequest(DateTimeOffset Since, bool FlaggedOnly, int Limit, string? ParserModel, string? MatcherModel);

public record EvalFresh(string Description, decimal Grams, int? FoodItemId, string? MatchedName);

public record EvalCaseResult(int RunId, string Text, AgreementResult Score, int RepeatedWrongFoods, string? Error, IReadOnlyList<EvalFresh> Fresh);

public record EvalReport(string ParserModel, string MatcherModel, AgreementResult Total, int RepeatedWrongFoods, IReadOnlyList<EvalCaseResult> Cases)
{
    public static string Percent(int n, int of) => of == 0 ? "-" : $"{100.0 * n / of:0}%";
}

/// <summary>
/// Re-runs the live parser and matcher on confirmed parses and compares the result with what the user saved
/// (SPEC 11). It spends OpenRouter credit, so it only runs by hand: <c>feedback eval</c> or the admin area's Eval page,
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
    public async Task<EvalReport> RunAsync(EvalRequest request, Action<EvalCaseResult, int, int>? onCase, CancellationToken ct)
    {
        var key = await settings.GetKeyAsync(ct) ?? throw new ValidationException("No OpenRouter key is set.");
        var parserModel = request.ParserModel ?? await settings.GetModelAsync(AiTask.Parser, ct);
        var matcherModel = request.MatcherModel is { } m
            ? m.Equals("jev", StringComparison.OrdinalIgnoreCase) ? JevFoodMatchDecider.Jev113 : m.ToLowerInvariant()
            : await settings.GetModelAsync(AiTask.Matcher, ct);

        var cases = (await admin.EvalCasesAsync(request.Since, request.FlaggedOnly, ct)).TakeLast(request.Limit).ToList();
        var total = AgreementResult.Zero;
        var repeats = 0;
        var results = new List<EvalCaseResult>();
        foreach (var c in cases)
        {
            var userId = await rootDb.ParseRuns.IgnoreQueryFilters().Where(r => r.Id == c.RunId).Select(r => r.UserId).SingleAsync(ct);
            await using var db = new AppDbContext(options, new FixedCurrentUser(userId));
            var foods = new FoodItemService(db, new FixedCurrentUser(userId), foodDatabase);
            IFoodMatchDecider decider = matcherModel == AiTasks.Off ? new NoDecider() : new KeyedDecider(jev, key, matcherModel);
            var matcher = new FoodMatcher(foods, decider);

            var parsed = await parser.ParseAsync(c.Text, key, parserModel, ct);
            var fresh = parsed.Items.Count == 0 ? [] : await matcher.ResolveAsync(c.Text, parsed.Items, ct);
            // Wrong-food items are not expected back; they are counted when the same food is picked again.
            var saved = c.Items.Where(i => i.Verdict != EvalVerdict.WrongFood)
                .Select(i => new SavedItem(i.Verdict != EvalVerdict.Removed, i.Parsed.NameOriginal, i.SavedGrams ?? i.Parsed.Grams, new Per100g(0, 0, 0, 0), i.FoodItemId))
                .ToList();
            var score = ParseAgreement.Score(saved, fresh.Select(f => new SuggestedItem(f.Description, f.Grams, f.Per100, f.FoodItemId)).ToList());
            var repeated = c.Items.Count(i => i.Verdict == EvalVerdict.WrongFood && i.FoodItemId is { } bad && fresh.Any(f => f.FoodItemId == bad));
            repeats += repeated;
            total += score;
            var result = new EvalCaseResult(c.RunId, c.Text, score, repeated, parsed.Error?.ToString(),
                fresh.Select(f => new EvalFresh(f.Description, f.Grams, f.FoodItemId, f.MatchedName)).ToList());
            results.Add(result);
            onCase?.Invoke(result, results.Count, cases.Count);
        }

        return new EvalReport(parserModel, matcherModel, total, repeats, results);
    }

    private sealed class NoDecider : IFoodMatchDecider
    {
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
    public static async Task<int> RunAsync(IServiceProvider services, string[] args, TextWriter o, bool json, CancellationToken ct)
    {
        var request = new EvalRequest(
            TimeWindow.Since(Option(args, "--since"), TimeSpan.FromDays(365), DateTimeOffset.UtcNow)!.Value,
            args.Contains("--flagged"),
            int.TryParse(Option(args, "--limit"), CultureInfo.InvariantCulture, out var l) && l > 0 ? l : 20,
            Option(args, "--parser"),
            Option(args, "--matcher"));
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

            o.WriteLine(CaseLine(c));
        }, ct);

        if (json)
        {
            Cli.FeedbackCommand.WriteJson(o, new { report.ParserModel, report.MatcherModel, total = report.Total, repeatedWrongFoods = report.RepeatedWrongFoods, cases = report.Cases });
        }
        else
        {
            o.WriteLine($"Parser {report.ParserModel}, matcher {report.MatcherModel}.");
            o.WriteLine(TotalLine(report));
            o.WriteLine("Cost is not reported here; check the OpenRouter activity page.");
        }

        return 0;
    }

    public static string CaseLine(EvalCaseResult c) =>
        $"run {c.RunId,-5} food {c.Score.FoodAgreed}/{c.Score.Expected}, grams {c.Score.GramsAgreed}/{c.Score.Expected}, extra {c.Score.Extra}{(c.RepeatedWrongFoods > 0 ? $", picked a rejected food {c.RepeatedWrongFoods}x" : "")}  \"{Clip(c.Text)}\""
        + (c.Error is { } error ? $"  [{error}]" : "");

    public static string TotalLine(EvalReport r) =>
        $"Total: food {r.Total.FoodAgreed}/{r.Total.Expected} ({EvalReport.Percent(r.Total.FoodAgreed, r.Total.Expected)}), grams {r.Total.GramsAgreed}/{r.Total.Expected} ({EvalReport.Percent(r.Total.GramsAgreed, r.Total.Expected)}), extra items {r.Total.Extra}, rejected foods picked again {r.RepeatedWrongFoods}.";

    private static string Clip(string text) => text.Length > 60 ? text[..60] + "…" : text;

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
