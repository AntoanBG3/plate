using Plate.Core.Domain;
using Microsoft.EntityFrameworkCore;
using static Plate.Core.Domain.Equipment;
using static Plate.Core.Domain.Muscle;

namespace Plate.Web.Data.Seed;

public static class ExerciseSeed
{
    private static Exercise E(string name, Equipment equipment, Muscle primary, params Muscle[] secondary) =>
        new() { Name = name, Equipment = equipment, PrimaryMuscle = primary, SecondaryMuscles = [.. secondary] };

    public static IReadOnlyList<Exercise> All() =>
    [
        // Chest
        E("Bench press", Barbell, Chest, Triceps, Shoulders),
        E("Incline bench press", Barbell, Chest, Shoulders, Triceps),
        E("Dumbbell bench press", Dumbbell, Chest, Triceps, Shoulders),
        E("Incline dumbbell press", Dumbbell, Chest, Shoulders, Triceps),
        E("Machine chest press", Machine, Chest, Triceps, Shoulders),
        E("Cable fly", Cable, Chest),
        E("Dip", Bodyweight, Chest, Triceps, Shoulders),
        E("Push-up", Bodyweight, Chest, Triceps, Shoulders),
        E("Decline bench press", Barbell, Chest, Triceps, Shoulders),
        E("Smith machine bench press", Machine, Chest, Triceps, Shoulders),
        E("Dumbbell fly", Dumbbell, Chest),
        E("Incline dumbbell fly", Dumbbell, Chest, Shoulders),
        E("Pec deck", Machine, Chest),
        E("Dumbbell pullover", Dumbbell, Chest, Back),

        // Back
        E("Deadlift", Barbell, Back, Glutes, Hamstrings, Forearms),
        E("Pull-up", Bodyweight, Back, Biceps),
        E("Chin-up", Bodyweight, Back, Biceps),
        E("Lat pulldown", Cable, Back, Biceps),
        E("Barbell row", Barbell, Back, Biceps),
        E("Dumbbell row", Dumbbell, Back, Biceps),
        E("Seated cable row", Cable, Back, Biceps),
        E("Chest-supported row", Machine, Back, Biceps),
        E("Neutral-grip pull-up", Bodyweight, Back, Biceps),
        E("Inverted row", Bodyweight, Back, Biceps),
        E("T-bar row", Barbell, Back, Biceps),
        E("Straight-arm pulldown", Cable, Back),
        E("Back extension", Bodyweight, Back, Glutes, Hamstrings),
        E("Barbell shrug", Barbell, Back, Forearms),
        E("Dumbbell shrug", Dumbbell, Back, Forearms),
        E("Sumo deadlift", Barbell, Glutes, Quads, Hamstrings, Back),
        E("Trap bar deadlift", Barbell, Quads, Glutes, Hamstrings, Back),

        // Shoulders
        E("Overhead press", Barbell, Shoulders, Triceps),
        E("Seated dumbbell shoulder press", Dumbbell, Shoulders, Triceps),
        E("Lateral raise", Dumbbell, Shoulders),
        E("Cable lateral raise", Cable, Shoulders),
        E("Rear delt fly", Dumbbell, Shoulders, Back),
        E("Face pull", Cable, Shoulders, Back),
        E("Arnold press", Dumbbell, Shoulders, Triceps),
        E("Machine shoulder press", Machine, Shoulders, Triceps),
        E("Front raise", Dumbbell, Shoulders),
        E("Upright row", Barbell, Shoulders, Back),
        E("Reverse pec deck", Machine, Shoulders, Back),

        // Arms
        E("Barbell curl", Barbell, Biceps, Forearms),
        E("Dumbbell curl", Dumbbell, Biceps, Forearms),
        E("Hammer curl", Dumbbell, Biceps, Forearms),
        E("Cable curl", Cable, Biceps),
        E("EZ-bar curl", Barbell, Biceps, Forearms),
        E("Preacher curl", Barbell, Biceps),
        E("Incline dumbbell curl", Dumbbell, Biceps),
        E("Concentration curl", Dumbbell, Biceps),
        E("Reverse curl", Barbell, Forearms, Biceps),
        E("Wrist curl", Barbell, Forearms),
        E("Triceps pushdown", Cable, Triceps),
        E("Overhead cable triceps extension", Cable, Triceps),
        E("Skull crusher", Barbell, Triceps),
        E("Close-grip bench press", Barbell, Triceps, Chest, Shoulders),
        E("Overhead dumbbell triceps extension", Dumbbell, Triceps),
        E("Triceps kickback", Dumbbell, Triceps),
        E("Bench dip", Bodyweight, Triceps, Chest, Shoulders),
        E("Diamond push-up", Bodyweight, Triceps, Chest, Shoulders),

        // Legs
        E("Squat", Barbell, Quads, Glutes, Hamstrings),
        E("Front squat", Barbell, Quads, Glutes),
        E("Leg press", Machine, Quads, Glutes),
        E("Bulgarian split squat", Dumbbell, Quads, Glutes),
        E("Leg extension", Machine, Quads),
        E("Romanian deadlift", Barbell, Hamstrings, Glutes, Back),
        E("Lying leg curl", Machine, Hamstrings),
        E("Seated leg curl", Machine, Hamstrings),
        E("Hip thrust", Barbell, Glutes, Hamstrings),
        E("Standing calf raise", Machine, Calves),
        E("Seated calf raise", Machine, Calves),
        E("Hack squat", Machine, Quads, Glutes),
        E("Smith machine squat", Machine, Quads, Glutes),
        E("Goblet squat", Dumbbell, Quads, Glutes),
        E("Dumbbell lunge", Dumbbell, Quads, Glutes, Hamstrings),
        E("Stiff-leg deadlift", Barbell, Hamstrings, Glutes, Back),
        E("Dumbbell Romanian deadlift", Dumbbell, Hamstrings, Glutes, Back),
        E("Good morning", Barbell, Hamstrings, Back, Glutes),
        E("Glute bridge", Bodyweight, Glutes, Hamstrings),
        E("Hip abduction machine", Machine, Glutes),
        E("Kettlebell swing", Other, Glutes, Hamstrings, Back),
        E("Leg press calf raise", Machine, Calves),

        // Abs
        E("Hanging leg raise", Bodyweight, Abs),
        E("Cable crunch", Cable, Abs),
        E("Plank", Bodyweight, Abs),
        E("Crunch", Bodyweight, Abs),
        E("Hanging knee raise", Bodyweight, Abs),
        E("Lying leg raise", Bodyweight, Abs),
        E("Russian twist", Bodyweight, Abs),
        E("Side plank", Bodyweight, Abs),
        E("Ab wheel rollout", Other, Abs, Shoulders),
    ];

    /// <summary>
    /// Adds every library exercise the shared library doesn't have yet, matched by name, so exercises added here reach
    /// an existing database. Custom exercises are not counted; archived shared ones are, so they stay archived.
    /// </summary>
    public static async Task SeedAsync(AppDbContext db, CancellationToken ct)
    {
        var existing = await db.Exercises.IgnoreQueryFilters()
            .Where(e => e.OwnerUserId == null)
            .Select(e => e.Name)
            .ToListAsync(ct);
        var missing = All().ExceptBy(existing, e => e.Name).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        db.Exercises.AddRange(missing);
        await db.SaveChangesAsync(ct);
    }
}
