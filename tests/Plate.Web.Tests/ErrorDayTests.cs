using FluentAssertions;
using Plate.Core.Domain;
using Plate.Web.Cli;
using Plate.Web.Features.Feedback;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Plate.Web.Tests;

/// <summary>Error groups count their occurrences per day, so a period's total is right (P10, SPEC 11).</summary>
public class ErrorDayTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static ErrorRecorder Recorder(AppFactory app, TimeProvider time) =>
        new(app.Services.GetRequiredService<IServiceScopeFactory>(), time, app.Services.GetRequiredService<IFeedbackNotifier>(), NullLogger<ErrorRecorder>.Instance);

    private static ErrorReport Report(string type) => new(ErrorOrigin.Server, type, "Entry 12 broke", "at Plate.X.Y()", "/food/entries/12", null, "t", "v");

    [Fact]
    public async Task A_period_counts_only_its_own_days()
    {
        await using var app = new AppFactory(fixture);
        var clock = new Clock { Now = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero) };
        var recorder = Recorder(app, clock);
        var type = "DayError" + Guid.NewGuid().ToString("N")[..8];

        await recorder.RecordAsync(Report(type), default);
        await recorder.RecordAsync(Report(type), default);
        clock.Now = clock.Now.AddDays(2);
        await recorder.RecordAsync(Report(type), default);

        await using var db = fixture.CreateContext(null);
        var error = await db.ErrorEvents.SingleAsync(e => e.Type == type);
        error.Count.Should().Be(3, "the lifetime count stays");
        var admin = app.Services.CreateScope().ServiceProvider.GetRequiredService<FeedbackAdmin>();
        (await admin.ErrorCountsSinceAsync([error.Id], new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero), default))[error.Id].Should().Be(1, "the two from the 1st are outside");
        (await admin.ErrorCountsSinceAsync([error.Id], new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), default))[error.Id].Should().Be(3);
    }

    [Fact]
    public async Task Concurrent_reports_lose_no_count()
    {
        await using var app = new AppFactory(fixture);
        var recorder = Recorder(app, TimeProvider.System);
        var type = "RaceError" + Guid.NewGuid().ToString("N")[..8];
        await recorder.RecordAsync(Report(type), default);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => recorder.RecordAsync(Report(type), default))));

        await using var db = fixture.CreateContext(null);
        var error = await db.ErrorEvents.SingleAsync(e => e.Type == type);
        error.Count.Should().Be(21);
        (await db.ErrorEventDays.Where(d => d.ErrorEventId == error.Id).SumAsync(d => d.Count)).Should().Be(21);
    }

    [Fact]
    public async Task Day_counts_older_than_90_days_are_removed()
    {
        await using var app = new AppFactory(fixture);
        var type = "OldError" + Guid.NewGuid().ToString("N")[..8];
        var now = DateTimeOffset.UtcNow;
        await Recorder(app, new Clock { Now = now.AddDays(-100) }).RecordAsync(Report(type), default);
        await Recorder(app, new Clock { Now = now.AddDays(-10) }).RecordAsync(Report(type), default);

        await using var db = fixture.CreateContext(null);
        await Plate.Web.Features.Undo.SoftDeletePurge.PurgeFeedbackAsync(db, now, default);

        var error = await db.ErrorEvents.SingleAsync(e => e.Type == type);
        (await db.ErrorEventDays.Where(d => d.ErrorEventId == error.Id).Select(d => d.Day).ToListAsync())
            .Should().Equal(DateOnly.FromDateTime(now.AddDays(-10).UtcDateTime));
    }

    [Fact]
    public async Task The_errors_command_says_how_often_in_the_period()
    {
        await using var app = new AppFactory(fixture);
        var type = "CliError" + Guid.NewGuid().ToString("N")[..8];
        var now = DateTimeOffset.UtcNow;
        await Recorder(app, new Clock { Now = now.AddDays(-20) }).RecordAsync(Report(type), default);
        await Recorder(app, new Clock { Now = now }).RecordAsync(Report(type), default);
        await Recorder(app, new Clock { Now = now }).RecordAsync(Report(type), default);

        var o = new StringWriter();
        (await FeedbackCommand.RunAsync(app.Services, ["feedback", "errors", "--since", "7d"], o)).Should().Be(0);

        o.ToString().Should().MatchRegex($@"{type} ×3 \(2 in this period\)");
    }
}
