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
    bool HasTemplates);

public record TrainViewModel(WorkoutSession? ActiveSession, IReadOnlyList<WorkoutTemplate> Templates);

/// <summary>One row in an exercise card: a logged set, or an empty row to fill in, pre-filled from the suggestion when there is one.</summary>
public record SetRowViewModel(int SessionExerciseId, bool IsWarmup, SetEntry? Logged, Guid ClientId, SetTarget? Target = null)
{
    public static SetRowViewModel Blank(int sessionExerciseId, bool isWarmup, SetTarget? target = null) =>
        new(sessionExerciseId, isWarmup, null, Guid.NewGuid(), target);

    public static SetRowViewModel For(SetEntry set) => new(set.SessionExerciseId, set.IsWarmup, set, set.ClientId);
}

public record SessionViewModel(WorkoutSession Session, IReadOnlyDictionary<int, ProgressionSuggestion?> Suggestions);

public record ExerciseCardViewModel(SessionExercise Exercise, ProgressionSuggestion? Suggestion)
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
    VolumeComparison Volume);

public record ExercisePickerViewModel(string Title, string PostUrl, string BackUrl, IReadOnlyList<Exercise> Exercises);
