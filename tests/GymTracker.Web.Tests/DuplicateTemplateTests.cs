using FluentAssertions;
using GymTracker.Web.Features;
using GymTracker.Web.Features.Training;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Tests;

public class DuplicateTemplateTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static string Copy(string name) => $"{name} (copy)";

    [Fact]
    public async Task Copies_exercises_and_settings_to_the_end_of_the_list()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var bench = await s.Db.Exercises.FirstAsync(e => e.Name == "Bench press");
        var row = await s.Db.Exercises.FirstAsync(e => e.Name == "Barbell row");
        var original = await s.Templates.CreateAsync("Upper A", default);
        await s.Templates.CreateAsync("Lower A", default);
        await s.Templates.AddExerciseAsync(original.Id, bench.Id, default);
        await s.Templates.AddExerciseAsync(original.Id, row.Id, default);
        var first = (await s.Templates.GetAsync(original.Id, default)).Exercises[0];
        await s.Templates.UpdateExerciseAsync(first.Id, new TemplateExerciseInput(4, 6, 10, 1.25m, 180), default);

        var copy = await s.Templates.DuplicateAsync(original.Id, Copy, default);

        s.Db.ChangeTracker.Clear();
        var loaded = await s.Templates.GetAsync(copy.Id, default);
        loaded.Name.Should().Be("Upper A (copy)");
        loaded.Exercises.Select(e => e.ExerciseId).Should().Equal(bench.Id, row.Id);
        var e0 = loaded.Exercises[0];
        (e0.WorkingSets, e0.RepMin, e0.RepMax, e0.WeightIncrementKg, e0.RestSeconds).Should().Be((4, 6, 10, 1.25m, 180));
        (await s.Templates.ListAsync(default)).Select(t => t.Name).Should().Equal("Upper A", "Lower A", "Upper A (copy)");
        (await s.Templates.GetAsync(original.Id, default)).Exercises.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_long_name_is_shortened_to_fit()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var original = await s.Templates.CreateAsync(new string('a', 100), default);

        var copy = await s.Templates.DuplicateAsync(original.Id, Copy, default);

        copy.Name.Should().HaveLength(100).And.EndWith(" (copy)");
    }

    [Fact]
    public async Task Another_users_template_is_not_found()
    {
        await using var owner = new UserScope(fixture, await fixture.CreateUserAsync());
        var template = await owner.Templates.CreateAsync("Mine", default);
        await using var other = new UserScope(fixture, await fixture.CreateUserAsync());

        await other.Invoking(x => x.Templates.DuplicateAsync(template.Id, Copy, default)).Should().ThrowAsync<NotFoundException>();
    }
}
