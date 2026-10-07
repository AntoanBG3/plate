using System.Net;
using FluentAssertions;
using GymTracker.Core.Domain;
using GymTracker.Web.Data;
using GymTracker.Web.Features.Admin;
using GymTracker.Web.Features.Feedback;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GymTracker.Web.Tests;

/// <summary>The join page (SPEC 9, Phase 12).</summary>
[Collection(PostgresCollection.Name)]
public class JoinTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string Password = "correct horse battery";

    // Test clients all share one address, so the join limit is raised except where it is the subject.
    private readonly AppFactory app = new(fixture, settings: new Dictionary<string, string?> { ["RateLimits:JoinPerWindow"] = "1000" });

    public async Task InitializeAsync()
    {
        await using var db = fixture.CreateContext(null);
        await db.Invites.ExecuteDeleteAsync();
    }

    public Task DisposeAsync()
    {
        app.Dispose();
        return Task.CompletedTask;
    }

    private static async Task<string> InviteAsync(AppFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<InviteAdmin>().CreateAsync(null, "Ivan from the gym", 7, default)).Token;
    }

    private static async Task<HttpResponseMessage> JoinAsync(HttpClient client, string token, string email, string password = Password, string name = "Ivan", string query = "")
    {
        var path = $"/join/{token}{query}";
        var fields = new Dictionary<string, string>
        {
            ["name"] = name,
            ["email"] = email,
            ["password"] = password,
            ["__RequestVerificationToken"] = await AppFactory.TokenAsync(client, path),
        };
        return await client.PostAsync(path, new FormUrlEncodedContent(fields));
    }

    private static string NewEmail() => $"{Guid.NewGuid():N}@test.local";

    private async Task<UserProfile?> ProfileAsync(string email)
    {
        await using var db = fixture.CreateContext(null);
        var id = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleOrDefaultAsync();
        return id is null ? null : await db.Profiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.UserId == id);
    }

    [Fact]
    public async Task An_open_invite_shows_the_form_and_asks_not_to_be_indexed()
    {
        var token = await InviteAsync(app);
        var response = await app.NewClient().GetAsync($"/join/{token}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("X-Robots-Tag").Should().Equal("noindex");
        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("Create your account").And.Contain("name=\"password\"").And.Contain("autocomplete=\"new-password\"");
    }

    [Fact]
    public async Task An_unknown_or_used_token_shows_one_message_and_no_form()
    {
        var token = await InviteAsync(app);
        (await JoinAsync(app.NewClient(), token, NewEmail())).StatusCode.Should().Be(HttpStatusCode.Redirect);

        foreach (var path in new[] { $"/join/{token}", $"/join/{InviteRules.NewToken().Token}", "/join/x" })
        {
            var response = await app.NewClient().GetAsync(path);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var html = await response.Content.ReadAsStringAsync();
            html.Should().Contain("This invite is no longer valid. Ask Antoan for a new one.").And.NotContain("name=\"password\"");
        }
    }

    [Theory]
    [InlineData("bg-BG,bg;q=0.9,en;q=0.8", "", "Създай профила си", Language.Bulgarian)]
    [InlineData("en-GB,en;q=0.9,bg;q=0.8", "", "Create your account", Language.English)]
    [InlineData("de-DE", "", "Create your account", Language.English)]
    [InlineData("en-GB", "?lang=bg", "Създай профила си", Language.Bulgarian)]
    public async Task The_page_and_the_new_account_follow_the_browsers_language_unless_one_is_picked(string acceptLanguage, string query, string title, Language language)
    {
        var token = await InviteAsync(app);
        var client = app.NewClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(acceptLanguage);

        var html = WebUtility.HtmlDecode(await client.GetStringAsync($"/join/{token}{query}"));
        html.Should().Contain(title).And.Contain($"lang=\"{(language == Language.Bulgarian ? "bg" : "en")}\"");

        var email = NewEmail();
        (await JoinAsync(client, token, email, query: query)).StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await ProfileAsync(email))!.Language.Should().Be(language);
    }

    [Fact]
    public async Task Joining_signs_the_new_account_in_and_sends_it_to_the_setup_chat()
    {
        var token = await InviteAsync(app);
        var client = app.NewClient();
        var email = NewEmail();

        var response = await JoinAsync(client, token, email);

        AppFactory.IsRedirectTo(response, "/").Should().BeTrue();
        AppFactory.IsRedirectTo(await client.GetAsync("/today"), "/welcome").Should().BeTrue();
        (await ProfileAsync(email))!.DisplayName.Should().Be("Ivan");
    }

    [Fact]
    public async Task A_failed_account_keeps_what_was_typed_shows_the_reason_in_the_pages_language_and_leaves_the_invite_open()
    {
        var token = await InviteAsync(app);
        var (taken, _) = await app.CreateUserAsync();
        var client = app.NewClient();

        var response = await JoinAsync(client, token, taken, name: "Иван", query: "?lang=bg");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        html.Should().Contain("Вече има профил с този имейл.").And.Contain("value=\"Иван\"").And.Contain($"value=\"{taken}\"");

        var email = NewEmail();
        html = await (await JoinAsync(client, token, email, password: "short")).Content.ReadAsStringAsync();
        html.Should().Contain("The password needs at least 12 characters.");

        AppFactory.IsRedirectTo(await JoinAsync(client, token, email), "/").Should().BeTrue();
    }

    [Fact]
    public async Task A_signed_in_visitor_is_told_to_log_out_and_does_not_use_the_invite()
    {
        var token = await InviteAsync(app);
        var (client, _) = await app.SignedInClientAsync();

        var html = await client.GetStringAsync($"/join/{token}");
        html.Should().Contain("Log out first to use this invite.").And.NotContain("name=\"password\"");

        // Posting anyway (an old tab) creates nothing either.
        var antiforgery = await AppFactory.TokenAsync(client, "/settings");
        var email = NewEmail();
        var post = await client.PostAsync($"/join/{token}", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["name"] = "Ivan", ["email"] = email, ["password"] = Password, ["__RequestVerificationToken"] = antiforgery,
        }));
        post.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ProfileAsync(email)).Should().BeNull();

        var stranger = app.NewClient();
        (await stranger.GetStringAsync($"/join/{token}")).Should().Contain("name=\"password\"", "the invite is still open");
    }

    [Fact]
    public async Task An_account_made_from_an_invite_sees_nothing_of_the_other_accounts()
    {
        var owners = new List<(int SessionId, string Secret)>();
        for (var i = 0; i < 2; i++)
        {
            var userId = await fixture.CreateUserAsync();
            await using var s = new UserScope(fixture, userId);
            var secret = $"Secret plan {Guid.NewGuid():N}";
            var template = await s.Templates.CreateAsync(secret, default);
            await s.Templates.AddExerciseAsync(template.Id, (await s.Db.Exercises.FirstAsync(e => e.Name == "Bench press")).Id, default);
            owners.Add(((await s.Sessions.StartAsync(template.Id, default)).Id, secret));
        }

        var client = app.NewClient();
        var email = NewEmail();
        AppFactory.IsRedirectTo(await JoinAsync(client, await InviteAsync(app), email), "/").Should().BeTrue();
        await using (var db = fixture.CreateContext(null))
        {
            // Past the setup chat, so pages answer directly.
            var id = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
            await db.Profiles.IgnoreQueryFilters().Where(p => p.UserId == id).ExecuteUpdateAsync(p => p.SetProperty(x => x.OnboardedAt, DateTimeOffset.UtcNow));
        }

        foreach (var (sessionId, secret) in owners)
        {
            (await client.GetAsync($"/train/sessions/{sessionId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            var export = ExportTests.Unzip(await client.GetByteArrayAsync("/settings/export"));
            string.Concat(export.Values).Should().NotContain(secret);
        }
    }

    [Fact]
    public async Task Join_requests_are_limited_per_address()
    {
        using var limited = new AppFactory(fixture, settings: new Dictionary<string, string?> { ["RateLimits:JoinPerWindow"] = "3" });
        var client = limited.NewClient();
        for (var i = 0; i < 3; i++)
        {
            (await client.GetAsync("/join/x")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        (await client.GetAsync("/join/x")).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Theory]
    [InlineData("/join/abc123", "/join/{token}")]
    [InlineData("/JOIN/abc123", "/join/{token}")]
    [InlineData("/today", "/today")]
    [InlineData(null, null)]
    public void The_error_log_never_keeps_an_invite_token(string? path, string? stored) =>
        ErrorCaptureHandler.Redact(path).Should().Be(stored);
}
