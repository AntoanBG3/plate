using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Plate.Core.Domain;
using Plate.Core.Nutrition;
using Plate.Web.Data;
using Plate.Web.Features.Admin;
using Microsoft.EntityFrameworkCore;

namespace Plate.Web.Features.Feedback;

public record FeedbackRow(int Id, DateTimeOffset CreatedAt, string Reporter, FeedbackSource Source, FeedbackKind Kind, FeedbackArea Area,
    FeedbackStatus Status, FeedbackRating? Rating, ParseReason Reasons, string? Message, bool HasReply, bool HasScreenshot, int? ParseRunId);

public record FeedbackDetail(FeedbackRow Row, string? Reply, string? FixedInVersion, DateTimeOffset? StatusChangedAt, JsonNode? Context,
    int? ParseItemIndex, ParseRunDetail? ParseRun, ErrorEvent? Error, (string ContentType, int Width, int Height, int Bytes)? Screenshot);

public record ParseRunDetail(int Id, DateTimeOffset CreatedAt, string Reporter, string Text, string? AiModel, string? MatchModel, int ParserMs, int MatchMs,
    string? ParserError, JsonNode? Parsed, JsonNode? Resolved, JsonNode? Saved, ParseCorrection Corrections, DateTimeOffset? ConfirmedAt,
    IReadOnlyList<(int? Item, FeedbackRating? Rating, ParseReason Reasons, string? Message)> Ratings, int? NamerMs = null, int? TotalMs = null, int? EstimateMs = null);

/// <summary>What the user's actions say about one parsed item.</summary>
public static class EvalVerdict
{
    /// <summary>Saved as suggested and not rated down: the matcher should keep picking this food.</summary>
    public const string Accepted = "accepted";

    /// <summary>Rated down as the wrong food: the matcher should stop picking it.</summary>
    public const string WrongFood = "wrong-food";

    /// <summary>Saved with changes, or rated down for another reason.</summary>
    public const string Corrected = "corrected";

    /// <summary>Unticked before saving.</summary>
    public const string Removed = "removed";
}

/// <summary>
/// One parsed item of an exported case. Exported cases are committed as fixtures (tests/Plate.Web.Tests/Fixtures), so the
/// JSON names are fixed here and pinned by EvalCaseJsonTests; cases exported before <see cref="Saved"/> existed still read.
/// </summary>
/// <param name="FoodItemId">The suggested food; with <see cref="ParseReason.WrongFood"/> in <paramref name="Reasons"/>, the rejected one.</param>
/// <param name="FoodName">The suggested food's English name, stable across databases unlike its id.</param>
/// <param name="Saved">What the user saved for the item, even when unticked; null in cases exported before it was kept.</param>
public record EvalItem(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("parsed")] ParsedFoodItem Parsed,
    [property: JsonPropertyName("verdict")] string Verdict,
    [property: JsonPropertyName("foodItemId")] int? FoodItemId,
    [property: JsonPropertyName("foodName")] string? FoodName,
    [property: JsonPropertyName("foodSource")] FoodSource? FoodSource,
    [property: JsonPropertyName("savedGrams")] decimal? SavedGrams,
    [property: JsonPropertyName("reasons")] ParseReason Reasons,
    [property: JsonPropertyName("saved")] EvalSaved? Saved = null)
{
    /// <summary>The suggested food when the user rated it as the wrong one.</summary>
    public int? RejectedFoodItemId => Reasons.HasFlag(ParseReason.WrongFood) ? FoodItemId : null;

    /// <summary>
    /// The saved food by name and source, or null when the record doesn't say which it was (or it is an AI estimate).
    /// An older case doesn't record the saved food, but an accepted item was saved as suggested.
    /// </summary>
    public (string Name, FoodSource Source)? SavedFood =>
        Saved is { } s ? (s.Included && s.FoodName is { } n && s.FoodSource is { } src ? (n, src) : null)
        : Verdict == EvalVerdict.Accepted && FoodName is { } name && FoodSource is { } source ? (name, source) : null;

    /// <summary>What the saved log says about this item, for scoring a replay.</summary>
    public ReplayExpectation Expectation()
    {
        if (Saved is { } s)
        {
            return new ReplayExpectation(s.Included, true, s.FoodItemId, s.Description, s.Grams, s.Per100, RejectedFoodItemId, s.FoodName);
        }

        // An older case: an unticked item is known from its verdict, an accepted one was saved as suggested, and for
        // the rest the saved food was not recorded. The saved per-100 g values never were.
        var included = Verdict != EvalVerdict.Removed && (Verdict != EvalVerdict.WrongFood || SavedGrams is not null);
        var known = Verdict == EvalVerdict.Accepted && FoodItemId is not null;
        return new ReplayExpectation(included, known, known ? FoodItemId : null, "", SavedGrams ?? Parsed.Grams, null, RejectedFoodItemId, known ? FoodName : null);
    }
}

/// <summary>One saved row of an exported case, with the food it was saved as.</summary>
/// <param name="FoodItemId">Null for an AI estimate.</param>
public record EvalSaved(
    [property: JsonPropertyName("included")] bool Included,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("grams")] decimal Grams,
    [property: JsonPropertyName("per100")] Per100g Per100,
    [property: JsonPropertyName("foodItemId")] int? FoodItemId,
    [property: JsonPropertyName("foodName")] string? FoodName,
    [property: JsonPropertyName("foodSource")] FoodSource? FoodSource);

/// <summary>One confirmed parse as a regression case.</summary>
/// <param name="Later">
/// Entries the user logged within <see cref="FeedbackAdmin.LaterWindow"/> after confirming, outside this parse: often the
/// food of a row they unticked, logged another way. Null in cases exported before they were kept.
/// </param>
/// <param name="SupersededBy">
/// A later run that typed this meal again (<see cref="ParseRepeats"/>); the eval skips this one, while its rejected foods
/// still count in the regression tests.
/// </param>
public record EvalCase(
    [property: JsonPropertyName("runId")] int RunId,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("aiModel")] string? AiModel,
    [property: JsonPropertyName("matchModel")] string? MatchModel,
    [property: JsonPropertyName("items")] IReadOnlyList<EvalItem> Items,
    [property: JsonPropertyName("later")] IReadOnlyList<EvalLater>? Later = null,
    [property: JsonPropertyName("supersededBy")] int? SupersededBy = null);

/// <summary>An entry logged right after a confirmed parse, outside it.</summary>
public record EvalLater(
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("grams")] decimal Grams,
    [property: JsonPropertyName("per100")] Per100g Per100,
    [property: JsonPropertyName("foodItemId")] int? FoodItemId,
    [property: JsonPropertyName("foodName")] string? FoodName)
{
    public LaterEntry Entry() => new(FoodItemId, FoodName, Description, Grams, Per100);
}

/// <param name="Accounts">What each account did in the period, busiest first; accounts that did nothing are left out.</param>
/// <param name="AiRecordedSince">When AI calls started being recorded (the first one), or null if none ever was; calls before it are unknown.</param>
/// <param name="ParseTimes">The whole parse's time over the period's runs that recorded it, or null when none did.</param>
/// <param name="ErrorsInPeriod">How often each listed error group happened in the period, from the day counts (P10).</param>
public record Digest(DateTimeOffset Since, IReadOnlyList<FeedbackRow> Reports, IReadOnlyList<ErrorEvent> Errors, int Parses, int Confirmed, int Corrected,
    int RatedUp, int RatedDown, IReadOnlyList<AccountActivity>? Accounts = null, DateTimeOffset? AiRecordedSince = null,
    ParseTimes? ParseTimes = null, IReadOnlyDictionary<int, int>? ErrorsInPeriod = null);

/// <summary>
/// One account's period in the digest (SPEC 11): counts and money only, never what was logged or typed.
/// </summary>
/// <param name="DaysActive">UTC days with a food entry, a set or a weigh-in.</param>
/// <param name="FoodBySource">Food entries added, by how they were added (Barcode, Search, Ai, Manual, Recent, Quick).</param>
public record AccountActivity(string Name, int DaysActive, IReadOnlyDictionary<string, int> FoodBySource, int Sets, int WeighIns, IReadOnlyList<AiTaskUse> Ai)
{
    public decimal AiCost => Ai.Sum(a => a.Cost);
}

/// <param name="Cost">US dollars OpenRouter reported; failed calls add nothing.</param>
public record AiTaskUse(string Task, int Calls, int Failed, decimal Cost);

/// <summary>Median and 95th percentile of the whole parse (P9), over <paramref name="Runs"/> runs; older runs have no total.</summary>
public record ParseTimes(int Runs, int MedianMs, int P95Ms)
{
    public static ParseTimes? Of(IReadOnlyList<int> totals)
    {
        if (totals.Count == 0) return null;
        var sorted = totals.Order().ToList();
        // Nearest rank: the smallest value with at least p of the runs at or below it.
        int Rank(double p) => sorted[Math.Max(0, (int)Math.Ceiling(p * sorted.Count) - 1)];
        return new ParseTimes(sorted.Count, Rank(0.5), Rank(0.95));
    }
}

/// <summary>
/// Feedback across all accounts, for the CLI and the admin area (SPEC 11, 12). Reads ignore the user filters, so
/// this must only be reachable from the CLI and from controllers under the admin policy (AdminGuardTests checks it).
/// Only the status, reply and changelog are written, and each write is audited.
/// </summary>
public class FeedbackAdmin(AppDbContext db, TimeProvider time, AdminAuditLog audit)
{
    /// <summary>How long after confirming a parse an entry logged another way still counts as part of that meal.</summary>
    public static readonly TimeSpan LaterWindow = TimeSpan.FromMinutes(30);

    private IQueryable<Core.Domain.Feedback> AllFeedback => db.Feedback.IgnoreQueryFilters();

    public async Task<List<FeedbackRow>> ListAsync(IReadOnlyCollection<FeedbackStatus>? statuses, DateTimeOffset? since, FeedbackArea? area, bool includeRatings, CancellationToken ct)
    {
        var query = AllFeedback.AsNoTracking();
        if (statuses is { Count: > 0 })
        {
            query = query.Where(f => statuses.Contains(f.Status));
        }

        if (since is { } from)
        {
            query = query.Where(f => f.CreatedAt >= from);
        }

        if (area is { } a)
        {
            query = query.Where(f => f.Area == a);
        }

        if (!includeRatings)
        {
            // A bare thumbs up says nothing to act on; a thumbs down is worth a look, with or without words.
            query = query.Where(f => f.Source != FeedbackSource.ParseRating || f.Rating == FeedbackRating.Down);
        }

        return await Rows(query.OrderByDescending(f => f.CreatedAt).Take(200), ct);
    }

    public async Task<FeedbackDetail?> ShowAsync(int id, CancellationToken ct)
    {
        var row = (await Rows(AllFeedback.AsNoTracking().Where(f => f.Id == id), ct)).SingleOrDefault();
        if (row is null)
        {
            return null;
        }

        var f = await AllFeedback.AsNoTracking().SingleAsync(x => x.Id == id, ct);
        var shot = await db.FeedbackAttachments.IgnoreQueryFilters().Where(a => a.FeedbackId == id)
            .Select(a => new { a.ContentType, a.Width, a.Height, Bytes = a.Bytes.Length })
            .FirstOrDefaultAsync(ct);
        var error = f.ErrorEventId is { } errorId ? await db.ErrorEvents.AsNoTracking().FirstOrDefaultAsync(e => e.Id == errorId, ct) : null;
        return new FeedbackDetail(row, f.Reply, f.FixedInVersion, f.StatusChangedAt, JsonNode.Parse(f.Context), f.ParseItemIndex,
            f.ParseRunId is { } runId ? await ParseRunAsync(runId, ct) : null, error,
            shot is null ? null : (shot.ContentType, shot.Width, shot.Height, shot.Bytes));
    }

    public async Task<FeedbackAttachment?> AttachmentAsync(int feedbackId, CancellationToken ct) =>
        await db.FeedbackAttachments.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(a => a.FeedbackId == feedbackId, ct);

    /// <summary>Sets the status and, optionally, the reply the reporter sees. Returns false when there is no such report.</summary>
    public async Task<bool> SetStatusAsync(int id, FeedbackStatus status, string? reply, string? version, CancellationToken ct)
    {
        var f = await AllFeedback.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (f is null)
        {
            return false;
        }

        f.Status = status;
        if (!string.IsNullOrWhiteSpace(reply))
        {
            var text = reply.Trim();
            f.Reply = text.Length > Core.Domain.Feedback.MaxReplyLength ? text[..Core.Domain.Feedback.MaxReplyLength] : text;
        }

        if (!string.IsNullOrWhiteSpace(version))
        {
            f.FixedInVersion = version.Trim();
        }

        f.StatusChangedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(AuditActions.Status, $"feedback #{id}",
            string.Join(", ", new[] { status.ToString(), string.IsNullOrWhiteSpace(reply) ? null : "reply", string.IsNullOrWhiteSpace(version) ? null : "version " + version.Trim() }.OfType<string>()), ct);
        return true;
    }

    /// <summary>Marks a new report Seen without touching its reply, as opening it in the admin area does.</summary>
    public async Task MarkSeenAsync(int id, CancellationToken ct)
    {
        var changed = await AllFeedback.Where(f => f.Id == id && f.Status == FeedbackStatus.New)
            .ExecuteUpdateAsync(u => u.SetProperty(f => f.Status, FeedbackStatus.Seen), ct);
        if (changed > 0)
        {
            await audit.WriteAsync(AuditActions.Status, $"feedback #{id}", "Seen (opened)", ct);
        }
    }

    public async Task<ParseRunDetail?> ParseRunAsync(int id, CancellationToken ct)
    {
        var run = await db.ParseRuns.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (run is null)
        {
            return null;
        }

        var ratings = await AllFeedback.AsNoTracking()
            .Where(f => f.ParseRunId == id)
            .OrderBy(f => f.CreatedAt)
            .Select(f => new { f.ParseItemIndex, f.Rating, f.Reasons, f.Message })
            .ToListAsync(ct);
        return new ParseRunDetail(run.Id, run.CreatedAt, await ReporterAsync(run.UserId, ct), run.Text, run.AiModel, run.MatchModel, run.ParserMs, run.MatchMs,
            run.ParserError, JsonNode.Parse(run.Parsed), JsonNode.Parse(run.Resolved), run.Saved is null ? null : JsonNode.Parse(run.Saved),
            run.Corrections, run.ConfirmedAt, ratings.Select(r => (r.ParseItemIndex, r.Rating, r.Reasons, r.Message)).ToList(), run.NamerMs, run.TotalMs, run.EstimateMs);
    }

    /// <summary>Parse runs since a time, newest first; with <paramref name="flaggedOnly"/>, only those corrected or rated down.</summary>
    public async Task<List<ParseRunDetail>> ParsesAsync(DateTimeOffset since, bool flaggedOnly, int limit, CancellationToken ct)
    {
        var rated = AllFeedback.Where(f => f.ParseRunId != null && (f.Rating == FeedbackRating.Down || f.Reasons != ParseReason.None)).Select(f => f.ParseRunId);
        var ids = await db.ParseRuns.IgnoreQueryFilters()
            .Where(r => r.CreatedAt >= since)
            .Where(r => !flaggedOnly || r.Corrections != ParseCorrection.None || rated.Contains(r.Id))
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => r.Id)
            .Take(limit)
            .ToListAsync(ct);
        var result = new List<ParseRunDetail>();
        foreach (var id in ids)
        {
            result.Add((await ParseRunAsync(id, ct))!);
        }

        return result;
    }

    /// <summary>
    /// Confirmed parses as regression cases, oldest first: per item what the parser read, the food that was suggested,
    /// what was saved (which may be another food), and the user's verdict. With <paramref name="flaggedOnly"/>, only cases with something not accepted.
    /// </summary>
    public async Task<List<EvalCase>> EvalCasesAsync(DateTimeOffset since, bool flaggedOnly, CancellationToken ct)
    {
        var runs = await db.ParseRuns.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.CreatedAt >= since && r.Saved != null)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync(ct);
        var runIds = runs.Select(r => r.Id).ToList();
        var ratings = await AllFeedback.AsNoTracking()
            .Where(f => f.ParseRunId != null && runIds.Contains(f.ParseRunId.Value) && f.ParseItemIndex != null && f.Rating == FeedbackRating.Down)
            .Select(f => new { RunId = f.ParseRunId!.Value, Item = f.ParseItemIndex!.Value, f.Reasons })
            .ToListAsync(ct);

        var cases = new List<EvalCase>();
        foreach (var run in runs)
        {
            var parsed = FeedbackJson.Read<List<ParsedFoodItem>>(run.Parsed) ?? [];
            var resolved = FeedbackJson.Read<List<ResolvedRow>>(run.Resolved) ?? [];
            var saved = FeedbackJson.Read<List<SavedRow>>(run.Saved) ?? [];
            var ids = resolved.Select(r => r.FoodItemId).Concat(saved.Select(r => r.FoodItemId)).OfType<int>().Distinct().ToList();
            var foods = await db.FoodItems.IgnoreQueryFilters().Where(f => ids.Contains(f.Id)).ToDictionaryAsync(f => f.Id, f => new { f.Name, f.Source }, ct);

            var items = new List<EvalItem>();
            for (var n = 0; n < Math.Min(parsed.Count, resolved.Count); n++)
            {
                var row = saved.ElementAtOrDefault(n);
                var down = ratings.Where(r => r.RunId == run.Id && r.Item == n).Select(r => r.Reasons).DefaultIfEmpty(ParseReason.None).Aggregate((a, b) => a | b);
                var rated = ratings.Any(r => r.RunId == run.Id && r.Item == n);
                var verdict = down.HasFlag(ParseReason.WrongFood) ? EvalVerdict.WrongFood
                    : row is null || !row.Included ? EvalVerdict.Removed
                    : row.Corrections != ParseCorrection.None || rated ? EvalVerdict.Corrected
                    : EvalVerdict.Accepted;
                var food = resolved[n].FoodItemId is { } id && foods.TryGetValue(id, out var f) ? f : null;
                var savedFood = row?.FoodItemId is { } savedId && foods.TryGetValue(savedId, out var sf) ? sf : null;
                items.Add(new EvalItem(n, parsed[n], verdict, resolved[n].FoodItemId, food?.Name, food?.Source, row?.Included == true ? row.Grams : null, down,
                    row is null ? null : new EvalSaved(row.Included, row.Description, row.Grams, row.Per100, row.FoodItemId, savedFood?.Name, savedFood?.Source)));
            }

            if (!flaggedOnly || items.Any(i => i.Verdict != EvalVerdict.Accepted))
            {
                var accepted = items.All(i => i.Verdict == EvalVerdict.Accepted);
                var by = runs.FirstOrDefault(r => r.Id != run.Id && r.UserId == run.UserId && r.CreatedAt > run.CreatedAt
                    && ParseRepeats.Supersedes(run.ConfirmedAt ?? run.CreatedAt, run.Text, accepted, r.CreatedAt, r.Text));
                cases.Add(new EvalCase(run.Id, run.Text, run.AiModel, run.MatchModel, items, await LaterAsync(run, ct), by?.Id));
            }
        }

        return cases;
    }

    /// <summary>
    /// What the user logged within <see cref="LaterWindow"/> after confirming the run, outside it, and still has. Quick
    /// entries have no grams and say nothing about a food, so they are left out.
    /// </summary>
    private async Task<List<EvalLater>> LaterAsync(Core.Domain.ParseRun run, CancellationToken ct)
    {
        if (run.ConfirmedAt is not { } confirmed)
        {
            return [];
        }

        var until = confirmed + LaterWindow;
        var entries = await db.FoodLog.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.UserId == run.UserId && e.DeletedAt == null && e.Grams > 0 && e.CreatedAt >= confirmed && e.CreatedAt <= until
                && (e.ParseRunId == null || e.ParseRunId != run.Id))
            .OrderBy(e => e.CreatedAt)
            .Select(e => new { e.Description, e.Grams, e.Kcal, e.ProteinG, e.CarbsG, e.FatG, e.FoodItemId, FoodName = e.FoodItem == null ? null : e.FoodItem.Name })
            .ToListAsync(ct);
        return entries.Select(e => new EvalLater(e.Description, e.Grams,
            new Per100g(Math.Round(e.Kcal * 100 / e.Grams, 2), Math.Round(e.ProteinG * 100 / e.Grams, 2), Math.Round(e.CarbsG * 100 / e.Grams, 2), Math.Round(e.FatG * 100 / e.Grams, 2)),
            e.FoodItemId, e.FoodName)).ToList();
    }

    public Task<List<ErrorEvent>> ErrorsAsync(DateTimeOffset since, CancellationToken ct) =>
        db.ErrorEvents.AsNoTracking().Where(e => e.LastSeenAt >= since).OrderByDescending(e => e.LastSeenAt).Take(100).ToListAsync(ct);

    public async Task<Digest> DigestAsync(DateTimeOffset since, CancellationToken ct)
    {
        var reports = await ListAsync(null, since, null, includeRatings: false, ct);
        var seen = await db.ErrorEvents.AsNoTracking().Where(e => e.LastSeenAt >= since).ToListAsync(ct);
        var inPeriod = await ErrorCountsSinceAsync(seen.Select(e => e.Id), since, ct);
        // Most frequent in the period first; the lifetime count breaks ties.
        var errors = seen.OrderByDescending(e => inPeriod.GetValueOrDefault(e.Id)).ThenByDescending(e => e.Count).ToList();
        var runs = db.ParseRuns.IgnoreQueryFilters().Where(r => r.CreatedAt >= since);
        var ratings = AllFeedback.Where(f => f.Source == FeedbackSource.ParseRating && f.CreatedAt >= since);
        return new Digest(since, reports, errors,
            await runs.CountAsync(ct),
            await runs.CountAsync(r => r.ConfirmedAt != null, ct),
            await runs.CountAsync(r => r.Corrections != ParseCorrection.None, ct),
            await ratings.CountAsync(f => f.Rating == FeedbackRating.Up, ct),
            await ratings.CountAsync(f => f.Rating == FeedbackRating.Down, ct),
            await ActivityAsync(since, ct),
            await db.AiCalls.IgnoreQueryFilters().MinAsync(c => (DateTimeOffset?)c.At, ct),
            ParseTimes.Of(await runs.Where(r => r.TotalMs != null).Select(r => r.TotalMs!.Value).ToListAsync(ct)),
            inPeriod);
    }

    /// <summary>Per account: active days, food entries by source, sets, weigh-ins and AI calls with their cost (SPEC 11).</summary>
    public async Task<List<AccountActivity>> ActivityAsync(DateTimeOffset since, CancellationToken ct)
    {
        var sinceDay = DateOnly.FromDateTime(since.UtcDateTime);
        var food = await db.FoodLog.IgnoreQueryFilters().Where(e => e.CreatedAt >= since && e.DeletedAt == null)
            .Select(e => new { e.UserId, e.CreatedAt, e.Source }).ToListAsync(ct);
        var sets = await db.Sets.IgnoreQueryFilters().Where(x => x.CompletedAt >= since && x.DeletedAt == null)
            .Select(x => new { x.SessionExercise.Session.UserId, x.CompletedAt }).ToListAsync(ct);
        var weighIns = await db.Bodyweights.IgnoreQueryFilters().Where(b => b.Date >= sinceDay)
            .Select(b => new { b.UserId, b.Date }).ToListAsync(ct);
        var ai = await db.AiCalls.IgnoreQueryFilters().Where(c => c.At >= since)
            .GroupBy(c => new { c.UserId, c.Task })
            .Select(g => new { g.Key.UserId, g.Key.Task, Calls = g.Count(), Failed = g.Count(c => c.Failed), Cost = g.Sum(c => c.Cost ?? 0) })
            .ToListAsync(ct);
        var names = await db.Profiles.IgnoreQueryFilters().ToDictionaryAsync(p => p.UserId, p => p.DisplayName, ct);

        return food.Select(e => e.UserId).Concat(sets.Select(x => x.UserId)).Concat(weighIns.Select(b => b.UserId)).Concat(ai.Select(a => a.UserId))
            .Distinct()
            .Select(id => new AccountActivity(
                names.GetValueOrDefault(id) ?? "?",
                food.Where(e => e.UserId == id).Select(e => DateOnly.FromDateTime(e.CreatedAt.UtcDateTime))
                    .Concat(sets.Where(x => x.UserId == id).Select(x => DateOnly.FromDateTime(x.CompletedAt.UtcDateTime)))
                    .Concat(weighIns.Where(b => b.UserId == id).Select(b => b.Date))
                    .Distinct().Count(),
                food.Where(e => e.UserId == id).GroupBy(e => e.Source.ToString()).OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
                    .ToDictionary(g => g.Key, g => g.Count()),
                sets.Count(x => x.UserId == id),
                weighIns.Count(b => b.UserId == id),
                ai.Where(a => a.UserId == id).OrderByDescending(a => a.Cost).ThenBy(a => a.Task)
                    .Select(a => new AiTaskUse(a.Task, a.Calls, a.Failed, a.Cost)).ToList()))
            .OrderByDescending(a => a.DaysActive).ThenByDescending(a => a.AiCost).ThenBy(a => a.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// How often each of these error groups happened since a time, added up from the day counts (P10). Whole UTC
    /// days: the first day counts from its midnight, not from the time given. Occurrences from before the day
    /// counts existed are not included.
    /// </summary>
    public async Task<Dictionary<int, int>> ErrorCountsSinceAsync(IEnumerable<int> ids, DateTimeOffset since, CancellationToken ct)
    {
        var list = ids.ToList();
        var day = DateOnly.FromDateTime(since.UtcDateTime);
        return list.Count == 0 ? []
            : await db.ErrorEventDays.Where(d => list.Contains(d.ErrorEventId) && d.Day >= day)
                .GroupBy(d => d.ErrorEventId).Select(g => new { g.Key, Count = g.Sum(d => d.Count) })
                .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
    }

    public async Task<ChangelogEntry> AddChangelogAsync(string textEn, string textBg, int? feedbackId, string? version, CancellationToken ct)
    {
        if (feedbackId is { } id && !await AllFeedback.AnyAsync(f => f.Id == id, ct))
        {
            throw new InvalidOperationException($"There is no feedback #{id}.");
        }

        var entry = new ChangelogEntry
        {
            CreatedAt = time.GetUtcNow(),
            TextEn = Clip(textEn),
            TextBg = Clip(textBg),
            FeedbackId = feedbackId,
            Version = string.IsNullOrWhiteSpace(version) ? null : version.Trim(),
        };
        db.Changelog.Add(entry);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(AuditActions.ChangelogAdd, $"changelog #{entry.Id}", entry.TextEn, ct);
        return entry;

        static string Clip(string text) => text.Trim().Length > ChangelogEntry.MaxTextLength ? text.Trim()[..ChangelogEntry.MaxTextLength] : text.Trim();
    }

    public Task<List<ChangelogEntry>> ChangelogAsync(int limit, CancellationToken ct) =>
        db.Changelog.AsNoTracking().OrderByDescending(c => c.CreatedAt).Take(limit).ToListAsync(ct);

    /// <summary>Removes a What's new line, e.g. one with a typo. Returns false when there is no such line.</summary>
    public async Task<bool> RemoveChangelogAsync(int id, CancellationToken ct)
    {
        var entry = await db.Changelog.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (entry is null)
        {
            return false;
        }

        db.Changelog.Remove(entry);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(AuditActions.ChangelogRemove, $"changelog #{id}", entry.TextEn, ct);
        return true;
    }

    private async Task<List<FeedbackRow>> Rows(IQueryable<Core.Domain.Feedback> query, CancellationToken ct)
    {
        var names = db.Profiles.IgnoreQueryFilters();
        return await query
            .Select(f => new FeedbackRow(f.Id, f.CreatedAt, names.Where(p => p.UserId == f.UserId).Select(p => p.DisplayName).FirstOrDefault() ?? "?",
                f.Source, f.Kind, f.Area, f.Status, f.Rating, f.Reasons, f.Message, f.Reply != null, f.Attachment != null, f.ParseRunId))
            .ToListAsync(ct);
    }

    private async Task<string> ReporterAsync(string userId, CancellationToken ct) =>
        await db.Profiles.IgnoreQueryFilters().Where(p => p.UserId == userId).Select(p => p.DisplayName).FirstOrDefaultAsync(ct) ?? "?";
}
