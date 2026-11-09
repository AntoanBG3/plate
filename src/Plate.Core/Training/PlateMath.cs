using Plate.Core.Domain;

namespace Plate.Core.Training;

/// <summary>Plates for one side, heaviest first, and what the bar then weighs.</summary>
/// <param name="Exact">True when the total equals the target.</param>
/// <param name="BelowBar">True when the target is lighter than the empty bar.</param>
public record PlateLoad(IReadOnlyList<decimal> PerSide, decimal TotalKg, bool Exact, bool BelowBar);

public static class PlateMath
{
    // Plates are searched in 50 g steps, the smallest common plate step (0.5, 1.25 and 2.5 are all multiples).
    private const decimal Step = 0.05m;

    /// <summary>
    /// The bar to load plates onto: the exercise's own bar weight if set, else the profile's bar for barbell
    /// exercises. Null means the exercise has no plate calculator.
    /// </summary>
    public static decimal? BarFor(Equipment equipment, decimal? exerciseBarKg, decimal profileBarKg) =>
        exerciseBarKg ?? (equipment == Equipment.Barbell ? profileBarKg : null);

    /// <summary>
    /// The heaviest load at or below the target, using the fewest plates (any number of pairs of each plate).
    /// An exact search, not greedy: plates {15, 10} make 20 per side as 10 + 10. The search stops at the heaviest
    /// weight a set can log, so a mistyped target can't make it allocate without bound.
    /// </summary>
    public static PlateLoad PerSide(decimal targetKg, decimal barKg, IEnumerable<decimal> platesKg)
    {
        if (targetKg < barKg)
        {
            return new PlateLoad([], barKg, false, true);
        }

        var plates = platesKg
            .Where(p => p > 0 && p % Step == 0)
            .Distinct()
            .OrderDescending()
            .Select(p => (Kg: p, Units: (int)(p / Step)))
            .ToList();
        var goal = (int)Math.Floor((Math.Min(targetKg, SetRules.MaxWeightKg) - barKg) / 2 / Step);

        // fewest[s] = fewest plates that make exactly s units per side; last[s] = the plate added to get there.
        var fewest = new int[goal + 1];
        var last = new int[goal + 1];
        Array.Fill(fewest, int.MaxValue);
        fewest[0] = 0;
        for (var s = 1; s <= goal; s++)
        {
            for (var i = 0; i < plates.Count; i++)
            {
                var rest = s - plates[i].Units;
                if (rest >= 0 && fewest[rest] != int.MaxValue && fewest[rest] + 1 < fewest[s])
                {
                    fewest[s] = fewest[rest] + 1;
                    last[s] = i;
                }
            }
        }

        var reach = goal;
        while (fewest[reach] == int.MaxValue)
        {
            reach--;
        }

        var perSide = new List<decimal>();
        for (var s = reach; s > 0; s -= plates[last[s]].Units)
        {
            perSide.Add(plates[last[s]].Kg);
        }

        perSide.Sort((a, b) => b.CompareTo(a));
        var total = barKg + 2 * perSide.Sum();
        return new PlateLoad(perSide, total, total == targetKg, false);
    }
}
