using System.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using System.Text.Json.Serialization;
using Plate.Core.Text;
using Plate.Web.ViewModels;
using Plate.Core.Domain;
using Plate.Core.Nutrition;
using Plate.Web.Data;
using Plate.Web.Features.Food;
using Microsoft.EntityFrameworkCore;

namespace Plate.Web.Features.Feedback;

/// <summary>The result of parsing typed food: the rows to confirm and the run they are recorded under.</summary>
public record ParseOutcome(FoodParseResult Result, IReadOnlyList<ConfirmItem> Items, int? RunId);

public record ReviewDestination(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("date")] DateOnly Date,
    [property: JsonPropertyName("meal")] Meal Meal,
    [property: JsonPropertyName("warning")] LocalText? Warning = null);

/// <summary>One suggested row as stored in <see cref="ParseRun.Resolved"/>.</summary>
/// <param name="Proposed">The namer's answer as it came back, kept even when it was rejected.</param>
/// <param name="EstimateModel">The estimator model whose values the row has; null for a database food or the parser's own estimate.</param>
public record ResolvedRow(string Description, decimal Grams, Per100g Per100, int? FoodItemId, string? MatchedName, Confidence Confidence,
    IReadOnlyList<string> Notes, string? MatchModel, MatchTrace? Match, FoodName? Proposed = null, string? Status = null, bool Usual = false,
    [property: JsonPropertyName("typed")] string? Typed = null,
    [property: JsonPropertyName("state")] FoodState State = FoodState.NotApplicable,
    [property: JsonPropertyName("reviewNotes")] IReadOnlyList<LocalText>? ReviewNotes = null,
    [property: JsonPropertyName("review")] ReviewDestination? Review = null,
    [property: JsonPropertyName("count")] CountedAmount? Count = null,
    [property: JsonPropertyName("stateChoice")] StateChoice? Choice = null,
    [property: JsonPropertyName("estimateModel")] string? EstimateModel = null);

/// <summary>One submitted row as stored in <see cref="ParseRun.Saved"/>.</summary>
public record SavedRow(bool Included, string Description, decimal Grams, Per100g Per100, int? FoodItemId, ParseCorrection Corrections);

/// <summary>
/// Parses typed food and records the run: what the parser and matcher suggested and, on confirm, what the user
/// saved and how it differs (SPEC 11). Recording never gets in the way of logging food.
/// </summary>
public class ParseRunService(AppDbContext db, ICurrentUser currentUser, IFoodParser parser, FoodMatcher matcher, IFoodNamer namer, TimeProvider time, ILogger<ParseRunService> log,
    IMemoryCache cache, RepeatLoggingService? repeat = null, IFoodEstimator? estimator = null)
{
    public async Task<ConfirmViewModel> CreateReviewAsync(DateOnly date, Meal meal, string text, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var (result, items, runId) = await ParseAsync(text, ct, new ReviewDestination(id, date, meal));
        var draft = new ConfirmViewModel(date, meal, text, result.Model, items, result.Error, runId, id);
        // If best-effort recording failed, the user can still open and save the review.
        if (runId is null && items.Count > 0)
            cache.Set((currentUser.UserId, id), draft, TimeSpan.FromHours(12));
        return draft;
    }

    public async Task<ParseOutcome> ParseAsync(string text, CancellationToken ct, ReviewDestination? review = null)
    {
        var total = Stopwatch.StartNew();
        var clock = Stopwatch.StartNew();
        var result = await parser.ParseAsync(text, ct);
        var parserMs = (int)clock.ElapsedMilliseconds;

        var trace = new List<MatchTrace>();
        IReadOnlyList<ConfirmItem> items = [];
        var matchMs = 0;
        var nearby = new List<IReadOnlyList<NearbyFood>>();
        if (result.Items.Count > 0)
        {
            clock.Restart();
            items = await matcher.ResolveAsync(text, result.Items, ct, trace, nearby: nearby);
            matchMs = (int)clock.ElapsedMilliseconds;
        }

        // The namer names the rows matched to a database food and the estimator fills the rows with none, so neither
        // needs the other's answer and the user waits for the slower of the two, not both.
        var matched = items;
        var naming = TimedAsync(() => NameAsync(matched, ct));
        var estimating = estimator is not null && FoodEstimation.Needed(matched, result.Items) ? TimedAsync(() => FoodEstimation.ApplyAsync(estimator, text, result.Items, matched, nearby, ct)) : null;
        await Task.WhenAll(estimating is null ? [naming] : [naming, estimating]);
        var (proposed, namerMs) = await naming;
        int? estimateMs = null;
        if (estimating is not null)
        {
            (items, var ms) = await estimating;
            estimateMs = ms;
        }

        items = items.Select((item, n) => item with
        {
            Description = FoodNaming.Accept(proposed[n]?.Name, item.State) ?? item.Description,
            Status = FoodNaming.AcceptStatus(proposed[n]?.Status, item.State) ?? item.Status,
        }).ToList();

        var run = new ParseRun
        {
            UserId = currentUser.UserId ?? throw new InvalidOperationException("No signed-in user."),
            CreatedAt = time.GetUtcNow(),
            Text = text.Length > 1000 ? text[..1000] : text,
            AiModel = Trim(result.Model),
            // The decider that answered, even if the ranking kept every item; null when the ranking alone decided.
            MatchModel = Trim(trace.Select(t => t.DeciderModel).FirstOrDefault(m => m is not null) ?? items.Select(i => i.MatchModel).FirstOrDefault(m => m is not null)),
            ParserMs = parserMs,
            MatchMs = matchMs,
            NamerMs = namerMs,
            EstimateMs = estimateMs,
            TotalMs = (int)total.ElapsedMilliseconds,
            ParserError = result.Error is { } error ? Trim(error.ToString(), 500) : null,
            Parsed = FeedbackJson.Write(result.Items),
            Resolved = FeedbackJson.Write(items.Select((item, n) => new ResolvedRow(item.Description, item.Grams, item.Per100, item.FoodItemId,
                item.MatchedName, item.Confidence, item.Notes.Select(n => n.ToString()).ToList(), item.MatchModel, trace.ElementAtOrDefault(n), proposed[n], item.Status, item.Usual,
                item.Typed, item.State, item.Notes, n == 0 && review is not null ? review with { Warning = result.Error } : null, item.Count, item.Choice,
                item.EstimateModel)).ToList()),
        };

        try
        {
            db.ParseRuns.Add(run);
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // The parse itself worked; losing its record must not lose the user's food.
            log.LogWarning(ex, "Could not record a parse run.");
            db.Entry(run).State = EntityState.Detached;
            return new ParseOutcome(result, items, null);
        }

        return new ParseOutcome(result, items, run.Id);
    }

    public Task<string?> SavedLocationAsync(Guid id, CancellationToken ct) =>
        db.FoodLogRequests.Where(r => r.RequestId == id).Select(r => r.Location).SingleOrDefaultAsync(ct);

    public async Task<ConfirmViewModel?> ReviewAsync(int? runId, Guid id, CancellationToken ct)
    {
        if (runId is null) return cache.Get<ConfirmViewModel>((currentUser.UserId, id));
        var run = await db.ParseRuns.Where(r => r.Id == runId)
            .Select(r => new { r.Text, r.AiModel, r.Resolved }).SingleOrDefaultAsync(ct);
        var rows = FeedbackJson.Read<List<ResolvedRow>>(run?.Resolved);
        if (rows?.FirstOrDefault()?.Review is not { } destination || destination.Id != id) return null;
        var items = rows.Select(r => new ConfirmItem(r.Description, r.Grams, r.Per100, r.FoodItemId,
            r.MatchedName, r.Confidence, r.ReviewNotes ?? [], r.MatchModel, r.Typed ?? "", r.State,
            Status: r.Status, Usual: r.Usual, Count: r.Count, Choice: r.Choice, EstimateModel: r.EstimateModel)).ToList();
        return new(destination.Date, destination.Meal, run!.Text, run.AiModel, items, destination.Warning, runId, id);
    }

    /// <summary>
    /// Short names and statuses for the rows matched to a database food, by row (null where there is none). A database
    /// name is already on the row, so a failed or missing name changes nothing.
    /// </summary>
    private static async Task<(T Value, int Ms)> TimedAsync<T>(Func<Task<T>> work)
    {
        var clock = Stopwatch.StartNew();
        var value = await work();
        return (value, (int)clock.ElapsedMilliseconds);
    }

    private async Task<FoodName?[]> NameAsync(IReadOnlyList<ConfirmItem> items, CancellationToken ct)
    {
        var result = new FoodName?[items.Count];
        var matched = items.Select((item, n) => (item, n)).Where(x => x.item.MatchedName is not null).ToList();
        if (matched.Count == 0)
        {
            return result;
        }

        var names = await namer.NameAsync(matched.Select(x => new NameRequest(x.item.Typed, x.item.MatchedName!, x.item.MatchedNameEn, x.item.State)).ToList(), ct);
        for (var i = 0; i < matched.Count && names is not null; i++)
        {
            result[matched[i].n] = names.ElementAtOrDefault(i);
        }

        return result;
    }

    /// <summary>
    /// Stores what was saved for a run and its corrections. Returns the run id to link the new entries to, or null
    /// when the run is missing or someone else's. An already-confirmed run returns its original id.
    /// </summary>
    public async Task<int?> ConfirmAsync(int? runId, IReadOnlyList<SavedItem> saved, CancellationToken ct)
    {
        if (runId is not { } id || await db.ParseRuns.FirstOrDefaultAsync(r => r.Id == id, ct) is not { } run)
        {
            return null;
        }

        if (run.ConfirmedAt is not null)
        {
            return run.Id;
        }

        var suggested = (FeedbackJson.Read<List<ResolvedRow>>(run.Resolved) ?? [])
            .Select(r => new SuggestedItem(r.Description, r.Grams, r.Per100, r.FoodItemId))
            .ToList();
        var corrections = ParseCorrections.PerItem(suggested, saved);
        run.Saved = FeedbackJson.Write(saved.Take(suggested.Count)
            .Select((s, n) => new SavedRow(s.Included, s.Description, s.Grams, s.Per100, s.FoodItemId, corrections[n]))
            .ToList());
        run.Corrections = ParseCorrections.Overall(corrections);
        run.ConfirmedAt = time.GetUtcNow();
        return run.Id;
    }

    /// <summary>
    /// Remembers the food saved for each typed name (SPEC 6.1, usual foods), from the run's parsed names. Without a run
    /// (missing or someone else's) there are no typed names, so nothing is remembered. It runs after the food is saved,
    /// so any failure is logged and dropped: an error here would show an error page for food already logged.
    /// </summary>
    public async Task RememberAsync(int? runId, IReadOnlyList<SavedItem> saved, CancellationToken ct)
    {
        try
        {
            if (repeat is null || runId is not { } id)
            {
                return;
            }

            // ConfirmAsync has usually just loaded the run (through the owner filter) in this context; reuse it.
            var run = db.ParseRuns.Local.FirstOrDefault(r => r.Id == id) ?? await db.ParseRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
            if (run is null)
            {
                return;
            }

            var parsed = FeedbackJson.Read<List<ParsedFoodItem>>(run.Parsed) ?? [];
            var resolved = FeedbackJson.Read<List<ResolvedRow>>(run.Resolved) ?? [];
            var pairs = saved.Take(parsed.Count)
                .Select((s, n) => (Saved: s, Typed: parsed[n].NameOriginal, Suggested: resolved.ElementAtOrDefault(n)))
                .Where(x => x.Saved.Included && x.Saved.FoodItemId is not null && !Unconfirmed(x.Suggested, x.Saved.FoodItemId))
                .Select(x => (x.Typed, x.Saved.FoodItemId!.Value))
                .ToList();
            await repeat.RememberAsync(pairs, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not remember usual foods.");
        }
    }

    /// <summary>
    /// After an entry's food was changed on its page (SPEC 7): the words typed for that entry's row now mean the new food,
    /// as picking another food on the review would have done. The row is the saved one that had the old food, under this
    /// entry's name when several did; without a run, or with no such row, nothing is remembered. Like confirming, a failure is logged and dropped.
    /// </summary>
    public async Task RememberChangedFoodAsync(int? runId, int? oldFoodItemId, string oldDescription, int newFoodItemId, CancellationToken ct)
    {
        try
        {
            if (repeat is null || runId is not { } id)
            {
                return;
            }

            var run = await db.ParseRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
            if (run is null)
            {
                return;
            }

            var parsed = FeedbackJson.Read<List<ParsedFoodItem>>(run.Parsed) ?? [];
            var saved = FeedbackJson.Read<List<SavedRow>>(run.Saved) ?? [];
            // Several rows can share the old food (or none, for estimates): the one saved under this entry's name wins.
            var rows = Enumerable.Range(0, saved.Count).Where(i => saved[i].Included && saved[i].FoodItemId == oldFoodItemId).ToList();
            var n = rows.Cast<int?>().FirstOrDefault(i => string.Equals(saved[i!.Value].Description.Trim(), oldDescription.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? rows.Cast<int?>().FirstOrDefault() ?? -1;
            if (n >= 0 && n < parsed.Count)
            {
                await repeat.RememberAsync([(parsed[n].NameOriginal, newFoodItemId)], ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not remember usual foods.");
        }
    }

    /// <summary>
    /// A row saved on the food the app suggested while it marked it "Check this" (Medium confidence): the app was unsure
    /// and the user didn't choose, so it says nothing about what these words mean. Remembered, it would come back as
    /// "Your usual" without the warning (QA 2026-11-24: "кафе с мляко" as a sweetened bottled coffee).
    /// </summary>
    internal static bool Unconfirmed(ResolvedRow? suggested, int? savedFoodItemId) =>
        suggested is { Confidence: Confidence.Medium, Usual: false } && suggested.FoodItemId == savedFoodItemId;

    private static string? Trim(string? value, int max = 100) => value is { Length: > 0 } v && v.Length > max ? v[..max] : value;
}
