using GymTracker.Core.Domain;
using GymTracker.Core.Text;

namespace GymTracker.Core.Onboarding;

/// <summary>What the setup chat asks for (SPEC 6.4).</summary>
public enum OnboardingField
{
    Name,
    Sex,
    BirthYear,
    Height,
    Weight,
    ActivityLevel,
    Goal,
    Rate,
    Protein,
    StartingMaintenance,
}

/// <summary>Values the model proposes in one turn. Null means it proposed nothing for that field.</summary>
public sealed record OnboardingFields(
    string? Name = null,
    Sex? Sex = null,
    int? BirthYear = null,
    int? HeightCm = null,
    decimal? WeightKg = null,
    ActivityLevel? ActivityLevel = null,
    Goal? Goal = null,
    decimal? RatePercentPerWeek = null,
    decimal? ProteinGramsPerKg = null,
    int? StartingMaintenanceKcal = null)
{
    public static readonly OnboardingFields None = new();
}

/// <summary>A value the model proposed that is outside the profile limits, with the reason, fed back on the next turn.</summary>
public sealed record OnboardingRejection(OnboardingField Field, LocalText Reason);

/// <summary>
/// The profile values gathered so far in the setup chat. The server keeps it, so the model never has to remember;
/// every proposed value is checked against <see cref="ProfileLimits"/> before it is kept.
/// </summary>
public sealed record OnboardingDraft
{
    public const decimal DefaultProteinGramsPerKg = 2.0m;

    public string? Name { get; init; }
    public Sex? Sex { get; init; }
    public int? BirthYear { get; init; }
    public int? HeightCm { get; init; }
    public decimal? WeightKg { get; init; }
    public ActivityLevel? ActivityLevel { get; init; }
    public Goal? Goal { get; init; }
    public decimal? RatePercentPerWeek { get; init; }
    public decimal? ProteinGramsPerKg { get; init; }
    public int? StartingMaintenanceKcal { get; init; }

    /// <summary>The latest weigh-in when the chat started, so finishing only logs a weight that changed.</summary>
    public decimal? LatestWeighInKg { get; init; }

    /// <summary>Starts from what the profile already holds, so an existing user is only asked to confirm it.</summary>
    public static OnboardingDraft FromProfile(UserProfile p, decimal? latestWeighInKg)
    {
        // A new profile is Maintain by default, which is not an answer yet. A profile saved from Settings
        // (the goal is in the same form as sex and height) or set up before has a chosen goal.
        var goalChosen = p.Goal != Domain.Goal.Maintain || p.OnboardedAt is not null || p.Sex is not null || p.HeightCm is not null;
        return new()
        {
            Name = string.IsNullOrWhiteSpace(p.DisplayName) ? null : p.DisplayName.Trim(),
            Sex = p.Sex,
            BirthYear = p.BirthYear,
            HeightCm = p.HeightCm,
            WeightKg = latestWeighInKg,
            ActivityLevel = p.ActivityLevel,
            Goal = goalChosen ? p.Goal : null,
            RatePercentPerWeek = p.Goal == Domain.Goal.Maintain ? null : p.TargetRatePercentPerWeek,
            ProteinGramsPerKg = p.ProteinGramsPerKg,
            StartingMaintenanceKcal = p.InitialMaintenanceKcal,
            LatestWeighInKg = latestWeighInKg,
        };
    }

    /// <summary>Keeps every proposed value inside the limits and reports the rest. A later answer replaces an earlier one.</summary>
    public (OnboardingDraft Draft, IReadOnlyList<OnboardingRejection> Rejected) Merge(OnboardingFields proposed, int currentYear)
    {
        var rejected = new List<OnboardingRejection>();
        var d = this;

        T? Keep<T>(OnboardingField field, T? value, T? current, LocalText? error)
        {
            if (value is null)
            {
                return current;
            }

            if (error is not null)
            {
                rejected.Add(new OnboardingRejection(field, error));
                return current;
            }

            return value;
        }

        var name = proposed.Name?.Trim();
        d = d with
        {
            Name = Keep(OnboardingField.Name, string.IsNullOrEmpty(name) ? null : name, d.Name,
                name is { Length: > ProfileLimits.MaxNameLength } ? new LocalText("Name must be 1 to {0} characters.", ProfileLimits.MaxNameLength) : null),
            Sex = proposed.Sex is { } s && !Enum.IsDefined(s) ? d.Sex : proposed.Sex ?? d.Sex,
            BirthYear = Keep(OnboardingField.BirthYear, proposed.BirthYear, d.BirthYear, ProfileRules.BirthYearError(proposed.BirthYear, currentYear)),
            HeightCm = Keep(OnboardingField.Height, proposed.HeightCm, d.HeightCm, ProfileRules.HeightError(proposed.HeightCm)),
            WeightKg = Keep(OnboardingField.Weight, proposed.WeightKg is { } w ? decimal.Round(w, 1) : null, d.WeightKg, ProfileRules.WeightError(proposed.WeightKg)),
            ActivityLevel = proposed.ActivityLevel is { } a && !Enum.IsDefined(a) ? d.ActivityLevel : proposed.ActivityLevel ?? d.ActivityLevel,
            Goal = proposed.Goal is { } g && !Enum.IsDefined(g) ? d.Goal : proposed.Goal ?? d.Goal,
            ProteinGramsPerKg = Keep(OnboardingField.Protein, proposed.ProteinGramsPerKg, d.ProteinGramsPerKg,
                proposed.ProteinGramsPerKg is { } p ? ProfileRules.ProteinError(p) : null),
            StartingMaintenanceKcal = Keep(OnboardingField.StartingMaintenance, proposed.StartingMaintenanceKcal, d.StartingMaintenanceKcal,
                ProfileRules.MaintenanceError(proposed.StartingMaintenanceKcal)),
        };

        // The rate's limit depends on the goal, which may have arrived in the same turn.
        if (proposed.RatePercentPerWeek is { } rate)
        {
            var error = ProfileRules.RateError(d.Goal ?? Domain.Goal.Cut, rate);
            d = error is null ? d with { RatePercentPerWeek = rate } : d;
            if (error is not null)
            {
                rejected.Add(new OnboardingRejection(OnboardingField.Rate, error));
            }
        }
        else if (d.Goal is { } goal && d.RatePercentPerWeek is { } kept && ProfileRules.RateError(goal, kept) is { } stale)
        {
            // A new goal can make a rate given earlier too fast: ask again rather than keep it.
            d = d with { RatePercentPerWeek = null };
            rejected.Add(new OnboardingRejection(OnboardingField.Rate, stale));
        }

        return (d, rejected);
    }

    /// <summary>What the chat still has to find out before the summary can be shown. Protein and starting maintenance are optional.</summary>
    public IReadOnlyList<OnboardingField> Missing()
    {
        var missing = new List<OnboardingField>();
        void Need(OnboardingField field, bool known)
        {
            if (!known)
            {
                missing.Add(field);
            }
        }

        Need(OnboardingField.Name, Name is not null);
        Need(OnboardingField.Sex, Sex is not null);
        Need(OnboardingField.BirthYear, BirthYear is not null);
        Need(OnboardingField.Height, HeightCm is not null);
        Need(OnboardingField.Weight, WeightKg is not null);
        Need(OnboardingField.ActivityLevel, ActivityLevel is not null);
        Need(OnboardingField.Goal, Goal is not null);
        Need(OnboardingField.Rate, Goal is null or Domain.Goal.Maintain || RatePercentPerWeek is not null);
        return missing;
    }

    public bool IsComplete => Missing().Count == 0;

    /// <summary>The profile this draft describes, for previewing the targets; the original is not changed.</summary>
    public UserProfile ApplyTo(UserProfile profile) => new()
    {
        UserId = profile.UserId,
        DisplayName = Name ?? profile.DisplayName,
        TimeZoneId = profile.TimeZoneId,
        Sex = Sex,
        BirthYear = BirthYear,
        HeightCm = HeightCm,
        ActivityLevel = ActivityLevel,
        Goal = Goal ?? Domain.Goal.Maintain,
        TargetRatePercentPerWeek = Goal is Domain.Goal.Maintain or null ? 0 : RatePercentPerWeek ?? 0,
        ProteinGramsPerKg = ProteinGramsPerKg ?? DefaultProteinGramsPerKg,
        InitialMaintenanceKcal = StartingMaintenanceKcal,
        DefaultWeightIncrementKg = profile.DefaultWeightIncrementKg,
        BarWeightKg = profile.BarWeightKg,
        PlatesKg = profile.PlatesKg,
    };
}
