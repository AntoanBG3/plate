using System.Globalization;
using GymTracker.Core.Nutrition;
using GymTracker.Web.Data;
using GymTracker.Web.Features.Food;
using GymTracker.Web.Integrations;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Features.Feedback;

/// <summary>
/// <c>feedback eval</c>: re-runs the live parser and matcher on confirmed parses and compares the result with what
/// the user saved (SPEC 11). It spends OpenRouter credit, so it is only run by hand, never by the tests.
/// Each case runs as its own user, so their saved foods are in the matching as they were.
/// </summary>
public static class ParseEvaluation
{
    public static async Task<int> RunAsync(IServiceProvider services, FeedbackAdmin admin, string[] args, TextWriter o, bool json, CancellationToken ct)
    {
        var settings = services.GetRequiredService<OpenRouterSettings>();
        if (await settings.GetKeyAsync(ct) is not { } key)
        {
            Console.Error.WriteLine("No OpenRouter key is set, so there is nothing to evaluate against.");
            return 1;
        }

        var parserModel = Option(args, "--parser") ?? await settings.GetModelAsync(AiTask.Parser, ct);
        var matcherModel = Option(args, "--matcher") is { } m ? (m.Equals("jev", StringComparison.OrdinalIgnoreCase) ? JevFoodMatchDecider.Jev113 : m.ToLowerInvariant())
            : await settings.GetModelAsync(AiTask.Matcher, ct);
        var since = Cli.FeedbackCommand.Since(Option(args, "--since"), TimeSpan.FromDays(365))!.Value;
        var limit = int.TryParse(Option(args, "--limit"), CultureInfo.InvariantCulture, out var l) && l > 0 ? l : 20;

        var cases = (await admin.EvalCasesAsync(since, args.Contains("--flagged"), ct)).TakeLast(limit).ToList();
        var parser = services.GetRequiredService<OpenRouterFoodParser>();
        var jev = services.GetRequiredService<JevFoodMatchDecider>();
        var options = services.GetRequiredService<DbContextOptions<AppDbContext>>();
        var foodDatabase = services.GetRequiredService<IFoodDatabase>();
        var rootDb = services.GetRequiredService<AppDbContext>();

        o.WriteLine($"Evaluating {cases.Count} parses with parser {parserModel} and matcher {matcherModel}.");
        var total = AgreementResult.Zero;
        var results = new List<object>();
        foreach (var c in cases)
        {
            var userId = await rootDb.ParseRuns.IgnoreQueryFilters().Where(r => r.Id == c.RunId).Select(r => r.UserId).SingleAsync(ct);
            await using var db = new AppDbContext(options, new FixedCurrentUser(userId));
            var foods = new FoodItemService(db, new FixedCurrentUser(userId), foodDatabase);
            IFoodMatchDecider decider = matcherModel == AiTasks.Off ? new NoDecider() : new KeyedDecider(jev, key, matcherModel);
            var matcher = new FoodMatcher(foods, decider);

            var parsed = await parser.ParseAsync(c.Text, key, parserModel, ct);
            var fresh = parsed.Items.Count == 0 ? [] : await matcher.ResolveAsync(c.Text, parsed.Items, ct);
            var saved = FeedbackJson.Read<List<SavedRow>>(c.Saved?.ToJsonString())?
                .Select(s => new SavedItem(s.Included, s.Description, s.Grams, s.Per100, s.FoodItemId)).ToList() ?? [];
            var score = ParseAgreement.Score(saved, fresh.Select(f => new SuggestedItem(f.Description, f.Grams, f.Per100, f.FoodItemId)).ToList());
            total += score;
            results.Add(new { c.RunId, c.Text, score, error = parsed.Error?.ToString(), fresh = fresh.Select(f => new { f.Description, f.Grams, f.FoodItemId, f.MatchedName }) });
            if (!json)
            {
                o.WriteLine($"run {c.RunId,-5} food {score.FoodAgreed}/{score.Expected}, grams {score.GramsAgreed}/{score.Expected}, extra {score.Extra}  \"{Clip(c.Text)}\""
                    + (parsed.Error is { } error ? $"  [{error}]" : ""));
            }
        }

        if (json)
        {
            Cli.FeedbackCommand.WriteJson(o, new { parserModel, matcherModel, total, cases = results });
        }
        else
        {
            o.WriteLine($"Total: food {total.FoodAgreed}/{total.Expected} ({Percent(total.FoodAgreed, total.Expected)}), grams {total.GramsAgreed}/{total.Expected} ({Percent(total.GramsAgreed, total.Expected)}), extra items {total.Extra}.");
            o.WriteLine("Cost is not reported here; check the OpenRouter activity page.");
        }

        return 0;
    }

    private static string Percent(int n, int of) => of == 0 ? "-" : $"{100.0 * n / of:0}%";

    private static string Clip(string text) => text.Length > 60 ? text[..60] + "…" : text;

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
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
