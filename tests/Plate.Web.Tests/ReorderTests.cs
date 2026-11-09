using FluentAssertions;
using Plate.Web.Features;
using Microsoft.EntityFrameworkCore;

namespace Plate.Web.Tests;

/// <summary>Reorder mode saves a whole new order at once (SPEC 7, Phase 11).</summary>
public class ReorderTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static async Task<int> AddAsync(UserScope s, int templateId, string name) =>
        await s.Templates.AddExerciseAsync(templateId, (await s.Db.Exercises.FirstAsync(e => e.Name == name)).Id, default);

    [Fact]
    public async Task A_template_takes_the_order_given()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var template = await s.Templates.CreateAsync("Upper", default);
        var a = await AddAsync(s, template.Id, "Bench press");
        var b = await AddAsync(s, template.Id, "Barbell row");
        var c = await AddAsync(s, template.Id, "Dip");

        await s.Templates.ReorderAsync(template.Id, [c, a, b], default);

        var order = await s.Db.TemplateExercises.Where(e => e.TemplateId == template.Id).OrderBy(e => e.SortOrder).Select(e => e.Id).ToListAsync();
        order.Should().Equal(c, a, b);
    }

    [Fact]
    public async Task A_session_takes_the_order_given()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var template = await s.Templates.CreateAsync("Upper", default);
        await AddAsync(s, template.Id, "Bench press");
        await AddAsync(s, template.Id, "Barbell row");
        var session = await s.Sessions.GetAsync((await s.Sessions.StartAsync(template.Id, default)).Id, default);
        var ids = session.Exercises.Select(e => e.Id).ToList();

        await s.Sessions.ReorderAsync(session.Id, [ids[1], ids[0]], default);
        s.Db.ChangeTracker.Clear(); // as a new request would read it

        (await s.Sessions.GetAsync(session.Id, default)).Exercises.Select(e => e.Id).Should().Equal(ids[1], ids[0]);
    }

    [Fact]
    public async Task An_order_that_leaves_out_adds_or_borrows_an_exercise_is_refused()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        await using var other = new UserScope(fixture, await fixture.CreateUserAsync());
        var template = await s.Templates.CreateAsync("Upper", default);
        var a = await AddAsync(s, template.Id, "Bench press");
        var b = await AddAsync(s, template.Id, "Barbell row");
        var theirs = await other.Templates.CreateAsync("Theirs", default);
        var x = await AddAsync(other, theirs.Id, "Dip");

        await s.Invoking(t => t.Templates.ReorderAsync(template.Id, [a], default)).Should().ThrowAsync<ValidationException>();
        await s.Invoking(t => t.Templates.ReorderAsync(template.Id, [a, b, x], default)).Should().ThrowAsync<ValidationException>();
        await other.Invoking(t => t.Templates.ReorderAsync(template.Id, [b, a], default)).Should().ThrowAsync<NotFoundException>();
    }
}
