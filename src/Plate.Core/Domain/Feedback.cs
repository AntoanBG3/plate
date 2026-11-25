using Plate.Core.Nutrition;

namespace Plate.Core.Domain;

/// <summary>Where a report was sent from (SPEC 11).</summary>
public enum FeedbackSource
{
    Settings,
    ParseRating,
    Barcode,
    Progression,
    Maintenance,
    FoodEntry,
    ErrorPage,
}

public enum FeedbackKind
{
    Problem,
    Idea,
    Other,
}

/// <summary>The part of the app a report is about. The form groups them under Training, Food, Targets and progress, and App.</summary>
public enum FeedbackArea
{
    WorkoutLogging,
    Progression,
    RestTimer,
    Templates,
    PlateCalculator,
    TextParsing,
    FoodMatching,
    BarcodeScanning,
    FoodSearch,
    NutritionValues,
    SavedMeals,
    CaloriesAndMaintenance,
    ProgressCharts,
    OfflineAndSync,
    SignIn,
    BulgarianWording,
    LayoutAndDesign,
    Speed,
    Other,
}

public enum FeedbackRating
{
    Up,
    Down,
}

/// <summary>Why a parse was rated down. Several can apply.</summary>
[Flags]
public enum ParseReason
{
    None = 0,
    WrongFood = 1,
    WrongAmount = 2,
    WrongValues = 4,
    MissedFood = 8,
    ExtraItem = 16,
    SplitOrMerge = 32,
    LanguageNotUnderstood = 64,
}

public enum FeedbackStatus
{
    New,
    Seen,
    Planned,
    Fixed,
    WontFix,
    NeedsInfo,
}

public enum ErrorOrigin
{
    Server,
    Client,
}

/// <summary>
/// One text parse: what the parser and matcher suggested and, once confirmed, what the user saved.
/// The difference is the correction signal (SPEC 11); JSON columns keep the snapshot as it was.
/// </summary>
public class ParseRun
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string Text { get; set; } = "";
    public string? AiModel { get; set; }
    public string? MatchModel { get; set; }
    public int ParserMs { get; set; }
    public int MatchMs { get; set; }

    /// <summary>The namer's time (SPEC 6.1); null for runs from before it was recorded, which is unknown, not zero.</summary>
    public int? NamerMs { get; set; }

    /// <summary>
    /// The estimator's time (SPEC 6.1 step 6), which runs alongside the namer; null when no row needed an estimate or
    /// for runs from before it was recorded.
    /// </summary>
    public int? EstimateMs { get; set; }

    /// <summary>
    /// The whole parse as the user waited for it, parser, matcher, estimator and namer together, whether it worked, timed out
    /// or failed; null for runs from before it was recorded (P9).
    /// </summary>
    public int? TotalMs { get; set; }

    public string? ParserError { get; set; }

    /// <summary>JSON: the parser's items as returned.</summary>
    public string Parsed { get; set; } = "[]";

    /// <summary>JSON: per item the suggested food, confidence, notes, top candidates and the decider's answer.</summary>
    public string Resolved { get; set; } = "[]";

    /// <summary>JSON: what the user saved per item index; null until confirmed.</summary>
    public string? Saved { get; set; }

    public ParseCorrection Corrections { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
}

/// <summary>A report, idea or rating from a user, and the agent's status and reply to it.</summary>
public class Feedback
{
    public const int MaxMessageLength = 2000;
    public const int MaxReplyLength = 1000;

    public int Id { get; set; }

    /// <summary>Generated in the browser so a report retried from the offline queue is saved once.</summary>
    public Guid ClientId { get; set; }

    public string UserId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public FeedbackSource Source { get; set; }
    public FeedbackKind Kind { get; set; }
    public FeedbackArea Area { get; set; }
    public FeedbackRating? Rating { get; set; }
    public ParseReason Reasons { get; set; }
    public string? Message { get; set; }

    /// <summary>JSON: page, app version, device details and a snapshot of what the report is about.</summary>
    public string Context { get; set; } = "{}";

    public int? ParseRunId { get; set; }
    public ParseRun? ParseRun { get; set; }
    public int? ParseItemIndex { get; set; }
    public int? ErrorEventId { get; set; }
    public FeedbackStatus Status { get; set; }
    public string? Reply { get; set; }
    public DateTimeOffset? StatusChangedAt { get; set; }
    public string? FixedInVersion { get; set; }

    /// <summary>When the reporter last saw the reply; older than StatusChangedAt means unread.</summary>
    public DateTimeOffset? ReplySeenAt { get; set; }

    public FeedbackAttachment? Attachment { get; set; }

    public bool HasUnreadReply => Reply is not null && StatusChangedAt is { } changed && (ReplySeenAt is null || ReplySeenAt < changed);
}

/// <summary>A screenshot attached to a report, kept apart so lists never load it.</summary>
public class FeedbackAttachment
{
    public const int MaxBytes = 900 * 1024;

    public int Id { get; set; }
    public int FeedbackId { get; set; }
    public Feedback Feedback { get; set; } = null!;
    public string ContentType { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public byte[] Bytes { get; set; } = [];
}

/// <summary>An unhandled server exception or browser error, one row per fingerprint. Never shown to users.</summary>
public class ErrorEvent
{
    public int Id { get; set; }
    public string Fingerprint { get; set; } = "";
    public ErrorOrigin Origin { get; set; }
    public string Type { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Path { get; set; }
    public string? Stack { get; set; }
    public string? UserId { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public int Count { get; set; }
    public string? LastTraceId { get; set; }
    public string? FirstVersion { get; set; }
    public string? LastVersion { get; set; }
}

/// <summary>A What's new line shown to every user once, in their language.</summary>
/// <summary>
/// How often an error group happened on one UTC day (P10), so a period's total can be added up; the group's own
/// <see cref="ErrorEvent.Count"/> is the lifetime total. Counted from when this table was added.
/// </summary>
public class ErrorEventDay
{
    public int ErrorEventId { get; set; }
    public DateOnly Day { get; set; }
    public int Count { get; set; }
}

public class ChangelogEntry
{
    public const int MaxTextLength = 300;

    public int Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string TextEn { get; set; } = "";
    public string TextBg { get; set; } = "";
    public int? FeedbackId { get; set; }
    public string? Version { get; set; }
}
