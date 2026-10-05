using FluentAssertions;
using GymTracker.Core.Domain;
using GymTracker.Web.Features.Analytics;
using GymTracker.Web.Features.Export;
using GymTracker.Web.Features.Training;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Tests;

/// <summary>Soft-deleted rows stay out of progression history and the export (spec: undo, section 7).</summary>
[Collection(PostgresCollection.Name)]
public class DeletedRowsTests(PostgresFixture fixture)
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>A finished first session with one 100 kg × 8 bench set, deleted by <paramref name="delete"/>, then the next session's suggestion.</summary>
    private async Task<(UserScope S, GymTracker.Core.Training.ProgressionSuggestion? Next)> NextSuggestionAsync(Func<SessionService, int, int, Task> delete)
    {
        var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var clock = new Clock(new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero));
        var sessions = new SessionService(s.Db, new GymTracker.Web.Data.FixedCurrentUser(s.UserId), new GymTracker.Web.Features.Profile.ProfileLookup(s.Db), clock);
        var bench = await s.Db.Exercises.FirstAsync(e => e.Name == "Bench press");
        var template = await s.Templates.CreateAsync("Push", default);
        await s.Templates.AddExerciseAsync(template.Id, bench.Id, default);

        var first = await sessions.GetAsync((await sessions.StartAsync(template.Id, default)).Id, default);
        var set = await sessions.LogSetAsync(new LogSetInput(Guid.NewGuid(), first.Exercises[0].Id, 100, 8, null, false, null), default);
        await delete(sessions, first.Exercises[0].Id, set.Id);
        await sessions.FinishAsync(first.Id, default);
        s.Db.ChangeTracker.Clear();

        clock.Now = clock.Now.AddDays(3);
        var second = await sessions.GetAsync((await sessions.StartAsync(template.Id, default)).Id, default);
        var suggestions = await new TrainingAnalytics(s.Db, clock).SuggestionsAsync(second, default);
        return (s, suggestions[second.Exercises[0].Id]);
    }

    [Fact]
    public async Task A_deleted_set_is_not_progression_history()
    {
        var (s, next) = await NextSuggestionAsync((sessions, _, setId) => sessions.DeleteSetAsync(setId, default));
        await using var _ = s;
        next.Should().BeNull("the only set was deleted, so there is no history");
    }

    [Fact]
    public async Task Sets_of_a_removed_exercise_are_not_progression_history()
    {
        var (s, next) = await NextSuggestionAsync((sessions, exerciseId, _) => sessions.RemoveExerciseAsync(exerciseId, default));
        await using var _ = s;
        next.Should().BeNull("the exercise was removed from the first session");
    }

    [Fact]
    public async Task The_export_leaves_out_deleted_entries_and_sets()
    {
        var (s, _) = await NextSuggestionAsync((sessions, _, setId) => sessions.DeleteSetAsync(setId, default));
        await using var __ = s;
        await s.FoodLog.AddQuickAsync(new DateOnly(2026, 9, 1), Meal.Lunch, "Gone wrap", 500, 30, 50, 15, null, null, default);
        await s.FoodLog.AddQuickAsync(new DateOnly(2026, 9, 1), Meal.Lunch, "Kept wrap", 400, 30, 40, 10, null, null, default);
        var gone = (await s.FoodLog.DayAsync(new DateOnly(2026, 9, 1), default)).Entries.Single(e => e.Description == "Gone wrap").Id;
        await s.FoodLog.DeleteAsync(gone, default);

        var files = ExportTests.Unzip(await new DataExport(s.Db).ZipAsync(default));

        files["food_log.csv"].Should().Contain("Kept wrap").And.NotContain("Gone wrap");
        files["sets.csv"].Should().NotContain("100,8", "the only logged set was deleted");
    }
}
