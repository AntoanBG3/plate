using Plate.Core.Domain;

namespace Plate.Core.Training;

public static class ExerciseOrder
{
    /// <summary>Candidates to swap in: the same primary muscle first, then the rest grouped by muscle; each by name.</summary>
    public static IReadOnlyList<Exercise> ForSwap(IEnumerable<Exercise> exercises, Exercise current) =>
        exercises
            .Where(e => e.Id != current.Id)
            .OrderBy(e => e.PrimaryMuscle == current.PrimaryMuscle ? 0 : 1)
            .ThenBy(e => e.PrimaryMuscle)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
