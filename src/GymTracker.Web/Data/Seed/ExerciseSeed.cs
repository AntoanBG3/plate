using GymTracker.Core.Domain;
using Microsoft.EntityFrameworkCore;
using static GymTracker.Core.Domain.Equipment;
using static GymTracker.Core.Domain.Muscle;

namespace GymTracker.Web.Data.Seed;

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

        // Back
        E("Deadlift", Barbell, Back, Glutes, Hamstrings, Forearms),
        E("Pull-up", Bodyweight, Back, Biceps),
        E("Chin-up", Bodyweight, Back, Biceps),
        E("Lat pulldown", Cable, Back, Biceps),
        E("Barbell row", Barbell, Back, Biceps),
        E("Dumbbell row", Dumbbell, Back, Biceps),
        E("Seated cable row", Cable, Back, Biceps),
        E("Chest-supported row", Machine, Back, Biceps),

        // Shoulders
        E("Overhead press", Barbell, Shoulders, Triceps),
        E("Seated dumbbell shoulder press", Dumbbell, Shoulders, Triceps),
        E("Lateral raise", Dumbbell, Shoulders),
        E("Cable lateral raise", Cable, Shoulders),
        E("Rear delt fly", Dumbbell, Shoulders, Back),
        E("Face pull", Cable, Shoulders, Back),

        // Arms
        E("Barbell curl", Barbell, Biceps, Forearms),
        E("Dumbbell curl", Dumbbell, Biceps, Forearms),
        E("Hammer curl", Dumbbell, Biceps, Forearms),
        E("Cable curl", Cable, Biceps),
        E("Triceps pushdown", Cable, Triceps),
        E("Overhead cable triceps extension", Cable, Triceps),
        E("Skull crusher", Barbell, Triceps),
        E("Close-grip bench press", Barbell, Triceps, Chest, Shoulders),

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

        // Abs
        E("Hanging leg raise", Bodyweight, Abs),
        E("Cable crunch", Cable, Abs),
        E("Plank", Bodyweight, Abs),
    ];

    /// <summary>Adds the shared library if it is empty. Custom exercises are not counted.</summary>
    public static async Task SeedAsync(AppDbContext db, CancellationToken ct)
    {
        if (await db.Exercises.IgnoreQueryFilters().AnyAsync(e => e.OwnerUserId == null, ct))
        {
            return;
        }

        db.Exercises.AddRange(All());
        await db.SaveChangesAsync(ct);
    }
}
