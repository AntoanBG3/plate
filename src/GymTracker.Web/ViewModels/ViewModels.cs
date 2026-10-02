using GymTracker.Core.Domain;

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

/// <summary>One row in an exercise card: either a logged set or an empty row to fill in.</summary>
public record SetRowViewModel(int SessionExerciseId, bool IsWarmup, SetEntry? Logged, Guid ClientId)
{
    public static SetRowViewModel Blank(int sessionExerciseId, bool isWarmup) => new(sessionExerciseId, isWarmup, null, Guid.NewGuid());

    public static SetRowViewModel For(SetEntry set) => new(set.SessionExerciseId, set.IsWarmup, set, set.ClientId);
}

public record ExercisePickerViewModel(string Title, string PostUrl, string BackUrl, IReadOnlyList<Exercise> Exercises);
