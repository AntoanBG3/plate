using System.Text.Json;
using System.Text.Json.Nodes;
using Plate.Core.Domain;
using Plate.Core.Nutrition;
using Plate.Core.Text;
using Plate.Web.Data;
using Plate.Web.Features.Analytics;
using Plate.Web.Features.Body;
using Plate.Web.Features.Localization;
using Plate.Web.Features.Profile;
using Microsoft.EntityFrameworkCore;

namespace Plate.Web.Features.Feedback;

/// <param name="Snapshot">What the report is about, as the user saw it; stored in the report's context.</param>
/// <param name="Description">One line for the form saying what will be attached.</param>
public record AboutSnapshot(JsonNode? Snapshot, LocalText? Description, int? ParseRunId)
{
    public static readonly AboutSnapshot None = new(null, null, null);
}

/// <summary>
/// Snapshots of what a contextual report is about (SPEC 11): enough to replay the case against Core in a test.
/// Rows are read through the user's query filters, so another user's id is not found.
/// </summary>
public class FeedbackSnapshots(AppDbContext db, TrainingAnalytics training, ProfileService profiles, BodyweightService bodyweights)
{
    public async Task<AboutSnapshot> ForAsync(FeedbackSource source, int? id, CancellationToken ct)
    {
        if (id is not { } aboutId)
        {
            return AboutSnapshot.None;
        }

        return source switch
        {
            FeedbackSource.FoodEntry => await FoodEntryAsync(aboutId, ct),
            FeedbackSource.Barcode => await FoodItemAsync(aboutId, ct),
            FeedbackSource.Progression => await ProgressionAsync(aboutId, ct),
            FeedbackSource.Maintenance => await MaintenanceAsync(aboutId, ct),
            _ => AboutSnapshot.None,
        };
    }

    private async Task<AboutSnapshot> FoodEntryAsync(int id, CancellationToken ct)
    {
        var entry = await db.FoodLog.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct) ?? throw new NotFoundException("Entry not found.");
        var snapshot = Node(new
        {
            kind = "foodEntry",
            entry.Id, entry.Date, entry.Meal, entry.Description, entry.Grams, entry.Kcal, entry.ProteinG, entry.CarbsG, entry.FatG,
            entry.FoodItemId, entry.Source, entry.AiModel, entry.MatchModel, entry.ParseRunId,
        });
        return new AboutSnapshot(snapshot, new LocalText("About: {0}", entry.Description), entry.ParseRunId);
    }

    private async Task<AboutSnapshot> FoodItemAsync(int id, CancellationToken ct)
    {
        var food = await db.FoodItems.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id, ct) ?? throw new NotFoundException("Food not found.");
        var snapshot = Node(new
        {
            kind = "foodItem",
            food.Id, food.Name, food.NameBg, food.Brand, food.Barcode, food.Source, shared = food.OwnerUserId is null,
            food.KcalPer100g, food.ProteinPer100g, food.CarbsPer100g, food.FatPer100g, food.FiberPer100g, food.SaturatedFatPer100g, food.DefaultServingGrams,
        });
        return new AboutSnapshot(snapshot, new LocalText("About: {0}", food.DisplayName()), null);
    }

    private async Task<AboutSnapshot> ProgressionAsync(int sessionExerciseId, CancellationToken ct)
    {
        var (exercise, rules, history, suggestion) = await training.SuggestionInputsAsync(sessionExerciseId, ct);
        var snapshot = Node(new
        {
            kind = "progression",
            sessionExerciseId, exercise.ExerciseId, exercise = exercise.Exercise.Name, rules, history,
            suggestion = suggestion is null ? null : new { suggestion.Kind, suggestion.Sets, reason = suggestion.Reason.ToString() },
        });
        return new AboutSnapshot(snapshot, new LocalText("About: the suggestion for {0}", new LocalText(exercise.Exercise.Name)), null);
    }

    private async Task<AboutSnapshot> MaintenanceAsync(int estimateId, CancellationToken ct)
    {
        var estimate = await db.MaintenanceEstimates.AsNoTracking().FirstOrDefaultAsync(e => e.Id == estimateId, ct)
            ?? throw new NotFoundException("Estimate not found.");
        var day = estimate.CalculatedOn;
        var profile = await profiles.GetAsync(ct);
        var weighIns = (await bodyweights.WeighInsAsync(ct)).Where(w => w.Date < day).ToList();
        var from = day.AddDays(-AdaptiveMaintenance.WindowDays);
        var intake = await db.FoodLog
            .Where(e => e.Date >= from && e.Date < day)
            .GroupBy(e => e.Date)
            .Select(g => new { Date = g.Key, Kcal = g.Sum(e => e.Kcal) })
            .OrderBy(x => x.Date)
            .ToListAsync(ct);
        var lastAdaptive = await db.MaintenanceEstimates
            .Where(e => e.Method == MaintenanceMethod.Adaptive && e.CalculatedOn < day)
            .OrderByDescending(e => e.CalculatedOn)
            .Select(e => (int?)e.EstimatedTdeeKcal)
            .FirstOrDefaultAsync(ct);

        // The inputs to AdaptiveMaintenance.Calculate as they were on that day, apart from profile edits made since.
        var snapshot = Node(new
        {
            kind = "maintenance",
            estimate = new
            {
                estimate.Id, estimate.CalculatedOn, estimate.EstimatedTdeeKcal, estimate.TargetKcal, estimate.TargetProteinG, estimate.TrendWeightKg,
                estimate.DaysOfData, estimate.LoggingCompleteness, estimate.Method, estimate.LowConfidence,
            },
            input = new
            {
                today = day,
                priorKcal = Maintenance.Prior(profile, TrendWeight.On(weighIns, day), day.Year),
                lastAdaptiveKcal = lastAdaptive,
                weighIns = weighIns.Where(w => w.Date >= from.AddDays(-60)),
                intakeByDay = intake,
            },
            profile = new { profile.Sex, profile.BirthYear, profile.HeightCm, profile.InitialMaintenanceKcal, profile.Goal, profile.TargetRatePercentPerWeek },
        });
        return new AboutSnapshot(snapshot, new LocalText("About: your maintenance estimate of {0} kcal", estimate.EstimatedTdeeKcal), null);
    }

    private static JsonNode? Node<T>(T value) => JsonSerializer.SerializeToNode(value, FeedbackJson.Options);
}
