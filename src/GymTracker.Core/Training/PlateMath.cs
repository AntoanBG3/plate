using GymTracker.Core.Domain;

namespace GymTracker.Core.Training;

public static class PlateMath
{
    /// <summary>
    /// The bar to load plates onto: the exercise's own bar weight if set, else the profile's bar for barbell
    /// exercises. Null means the exercise has no plate calculator.
    /// </summary>
    public static decimal? BarFor(Equipment equipment, decimal? exerciseBarKg, decimal profileBarKg) =>
        exerciseBarKg ?? (equipment == Equipment.Barbell ? profileBarKg : null);
}
