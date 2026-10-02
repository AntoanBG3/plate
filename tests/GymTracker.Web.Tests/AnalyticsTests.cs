using FluentAssertions;
using GymTracker.Core.Training;
using GymTracker.Web.Features.Analytics;
using GymTracker.Web.Features.Training;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Tests;

[Collection(PostgresCollection.Name)]
public class AnalyticsTests(PostgresFixture fixture)
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Logs one finished session of the template with the given working sets for the first exercise.</summary>
    private static async Task<int> SessionAsync(UserScope s, SessionService sessions, int templateId, params (decimal Kg, int Reps)[] sets)
    {
        var session = await sessions.StartAsync(templateId, default);
        session = await sessions.GetAsync(session.Id, default);
        await sessions.LogSetAsync(new LogSetInput(Guid.NewGuid(), session.Exercises[0].Id, 40, 10, null, true, null), default);
        foreach (var (kg, reps) in sets)
        {
            await sessions.LogSetAsync(new LogSetInput(Guid.NewGuid(), session.Exercises[0].Id, kg, reps, null, false, null), default);
        }

        await sessions.FinishAsync(session.Id, default);
        s.Db.ChangeTracker.Clear();
        return session.Id;
    }

    private async Task<(UserScope Scope, Clock Clock, SessionService Sessions, TrainingAnalytics Analytics, int TemplateId)> SetupAsync()
    {
        var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var clock = new Clock(new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero));
        var sessions = new SessionService(s.Db, new GymTracker.Web.Data.FixedCurrentUser(s.UserId), new GymTracker.Web.Features.Profile.ProfileLookup(s.Db), clock);
        var bench = await s.Db.Exercises.FirstAsync(e => e.Name == "Bench press");
        var template = await s.Templates.CreateAsync("Upper A", default);
        await s.Templates.AddExerciseAsync(template.Id, bench.Id, default);
        return (s, clock, sessions, new TrainingAnalytics(s.Db, clock), template.Id);
    }

    [Fact]
    public async Task Suggestion_uses_the_previous_sessions_of_that_exercise()
    {
        var (s, clock, sessions, analytics, templateId) = await SetupAsync();
        await using var _ = s;

        await SessionAsync(s, sessions, templateId, (80, 12), (80, 12), (80, 12));
        clock.Now = clock.Now.AddDays(2);
        var current = await sessions.GetAsync((await sessions.StartAsync(templateId, default)).Id, default);

        var suggestion = (await analytics.SuggestionsAsync(current, default))[current.Exercises[0].Id];

        suggestion!.Kind.Should().Be(ProgressionKind.Progress);
        suggestion.Sets[0].WeightKg.Should().Be(82.5m);
    }

    [Fact]
    public async Task First_time_doing_an_exercise_has_no_suggestion_and_no_record()
    {
        var (s, _, sessions, analytics, templateId) = await SetupAsync();
        await using var __ = s;

        var id = await SessionAsync(s, sessions, templateId, (60, 10));
        var session = await sessions.GetAsync(id, default);

        (await analytics.SuggestionsAsync(session, default)).Values.Should().ContainSingle().Which.Should().BeNull();
        (await analytics.RecordsAsync(session, default)).Should().BeEmpty();
    }

    [Fact]
    public async Task Records_compare_against_earlier_sessions_only()
    {
        var (s, clock, sessions, analytics, templateId) = await SetupAsync();
        await using var _ = s;

        await SessionAsync(s, sessions, templateId, (80, 10));      // 106.7
        clock.Now = clock.Now.AddDays(2);
        var better = await SessionAsync(s, sessions, templateId, (85, 8));   // 107.7
        clock.Now = clock.Now.AddDays(2);
        var worse = await SessionAsync(s, sessions, templateId, (70, 10));   // 93.3

        var records = await analytics.RecordsAsync(await sessions.GetAsync(better, default), default);
        records.Should().ContainSingle().Which.Should().Be(new PersonalRecord("Bench press", 107.7m, 106.7m));
        (await analytics.RecordsAsync(await sessions.GetAsync(worse, default), default)).Should().BeEmpty();
    }

    [Fact]
    public async Task E1rm_series_has_one_point_per_session_in_order()
    {
        var (s, clock, sessions, analytics, templateId) = await SetupAsync();
        await using var _ = s;
        var bench = await s.Db.Exercises.FirstAsync(e => e.Name == "Bench press");

        await SessionAsync(s, sessions, templateId, (80, 10), (85, 5));
        clock.Now = clock.Now.AddDays(3);
        await SessionAsync(s, sessions, templateId, (82.5m, 10));

        var series = await analytics.E1rmSeriesAsync(bench.Id, default);
        series.Select(p => p.E1rmKg).Should().Equal(106.7m, 110m);
        series.Select(p => p.Date).Should().Equal(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 4));
    }

    [Fact]
    public async Task A_queued_set_keeps_the_time_it_was_done_unless_the_clock_is_implausible()
    {
        var (s, clock, sessions, _, templateId) = await SetupAsync();
        await using var __ = s;
        var session = await sessions.GetAsync((await sessions.StartAsync(templateId, default)).Id, default);
        var exId = session.Exercises[0].Id;
        clock.Now = clock.Now.AddMinutes(30);

        var done = clock.Now.AddMinutes(-10);
        (await sessions.LogSetAsync(new LogSetInput(Guid.NewGuid(), exId, 80, 8, null, false, done), default))
            .CompletedAt.Should().Be(done);

        (await sessions.LogSetAsync(new LogSetInput(Guid.NewGuid(), exId, 80, 8, null, false, clock.Now.AddHours(3)), default))
            .CompletedAt.Should().Be(clock.Now, "a phone clock in the future is not trusted");
        (await sessions.LogSetAsync(new LogSetInput(Guid.NewGuid(), exId, 80, 8, null, false, clock.Now.AddDays(-2)), default))
            .CompletedAt.Should().Be(clock.Now, "a time before the session started is not trusted");
    }
}
