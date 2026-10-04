using FluentAssertions;
using GymTracker.Core.Domain;
using GymTracker.Core.Training;

namespace GymTracker.Core.Tests;

public class ExerciseOrderTests
{
    private static Exercise Ex(int id, string name, Muscle muscle) => new() { Id = id, Name = name, PrimaryMuscle = muscle };

    [Fact]
    public void Same_muscle_comes_first_then_the_rest_by_muscle_and_name()
    {
        var bench = Ex(1, "Bench press", Muscle.Chest);
        var all = new[]
        {
            Ex(2, "Squat", Muscle.Quads),
            Ex(3, "Incline dumbbell press", Muscle.Chest),
            bench,
            Ex(4, "Barbell row", Muscle.Back),
            Ex(5, "Cable fly", Muscle.Chest),
        };

        ExerciseOrder.ForSwap(all, bench).Select(e => e.Id).Should().Equal(5, 3, 4, 2);
    }

    [Fact]
    public void The_current_exercise_is_left_out()
    {
        var bench = Ex(1, "Bench press", Muscle.Chest);
        ExerciseOrder.ForSwap([bench], bench).Should().BeEmpty();
    }
}
