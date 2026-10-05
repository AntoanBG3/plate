using FluentAssertions;
using GymTracker.Core.Domain;
using GymTracker.Web.Data;
using GymTracker.Web.Features;
using GymTracker.Web.Features.Training;
using GymTracker.Web.Features.Undo;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Tests;

[Collection(PostgresCollection.Name)]
public class UndoTests(PostgresFixture fixture)
{
    private static readonly DateOnly Day = new(2026, 10, 4);

    private async Task<(UserScope S, int SessionExerciseId)> SessionAsync()
    {
        var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var bench = await s.Db.Exercises.FirstAsync(e => e.Name == "Bench press");
        var template = await s.Templates.CreateAsync("Push", default);
        await s.Templates.AddExerciseAsync(template.Id, bench.Id, default);
        var session = await s.Sessions.GetAsync((await s.Sessions.StartAsync(template.Id, default)).Id, default);
        return (s, session.Exercises[0].Id);
    }

    private static async Task<int> QuickEntryAsync(UserScope s, string name = "Wrap")
    {
        await s.FoodLog.AddQuickAsync(Day, Meal.Lunch, name, 500, 30, 50, 15, null, null, default);
        return (await s.FoodLog.DayAsync(Day, default)).Entries.Single(e => e.Description == name).Id;
    }

    [Fact]
    public async Task A_deleted_entry_leaves_the_day_and_comes_back_on_undo()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var id = await QuickEntryAsync(s);

        await s.FoodLog.DeleteAsync(id, default);
        (await s.FoodLog.DayAsync(Day, default)).Totals.Kcal.Should().Be(0);

        await s.Undo.RestoreAsync(UndoKind.Entry, id, default);
        (await s.FoodLog.DayAsync(Day, default)).Totals.Kcal.Should().Be(500);
    }

    [Fact]
    public async Task A_removed_exercise_hides_its_sets_until_undone()
    {
        var (s, seId) = await SessionAsync();
        await using var _ = s;
        await s.Sessions.LogSetAsync(new LogSetInput(Guid.NewGuid(), seId, 80, 8, null, false, null), default);

        await s.Sessions.RemoveExerciseAsync(seId, default);
        s.Db.ChangeTracker.Clear();
        (await s.Db.Sets.CountAsync(x => x.SessionExerciseId == seId)).Should().Be(0);

        await s.Undo.RestoreAsync(UndoKind.SessionExercise, seId, default);
        s.Db.ChangeTracker.Clear();
        (await s.Db.Sets.CountAsync(x => x.SessionExerciseId == seId)).Should().Be(1);
    }

    [Fact]
    public async Task Restoring_a_set_renumbers_its_siblings()
    {
        var (s, seId) = await SessionAsync();
        await using var _ = s;
        await s.Sessions.LogSetAsync(new LogSetInput(Guid.NewGuid(), seId, 80, 8, null, false, null), default);
        var second = await s.Sessions.LogSetAsync(new LogSetInput(Guid.NewGuid(), seId, 80, 8, null, false, null), default);
        await s.Sessions.DeleteSetAsync(second.Id, default);
        await s.Sessions.LogSetAsync(new LogSetInput(Guid.NewGuid(), seId, 82.5m, 6, null, false, null), default);

        await s.Undo.RestoreAsync(UndoKind.Set, second.Id, default);

        s.Db.ChangeTracker.Clear();
        (await s.Db.Sets.Where(x => x.SessionExerciseId == seId).OrderBy(x => x.SetNumber).Select(x => x.SetNumber).ToListAsync())
            .Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task Restoring_a_template_exercise_keeps_sort_orders_unique()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var template = await s.Templates.CreateAsync("Full", default);
        foreach (var n in new[] { "Bench press", "Barbell row", "Squat" })
        {
            await s.Templates.AddExerciseAsync(template.Id, (await s.Db.Exercises.FirstAsync(e => e.Name == n)).Id, default);
        }

        var middle = (await s.Templates.GetAsync(template.Id, default)).Exercises[1].Id;
        await s.Templates.RemoveExerciseAsync(middle, default);
        s.Db.ChangeTracker.Clear();
        await s.Templates.AddExerciseAsync(template.Id, (await s.Db.Exercises.FirstAsync(e => e.Name == "Deadlift")).Id, default);

        await s.Undo.RestoreAsync(UndoKind.TemplateExercise, middle, default);

        s.Db.ChangeTracker.Clear();
        var orders = (await s.Templates.GetAsync(template.Id, default)).Exercises.Select(e => e.SortOrder).ToList();
        orders.Should().OnlyHaveUniqueItems().And.HaveCount(4);
    }

    [Fact]
    public async Task Undo_is_idempotent()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var id = await QuickEntryAsync(s);
        await s.FoodLog.DeleteAsync(id, default);

        await s.Undo.RestoreAsync(UndoKind.Entry, id, default);
        await s.Undo.RestoreAsync(UndoKind.Entry, id, default);

        (await s.FoodLog.DayAsync(Day, default)).Entries.Should().ContainSingle();
    }

    [Fact]
    public async Task Undo_after_ten_minutes_is_refused()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var id = await QuickEntryAsync(s);
        await s.FoodLog.DeleteAsync(id, default);
        await s.Db.FoodLog.IgnoreQueryFilters([AppDbContext.SoftDeleteFilter]).Where(e => e.Id == id)
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.DeletedAt, DateTimeOffset.UtcNow.AddMinutes(-11)));
        s.Db.ChangeTracker.Clear();

        await s.Invoking(x => x.Undo.RestoreAsync(UndoKind.Entry, id, default)).Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task Undo_of_another_users_row_is_not_found()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var id = await QuickEntryAsync(s);
        await s.FoodLog.DeleteAsync(id, default);
        await using var other = new UserScope(fixture, await fixture.CreateUserAsync());

        await other.Invoking(x => x.Undo.RestoreAsync(UndoKind.Entry, id, default)).Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Purge_removes_only_rows_deleted_more_than_a_day_ago()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var old = await QuickEntryAsync(s, "Old");
        var recent = await QuickEntryAsync(s, "Recent");
        await s.FoodLog.DeleteAsync(old, default);
        await s.FoodLog.DeleteAsync(recent, default);
        var now = DateTimeOffset.UtcNow;
        await s.Db.FoodLog.IgnoreQueryFilters([AppDbContext.SoftDeleteFilter]).Where(e => e.Id == old)
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.DeletedAt, now.AddDays(-2)));

        await using (var system = fixture.CreateContext(null))
        {
            await SoftDeletePurge.PurgeAsync(system, now - SoftDeletePurge.Keep, default);
        }

        var left = await s.Db.FoodLog.IgnoreQueryFilters([AppDbContext.SoftDeleteFilter]).Where(e => e.Id == old || e.Id == recent).Select(e => e.Id).ToListAsync();
        left.Should().Equal(recent);
    }
}
