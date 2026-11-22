using System.IO.Compression;
using System.Text;
using Plate.Core.Domain;
using Plate.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Plate.Web.Features.Export;

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
            ["food_requests.csv"] = await FoodRequestsAsync(ct),
            ["maintenance_estimates.csv"] = await EstimatesAsync(ct),
            ["nutrition_targets.csv"] = await NutritionTargetsAsync(ct),
            ["weekly_summaries.csv"] = await WeeklySummariesAsync(ct),
            ["feedback.csv"] = await FeedbackAsync(ct),
            ["label_scans.csv"] = await LabelScansAsync(ct),
            ["private_foods.csv"] = await PrivateFoodsAsync(ct),
            ["saved_meals.csv"] = await SavedMealsAsync(ct),
            ["saved_meal_items.csv"] = await SavedMealItemsAsync(ct),
            ["exercise_settings.csv"] = await ExerciseSettingsAsync(ct),
            ["usual_foods.csv"] = await UsualFoodsAsync(ct),
            ["ai_calls.csv"] = await AiCallsAsync(ct),
            ["recipes.csv"] = await RecipesAsync(ct),
            ["recipe_ingredients.csv"] = await RecipeIngredientsAsync(ct),
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
            "goal", "target_rate_percent_per_week", "protein_g_per_kg", "default_weight_increment_kg",
            "activity_level", "language", "theme", "weekly_summary_enabled", "cut_started_on", "diet_break_until",
            "cut_check_dismissed_until", "bar_weight_kg", "plates_kg", "changelog_seen_at_utc", "onboarded_at_utc");
        foreach (var p in await db.Profiles.AsNoTracking().ToListAsync(ct))
        {
            csv.Row(p.DisplayName, p.TimeZoneId, p.Sex?.ToString(), p.HeightCm, p.BirthYear, p.InitialMaintenanceKcal,
                p.Goal.ToString(), p.TargetRatePercentPerWeek, p.ProteinGramsPerKg, p.DefaultWeightIncrementKg,
                p.ActivityLevel, p.Language, p.Theme, p.WeeklySummaryEnabled, p.CutStartedOn, p.DietBreakUntil,
                p.CutCheckDismissedUntil, p.BarWeightKg, string.Join(";", p.PlatesKg.Select(kg => kg.ToString("G29", System.Globalization.CultureInfo.InvariantCulture))),
                p.ChangelogSeenAt, p.OnboardedAt);
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
        var csv = new Csv("name", "primary_muscle", "secondary_muscles", "equipment", "archived", "exercise_id");
        var custom = await db.Exercises.AsNoTracking().Where(e => e.OwnerUserId != null).OrderBy(e => e.Name).ToListAsync(ct);
        foreach (var e in custom)
        {
            csv.Row(e.Name, e.PrimaryMuscle.ToString(), string.Join(';', e.SecondaryMuscles), e.Equipment.ToString(), e.IsArchived, e.Id);
        }

        return csv;
    }

    private async Task<Csv> PrivateFoodsAsync(CancellationToken ct)
    {
        var csv = new Csv("food_item_id", "name", "name_bg", "brand", "barcode", "kcal_per_100g", "protein_per_100g",
            "carbs_per_100g", "fat_per_100g", "fiber_per_100g", "saturated_fat_per_100g", "default_serving_g", "source", "unit");
        foreach (var f in await db.FoodItems.AsNoTracking().Where(f => f.OwnerUserId != null).OrderBy(f => f.Id).ToListAsync(ct))
        {
            csv.Row(f.Id, f.Name, f.NameBg, f.Brand, f.Barcode, f.KcalPer100g, f.ProteinPer100g, f.CarbsPer100g,
                f.FatPer100g, f.FiberPer100g, f.SaturatedFatPer100g, f.DefaultServingGrams, f.Source, UnitName(f.Unit));
        }
        return csv;
    }

    /// <summary>"g" or "ml": what the amount ("grams") and the per-100 values of a row count in (SPEC 4).</summary>
    private static string UnitName(FoodUnit unit) => unit == FoodUnit.Millilitres ? "ml" : "g";
    private async Task<Csv> AiCallsAsync(CancellationToken ct)
    {
        var csv = new Csv("at_utc", "task", "model", "input_tokens", "output_tokens", "cost_usd", "duration_ms", "failed");
        foreach (var c in await db.AiCalls.AsNoTracking().OrderBy(c => c.At).ThenBy(c => c.Id).ToListAsync(ct))
            // numeric(12,8) reads back with trailing zeros; the dollars are written as OpenRouter gave them.
            csv.Row(c.At, c.Task, c.Model, c.InputTokens, c.OutputTokens, c.Cost?.ToString("0.########", System.Globalization.CultureInfo.InvariantCulture), c.DurationMs, c.Failed);
        return csv;
    }

    /// <summary>Recipes by their food's id; the food itself, with the values per 100 g, is in private_foods.csv.</summary>
    private async Task<Csv> RecipesAsync(CancellationToken ct)
    {
        var csv = new Csv("food_item_id", "name", "weight_when_cooked_g", "updated_at_utc");
        foreach (var r in await db.Recipes.AsNoTracking().Include(r => r.FoodItem).OrderBy(r => r.FoodItemId).ToListAsync(ct))
            csv.Row(r.FoodItemId, r.FoodItem.Name, r.YieldGrams, r.UpdatedAt);
        return csv;
    }

    private async Task<Csv> RecipeIngredientsAsync(CancellationToken ct)
    {
        var csv = new Csv("recipe_food_item_id", "position", "food_item_id", "description", "grams", "kcal_per_100g", "protein_per_100g",
            "carbs_per_100g", "fat_per_100g", "fiber_per_100g", "saturated_fat_per_100g");
        foreach (var i in await db.RecipeIngredients.AsNoTracking().OrderBy(i => i.RecipeFoodItemId).ThenBy(i => i.SortOrder).ToListAsync(ct))
        {
            csv.Row(i.RecipeFoodItemId, i.SortOrder, i.FoodItemId, i.Description, i.Grams, i.KcalPer100g, i.ProteinPer100g, i.CarbsPer100g,
                i.FatPer100g, i.FiberPer100g, i.SaturatedFatPer100g);
        }
        return csv;
    }

    private async Task<Csv> SavedMealsAsync(CancellationToken ct)
    {
        var csv = new Csv("saved_meal_id", "name", "created_at_utc");
        foreach (var m in await db.SavedMeals.AsNoTracking().OrderBy(m => m.Id).ToListAsync(ct))
            csv.Row(m.Id, m.Name, m.CreatedAt);
        return csv;
    }

    private async Task<Csv> SavedMealItemsAsync(CancellationToken ct)
    {
        var csv = new Csv("saved_meal_id", "position", "food_item_id", "description", "grams", "kcal", "protein_g",
            "carbs_g", "fat_g", "fiber_g", "saturated_fat_g", "source", "unit");
        var items = await db.SavedMealItems.AsNoTracking().OrderBy(i => i.SavedMealId).ThenBy(i => i.SortOrder).ThenBy(i => i.Id).ToListAsync(ct);
        foreach (var i in items)
        {
            csv.Row(i.SavedMealId, i.SortOrder, i.FoodItemId, i.Description, i.Grams, i.Kcal, i.ProteinG, i.CarbsG,
                i.FatG, i.FiberG, i.SaturatedFatG, i.Source, UnitName(i.Unit));
        }
        return csv;
    }

    private async Task<Csv> ExerciseSettingsAsync(CancellationToken ct)
    {
        var csv = new Csv("exercise_id", "exercise", "note", "bar_weight_kg");
        var settings = await (from s in db.ExerciseSettings join e in db.Exercises on s.ExerciseId equals e.Id
                              orderby s.ExerciseId select new { s.ExerciseId, e.Name, s.Note, s.BarWeightKg }).ToListAsync(ct);
        foreach (var s in settings)
            csv.Row(s.ExerciseId, s.Name, s.Note, s.BarWeightKg);
        return csv;
    }

    private async Task<Csv> FoodLogAsync(CancellationToken ct)
    {
        var csv = new Csv("date", "meal", "description", "status", "grams", "kcal", "protein_g", "carbs_g", "fat_g", "fiber_g", "saturated_fat_g", "source", "ai_model", "match_model", "created_at_utc", "unit");
        var entries = await db.FoodLog.AsNoTracking().OrderBy(e => e.Date).ThenBy(e => e.CreatedAt).ToListAsync(ct);
        foreach (var e in entries)
        {
            csv.Row(e.Date, e.Meal.ToString(), e.Description, e.Status, e.Grams, e.Kcal, e.ProteinG, e.CarbsG, e.FatG, e.FiberG, e.SaturatedFatG, e.Source.ToString(), e.AiModel, e.MatchModel, e.CreatedAt, UnitName(e.Unit));
        }

        return csv;
    }

    private async Task<Csv> FoodRequestsAsync(CancellationToken ct)
    {
        var csv = new Csv("request_id", "created_at_utc", "redirect");
        foreach (var request in await db.FoodLogRequests.AsNoTracking().OrderBy(x => x.CreatedAt).ThenBy(x => x.RequestId).ToListAsync(ct))
        {
            csv.Row(request.RequestId, request.CreatedAt, request.Location);
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

    private async Task<Csv> NutritionTargetsAsync(CancellationToken ct)
    {
        var csv = new Csv("date", "values");
        foreach (var day in await db.NutritionTargetDays.AsNoTracking().OrderBy(x => x.Date).ToListAsync(ct))
        {
            csv.Row(day.Date, day.Values);
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
                f.Reasons == Plate.Core.Domain.ParseReason.None ? null : f.Reasons.ToString(), f.Message, f.Status.ToString(), f.Reply);
        }

        return csv;
    }

    /// <summary>Which food the account means by words it types (SPEC 6.1).</summary>
    private async Task<Csv> UsualFoodsAsync(CancellationToken ct)
    {
        var csv = new Csv("phrase", "food_item_id", "food", "last_used_at_utc");
        var rows = await (from u in db.UsualFoods.AsNoTracking()
                          join f in db.FoodItems.AsNoTracking() on u.FoodItemId equals f.Id
                          orderby u.Phrase
                          select new { u.Phrase, u.FoodItemId, f.Name, u.LastUsedAt }).ToListAsync(ct);
        foreach (var r in rows)
        {
            csv.Row(r.Phrase, r.FoodItemId, r.Name, r.LastUsedAt);
        }

        return csv;
    }

    /// <summary>What the label model read and what was saved; the photos stay out (SPEC 11).</summary>
    private async Task<Csv> LabelScansAsync(CancellationToken ct)
    {
        var csv = new Csv("created_at_utc", "model", "latency_ms", "error", "edited", "values", "saved");
        var scans = await db.LabelScans.AsNoTracking().OrderBy(s => s.CreatedAt).ToListAsync(ct);
        foreach (var s in scans)
        {
            csv.Row(s.CreatedAt, s.Model, s.LatencyMs, s.Error, s.Edited, s.Values, s.Saved);
        }

        return csv;
    }
}
