using System.Diagnostics;
using GymTracker.Core.Domain;
using GymTracker.Core.Nutrition;
using GymTracker.Web.Data;
using GymTracker.Web.Features.Food;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Features.Feedback;

/// <summary>The result of parsing typed food: the rows to confirm and the run they are recorded under.</summary>
public record ParseOutcome(FoodParseResult Result, IReadOnlyList<ConfirmItem> Items, int? RunId);

/// <summary>One suggested row as stored in <see cref="ParseRun.Resolved"/>.</summary>
public record ResolvedRow(string Description, decimal Grams, Per100g Per100, int? FoodItemId, string? MatchedName, Confidence Confidence,
    IReadOnlyList<string> Notes, string? MatchModel, MatchTrace? Match);

/// <summary>One submitted row as stored in <see cref="ParseRun.Saved"/>.</summary>
public record SavedRow(bool Included, string Description, decimal Grams, Per100g Per100, int? FoodItemId, ParseCorrection Corrections);

/// <summary>
/// Parses typed food and records the run: what the parser and matcher suggested and, on confirm, what the user
/// saved and how it differs (SPEC 11). Recording never gets in the way of logging food.
/// </summary>
public class ParseRunService(AppDbContext db, ICurrentUser currentUser, IFoodParser parser, FoodMatcher matcher, TimeProvider time, ILogger<ParseRunService> log)
{
    public async Task<ParseOutcome> ParseAsync(string text, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var result = await parser.ParseAsync(text, ct);
        var parserMs = (int)clock.ElapsedMilliseconds;

        var trace = new List<MatchTrace>();
        IReadOnlyList<ConfirmItem> items = [];
        var matchMs = 0;
        if (result.Items.Count > 0)
        {
            clock.Restart();
            items = await matcher.ResolveAsync(text, result.Items, ct, trace);
            matchMs = (int)clock.ElapsedMilliseconds;
        }

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
            ParserError = result.Error is { } error ? Trim(error.ToString(), 500) : null,
            Parsed = FeedbackJson.Write(result.Items),
            Resolved = FeedbackJson.Write(items.Select((item, n) => new ResolvedRow(item.Description, item.Grams, item.Per100, item.FoodItemId,
                item.MatchedName, item.Confidence, item.Notes.Select(n => n.ToString()).ToList(), item.MatchModel, trace.ElementAtOrDefault(n))).ToList()),
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

    /// <summary>
    /// Stores what was saved for a run and its corrections. Returns the run id to link the new entries to, or null
    /// when the run is missing, someone else's, or already confirmed (the form was submitted twice).
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

    private static string? Trim(string? value, int max = 100) => value is { Length: > 0 } v && v.Length > max ? v[..max] : value;
}
