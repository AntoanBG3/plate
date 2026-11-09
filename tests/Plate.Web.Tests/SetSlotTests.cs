using FluentAssertions;
using Plate.Web.Features;
using Plate.Web.Features.Training;
using Microsoft.EntityFrameworkCore;

namespace Plate.Web.Tests;

/// <summary>Added set rows are stored on the session exercise, so a reload keeps them (SPEC 7, Phase 11).</summary>
public class SetSlotTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private async Task<(UserScope Scope, int ExerciseId)> SessionExerciseAsync()
    {
        var scope = new UserScope(fixture, await fixture.CreateUserAsync());
        var bench = await scope.Db.Exercises.FirstAsync(e => e.Name == "Bench press");
        var template = await scope.Templates.CreateAsync("Upper A", default);
        await scope.Templates.AddExerciseAsync(template.Id, bench.Id, default);
        var session = await scope.Sessions.GetAsync((await scope.Sessions.StartAsync(template.Id, default)).Id, default);
        return (scope, session.Exercises[0].Id);
    }

    [Fact]
    public async Task Adding_rows_stores_the_working_and_warmup_counts()
    {
        var (s, id) = await SessionExerciseAsync();
        await using var _ = s;

        await s.Sessions.AddSlotAsync(id, warmup: false, default);
        await s.Sessions.AddSlotAsync(id, warmup: true, default);
        await s.Sessions.AddSlotAsync(id, warmup: true, default);

        var item = await s.Db.SessionExercises.SingleAsync(e => e.Id == id);
        item.WorkingSets.Should().Be(4);
        item.WarmupSlots.Should().Be(2);
    }

    [Fact]
    public async Task Removing_a_row_only_removes_an_empty_one_and_keeps_one_working_set()
    {
        var (s, id) = await SessionExerciseAsync();
        await using var _ = s;
        await s.Sessions.LogSetAsync(new LogSetInput(Guid.NewGuid(), id, 60, 10, null, false, null), default);

        (await s.Sessions.RemoveSlotAsync(id, warmup: false, default)).Should().BeTrue();
        (await s.Sessions.RemoveSlotAsync(id, warmup: false, default)).Should().BeTrue();
        (await s.Sessions.RemoveSlotAsync(id, warmup: false, default)).Should().BeFalse("the only row left holds a logged set");
        (await s.Sessions.RemoveSlotAsync(id, warmup: true, default)).Should().BeFalse("there is no empty warm-up row");

        (await s.Db.SessionExercises.SingleAsync(e => e.Id == id)).WorkingSets.Should().Be(1);
    }

    [Fact]
    public async Task Logging_more_sets_than_rows_grows_the_count()
    {
        var (s, id) = await SessionExerciseAsync();
        await using var _ = s;

        // A set sent from the offline queue by an older page, which added rows without telling the server.
        for (var i = 0; i < 4; i++)
        {
            await s.Sessions.LogSetAsync(new LogSetInput(Guid.NewGuid(), id, 60, 10, null, false, null), default);
        }

        await s.Sessions.LogSetAsync(new LogSetInput(Guid.NewGuid(), id, 40, 10, null, true, null), default);

        var item = await s.Db.SessionExercises.SingleAsync(e => e.Id == id);
        item.WorkingSets.Should().Be(4);
        item.WarmupSlots.Should().Be(1);
    }

    [Fact]
    public async Task Another_users_session_exercise_is_not_found()
    {
        var (s, id) = await SessionExerciseAsync();
        await using var _ = s;
        await using var other = new UserScope(fixture, await fixture.CreateUserAsync());

        await other.Invoking(o => o.Sessions.AddSlotAsync(id, warmup: false, default)).Should().ThrowAsync<NotFoundException>();
        await other.Invoking(o => o.Sessions.RemoveSlotAsync(id, warmup: false, default)).Should().ThrowAsync<NotFoundException>();
    }
}
