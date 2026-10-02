using System.Net;
using FluentAssertions;
using GymTracker.Web.Features.Training;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Tests;

[Collection(PostgresCollection.Name)]
public class HttpSecurityTests(PostgresFixture db) : IDisposable
{
    private readonly AppFactory app = new(db);

    public void Dispose() => app.Dispose();

    [Theory]
    [InlineData("/today")]
    [InlineData("/train")]
    [InlineData("/train/history")]
    [InlineData("/settings")]
    public async Task Pages_require_login(string path)
    {
        var client = app.NewClient();

        var page = await client.GetAsync(path);
        AppFactory.IsRedirectTo(page, "https://localhost/account/login").Should().BeTrue(page.Headers.Location?.ToString());

        var htmx = new HttpRequestMessage(HttpMethod.Get, path);
        htmx.Headers.Add("HX-Request", "true");
        (await client.SendAsync(htmx)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Static_assets_and_login_page_are_public()
    {
        var client = app.NewClient();
        (await client.GetAsync("/account/login")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/css/site.css")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/lib/htmx/htmx.min.js")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Login_succeeds_and_ignores_external_return_urls()
    {
        var (email, password) = await app.CreateUserAsync();
        var client = app.NewClient();

        var response = await AppFactory.LoginAsync(client, email, password, returnUrl: "https://evil.example/");

        AppFactory.IsRedirectTo(response, "/today").Should().BeTrue();
        response.Headers.GetValues("Set-Cookie").Should().Contain(c => c.StartsWith("gym.auth=") && c.Contains("secure") && c.Contains("httponly") && c.Contains("expires="));
        (await client.GetAsync("/today")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Account_locks_after_five_failed_attempts()
    {
        var (email, password) = await app.CreateUserAsync();
        var client = app.NewClient();

        for (var i = 0; i < 5; i++)
        {
            var failed = await AppFactory.LoginAsync(client, email, "wrong password!!");
            failed.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var locked = await AppFactory.LoginAsync(client, email, password);
        locked.StatusCode.Should().Be(HttpStatusCode.OK, "the correct password must not work while locked out");
        (await locked.Content.ReadAsStringAsync()).Should().Contain("Too many failed attempts");
    }

    [Fact]
    public async Task Posts_without_an_antiforgery_token_are_rejected()
    {
        var (email, password) = await app.CreateUserAsync();
        var client = app.NewClient();
        await AppFactory.LoginAsync(client, email, password);

        var response = await client.PostAsync("/today/weight", new FormUrlEncodedContent(new Dictionary<string, string> { ["weightKg"] = "80" }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Decimal_weights_parse_regardless_of_server_culture()
    {
        var (email, password) = await app.CreateUserAsync();
        var client = app.NewClient();
        await AppFactory.LoginAsync(client, email, password);
        var token = await AppFactory.TokenAsync(client, "/today");

        var response = await AppFactory.PostAsync(client, "/today/weight", token, new() { ["weightKg"] = "82.4" });

        AppFactory.IsRedirectTo(response, "/today").Should().BeTrue();
        (await client.GetStringAsync("/today")).Should().Contain("Logged today: 82.4 kg");
    }

    [Fact]
    public async Task Another_users_session_and_sets_are_not_found_over_http()
    {
        var (emailA, passwordA) = await app.CreateUserAsync();
        var (emailB, passwordB) = await app.CreateUserAsync();

        // B starts a freestyle session and logs a set.
        var b = app.NewClient();
        await AppFactory.LoginAsync(b, emailB, passwordB);
        var bToken = await AppFactory.TokenAsync(b, "/train");
        var start = await AppFactory.PostAsync(b, "/train/start", bToken);
        var sessionPath = start.Headers.Location!.OriginalString;
        var sessionId = int.Parse(sessionPath.Split('/').Last());
        int setId;
        await using (var check = db.CreateContext(null))
        {
            var bUserId = (await check.Sessions.IgnoreQueryFilters().SingleAsync(s => s.Id == sessionId)).UserId;
            await using var asB = new UserScope(db, bUserId);
            var bench = await asB.Db.Exercises.FirstAsync(e => e.Name == "Bench press");
            await asB.Sessions.AddExerciseAsync(sessionId, bench.Id, default);
            var exId = (await asB.Sessions.GetAsync(sessionId, default)).Exercises[0].Id;
            setId = (await asB.Sessions.LogSetAsync(new LogSetInput(Guid.NewGuid(), exId, 50, 5, null, false, null), default)).Id;
        }

        var a = app.NewClient();
        await AppFactory.LoginAsync(a, emailA, passwordA);
        var aToken = await AppFactory.TokenAsync(a, "/train");

        (await a.GetAsync(sessionPath)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await a.GetAsync($"{sessionPath}/summary")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await AppFactory.PostAsync(a, $"/train/sets/{setId}/delete", aToken)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await AppFactory.PostAsync(a, $"/train/sets/{setId}/update", aToken, new() { ["weightKg"] = "1", ["reps"] = "1" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await AppFactory.PostAsync(a, $"{sessionPath}/finish", aToken)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await AppFactory.PostAsync(a, $"{sessionPath}/delete", aToken)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await b.GetAsync(sessionPath)).StatusCode.Should().Be(HttpStatusCode.OK, "B can still see their own session");
    }
}

[Collection(PostgresCollection.Name)]
public class LoginRateLimitTests(PostgresFixture db)
{
    [Fact]
    public async Task Login_posts_are_rate_limited()
    {
        using var app = new AppFactory(db, loginLimit: 3);
        var client = app.NewClient();

        for (var i = 0; i < 3; i++)
        {
            (await AppFactory.LoginAsync(client, "nobody@test.local", "whatever password")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        (await AppFactory.LoginAsync(client, "nobody@test.local", "whatever password")).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }
}
