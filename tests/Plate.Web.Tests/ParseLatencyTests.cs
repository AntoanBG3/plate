using FluentAssertions;
using Plate.Core.Domain;
using Plate.Core.Nutrition;
using Plate.Web.Cli;
using Plate.Web.Features.Feedback;
using Plate.Web.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Plate.Web.Tests;

/// <summary>Each parse run keeps the whole time the user waited, namer included (P9, SPEC 11).</summary>
public class ParseLatencyTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    /// <summary>The fallback parser, slowed down, or failing after the wait.</summary>
    private sealed class SlowParser(TimeSpan delay, bool fail = false) : IFoodParser
    {
        public async Task<FoodParseResult> ParseAsync(string text, CancellationToken ct)
        {
            await Task.Delay(delay, ct);
            return fail ? FoodParseResult.Failed("The model didn't answer in time.") : await new FakeFoodParser().ParseAsync(text, ct);
        }
    }

    private sealed class SlowNamer(TimeSpan delay) : IFoodNamer
    {
        public async Task<IReadOnlyList<FoodName?>?> NameAsync(IReadOnlyList<NameRequest> items, CancellationToken ct)
        {
            await Task.Delay(delay, ct);
            return null;
        }
    }

    /// <summary>Says it has started, then waits a while for the other side to start too.</summary>
    private sealed class Meeting(TaskCompletionSource started, TaskCompletionSource other) : IFoodNamer, IFoodEstimator
    {
        public bool SawOther { get; private set; }

        public async Task<IReadOnlyList<FoodName?>?> NameAsync(IReadOnlyList<NameRequest> items, CancellationToken ct)
        {
            await MeetAsync();
            return null;
        }

        public async Task<FoodEstimates?> EstimateAsync(string text, IReadOnlyList<EstimateRequest> items, CancellationToken ct)
        {
            await MeetAsync();
            return null;
        }

        private async Task MeetAsync()
        {
            started.TrySetResult();
            SawOther = await Task.WhenAny(other.Task, Task.Delay(TimeSpan.FromSeconds(2))) == other.Task;
        }
    }

    private static ParseRunService Service(UserScope s, IFoodParser parser, IFoodNamer namer, IFoodEstimator? estimator = null) =>
        new(s.Db, new Plate.Web.Data.FixedCurrentUser(s.UserId), parser, s.Matcher, namer, TimeProvider.System,
            NullLogger<ParseRunService>.Instance, new MemoryCache(new MemoryCacheOptions()), s.Repeat, estimator);

    [Fact]
    public async Task The_namer_and_the_estimator_run_at_the_same_time()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var namerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var estimatorStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var namer = new Meeting(namerStarted, estimatorStarted);
        var estimator = new Meeting(estimatorStarted, namerStarted);

        // Chicken breast is matched, so it is named; "zzqx" matches nothing, so it is estimated.
        var outcome = await Service(s, new FakeFoodParser(), namer, estimator).ParseAsync("200g chicken breast raw, 50g zzqx", default);

        namer.SawOther.Should().BeTrue("the estimator started while the namer was still waiting");
        estimator.SawOther.Should().BeTrue("the namer started while the estimator was still waiting");
        var run = await s.Db.ParseRuns.SingleAsync(r => r.Id == outcome.RunId);
        run.EstimateMs.Should().NotBeNull();
        run.TotalMs.Should().BeGreaterThanOrEqualTo(run.ParserMs + run.MatchMs + Math.Max(run.NamerMs!.Value, run.EstimateMs!.Value));
    }

    [Fact]
    public async Task A_run_with_nothing_to_estimate_has_no_estimator_time()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var estimator = new StubFoodEstimator();

        var outcome = await Service(s, new FakeFoodParser(), new SlowNamer(TimeSpan.Zero), estimator).ParseAsync("200g chicken breast raw", default);

        estimator.Calls.Should().BeEmpty();
        (await s.Db.ParseRuns.SingleAsync(r => r.Id == outcome.RunId)).EstimateMs.Should().BeNull();
    }

    [Fact]
    public async Task The_total_covers_the_parser_the_matcher_and_the_namer()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());

        var outcome = await Service(s, new SlowParser(TimeSpan.FromMilliseconds(300)), new SlowNamer(TimeSpan.FromMilliseconds(100)))
            .ParseAsync("1 banana", default);

        var run = await s.Db.ParseRuns.SingleAsync(r => r.Id == outcome.RunId);
        // Task.Delay can end a millisecond before the stopwatch's whole milliseconds reach the delay (299 for 300 in a deploy gate).
        run.ParserMs.Should().BeGreaterThanOrEqualTo(295);
        run.NamerMs.Should().BeGreaterThanOrEqualTo(95);
        run.TotalMs.Should().BeGreaterThanOrEqualTo(run.ParserMs + run.MatchMs + run.NamerMs!.Value);
    }

    [Fact]
    public async Task A_failed_parse_records_its_time_too()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());

        var outcome = await Service(s, new SlowParser(TimeSpan.FromMilliseconds(200), fail: true), new SlowNamer(TimeSpan.Zero))
            .ParseAsync("1 banana", default);

        var run = await s.Db.ParseRuns.SingleAsync(r => r.Id == outcome.RunId);
        run.ParserError.Should().NotBeNull();
        run.TotalMs.Should().BeGreaterThanOrEqualTo(200);
    }

    [Fact]
    public async Task Runs_from_before_say_their_total_is_unknown_and_the_digest_counts_only_timed_runs()
    {
        await using var app = new AppFactory(fixture);
        var userId = await fixture.CreateUserAsync();
        var now = DateTimeOffset.UtcNow;
        int oldId;
        await using (var db = fixture.CreateContext(null))
        {
            var old = new ParseRun { UserId = userId, CreatedAt = now.AddMinutes(-5), Text = "old run", ParserMs = 900, MatchMs = 40 };
            db.ParseRuns.Add(old);
            foreach (var total in new[] { 1000, 2000, 3000, 4000 })
            {
                db.ParseRuns.Add(new ParseRun { UserId = userId, CreatedAt = now.AddMinutes(-1), Text = "timed", ParserMs = total / 2, MatchMs = 10, NamerMs = 5, TotalMs = total });
            }

            await db.SaveChangesAsync();
            oldId = old.Id;
        }

        var show = new StringWriter();
        (await FeedbackCommand.RunAsync(app.Services, ["feedback", "parses", "--since", "1d"], show)).Should().Be(0);
        var text = show.ToString();
        text.Should().MatchRegex($"Parse run {oldId} [^\\n]*\\n  parser - 900 ms, matcher ranking 40 ms, total unknown, ");
        text.Should().Contain("namer 5 ms, total 4000 ms");

        // The digest's percentiles come from this class's database, which other tests here add runs to: only the
        // runs above have totals of whole seconds, so check the shape and that untimed runs are said to be left out.
        var digest = new StringWriter();
        (await FeedbackCommand.RunAsync(app.Services, ["feedback", "digest", "--since", "1d"], digest)).Should().Be(0);
        digest.ToString().Should().MatchRegex(@"Parse time, start to Check foods: median \d+ ms, 95th percentile \d+ ms, over \d+ of \d+ parses \(older runs didn't record it\)");
    }

    [Theory]
    [InlineData(new[] { 1000, 2000, 3000, 4000 }, 2000, 4000)]
    [InlineData(new[] { 500 }, 500, 500)]
    public void Percentiles_use_the_nearest_rank(int[] totals, int median, int p95) =>
        ParseTimes.Of(totals).Should().Be(new ParseTimes(totals.Length, median, p95));

    [Fact]
    public void No_timed_runs_give_no_percentiles() => ParseTimes.Of([]).Should().BeNull();
}
