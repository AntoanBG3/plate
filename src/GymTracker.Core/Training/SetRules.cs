using GymTracker.Core.Text;

namespace GymTracker.Core.Training;

public static class SetRules
{
    public const decimal MaxWeightKg = 9999.99m;
    public const int MaxReps = 200;

    /// <summary>Returns an error message, or null if the set is valid.</summary>
    public static LocalText? Validate(decimal weightKg, int reps, decimal? rpe)
    {
        if (weightKg < 0 || weightKg > MaxWeightKg)
        {
            return "Weight must be between 0 and 9999.99 kg.";
        }

        if (decimal.Round(weightKg, 2) != weightKg)
        {
            return "Weight can have at most two decimal places.";
        }

        if (reps < 0 || reps > MaxReps)
        {
            return new LocalText("Reps must be between 0 and {0}.", MaxReps);
        }

        if (rpe is { } r && (r < 5 || r > 10 || r * 2 != decimal.Truncate(r * 2)))
        {
            return "RPE must be between 5 and 10 in steps of 0.5.";
        }

        return null;
    }
}
