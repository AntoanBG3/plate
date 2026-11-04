using System.Security.Claims;
using FluentAssertions;
using GymTracker.Web.Features.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Tests;

public class QueueOwnerTests
{
    [Theory]
    [InlineData("A", "A", 204)]
    [InlineData("A", "B", 409)]
    [InlineData(null, "B", 503)]
    [InlineData("", "B", 503)]
    public async Task Queue_requests_must_match_the_current_account_before_antiforgery_runs(string? owner, string signedInAs, int status)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, signedInAs)], "test")),
        };
        context.Request.Headers[QueueOwnerMiddleware.QueueHeader] = "1";
        if (owner is not null)
        {
            context.Request.Headers[QueueOwnerMiddleware.OwnerHeader] = owner;
        }

        var middleware = new QueueOwnerMiddleware(http =>
        {
            http.Response.StatusCode = 204;
            return Task.CompletedTask;
        });
        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(status);
        if (status == 503)
        {
            context.Response.Headers["Retry-After"].ToString().Should().Be("60");
        }
    }
}

public class QueueOwnerHttpTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task A_pre_upgrade_client_gets_a_retryable_response_without_writing_the_report()
    {
        await using var app = new AppFactory(fixture);
        var (client, email) = await app.SignedInClientAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/feedback")
        {
            Content = new FormUrlEncodedContent(FeedbackTests.Report("Queued before the upgrade")),
        };
        request.Headers.Add("X-Gym-Queue", "1"); // The old client has no X-Gym-User header.
        request.Headers.Add("RequestVerificationToken", await AppFactory.TokenAsync(client, "/feedback/new"));
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(60));
        await using var db = fixture.CreateContext(null);
        var id = await db.Users.Where(x => x.Email == email).Select(x => x.Id).SingleAsync();
        (await db.Feedback.IgnoreQueryFilters().CountAsync(x => x.UserId == id)).Should().Be(0);
    }

    [Fact]
    public async Task An_old_tab_cannot_submit_its_report_after_another_account_signs_in()
    {
        await using var app = new AppFactory(fixture);
        var (a, _) = await app.SignedInClientAsync();
        var oldToken = await AppFactory.TokenAsync(a, "/feedback/new");
        var owner = await AppFactory.QueueOwnerAsync(a);
        var (b, emailB) = await app.SignedInClientAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/feedback")
        {
            Content = new FormUrlEncodedContent(FeedbackTests.Report("Private report from the old tab")),
        };
        request.Headers.Add("X-Gym-Queue", "1");
        request.Headers.Add("X-Gym-User", owner);
        request.Headers.Add("RequestVerificationToken", oldToken);
        var response = await b.SendAsync(request);

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Conflict);
        await using var db = fixture.CreateContext(null);
        var idB = await db.Users.Where(x => x.Email == emailB).Select(x => x.Id).SingleAsync();
        (await db.Feedback.IgnoreQueryFilters().CountAsync(x => x.UserId == idB)).Should().Be(0);
    }
}
