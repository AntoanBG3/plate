using GymTracker.Core.Domain;
using GymTracker.Core.Training;
using GymTracker.Web.Features.Analytics;
using GymTracker.Web.Features.Training;

namespace GymTracker.Web.ViewModels;

public record LoginViewModel(string? Email, string? ReturnUrl, string? Error);

public record TodayViewModel(
    string DisplayName,
    DateOnly Today,
    BodyweightEntry? TodayWeight,
    BodyweightEntry? LatestWeight,
    WorkoutSession? ActiveSession,
    WorkoutTemplate? NextTemplate,
    bool HasTemplates,
    GymTracker.Core.Nutrition.Totals Eaten,
    GymTracker.Web.Features.Food.DailyTargets Targets,
    IReadOnlyList<GymTracker.Core.Nutrition.TrendPoint> RecentWeights,
    string? LastFood);

public record TrainViewModel(WorkoutSession? ActiveSession, IReadOnlyList<WorkoutTemplate> Templates);

/// <summary>One row in an exercise card: a logged set, or an empty row to fill in, pre-filled from the suggestion when there is one.</summary>
public record SetRowViewModel(int SessionExerciseId, bool IsWarmup, SetEntry? Logged, Guid ClientId, SetTarget? Target = null)
{
    public static SetRowViewModel Blank(int sessionExerciseId, bool isWarmup, SetTarget? target = null) =>
        new(sessionExerciseId, isWarmup, null, Guid.NewGuid(), target);

    public static SetRowViewModel For(SetEntry set) => new(set.SessionExerciseId, set.IsWarmup, set, set.ClientId);
}

public record SessionViewModel(
    WorkoutSession Session,
    IReadOnlyDictionary<int, ProgressionSuggestion?> Suggestions,
    IReadOnlyDictionary<int, UserExerciseSettings> Settings,
    decimal ProfileBarKg)
{
    public ExerciseCardViewModel CardFor(SessionExercise exercise)
    {
        var settings = Settings.GetValueOrDefault(exercise.ExerciseId);
        return new ExerciseCardViewModel(
            exercise,
            Suggestions.GetValueOrDefault(exercise.Id),
            settings?.Note,
            PlateMath.BarFor(exercise.Exercise.Equipment, settings?.BarWeightKg, ProfileBarKg));
    }
}

public record ExerciseCardViewModel(SessionExercise Exercise, ProgressionSuggestion? Suggestion, string? Note = null, decimal? BarKg = null)
{
    /// <summary>The target for the n-th working set (0-based), repeating the last one past the planned count.</summary>
    public SetTarget? TargetFor(int index) =>
        Suggestion is { Sets.Count: > 0 } s ? s.Sets[Math.Min(index, s.Sets.Count - 1)] : null;
}

public record SummaryViewModel(SessionSummary Summary, IReadOnlyList<PersonalRecord> Records);

public record ProgressViewModel(
    IReadOnlyList<ExerciseOption> Exercises,
    ExerciseOption? Selected,
    IReadOnlyList<E1rmPoint> E1rm,
    VolumeComparison Volume,
    GymTracker.Web.Features.Body.WeightChart Weight,
    IReadOnlyList<GymTracker.Web.Features.Body.CalorieDay> Calories);

public record PlatesViewModel(int SessionExerciseId, decimal BarKg, decimal? TargetKg, PlateLoad? Load);

public record TemplateEditViewModel(WorkoutTemplate Template, IReadOnlyDictionary<int, UserExerciseSettings> Settings);

public record ExerciseSettingsFormViewModel(Exercise Exercise, UserExerciseSettings? Settings, decimal ProfileBarKg, string ReturnUrl);

public record ExercisePickerViewModel(GymTracker.Core.Text.LocalText Title, string PostUrl, string BackUrl, IReadOnlyList<Exercise> Exercises, Muscle? FirstMuscle = null);

public record FoodDayViewModel(GymTracker.Web.Features.Food.DayLog Day, DateOnly Today, GymTracker.Web.Features.Food.DailyTargets Targets);

public record FoodAddViewModel(
    DateOnly Date,
    Meal Meal,
    string Tab,
    string? Text,
    GymTracker.Core.Text.LocalText? Error,
    string? Query,
    IReadOnlyList<GymTracker.Web.Features.Food.FoodMatch> Local,
    GymTracker.Core.Nutrition.ProductSearch? OpenFoodFacts,
    IReadOnlyList<GymTracker.Web.Features.Food.RecentFood> Recents);

public record ConfirmViewModel(DateOnly Date, Meal Meal, string Text, string? AiModel, IReadOnlyList<GymTracker.Web.Features.Food.ConfirmItem> Items, GymTracker.Core.Text.LocalText? Warning);

public record BarcodeViewModel(DateOnly Date, Meal Meal, string Code, GymTracker.Web.Features.Food.BarcodeResult Result);

/// <param name="ModelInfo">The current model's catalog entry, or null when the catalog can't be loaded or no longer lists it.</param>
public record ParserStatus(GymTracker.Web.Integrations.OpenRouterKeyStatus Key, string Model, GymTracker.Web.Integrations.OpenRouterModel? ModelInfo);

/// <param name="Models">Null when OpenRouter's model list can't be loaded.</param>
public record ModelPickerViewModel(IReadOnlyList<GymTracker.Web.Integrations.OpenRouterModel>? Models, string Current);
