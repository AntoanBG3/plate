using Plate.Core.Domain;
using Plate.Core.Training;
using Plate.Web.Features.Analytics;
using Plate.Web.Features.Training;

namespace Plate.Web.ViewModels;

public record LoginViewModel(string? Email, string? ReturnUrl, string? Error, bool MoveNotice = false);

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
    Plate.Core.Nutrition.Totals Eaten,
    Plate.Web.Features.Food.DailyTargets Targets,
    IReadOnlyList<Plate.Core.Nutrition.TrendPoint> RecentWeights,
    string? LastFood,
    Plate.Core.Nutrition.DayQuality Quality,
    IReadOnlyList<Plate.Core.Nutrition.CutCheckIn> CutChecks,
    DateOnly? DietBreakUntil,
    Plate.Web.Features.Summary.WeeklySummaryState Weekly,
    IReadOnlyList<Plate.Web.Features.Training.HistoryItem>? DoneToday = null);

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
    Plate.Web.Features.Body.WeightChart Weight,
    IReadOnlyList<Plate.Web.Features.Body.CalorieDay> Calories,
    Plate.Web.Features.Food.WeekCard? Week,
    int? ProteinMinG);

public record PlatesViewModel(int SessionExerciseId, decimal BarKg, decimal? TargetKg, PlateLoad? Load);

/// <param name="DefaultIncrementKg">The profile's weight jump, used where an exercise leaves its own empty.</param>
public record TemplateEditViewModel(WorkoutTemplate Template, IReadOnlyDictionary<int, UserExerciseSettings> Settings, decimal DefaultIncrementKg);

public record ExerciseSettingsFormViewModel(Exercise Exercise, UserExerciseSettings? Settings, decimal ProfileBarKg, string ReturnUrl);

/// <param name="Multi">Several can be picked at once (adding); Swap picks one.</param>
/// <param name="AlreadyIn">Exercises already in the template or session, marked "Added".</param>
/// <param name="AddTo">"template:{id}" or "session:{id}": where a custom exercise created from here goes.</param>
public record ExercisePickerViewModel(
    Plate.Core.Text.LocalText Title,
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
    Plate.Web.Features.Training.TemplateExerciseInput? Posted = null,
    Plate.Core.Text.LocalText? Error = null,
    bool Saved = false);

public record AdminUnlockViewModel(string ReturnUrl, bool HasPasskey);

/// <summary>Settings' protein note with today's numbers; sent out of band after the profile saves itself.</summary>
public record ProteinNoteViewModel(UserProfile Profile, Plate.Web.Features.Food.DailyTargets? Targets, bool OutOfBand = false);

public record TemplateNameFormViewModel(int TemplateId, string Name, Plate.Core.Text.LocalText? Error = null, bool Saved = false);

public record QuickAddViewModel(DateOnly Date, Meal Meal);

public record SaveMealFormViewModel(DateOnly Date, Meal Meal, string DefaultName);

public record SavedMealViewModel(SavedMeal Meal, DateOnly Date, Meal LogTo);

/// <param name="Targets">The targets in force on the day shown; null on a past day before the first estimate.</param>
/// <param name="TargetsStart">When that is so, the day targets start (the first estimate).</param>
/// <param name="PreviousDayMeals">The meals the day before has entries in, which can be copied.</param>
public record FoodDayViewModel(
    Plate.Web.Features.Food.DayLog Day,
    DateOnly Today,
    Plate.Web.Features.Food.DailyTargets? Targets,
    Plate.Web.Features.Food.WeekCard? Week,
    DateOnly? TargetsStart = null,
    IReadOnlySet<Meal>? PreviousDayMeals = null);

public record FoodAddViewModel(
    DateOnly Date,
    Meal Meal,
    string Tab,
    string? Text,
    Plate.Core.Text.LocalText? Error,
    string? Query,
    IReadOnlyList<Plate.Web.Features.Food.FoodMatch> Local,
    Plate.Core.Nutrition.ProductSearch? OpenFoodFacts,
    IReadOnlyList<Plate.Web.Features.Food.RecentFood> Recents,
    IReadOnlyList<SavedMeal> SavedMeals,
    bool LabelAvailable = false,
    IReadOnlyDictionary<int, decimal>? LastGrams = null,
    ManualFoodFormModel? Manual = null);

public record ConfirmViewModel(DateOnly Date, Meal Meal, string Text, string? AiModel, IReadOnlyList<Plate.Web.Features.Food.ConfirmItem> Items, Plate.Core.Text.LocalText? Warning, int? ParseRunId = null);

/// <param name="AddedByAnother">The food is a shared manual one another account added (SPEC 7).</param>
/// <param name="LastGrams">This account's last amount for the product (SPEC 5.10), or null.</param>
public record BarcodeViewModel(DateOnly Date, Meal Meal, string Code, Plate.Web.Features.Food.BarcodeResult Result, bool AddedByAnother = false, bool LabelAvailable = false, decimal? LastGrams = null);

/// <summary>The manual food form (SPEC 7), on the Manual tab, after a barcode miss, and filled from a label photo (6.6).</summary>
/// <param name="Barcode">A scanned code, sent as a hidden field after a barcode miss.</param>
/// <param name="BarcodeField">Whether the optional barcode field is shown (everywhere except after a barcode miss).</param>
/// <param name="BarcodeValue">Pre-fills the optional barcode field, e.g. with a code found in the label photo.</param>
/// <param name="Share">The share choice, kept when a label photo redraws the form.</param>
/// <param name="TypedGrams">"You ate" as typed before a label photo; it wins over the label's serving.</param>
/// <param name="TypedServing">"One serving" as typed before a label photo; used when the label prints none.</param>
public record ManualFoodFormModel(
    DateOnly Date,
    Meal Meal,
    string? Barcode,
    string? Name,
    bool BarcodeField,
    Plate.Core.Nutrition.LabelValues? Label = null,
    int? LabelScanId = null,
    bool LabelAvailable = false,
    string? BarcodeValue = null,
    bool Share = true,
    decimal? TypedGrams = null,
    decimal? TypedServing = null,
    string? InvalidField = null,
    bool Submitted = false);

/// <summary>A food the app already has for a barcode in the manual form, offered instead of a new one (SPEC 7).</summary>
/// <param name="Own">The user's own private food; rejecting it clears the barcode, since one account can't have two foods with it.</param>
/// <param name="SharedByYou">A shared food this user added; rejecting it keeps the new one private, as for another account's.</param>
public record KnownBarcodeModel(DateOnly Date, Meal Meal, string Code, FoodItem Item, bool Own, bool AddedByAnother, bool SharedByYou);

/// <param name="ModelInfo">The current model's catalog entry, or null when the catalog can't be loaded or no longer lists it.</param>
/// <param name="ModelInfo">Null when the model isn't in the task's list (e.g. OpenRouter's list can't be loaded).</param>
public record AiTaskStatus(Plate.Web.Integrations.AiTask Task, string Model, Plate.Web.Integrations.OpenRouterModel? ModelInfo);

public record OpenRouterStatus(Plate.Web.Integrations.OpenRouterKeyStatus Key, IReadOnlyList<AiTaskStatus> Tasks);

/// <param name="Models">Null when OpenRouter's model list can't be loaded.</param>
public record ModelPickerViewModel(Plate.Web.Integrations.AiTask Task, IReadOnlyList<Plate.Web.Integrations.OpenRouterModel>? Models, string Current,
    IReadOnlyDictionary<string, Plate.Core.Nutrition.ModelStats>? Accuracy = null);

public record FeedbackFormViewModel(Plate.Web.Features.Feedback.ReportDraft Draft, Guid ClientId, string? TraceId, string? Page);

public record AdminDashboard(Plate.Web.Features.Feedback.Digest Digest, Plate.Web.Integrations.OpenRouterKeyStatus Key, IReadOnlyList<AiTaskStatus> Models,
    DateTimeOffset? UnlockEnds);

public record AdminUserRow(string Name, string Email, bool Dev, int Passkeys, DateTimeOffset? LastReport, string? InviteNote);

/// <summary>An invite just made: the only time its link can be shown.</summary>
public record NewInvite(int Id, string? Note, string Link);

public record AdminInvitesPage(List<Plate.Web.Features.Admin.InviteRow> Invites, NewInvite? Created);

public record AdminAuditRow(Plate.Web.Data.AdminAudit Entry, string? Email);

public record AdminFeedbackFilter(string Status, string Since, Plate.Core.Domain.FeedbackArea? Area, bool Ratings);

public record AdminFeedbackList(AdminFeedbackFilter Filter, IReadOnlyList<Plate.Web.Features.Feedback.FeedbackRow> Rows);

public record AdminParseList(bool Flagged, string Since, IReadOnlyList<Plate.Web.Features.Feedback.ParseRunDetail> Runs);

public record AdminErrorList(string Since, IReadOnlyList<Plate.Core.Domain.ErrorEvent> Errors);

public record AdminLabelList(string Since, bool Edited, bool Failed, IReadOnlyList<Plate.Web.Features.Feedback.LabelScanRow> Scans);

public record AdminEvalPage(Plate.Web.Features.Admin.EvalJob? Job, string ParserModel, string MatcherModel, bool HasKey,
    IReadOnlyList<Plate.Web.Integrations.OpenRouterModel> MatcherModels);
