using FluentAssertions;
using GymTracker.Core.Domain;
using GymTracker.Web.Features;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Tests;

/// <summary>Custom exercises (SPEC 7 Settings): one user's own, never seen or changed by the other.</summary>
public class CustomExerciseTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task A_created_exercise_is_listed_for_its_owner_only()
    {
        await using var me = new UserScope(fixture, await fixture.CreateUserAsync());
        await using var other = new UserScope(fixture, await fixture.CreateUserAsync());

        var created = await me.Exercises.CreateAsync("Landmine press", Muscle.Shoulders, Equipment.Barbell, [Muscle.Triceps], default);

        created.OwnerUserId.Should().Be(me.UserId);
        (await me.Exercises.ListAsync(default)).Should().Contain(e => e.Id == created.Id);
        (await other.Exercises.ListAsync(default)).Should().NotContain(e => e.Id == created.Id);
        (await me.Exercises.ListOwnAsync(default)).Should().ContainSingle().Which.SecondaryMuscles.Should().Equal(Muscle.Triceps);
    }

    [Fact]
    public async Task Names_must_be_new_and_of_a_sensible_length()
    {
        await using var me = new UserScope(fixture, await fixture.CreateUserAsync());
        await me.Exercises.CreateAsync("Landmine press", Muscle.Shoulders, Equipment.Barbell, [], default);

        await me.Invoking(s => s.Exercises.CreateAsync("landmine PRESS", Muscle.Chest, Equipment.Other, [], default))
            .Should().ThrowAsync<ValidationException>();
        await me.Invoking(s => s.Exercises.CreateAsync("Bench press", Muscle.Chest, Equipment.Barbell, [], default))
            .Should().ThrowAsync<ValidationException>("the shared library has one");
        await me.Invoking(s => s.Exercises.CreateAsync("  ", Muscle.Chest, Equipment.Other, [], default))
            .Should().ThrowAsync<ValidationException>();
        await me.Invoking(s => s.Exercises.CreateAsync(new string('x', 61), Muscle.Chest, Equipment.Other, [], default))
            .Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task Only_the_owner_can_rename_or_archive_and_the_library_is_read_only()
    {
        await using var me = new UserScope(fixture, await fixture.CreateUserAsync());
        await using var other = new UserScope(fixture, await fixture.CreateUserAsync());
        var mine = await me.Exercises.CreateAsync("Landmine press", Muscle.Shoulders, Equipment.Barbell, [], default);
        var bench = await me.Db.Exercises.FirstAsync(e => e.Name == "Bench press");

        await other.Invoking(s => s.Exercises.RenameAsync(mine.Id, "Mine now", default)).Should().ThrowAsync<NotFoundException>();
        await other.Invoking(s => s.Exercises.ArchiveAsync(mine.Id, default)).Should().ThrowAsync<NotFoundException>();
        await me.Invoking(s => s.Exercises.RenameAsync(bench.Id, "Bench", default)).Should().ThrowAsync<NotFoundException>();
        await me.Invoking(s => s.Exercises.ArchiveAsync(bench.Id, default)).Should().ThrowAsync<NotFoundException>();

        await me.Exercises.RenameAsync(mine.Id, "Landmine press, one arm", default);
        await me.Exercises.ArchiveAsync(mine.Id, default);
        (await me.Exercises.ListAsync(default)).Should().NotContain(e => e.Id == mine.Id);
        (await me.Exercises.ListOwnAsync(default)).Should().BeEmpty();
    }
}
