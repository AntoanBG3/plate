using GymTracker.Core.Domain;
using GymTracker.Core.Training;
using GymTracker.Web.Features.Analytics;
using GymTracker.Web.Features.Training;

namespace GymTracker.Web.ViewModels;

public record LoginViewModel(string? Email, string? ReturnUrl, string? Error);

public enum JoinState
{
    Form,
    Invalid,
    SignedIn,
}

public record JoinViewModel(string Token, JoinState State, Language Language, string? Name = null, string? Email = null, IReadOnlyList<string>? Errors = null);

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
    string? LastFood,
    GymTracker.Core.Nutrition.DayQuality Quality,
    IReadOnlyList<GymTracker.Core.Nutrition.CutCheckIn> CutChecks,
    DateOnly? DietBreakUntil,
    GymTracker.Web.Features.Summary.WeeklySummaryState Weekly,
    IReadOnlyList<GymTracker.Web.Features.Training.HistoryItem>? DoneToday = null);

public record TrainViewModel(WorkoutSession? ActiveSession, IReadOnlyList<WorkoutTemplate> Templates);

/// <summary>One row in an exercise card: a logged set, or an empty row to fill in, pre-filled from the suggestion when there is one.</summary>
/// <param name="JustSaved">The row comes back from an in-place edit, so it shows "Saved" for a moment.</param>
/// <param name="Number">The working set's position in its card; rows sent alone get theirs from session.js.</param>
/// <param name="Carried">The target is the last set logged, not a suggestion, so a newer logged set replaces it.</param>
public record SetRowViewModel(int SessionExerciseId, bool IsWarmup, SetEntry? Logged, Guid ClientId, SetTarget? Target = null, bool JustSaved = false, int? Number = null, bool Carried = false)
{
    public static SetRowViewModel Blank(int sessionExerciseId, bool isWarmup, SetTarget? target = null) =>
        new(sessionExerciseId, isWarmup, null, Guid.NewGuid(), target);

    public static SetRowViewModel For(SetEntry set) => new(set.SessionExerciseId, set.IsWarmup, set, set.ClientId);

    /// <summary>
    /// The row for a set the phone sent. Blank when that set was deleted meanwhile, or its exercise removed, so a retry
    /// from the offline queue doesn't put it back on screen.
    /// </summary>
    public static SetRowViewModel AfterLog(SetEntry set) =>
        set.DeletedAt is null && set.SessionExercise?.DeletedAt is null ? For(set) : Blank(set.SessionExerciseId, set.IsWarmup);
}

/// <param name="LastTime">The previous session's working sets per session exercise, for the "Last time" line.</param>
public record SessionViewModel(
    WorkoutSession Session,
    IReadOnlyDictionary<int, ProgressionSuggestion?> Suggestions,
    IReadOnlyDictionary<int, UserExerciseSettings> Settings,
    decimal ProfileBarKg,
    IReadOnlyDictionary<int, IReadOnlyList<LoggedSet>> LastTime)
{
    /// <param name="index">The card's place in the session, which decides the Move up and Move down items.</param>
    public ExerciseCardViewModel CardFor(SessionExercise exercise, int index)
    {
        var settings = Settings.GetValueOrDefault(exercise.ExerciseId);
        return new ExerciseCardViewModel(
            exercise,
            Suggestions.GetValueOrDefault(exercise.Id),
            settings?.Note,
            PlateMath.BarFor(exercise.Exercise.Equipment, settings?.BarWeightKg, ProfileBarKg),
            IsFirst: index == 0,
            IsLast: index == Session.Exercises.Count - 1,
            LastTime: LastTime.GetValueOrDefault(exercise.Id));
    }
}

public record ExerciseCardViewModel(
    SessionExercise Exercise,
    ProgressionSuggestion? Suggestion,
    string? Note = null,
    decimal? BarKg = null,
    bool IsFirst = true,
    bool IsLast = true,
    IReadOnlyList<LoggedSet>? LastTime = null)
{
    /// <summary>Targets come from the last set logged in this session rather than a progression suggestion.</summary>
    public bool Carried => Suggestion is not { Sets.Count: > 0 };

    /// <summary>
    /// The target for the n-th working set (0-based), repeating the last one past the planned count. Without a
    /// suggestion (no history yet), the last working set logged in this session, so the next rows start from it.
    /// </summary>
    public SetTarget? TargetFor(int index)
    {
        if (Suggestion is { Sets.Count: > 0 } s)
        {
            return s.Sets[Math.Min(index, s.Sets.Count - 1)];
        }

        var last = Exercise.Sets.Where(x => !x.IsWarmup).MaxBy(x => x.SetNumber);
        return last is null ? null : new SetTarget(last.WeightKg, last.Reps);
    }
}

/// <param name="FirstTimes">Exercises done for the first time: a baseline, listed apart from the records.</param>
public record SummaryViewModel(SessionSummary Summary, IReadOnlyList<PersonalRecord> Records, IReadOnlyList<PersonalRecord> FirstTimes);

public record ProgressViewModel(
    IReadOnlyList<ExerciseOption> Exercises,
    ExerciseOption? Selected,
    IReadOnlyList<E1rmPoint> E1rm,
    VolumeComparison Volume,
    GymTracker.Web.Features.Body.WeightChart Weight,
    IReadOnlyList<GymTracker.Web.Features.Body.CalorieDay> Calories,
    GymTracker.Web.Features.Food.WeekCard? Week,
    int? ProteinMinG);

public record PlatesViewModel(int SessionExerciseId, decimal BarKg, decimal? TargetKg, PlateLoad? Load);

/// <param name="DefaultIncrementKg">The profile's weight jump, used where an exercise leaves its own empty.</param>
public record TemplateEditViewModel(WorkoutTemplate Template, IReadOnlyDictionary<int, UserExerciseSettings> Settings, decimal DefaultIncrementKg);

public record ExerciseSettingsFormViewModel(Exercise Exercise, UserExerciseSettings? Settings, decimal ProfileBarKg, string ReturnUrl);

/// <param name="Multi">Several can be picked at once (adding); Swap picks one.</param>
/// <param name="AlreadyIn">Exercises already in the template or session, marked "Added".</param>
/// <param name="AddTo">"template:{id}" or "session:{id}": where a custom exercise created from here goes.</param>
public record ExercisePickerViewModel(
    GymTracker.Core.Text.LocalText Title,
    string PostUrl,
    string BackUrl,
    IReadOnlyList<Exercise> Exercises,
    bool Multi = false,
    IReadOnlySet<int>? AlreadyIn = null,
    string? AddTo = null);

/// <summary>Reorder mode for a template's or a session's exercises.</summary>
public record ReorderViewModel(string PostUrl, string BackUrl, IReadOnlyList<(int Id, string Name)> Items);

/// <summary>A custom exercise's form: new (Id null) or existing. AddTo is where a new one goes after it is made.</summary>
public record CustomExerciseViewModel(int? Id, string Name, Muscle PrimaryMuscle, Equipment Equipment, IReadOnlyList<Muscle> SecondaryMuscles, (string Kind, int Id)? AddTo);

/// <summary>One exercise's fields in the template editor; after a refused change, what was typed and why.</summary>
public record TemplateExerciseFormViewModel(
    TemplateExercise Item,
    decimal DefaultIncrementKg,
    GymTracker.Web.Features.Training.TemplateExerciseInput? Posted = null,
    GymTracker.Core.Text.LocalText? Error = null,
    bool Saved = false);

public record AdminUnlockViewModel(string ReturnUrl, bool HasPasskey);

/// <summary>Settings' protein note with today's numbers; sent out of band after the profile saves itself.</summary>
public record ProteinNoteViewModel(UserProfile Profile, GymTracker.Web.Features.Food.DailyTargets? Targets, bool OutOfBand = false);

public record TemplateNameFormViewModel(int TemplateId, string Name, GymTracker.Core.Text.LocalText? Error = null, bool Saved = false);

public record QuickAddViewModel(DateOnly Date, Meal Meal);

public record SaveMealFormViewModel(DateOnly Date, Meal Meal, string DefaultName);

public record SavedMealViewModel(SavedMeal Meal, DateOnly Date, Meal LogTo);

/// <param name="Targets">The targets in force on the day shown; null on a past day before the first estimate.</param>
/// <param name="TargetsStart">When that is so, the day targets start (the first estimate).</param>
/// <param name="PreviousDayMeals">The meals the day before has entries in, which can be copied.</param>
public record FoodDayViewModel(
    GymTracker.Web.Features.Food.DayLog Day,
    DateOnly Today,
    GymTracker.Web.Features.Food.DailyTargets? Targets,
    GymTracker.Web.Features.Food.WeekCard? Week,
    DateOnly? TargetsStart = null,
    IReadOnlySet<Meal>? PreviousDayMeals = null);

public record FoodAddViewModel(
    DateOnly Date,
    Meal Meal,
    string Tab,
    string? Text,
    GymTracker.Core.Text.LocalText? Error,
    string? Query,
    IReadOnlyList<GymTracker.Web.Features.Food.FoodMatch> Local,
    GymTracker.Core.Nutrition.ProductSearch? OpenFoodFacts,
    IReadOnlyList<GymTracker.Web.Features.Food.RecentFood> Recents,
    IReadOnlyList<SavedMeal> SavedMeals);

public record ConfirmViewModel(DateOnly Date, Meal Meal, string Text, string? AiModel, IReadOnlyList<GymTracker.Web.Features.Food.ConfirmItem> Items, GymTracker.Core.Text.LocalText? Warning, int? ParseRunId = null);

public record BarcodeViewModel(DateOnly Date, Meal Meal, string Code, GymTracker.Web.Features.Food.BarcodeResult Result);

/// <param name="ModelInfo">The current model's catalog entry, or null when the catalog can't be loaded or no longer lists it.</param>
/// <param name="ModelInfo">Null when the model isn't in the task's list (e.g. OpenRouter's list can't be loaded).</param>
public record AiTaskStatus(GymTracker.Web.Integrations.AiTask Task, string Model, GymTracker.Web.Integrations.OpenRouterModel? ModelInfo);

public record OpenRouterStatus(GymTracker.Web.Integrations.OpenRouterKeyStatus Key, IReadOnlyList<AiTaskStatus> Tasks);

/// <param name="Models">Null when OpenRouter's model list can't be loaded.</param>
public record ModelPickerViewModel(GymTracker.Web.Integrations.AiTask Task, IReadOnlyList<GymTracker.Web.Integrations.OpenRouterModel>? Models, string Current,
    IReadOnlyDictionary<string, GymTracker.Core.Nutrition.ModelStats>? Accuracy = null);

public record FeedbackFormViewModel(GymTracker.Web.Features.Feedback.ReportDraft Draft, Guid ClientId, string? TraceId, string? Page);

public record AdminDashboard(GymTracker.Web.Features.Feedback.Digest Digest, GymTracker.Web.Integrations.OpenRouterKeyStatus Key, IReadOnlyList<AiTaskStatus> Models,
    DateTimeOffset? UnlockEnds);

public record AdminUserRow(string Name, string Email, bool Dev, int Passkeys, DateTimeOffset? LastReport);

public record AdminAuditRow(GymTracker.Web.Data.AdminAudit Entry, string? Email);

public record AdminFeedbackFilter(string Status, string Since, GymTracker.Core.Domain.FeedbackArea? Area, bool Ratings);

public record AdminFeedbackList(AdminFeedbackFilter Filter, IReadOnlyList<GymTracker.Web.Features.Feedback.FeedbackRow> Rows);

public record AdminParseList(bool Flagged, string Since, IReadOnlyList<GymTracker.Web.Features.Feedback.ParseRunDetail> Runs);

public record AdminErrorList(string Since, IReadOnlyList<GymTracker.Core.Domain.ErrorEvent> Errors);

public record AdminEvalPage(GymTracker.Web.Features.Admin.EvalJob? Job, string ParserModel, string MatcherModel, bool HasKey);
