using GymTracker.Core.Domain;
using GymTracker.Core.Text;

namespace GymTracker.Web.Features.Feedback;

/// <summary>The labels for feedback choices, as LocalText so they are translated.</summary>
public static class FeedbackText
{
    public static readonly IReadOnlyDictionary<FeedbackKind, LocalText> Kinds = new Dictionary<FeedbackKind, LocalText>
    {
        [FeedbackKind.Problem] = new LocalText("Something's wrong"),
        [FeedbackKind.Idea] = new LocalText("Idea"),
        [FeedbackKind.Other] = new LocalText("Other"),
    };

    /// <summary>The areas in the order and groups the form shows them.</summary>
    public static readonly IReadOnlyList<(LocalText Group, IReadOnlyList<FeedbackArea> Areas)> AreaGroups =
    [
        (new LocalText("Training"), [FeedbackArea.WorkoutLogging, FeedbackArea.Progression, FeedbackArea.RestTimer, FeedbackArea.Templates, FeedbackArea.PlateCalculator]),
        (new LocalText("Food"), [FeedbackArea.TextParsing, FeedbackArea.FoodMatching, FeedbackArea.BarcodeScanning, FeedbackArea.FoodSearch, FeedbackArea.NutritionValues, FeedbackArea.SavedMeals]),
        (new LocalText("Targets and progress"), [FeedbackArea.CaloriesAndMaintenance, FeedbackArea.ProgressCharts]),
        (new LocalText("App"), [FeedbackArea.OfflineAndSync, FeedbackArea.SignIn, FeedbackArea.BulgarianWording, FeedbackArea.LayoutAndDesign, FeedbackArea.Speed, FeedbackArea.Other]),
    ];

    public static readonly IReadOnlyDictionary<FeedbackArea, LocalText> Areas = new Dictionary<FeedbackArea, LocalText>
    {
        [FeedbackArea.WorkoutLogging] = new LocalText("Logging workouts"),
        [FeedbackArea.Progression] = new LocalText("Weight and rep suggestions"),
        [FeedbackArea.RestTimer] = new LocalText("Rest timer"),
        [FeedbackArea.Templates] = new LocalText("Templates"),
        [FeedbackArea.PlateCalculator] = new LocalText("Plate calculator"),
        [FeedbackArea.TextParsing] = new LocalText("Reading typed food"),
        [FeedbackArea.FoodMatching] = new LocalText("Picking the right food"),
        [FeedbackArea.BarcodeScanning] = new LocalText("Barcode scanning"),
        [FeedbackArea.FoodSearch] = new LocalText("Food search"),
        [FeedbackArea.NutritionValues] = new LocalText("Nutrition values"),
        [FeedbackArea.SavedMeals] = new LocalText("Saved meals"),
        [FeedbackArea.CaloriesAndMaintenance] = new LocalText("Calorie target and maintenance"),
        [FeedbackArea.ProgressCharts] = new LocalText("Progress charts"),
        [FeedbackArea.OfflineAndSync] = new LocalText("Offline and sending"),
        [FeedbackArea.SignIn] = new LocalText("Signing in and passkeys"),
        [FeedbackArea.BulgarianWording] = new LocalText("Bulgarian wording"),
        [FeedbackArea.LayoutAndDesign] = new LocalText("Layout and design"),
        [FeedbackArea.Speed] = new LocalText("Speed"),
        [FeedbackArea.Other] = new LocalText("Something else"),
    };

    public static readonly IReadOnlyDictionary<FeedbackStatus, LocalText> Statuses = new Dictionary<FeedbackStatus, LocalText>
    {
        [FeedbackStatus.New] = new LocalText("Sent"),
        [FeedbackStatus.Seen] = new LocalText("Seen"),
        [FeedbackStatus.Planned] = new LocalText("Planned"),
        [FeedbackStatus.Fixed] = new LocalText("Fixed"),
        [FeedbackStatus.WontFix] = new LocalText("Won't change"),
        [FeedbackStatus.NeedsInfo] = new LocalText("Needs more detail"),
    };

    public static readonly IReadOnlyList<(ParseReason Reason, LocalText Label)> Reasons =
    [
        (ParseReason.WrongFood, new LocalText("Wrong food")),
        (ParseReason.WrongAmount, new LocalText("Wrong amount")),
        (ParseReason.WrongValues, new LocalText("Wrong calories or macros")),
        (ParseReason.MissedFood, new LocalText("Missed a food")),
        (ParseReason.ExtraItem, new LocalText("Added something I didn't eat")),
        (ParseReason.SplitOrMerge, new LocalText("Should be split or joined")),
        (ParseReason.LanguageNotUnderstood, new LocalText("Didn't understand my wording")),
    ];
}
