using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using GymTracker.Core.Domain;
using GymTracker.Web.Cli;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Tests;

/// <summary>The admin area's Invites page (SPEC 12, Phase 12).</summary>
[Collection(PostgresCollection.Name)]
public partial class AdminInvitesTests(PostgresFixture fixture) : IAsyncLifetime
{
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

    /// <summary>A plain form post, as the page's own form sends it.</summary>
    private static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, string path, Dictionary<string, string>? fields = null)
    {
        fields ??= [];
        fields["__RequestVerificationToken"] = await AppFactory.TokenAsync(client, "/admin/invites");
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(fields) };
        request.Headers.Referrer = new Uri("https://localhost/admin/invites");
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task Making_an_invite_shows_its_link_once_and_the_link_works()
    {
        var client = await AdminTests.UnlockedDevAsync(app);

        var response = await PostFormAsync(client, "/admin/invites", new() { ["note"] = "Ivan from the gym", ["days"] = "3" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        var link = LinkPattern().Match(html).Value;
        link.Should().StartWith("https://localhost/join/");
        html.Should().Contain("data-module=\"invite-link\"").And.Contain("Ivan from the gym");

        var again = WebUtility.HtmlDecode(await client.GetStringAsync("/admin/invites"));
        again.Should().NotContain(link["https://localhost/join/".Length..]).And.Contain("open").And.Contain("Ivan from the gym");

        (await app.NewClient().GetStringAsync(new Uri(link).PathAndQuery)).Should().Contain("name=\"password\"");

        var audit = new StringWriter();
        await AdminCommand.RunAsync(app.Services, ["admin", "audit", "--since", "1h"], audit);
        audit.ToString().Should().Contain("web  invite-create");
    }

    [Fact]
    public async Task Revoking_closes_the_link_and_a_sixth_open_invite_is_refused_with_a_message()
    {
        var client = await AdminTests.UnlockedDevAsync(app);
        for (var i = 0; i < InviteRules.MaxOpen; i++)
        {
            (await PostFormAsync(client, "/admin/invites", new() { ["days"] = "7" })).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var refused = await PostFormAsync(client, "/admin/invites", new() { ["days"] = "7" });
        AppFactory.IsRedirectTo(refused, "https://localhost/admin/invites").Should().BeTrue();
        var page = WebUtility.HtmlDecode(await client.GetStringAsync("/admin/invites"));
        page.Should().Contain("There are already 5 open invites").And.Contain("5 of 5 open invites");

        await using var db = fixture.CreateContext(null);
        var id = await db.Invites.Select(i => i.Id).FirstAsync();
        AppFactory.IsRedirectTo(await PostFormAsync(client, $"/admin/invites/{id}/revoke"), "/admin/invites").Should().BeTrue();
        (await db.Invites.AsNoTracking().SingleAsync(i => i.Id == id)).RevokedAt.Should().NotBeNull();
        (await PostFormAsync(client, "/admin/invites", new() { ["days"] = "7" })).StatusCode.Should().Be(HttpStatusCode.OK, "a revoked invite frees a place");
    }

    [Fact]
    public async Task The_users_page_names_the_invite_an_account_came_from()
    {
        var client = await AdminTests.UnlockedDevAsync(app);
        var html = WebUtility.HtmlDecode(await (await PostFormAsync(client, "/admin/invites", new() { ["note"] = "Maria, Dad's friend", ["days"] = "7" })).Content.ReadAsStringAsync());
        var path = new Uri(LinkPattern().Match(html).Value).PathAndQuery;

        var joiner = app.NewClient();
        var fields = new Dictionary<string, string>
        {
            ["name"] = "Maria", ["email"] = $"{Guid.NewGuid():N}@test.local", ["password"] = "correct horse battery",
            ["__RequestVerificationToken"] = await AppFactory.TokenAsync(joiner, path),
        };
        AppFactory.IsRedirectTo(await joiner.PostAsync(path, new FormUrlEncodedContent(fields)), "/").Should().BeTrue();

        WebUtility.HtmlDecode(await client.GetStringAsync("/admin/users")).Should().Contain("invited: Maria, Dad's friend");
    }

    [GeneratedRegex(@"https://localhost/join/[A-Za-z0-9_-]{43}")]
    private static partial Regex LinkPattern();
}
