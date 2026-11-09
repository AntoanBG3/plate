using FluentAssertions;
using Plate.Web.Features;
using Plate.Web.Features.Training;
using Microsoft.EntityFrameworkCore;

namespace Plate.Web.Tests;

public class TrainingTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private async Task<(UserScope Scope, int TemplateId)> UserWithTemplateAsync()
    {
        var scope = new UserScope(fixture, await fixture.CreateUserAsync());
        var bench = await scope.Db.Exercises.FirstAsync(e => e.Name == "Bench press");
        var row = await scope.Db.Exercises.FirstAsync(e => e.Name == "Barbell row");
        var template = await scope.Templates.CreateAsync("Upper A", default);
        await scope.Templates.AddExerciseAsync(template.Id, bench.Id, default);
        await scope.Templates.AddExerciseAsync(template.Id, row.Id, default);
        return (scope, template.Id);
    }

    [Fact]
    public async Task Repeating_a_set_with_the_same_client_id_does_not_duplicate_it()
    {
        var (s, templateId) = await UserWithTemplateAsync();
        await using var _ = s;
        var session = await s.Sessions.GetAsync((await s.Sessions.StartAsync(templateId, default)).Id, default);
        var input = new LogSetInput(Guid.NewGuid(), session.Exercises[0].Id, 80, 8, null, false, null);

        var first = await s.Sessions.LogSetAsync(input, default);
        var second = await s.Sessions.LogSetAsync(input, default);

        second.Id.Should().Be(first.Id);
        (await s.Db.Sets.CountAsync(x => x.SessionExerciseId == input.SessionExerciseId)).Should().Be(1);
    }

    [Fact]
    public async Task Concurrent_retries_of_the_same_set_store_one_row()
    {
        var (s, templateId) = await UserWithTemplateAsync();
        await using var _ = s;
        var session = await s.Sessions.GetAsync((await s.Sessions.StartAsync(templateId, default)).Id, default);
        var input = new LogSetInput(Guid.NewGuid(), session.Exercises[0].Id, 80, 8, null, false, null);

        // Separate contexts, like separate HTTP requests.
        var scopes = Enumerable.Range(0, 5).Select(_ => new UserScope(fixture, s.UserId)).ToList();
        var results = await Task.WhenAll(scopes.Select(x => x.Sessions.LogSetAsync(input, default)));
        foreach (var x in scopes)
        {
            await x.DisposeAsync();
        }

        results.Select(r => r.Id).Distinct().Should().ContainSingle();
        (await s.Db.Sets.CountAsync(x => x.ClientId == input.ClientId)).Should().Be(1);
    }

    [Fact]
    public async Task Set_numbers_count_warmups_and_working_sets_separately()
    {
        var (s, templateId) = await UserWithTemplateAsync();
        await using var _ = s;
        var session = await s.Sessions.GetAsync((await s.Sessions.StartAsync(templateId, default)).Id, default);
        var exId = session.Exercises[0].Id;

        var w1 = await s.Sessions.LogSetAsync(new LogSetInput(Guid.NewGuid(), exId, 40, 10, null, true, null), default);
        var s1 = await s.Sessions.LogSetAsync(new LogSetInput(Guid.NewGuid(), exId, 80, 8, null, false, null), default);
        var w2 = await s.Sessions.LogSetAsync(new LogSetInput(Guid.NewGuid(), exId, 60, 5, null, true, null), default);
        var s2 = await s.Sessions.LogSetAsync(new LogSetInput(Guid.NewGuid(), exId, 80, 8, null, false, null), default);

        new[] { w1.SetNumber, w2.SetNumber, s1.SetNumber, s2.SetNumber }.Should().Equal(1, 2, 1, 2);
    }

    [Fact]
    public async Task Only_one_session_can_be_active()
    {
        var (s, templateId) = await UserWithTemplateAsync();
        await using var _ = s;

        var first = await s.Sessions.StartAsync(templateId, default);
        var second = await s.Sessions.StartAsync(null, default);

        second.Id.Should().Be(first.Id);
        (await s.Db.Sessions.CountAsync(x => x.EndedAt == null)).Should().Be(1);

        await s.Sessions.FinishAsync(first.Id, default);
        var third = await s.Sessions.StartAsync(null, default);
        third.Id.Should().NotBe(first.Id);
    }

    [Fact]
    public async Task Editing_a_template_does_not_change_a_started_session()
    {
        var (s, templateId) = await UserWithTemplateAsync();
        await using var _ = s;
        var template = await s.Templates.GetAsync(templateId, default);
        await s.Templates.UpdateExerciseAsync(template.Exercises[0].Id, new TemplateExerciseInput(4, 6, 10, 1.25m, 180), default);

        var started = await s.Sessions.StartAsync(templateId, default);
        await s.Templates.UpdateExerciseAsync(template.Exercises[0].Id, new TemplateExerciseInput(5, 3, 5, 5m, 240), default);

        s.Db.ChangeTracker.Clear();
        var session = await s.Sessions.GetAsync(started.Id, default);
        var ex = session.Exercises[0];
        (ex.WorkingSets, ex.RepMin, ex.RepMax, ex.WeightIncrementKg, ex.RestSeconds).Should().Be((4, 6, 10, 1.25m, 180));
        session.Exercises[1].WeightIncrementKg.Should().Be(2.5m, "an unset increment falls back to the profile default");
    }

    [Fact]
    public async Task Invalid_template_values_are_rejected()
    {
        var (s, templateId) = await UserWithTemplateAsync();
        await using var _ = s;
        var item = (await s.Templates.GetAsync(templateId, default)).Exercises[0];

        await s.Invoking(x => x.Templates.UpdateExerciseAsync(item.Id, new TemplateExerciseInput(3, 12, 8, null, 120), default))
            .Should().ThrowAsync<ValidationException>();
        await s.Invoking(x => x.Templates.UpdateExerciseAsync(item.Id, new TemplateExerciseInput(0, 8, 12, null, 120), default))
            .Should().ThrowAsync<ValidationException>();
        await s.Invoking(x => x.Templates.CreateAsync("   ", default)).Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task Invalid_sets_are_rejected()
    {
        var (s, templateId) = await UserWithTemplateAsync();
        await using var _ = s;
        var session = await s.Sessions.GetAsync((await s.Sessions.StartAsync(templateId, default)).Id, default);

        await s.Invoking(x => x.Sessions.LogSetAsync(new LogSetInput(Guid.NewGuid(), session.Exercises[0].Id, -5, 8, null, false, null), default))
            .Should().ThrowAsync<ValidationException>();
        await s.Invoking(x => x.Sessions.LogSetAsync(new LogSetInput(Guid.NewGuid(), session.Exercises[0].Id, 80, 8, 11, false, null), default))
            .Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task Reordering_and_removing_template_exercises()
    {
        var (s, templateId) = await UserWithTemplateAsync();
        await using var _ = s;
        var items = (await s.Templates.GetAsync(templateId, default)).Exercises;

        await s.Templates.MoveExerciseAsync(items[1].Id, -1, default);
        s.Db.ChangeTracker.Clear();
        (await s.Templates.GetAsync(templateId, default)).Exercises.Select(e => e.Exercise.Name).Should().Equal("Barbell row", "Bench press");

        await s.Templates.RemoveExerciseAsync(items[0].Id, default);
        s.Db.ChangeTracker.Clear();
        (await s.Templates.GetAsync(templateId, default)).Exercises.Select(e => e.Exercise.Name).Should().Equal("Barbell row");
    }

    [Fact]
    public async Task Last_completed_template_ignores_active_and_freestyle_sessions()
    {
        var (s, templateId) = await UserWithTemplateAsync();
        await using var _ = s;
        var other = await s.Templates.CreateAsync("Lower A", default);

        var first = await s.Sessions.StartAsync(templateId, default);
        await s.Sessions.FinishAsync(first.Id, default);
        var freestyle = await s.Sessions.StartAsync(null, default);
        await s.Sessions.FinishAsync(freestyle.Id, default);
        await s.Sessions.StartAsync(other.Id, default);

        (await s.Sessions.LastCompletedTemplateIdAsync(default)).Should().Be(templateId);
    }

    [Fact]
    public async Task Second_weigh_in_on_a_day_overwrites_the_first()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var day = new DateOnly(2026, 10, 2);

        await s.Bodyweights.SetAsync(day, 82.4m, default);
        await s.Bodyweights.SetAsync(day, 82.1m, default);

        (await s.Db.Bodyweights.Where(b => b.Date == day).Select(b => b.WeightKg).ToListAsync()).Should().Equal(82.1m);
        await s.Invoking(x => x.Bodyweights.SetAsync(day, 5, default)).Should().ThrowAsync<ValidationException>();
    }
}
