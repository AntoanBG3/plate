using FluentAssertions;
using Plate.Core.Domain;
using Plate.Web.Features;
using Plate.Web.Features.Training;
using Microsoft.EntityFrameworkCore;

namespace Plate.Web.Tests;

/// <summary>User A must not be able to read or change anything of user B's by ID (SPEC section 9).</summary>
public class UserIsolationTests(PostgresFixture fixture) : IAsyncLifetime, IClassFixture<PostgresFixture>
{
    private string userA = "";
    private BData b = null!;

    private sealed record BData(int TemplateId, int TemplateExerciseId, int SessionId, int SessionExerciseId, int SetId, Guid SetClientId, int CustomExerciseId, DateOnly WeighInDate);

    public async Task InitializeAsync()
    {
        userA = await fixture.CreateUserAsync();
        var userB = await fixture.CreateUserAsync();

        await using var asB = new UserScope(fixture, userB);
        var bench = await asB.Db.Exercises.FirstAsync(e => e.Name == "Bench press");
        var custom = new Exercise { Name = "B's secret move", PrimaryMuscle = Muscle.Chest, Equipment = Equipment.Other, OwnerUserId = userB };
        asB.Db.Exercises.Add(custom);
        await asB.Db.SaveChangesAsync();

        var template = await asB.Templates.CreateAsync("B upper", default);
        await asB.Templates.AddExerciseAsync(template.Id, bench.Id, default);
        template = await asB.Templates.GetAsync(template.Id, default);

        var session = await asB.Sessions.StartAsync(template.Id, default);
        session = await asB.Sessions.GetAsync(session.Id, default);
        var clientId = Guid.NewGuid();
        var set = await asB.Sessions.LogSetAsync(new LogSetInput(clientId, session.Exercises[0].Id, 100, 5, null, false, null), default);

        var date = new DateOnly(2026, 10, 1);
        await asB.Bodyweights.SetAsync(date, 90, default);

        b = new BData(template.Id, template.Exercises[0].Id, session.Id, session.Exercises[0].Id, set.Id, clientId, custom.Id, date);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private UserScope AsA() => new(fixture, userA);

    [Fact]
    public async Task Cannot_see_parse_runs_feedback_or_screenshots()
    {
        var userB = await fixture.CreateUserAsync();
        await using (var asB = new UserScope(fixture, userB))
        {
            var outcome = await asB.ParseRuns.ParseAsync("a banana", default);
            asB.Db.Feedback.Add(new Feedback
            {
                ClientId = Guid.NewGuid(), UserId = userB, CreatedAt = DateTimeOffset.UtcNow, Message = "B's report", ParseRunId = outcome.RunId,
                Attachment = new FeedbackAttachment { ContentType = "image/png", Bytes = [1, 2, 3] },
            });
            await asB.Db.SaveChangesAsync();
        }

        await using var a = AsA();
        (await a.Db.ParseRuns.AnyAsync(r => r.UserId == userB)).Should().BeFalse();
        (await a.Db.Feedback.AnyAsync(f => f.UserId == userB)).Should().BeFalse();
        (await a.Db.FeedbackAttachments.AnyAsync(x => x.Feedback.UserId == userB)).Should().BeFalse();
        (await a.Db.ParseRuns.IgnoreQueryFilters().AnyAsync(r => r.UserId == userB)).Should().BeTrue("the rows exist; only the filter hides them");
    }

    [Fact]
    public async Task Export_contains_none_of_the_other_users_data()
    {
        await using var a = AsA();

        var files = ExportTests.Unzip(await new Plate.Web.Features.Export.DataExport(a.Db).ZipAsync(default));

        var all = string.Concat(files.Values);
        all.Should().NotContain("B's secret move").And.NotContain("B upper").And.NotContain("Bench press").And.NotContain("2026-10-01");
        files["profile.csv"].Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(2, "a header and A's own profile");
    }

    [Fact]
    public async Task Cannot_read_sessions_sets_templates_or_weigh_ins()
    {
        await using var a = AsA();

        await a.Invoking(x => x.Sessions.GetAsync(b.SessionId, default)).Should().ThrowAsync<NotFoundException>();
        await a.Invoking(x => x.Sessions.SummaryAsync(b.SessionId, default)).Should().ThrowAsync<NotFoundException>();
        await a.Invoking(x => x.Sessions.GetExerciseAsync(b.SessionExerciseId, default)).Should().ThrowAsync<NotFoundException>();
        await a.Invoking(x => x.Templates.GetAsync(b.TemplateId, default)).Should().ThrowAsync<NotFoundException>();

        (await a.Sessions.GetActiveAsync(default)).Should().BeNull();
        (await a.Sessions.HistoryAsync(100, default)).Should().BeEmpty();
        (await a.Templates.ListAsync(default)).Should().BeEmpty();
        (await a.Bodyweights.GetForDayAsync(b.WeighInDate, default)).Should().BeNull();
        (await a.Bodyweights.LatestAsync(default)).Should().BeNull();
        (await a.Db.Sets.AnyAsync(s => s.Id == b.SetId)).Should().BeFalse();
        (await a.Exercises.ListAsync(default)).Should().NotContain(e => e.Id == b.CustomExerciseId);
    }

    [Fact]
    public async Task Cannot_modify_sets()
    {
        await using (var a = AsA())
        {
            await a.Invoking(x => x.Sessions.UpdateSetAsync(b.SetId, 1, 1, null, default)).Should().ThrowAsync<NotFoundException>();
            await a.Invoking(x => x.Sessions.DeleteSetAsync(b.SetId, default)).Should().ThrowAsync<NotFoundException>();
            await a.Invoking(x => x.Sessions.LogSetAsync(new LogSetInput(Guid.NewGuid(), b.SessionExerciseId, 1, 1, null, false, null), default))
                .Should().ThrowAsync<NotFoundException>();

            // Replaying B's ClientId must not hand back B's set.
            await a.Invoking(x => x.Sessions.LogSetAsync(new LogSetInput(b.SetClientId, b.SessionExerciseId, 1, 1, null, false, null), default))
                .Should().ThrowAsync<NotFoundException>();
        }

        await using var check = fixture.CreateContext(null);
        var set = await check.Sets.IgnoreQueryFilters().SingleAsync(s => s.Id == b.SetId);
        set.WeightKg.Should().Be(100);
        set.Reps.Should().Be(5);
        (await check.Sets.IgnoreQueryFilters().CountAsync(s => s.SessionExerciseId == b.SessionExerciseId)).Should().Be(1);
    }

    [Fact]
    public async Task Cannot_modify_sessions()
    {
        await using (var a = AsA())
        {
            var bench = await a.Db.Exercises.FirstAsync(e => e.Name == "Bench press");
            await a.Invoking(x => x.Sessions.AddExerciseAsync(b.SessionId, bench.Id, default)).Should().ThrowAsync<NotFoundException>();
            await a.Invoking(x => x.Sessions.MoveExerciseAsync(b.SessionExerciseId, 1, default)).Should().ThrowAsync<NotFoundException>();
            await a.Invoking(x => x.Sessions.RemoveExerciseAsync(b.SessionExerciseId, default)).Should().ThrowAsync<NotFoundException>();
            await a.Invoking(x => x.Sessions.SetNotesAsync(b.SessionId, "hi", default)).Should().ThrowAsync<NotFoundException>();
            await a.Invoking(x => x.Sessions.FinishAsync(b.SessionId, default)).Should().ThrowAsync<NotFoundException>();
            await a.Invoking(x => x.Sessions.DeleteAsync(b.SessionId, default)).Should().ThrowAsync<NotFoundException>();
            await a.Invoking(x => x.Sessions.StartAsync(b.TemplateId, default)).Should().ThrowAsync<NotFoundException>();
        }

        await using var check = fixture.CreateContext(null);
        var session = await check.Sessions.IgnoreQueryFilters().Include(s => s.Exercises).SingleAsync(s => s.Id == b.SessionId);
        session.EndedAt.Should().BeNull();
        session.Notes.Should().BeNull();
        session.Exercises.Should().ContainSingle();
    }

    [Fact]
    public async Task Cannot_modify_templates()
    {
        await using (var a = AsA())
        {
            var bench = await a.Db.Exercises.FirstAsync(e => e.Name == "Bench press");
            await a.Invoking(x => x.Templates.RenameAsync(b.TemplateId, "mine", default)).Should().ThrowAsync<NotFoundException>();
            await a.Invoking(x => x.Templates.ArchiveAsync(b.TemplateId, default)).Should().ThrowAsync<NotFoundException>();
            await a.Invoking(x => x.Templates.AddExerciseAsync(b.TemplateId, bench.Id, default)).Should().ThrowAsync<NotFoundException>();
            await a.Invoking(x => x.Templates.UpdateExerciseAsync(b.TemplateExerciseId, new TemplateExerciseInput(1, 1, 1, null, 0), default))
                .Should().ThrowAsync<NotFoundException>();
            await a.Invoking(x => x.Templates.RemoveExerciseAsync(b.TemplateExerciseId, default)).Should().ThrowAsync<NotFoundException>();

            // A's own template can't use B's custom exercise either.
            var mine = await a.Templates.CreateAsync("A upper", default);
            await a.Invoking(x => x.Templates.AddExerciseAsync(mine.Id, b.CustomExerciseId, default)).Should().ThrowAsync<NotFoundException>();
        }

        await using var check = fixture.CreateContext(null);
        var template = await check.Templates.IgnoreQueryFilters().Include(t => t.Exercises).SingleAsync(t => t.Id == b.TemplateId);
        template.Name.Should().Be("B upper");
        template.IsArchived.Should().BeFalse();
        template.Exercises.Should().ContainSingle().Which.WorkingSets.Should().Be(3);
    }

    [Fact]
    public async Task Weigh_in_on_same_date_does_not_touch_other_user()
    {
        await using (var a = AsA())
        {
            await a.Bodyweights.SetAsync(b.WeighInDate, 70, default);
        }

        await using var check = fixture.CreateContext(null);
        var entries = await check.Bodyweights.IgnoreQueryFilters().Where(w => w.Date == b.WeighInDate && (w.UserId == userA || w.WeightKg == 90)).ToListAsync();
        entries.Should().Contain(w => w.UserId == userA && w.WeightKg == 70);
        entries.Should().Contain(w => w.UserId != userA && w.WeightKg == 90);
    }
}
