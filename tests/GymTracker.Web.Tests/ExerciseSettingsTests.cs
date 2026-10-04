using FluentAssertions;
using GymTracker.Web.Features;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Tests;

[Collection(PostgresCollection.Name)]
public class ExerciseSettingsTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Notes_are_per_user()
    {
        await using var a = new UserScope(fixture, await fixture.CreateUserAsync());
        await using var b = new UserScope(fixture, await fixture.CreateUserAsync());
        var press = await a.Db.Exercises.FirstAsync(e => e.Name == "Leg press");

        await a.ExerciseSettings.SaveAsync(press.Id, "  seat 4  ", null, default);
        await b.ExerciseSettings.SaveAsync(press.Id, "seat 7", 0m, default);

        (await a.ExerciseSettings.GetAsync(press.Id, default))!.Note.Should().Be("seat 4");
        var mine = await b.ExerciseSettings.GetAsync(press.Id, default);
        (mine!.Note, mine.BarWeightKg).Should().Be(("seat 7", 0m));
    }

    [Fact]
    public async Task Saving_nothing_removes_the_row()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var squat = await s.Db.Exercises.FirstAsync(e => e.Name == "Squat");
        await s.ExerciseSettings.SaveAsync(squat.Id, "low bar", 20m, default);

        await s.ExerciseSettings.SaveAsync(squat.Id, "   ", null, default);

        (await s.ExerciseSettings.GetAsync(squat.Id, default)).Should().BeNull();
    }

    [Fact]
    public async Task Long_notes_and_impossible_bars_are_rejected()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var squat = await s.Db.Exercises.FirstAsync(e => e.Name == "Squat");

        await s.Invoking(x => x.ExerciseSettings.SaveAsync(squat.Id, new string('x', 201), null, default)).Should().ThrowAsync<ValidationException>();
        await s.Invoking(x => x.ExerciseSettings.SaveAsync(squat.Id, null, 51m, default)).Should().ThrowAsync<ValidationException>();
        await s.Invoking(x => x.ExerciseSettings.SaveAsync(squat.Id, null, -1m, default)).Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task Another_users_custom_exercise_is_not_found()
    {
        await using var owner = new UserScope(fixture, await fixture.CreateUserAsync());
        var custom = new GymTracker.Core.Domain.Exercise { Name = "Secret curl", OwnerUserId = owner.UserId };
        owner.Db.Exercises.Add(custom);
        await owner.Db.SaveChangesAsync();
        await using var other = new UserScope(fixture, await fixture.CreateUserAsync());

        await other.Invoking(x => x.ExerciseSettings.SaveAsync(custom.Id, "mine now", null, default)).Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Exercise_lookup_sees_the_library_and_own_exercises_only()
    {
        await using var owner = new UserScope(fixture, await fixture.CreateUserAsync());
        var custom = new GymTracker.Core.Domain.Exercise { Name = "Secret row", OwnerUserId = owner.UserId };
        owner.Db.Exercises.Add(custom);
        await owner.Db.SaveChangesAsync();
        var squat = await owner.Db.Exercises.FirstAsync(e => e.Name == "Squat");
        await using var other = new UserScope(fixture, await fixture.CreateUserAsync());

        (await owner.Exercises.GetAsync(custom.Id, default)).Name.Should().Be("Secret row");
        (await other.Exercises.GetAsync(squat.Id, default)).Name.Should().Be("Squat");
        await other.Invoking(x => x.Exercises.GetAsync(custom.Id, default)).Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Profile_defaults_to_a_20_kg_bar_and_a_standard_plate_set()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var profile = await s.Db.Profiles.SingleAsync();
        profile.BarWeightKg.Should().Be(20m);
        profile.PlatesKg.Should().Equal(25m, 20m, 15m, 10m, 5m, 2.5m, 1.25m);
    }
}
