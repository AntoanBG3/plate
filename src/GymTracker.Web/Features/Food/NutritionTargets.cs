using GymTracker.Core.Nutrition;
using GymTracker.Web.Features.Body;
using GymTracker.Web.Features.Profile;

namespace GymTracker.Web.Features.Food;

/// <summary>Calorie and protein targets. Phase 3 uses the starting maintenance estimate; Phase 4 adds the adaptive estimate and goals.</summary>
public record DailyTargets(int? Kcal, int? ProteinG, string? Missing);

public class NutritionTargets(ProfileService profiles, BodyweightService bodyweights, TimeProvider time)
{
    public async Task<DailyTargets> ForTodayAsync(CancellationToken ct)
    {
        var profile = await profiles.GetAsync(ct);
        var weight = (await bodyweights.LatestAsync(ct))?.WeightKg;
        var kcal = Maintenance.Prior(profile, weight, time.GetUtcNow().Year);
        var protein = Maintenance.ProteinTarget(profile, weight);

        string? missing = null;
        if (kcal is null)
        {
            missing = weight is null
                ? "Log your weight and fill in your profile to get a calorie target."
                : "Fill in sex, height and birth year (or a starting maintenance) in Settings to get a calorie target.";
        }
        else if (protein is null)
        {
            missing = "Log your weight to get a protein target.";
        }

        return new DailyTargets(kcal, protein, missing);
    }
}
