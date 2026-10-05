using System.IO.Compression;
using System.Text;
using GymTracker.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Features.Export;

/// <summary>All of the current user's data as CSV files in a zip (SPEC section 7, Settings).</summary>
public class DataExport(AppDbContext db)
{
    // Excel only reads UTF-8 (and so Cyrillic) correctly when the file starts with a byte order mark.
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    public async Task<byte[]> ZipAsync(CancellationToken ct)
    {
        var files = new Dictionary<string, Csv>
        {
            ["profile.csv"] = await ProfileAsync(ct),
            ["bodyweight.csv"] = await BodyweightAsync(ct),
            ["workouts.csv"] = await WorkoutsAsync(ct),
            ["sets.csv"] = await SetsAsync(ct),
            ["templates.csv"] = await TemplatesAsync(ct),
            ["custom_exercises.csv"] = await CustomExercisesAsync(ct),
            ["food_log.csv"] = await FoodLogAsync(ct),
            ["maintenance_estimates.csv"] = await EstimatesAsync(ct),
            ["weekly_summaries.csv"] = await WeeklySummariesAsync(ct),
            ["feedback.csv"] = await FeedbackAsync(ct),
        };

        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, csv) in files)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                await using var writer = new StreamWriter(entry.Open(), Utf8WithBom);
                await writer.WriteAsync(csv.ToString());
            }
        }

        return stream.ToArray();
    }

    private async Task<Csv> ProfileAsync(CancellationToken ct)
    {
        var csv = new Csv("display_name", "time_zone", "sex", "height_cm", "birth_year", "initial_maintenance_kcal",
            "goal", "target_rate_percent_per_week", "protein_g_per_kg", "default_weight_increment_kg");
        foreach (var p in await db.Profiles.AsNoTracking().ToListAsync(ct))
        {
            csv.Row(p.DisplayName, p.TimeZoneId, p.Sex?.ToString(), p.HeightCm, p.BirthYear, p.InitialMaintenanceKcal,
                p.Goal.ToString(), p.TargetRatePercentPerWeek, p.ProteinGramsPerKg, p.DefaultWeightIncrementKg);
        }

        return csv;
    }

    private async Task<Csv> BodyweightAsync(CancellationToken ct)
    {
        var csv = new Csv("date", "weight_kg");
        foreach (var b in await db.Bodyweights.AsNoTracking().OrderBy(b => b.Date).ToListAsync(ct))
        {
            csv.Row(b.Date, b.WeightKg);
        }

        return csv;
    }

    private async Task<Csv> WorkoutsAsync(CancellationToken ct)
    {
        var csv = new Csv("workout_id", "template", "started_at_utc", "ended_at_utc", "notes");
        var sessions = await db.Sessions.AsNoTracking()
            .OrderBy(s => s.StartedAt)
            .Select(s => new { s.Id, Template = s.Template == null ? null : s.Template.Name, s.StartedAt, s.EndedAt, s.Notes })
            .ToListAsync(ct);
        foreach (var s in sessions)
        {
            csv.Row(s.Id, s.Template, s.StartedAt, s.EndedAt, s.Notes);
        }

        return csv;
    }

    private async Task<Csv> SetsAsync(CancellationToken ct)
    {
        var csv = new Csv("workout_id", "exercise", "exercise_order", "set_number", "warmup", "weight_kg", "reps", "rpe", "completed_at_utc");
        var sets = await db.Sets.AsNoTracking()
            .OrderBy(s => s.SessionExercise.Session.StartedAt)
            .ThenBy(s => s.SessionExercise.SortOrder)
            .ThenBy(s => s.SetNumber)
            .Select(s => new
            {
                s.SessionExercise.SessionId,
                Exercise = s.SessionExercise.Exercise.Name,
                s.SessionExercise.SortOrder,
                s.SetNumber,
                s.IsWarmup,
                s.WeightKg,
                s.Reps,
                s.Rpe,
                s.CompletedAt,
            })
            .ToListAsync(ct);
        foreach (var s in sets)
        {
            csv.Row(s.SessionId, s.Exercise, s.SortOrder, s.SetNumber, s.IsWarmup, s.WeightKg, s.Reps, s.Rpe, s.CompletedAt);
        }

        return csv;
    }

    private async Task<Csv> TemplatesAsync(CancellationToken ct)
    {
        var csv = new Csv("template", "archived", "exercise", "exercise_order", "working_sets", "rep_min", "rep_max", "weight_increment_kg", "rest_seconds");
        var rows = await db.TemplateExercises.AsNoTracking()
            .OrderBy(t => t.Template.SortOrder).ThenBy(t => t.SortOrder)
            .Select(t => new
            {
                Template = t.Template.Name,
                t.Template.IsArchived,
                Exercise = t.Exercise.Name,
                t.SortOrder,
                t.WorkingSets,
                t.RepMin,
                t.RepMax,
                t.WeightIncrementKg,
                t.RestSeconds,
            })
            .ToListAsync(ct);
        foreach (var t in rows)
        {
            csv.Row(t.Template, t.IsArchived, t.Exercise, t.SortOrder, t.WorkingSets, t.RepMin, t.RepMax, t.WeightIncrementKg, t.RestSeconds);
        }

        return csv;
    }

    private async Task<Csv> CustomExercisesAsync(CancellationToken ct)
    {
        var csv = new Csv("name", "primary_muscle", "secondary_muscles", "equipment", "archived");
        var custom = await db.Exercises.AsNoTracking().Where(e => e.OwnerUserId != null).OrderBy(e => e.Name).ToListAsync(ct);
        foreach (var e in custom)
        {
            csv.Row(e.Name, e.PrimaryMuscle.ToString(), string.Join(';', e.SecondaryMuscles), e.Equipment.ToString(), e.IsArchived);
        }

        return csv;
    }

    private async Task<Csv> FoodLogAsync(CancellationToken ct)
    {
        var csv = new Csv("date", "meal", "description", "grams", "kcal", "protein_g", "carbs_g", "fat_g", "fiber_g", "saturated_fat_g", "source", "ai_model", "match_model", "created_at_utc");
        var entries = await db.FoodLog.AsNoTracking().OrderBy(e => e.Date).ThenBy(e => e.CreatedAt).ToListAsync(ct);
        foreach (var e in entries)
        {
            csv.Row(e.Date, e.Meal.ToString(), e.Description, e.Grams, e.Kcal, e.ProteinG, e.CarbsG, e.FatG, e.FiberG, e.SaturatedFatG, e.Source.ToString(), e.AiModel, e.MatchModel, e.CreatedAt);
        }

        return csv;
    }

    private async Task<Csv> EstimatesAsync(CancellationToken ct)
    {
        var csv = new Csv("calculated_on", "method", "maintenance_kcal", "target_kcal", "target_protein_g", "trend_weight_kg", "days_of_data", "logging_completeness", "low_confidence");
        var estimates = await db.MaintenanceEstimates.AsNoTracking().OrderBy(e => e.CalculatedOn).ToListAsync(ct);
        foreach (var e in estimates)
        {
            csv.Row(e.CalculatedOn, e.Method.ToString(), e.EstimatedTdeeKcal, e.TargetKcal, e.TargetProteinG, e.TrendWeightKg, e.DaysOfData, e.LoggingCompleteness, e.LowConfidence);
        }

        return csv;
    }

    private async Task<Csv> WeeklySummariesAsync(CancellationToken ct)
    {
        var csv = new Csv("week_start", "summary", "tips", "motivation", "language", "model", "created_at_utc");
        foreach (var s in await db.WeeklySummaries.AsNoTracking().OrderBy(s => s.WeekStart).ToListAsync(ct))
        {
            csv.Row(s.WeekStart, s.Summary, string.Join(" | ", s.Tips), s.Motivation, s.Language.ToString(), s.Model, s.CreatedAt);
        }

        return csv;
    }

    /// <summary>The user's own reports and ratings with their status and reply; screenshots are left out.</summary>
    private async Task<Csv> FeedbackAsync(CancellationToken ct)
    {
        var csv = new Csv("created_at_utc", "source", "kind", "area", "rating", "reasons", "message", "status", "reply");
        var items = await db.Feedback.AsNoTracking().OrderBy(f => f.CreatedAt).ToListAsync(ct);
        foreach (var f in items)
        {
            csv.Row(f.CreatedAt, f.Source.ToString(), f.Kind.ToString(), f.Area.ToString(), f.Rating?.ToString(),
                f.Reasons == GymTracker.Core.Domain.ParseReason.None ? null : f.Reasons.ToString(), f.Message, f.Status.ToString(), f.Reply);
        }

        return csv;
    }
}
