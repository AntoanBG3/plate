using FluentAssertions;
using Plate.Core.Training;

namespace Plate.Core.Tests;

public class NextWorkoutTests
{
    private static readonly TemplateOrder[] Templates = [new(10, 0), new(20, 1), new(30, 2)];

    [Fact]
    public void No_templates_returns_null() =>
        NextWorkout.Pick([], lastCompletedTemplateId: 10).Should().BeNull();

    [Fact]
    public void No_history_returns_first_in_sort_order() =>
        NextWorkout.Pick([new(30, 2), new(10, 0), new(20, 1)], null).Should().Be(10);

    [Fact]
    public void Returns_template_after_last_completed() =>
        NextWorkout.Pick(Templates, 10).Should().Be(20);

    [Fact]
    public void Wraps_around_after_last_template() =>
        NextWorkout.Pick(Templates, 30).Should().Be(10);

    [Fact]
    public void Archived_or_unknown_last_template_falls_back_to_first() =>
        NextWorkout.Pick(Templates, 99).Should().Be(10);

    [Fact]
    public void Single_template_repeats() =>
        NextWorkout.Pick([new(10, 0)], 10).Should().Be(10);
}
