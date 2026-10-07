using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using GymTracker.Core.Domain;
using GymTracker.Web.Cli;
using GymTracker.Web.Data;
using GymTracker.Web.Features.Admin;
using GymTracker.Web.Integrations;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GymTracker.Web.Tests;

/// <summary>The admin area (SPEC 12): who gets in, the passkey unlock, and the pages that write.</summary>
[Collection(PostgresCollection.Name)]
public class AdminTests(PostgresFixture fixture) : IDisposable
{
    private readonly AppFactory app = new(fixture);

    public void Dispose() => app.Dispose();

    /// <summary>A signed-in account with the Dev role and a passkey, not yet unlocked.</summary>
    internal static async Task<(HttpClient Client, string Email, SoftPasskey Passkey)> DevAsync(AppFactory app)
    {
        var (email, password) = await app.CreateUserAsync();
        (await RoleCommand.RunAsync(app.Services, ["role", "grant", "dev", "--email", email], new StringWriter())).Should().Be(0);
        var client = app.NewClient();
        await AppFactory.LoginAsync(client, email, password);
        return (client, email, await SoftPasskey.RegisterAsync(app.Services, email));
    }

    internal static async Task<HttpResponseMessage> UnlockAsync(HttpClient client, SoftPasskey passkey, bool userVerified = true)
    {
        var token = await AppFactory.TokenAsync(client, "/admin/unlock");
        var options = await PostJson(client, "/admin/unlock/options", token, null);
        options.StatusCode.Should().Be(HttpStatusCode.OK);
        var credential = passkey.Assert(await options.Content.ReadAsStringAsync(), userVerified);
        return await PostJson(client, "/admin/unlock", token, new { credential, returnUrl = "/admin/ai" });
    }

    internal static async Task<HttpClient> UnlockedDevAsync(AppFactory app)
    {
        var (client, _, passkey) = await DevAsync(app);
        (await UnlockAsync(client, passkey)).StatusCode.Should().Be(HttpStatusCode.OK);
        return client;
    }

    private static Task<HttpResponseMessage> PostJson(HttpClient client, string path, string token, object? body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Add("RequestVerificationToken", token);
        // As a browser's fetch does; the passkey check compares it with the origin the authenticator signed.
        request.Headers.Add("Origin", "https://localhost");
        return client.SendAsync(request);
    }

    [Fact]
    public async Task The_unlock_page_names_the_passkey_and_follows_the_accounts_language()
    {
        var (client, email, _) = await DevAsync(app);
        var html = System.Net.WebUtility.HtmlDecode(await client.GetStringAsync("/admin/unlock"));
        html.Should().Contain("Unlock with a passkey").And.NotContain("Face ID");

        await using (var db = fixture.CreateContext(null))
        {
            var id = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
            await db.Profiles.IgnoreQueryFilters().Where(p => p.UserId == id).ExecuteUpdateAsync(p => p.SetProperty(x => x.Language, Language.Bulgarian));
        }

        System.Net.WebUtility.HtmlDecode(await client.GetStringAsync("/admin/unlock")).Should().Contain("Отключи с ключ за достъп").And.Contain(">Настройки<");
    }

    [Fact]
    public async Task Without_a_passkey_the_unlock_page_says_to_add_one()
    {
        var (email, password) = await app.CreateUserAsync();
        (await RoleCommand.RunAsync(app.Services, ["role", "grant", "dev", "--email", email], new StringWriter())).Should().Be(0);
        var client = app.NewClient();
        await AppFactory.LoginAsync(client, email, password);

        var html = System.Net.WebUtility.HtmlDecode(await client.GetStringAsync("/admin/unlock"));
        html.Should().Contain("Add a passkey in Settings first").And.Contain("href=\"/settings#passkeys\"").And.NotContain("data-module=\"passkey-unlock\"");
    }

    private async Task<List<AdminAudit>> AuditAsync()
    {
        await using var db = fixture.CreateContext(null);
        return await db.AdminAudit.OrderBy(a => a.Id).ToListAsync();
    }

    [Theory]
    [InlineData("/admin")]
    [InlineData("/admin/unlock")]
    [InlineData("/admin/ai")]
    [InlineData("/admin/feedback")]
    [InlineData("/admin/audit")]
    [InlineData("/admin/invites")]
    public async Task Other_accounts_get_a_404_and_anonymous_visitors_the_login_page(string path)
    {
        (await app.NewClient().GetAsync(path)).Headers.Location!.AbsolutePath.Should().Be("/account/login");
        var (client, _) = await app.SignedInClientAsync();
        (await client.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_old_settings_routes_for_the_key_and_models_are_gone()
    {
        var (client, _) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(client, "/settings");
        (await client.GetAsync("/settings/model/parser")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        foreach (var path in new[] { "/settings/openrouter-key", "/settings/openrouter-key/test", "/settings/openrouter-key/remove", "/settings/model/matcher" })
        {
            (await AppFactory.PostAsync(client, path, token, new() { ["apiKey"] = "sk-or-v1-0123456789abcdef0123", ["modelId"] = "off" }))
                .StatusCode.Should().BeOneOf([HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed], path);
        }

        var settings = await client.GetStringAsync("/settings");
        settings.Should().Contain("AI models").And.Contain("Simple built-in parser").And.Contain("The developer manages the AI models.");
        settings.Should().NotContain("apiKey").And.NotContain("OpenRouter, with").And.NotContain("Key ending").And.NotContain("href=\"/admin\"");
        // Each task is named for what it does; the label reader isn't listed as another setup chat.
        settings.Should().Contain("Label reader").And.Contain("Off: no label photos");
        System.Text.RegularExpressions.Regex.Matches(settings, "Setup chat").Count.Should().Be(1);
    }

    [Fact]
    public async Task A_dev_account_must_unlock_with_its_own_passkey_first()
    {
        var (client, email, passkey) = await DevAsync(app);

        var locked = await client.GetAsync("/admin/feedback?status=all");
        AppFactory.IsRedirectTo(locked, "/admin/unlock?returnUrl=%2Fadmin%2Ffeedback%3Fstatus%3Dall").Should().BeTrue(locked.Headers.Location?.ToString());
        (await client.GetStringAsync("/settings")).Should().Contain("href=\"/admin\"");

        var unlocked = await UnlockAsync(client, passkey);
        unlocked.StatusCode.Should().Be(HttpStatusCode.OK, string.Join(" | ", (await AuditAsync()).Select(a => a.Detail)));
        JsonNode.Parse(await unlocked.Content.ReadAsStringAsync())!["redirect"]!.GetValue<string>().Should().Be("/admin/ai");
        var cookie = unlocked.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("__Host-gym.admin="));
        cookie.Should().Contain("path=/").And.Contain("secure").And.Contain("httponly").And.Contain("samesite=lax").And.NotContain("expires=", "it is a session cookie");

        var page = await client.GetAsync("/admin");
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        page.Headers.CacheControl!.NoStore.Should().BeTrue();
        (await page.Content.ReadAsStringAsync()).Should().Contain("Lock now");

        var audit = await AuditAsync();
        audit.Should().Contain(a => a.Action == AuditActions.RoleGrant && a.Target == email && a.Via == AdminVia.Cli);
        audit.Should().Contain(a => a.Action == AuditActions.Unlock && a.UserId == passkey.UserId && a.Via == AdminVia.Web);

        var token = await AppFactory.TokenAsync(client, "/admin");
        AppFactory.IsRedirectTo(await AppFactory.PostAsync(client, "/admin/lock", token), "/settings").Should().BeTrue();
        AppFactory.IsRedirectTo(await client.GetAsync("/admin"), "/admin/unlock").Should().BeTrue("Lock now ends the unlock");
    }

    [Fact]
    public async Task Another_accounts_passkey_or_one_without_Face_ID_does_not_unlock()
    {
        var (client, _, _) = await DevAsync(app);
        var (otherEmail, _) = await app.CreateUserAsync();
        using var other = await SoftPasskey.RegisterAsync(app.Services, otherEmail);
        (await UnlockAsync(client, other)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var (client2, _, own) = await DevAsync(app);
        (await UnlockAsync(client2, own, userVerified: false)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        AppFactory.IsRedirectTo(await client2.GetAsync("/admin"), "/admin/unlock").Should().BeTrue();

        (await AuditAsync()).Count(a => a.Action == AuditActions.UnlockFailed).Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task Revoking_the_role_closes_the_admin_area_at_once_and_log_out_everywhere_ends_the_unlock()
    {
        var (client, email, passkey) = await DevAsync(app);
        (await UnlockAsync(client, passkey)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/admin")).StatusCode.Should().Be(HttpStatusCode.OK);

        await RoleCommand.RunAsync(app.Services, ["role", "revoke", "dev", "--email", email], new StringWriter());
        (await client.GetAsync("/admin")).StatusCode.Should().Be(HttpStatusCode.NotFound, "the role is checked in the database, not only in the cookie");

        // Granted again: the unlock still belongs to this sign-in until the security stamp changes.
        await RoleCommand.RunAsync(app.Services, ["role", "grant", "dev", "--email", email], new StringWriter());
        (await client.GetAsync("/admin")).StatusCode.Should().Be(HttpStatusCode.OK);
        using (var scope = app.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            await users.UpdateSecurityStampAsync((await users.FindByEmailAsync(email))!);
        }

        AppFactory.IsRedirectTo(await client.GetAsync("/admin"), "/admin/unlock").Should().BeTrue("the unlock names the old security stamp");
    }

    [Fact]
    public void An_unlock_lasts_four_hours_at_most_and_only_for_its_account_and_stamp()
    {
        var at = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        var principal = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
        [
            new(System.Security.Claims.ClaimTypes.NameIdentifier, "u1"),
            new("stamp", "s1"),
            new("unlocked_at", at.ToUnixTimeSeconds().ToString()),
        ], AdminUnlock.Scheme));

        AdminUnlock.Expires(principal, at.AddMinutes(30), "u1", "s1", at.AddMinutes(10)).Should().Be(at.AddMinutes(30));
        AdminUnlock.Expires(principal, at.AddHours(5), "u1", "s1", at.AddHours(3)).Should().Be(at.AddHours(4), "sliding never passes the hard limit");
        AdminUnlock.Expires(principal, at.AddHours(5), "u1", "s1", at.AddHours(4)).Should().BeNull();
        AdminUnlock.Expires(principal, at.AddMinutes(30), "u2", "s1", at).Should().BeNull();
        AdminUnlock.Expires(principal, at.AddMinutes(30), "u1", "s2", at).Should().BeNull();
    }

    [Fact]
    public async Task A_status_and_reply_from_the_admin_area_reach_the_reporter_and_are_audited()
    {
        var (reporter, _) = await app.SignedInClientAsync();
        var feedbackToken = await AppFactory.TokenAsync(reporter, "/feedback/new");
        var id = await FeedbackTests.IdOf(await FeedbackTests.QueuePost(reporter, "/feedback", feedbackToken, FeedbackTests.Report("Rest timer is silent <b>today</b>")));

        var admin = await UnlockedDevAsync(app);
        var page = await admin.GetStringAsync($"/admin/feedback/{id}");
        page.Should().Contain("Rest timer is silent &lt;b&gt;today&lt;/b&gt;", "user text is encoded").And.Contain("390x844");
        await using (var db = fixture.CreateContext(null))
        {
            (await db.Feedback.IgnoreQueryFilters().SingleAsync(f => f.Id == id)).Status.Should().Be(FeedbackStatus.Seen, "opening a new report marks it seen");
        }

        var token = await AppFactory.TokenAsync(admin, $"/admin/feedback/{id}");
        var saved = await AppFactory.PostAsync(admin, $"/admin/feedback/{id}/status", token, new() { ["status"] = "Fixed", ["reply"] = "Fixed, thanks!", ["version"] = "abc1234" });
        AppFactory.IsRedirectTo(saved, $"/admin/feedback/{id}?fixed=1").Should().BeTrue();

        (await reporter.GetStringAsync("/settings")).Should().Contain("count-badge");
        (await reporter.GetStringAsync("/feedback")).Should().Contain("Fixed, thanks!");
        (await AuditAsync()).Should().Contain(a => a.Action == AuditActions.Status && a.Target == $"feedback #{id}" && a.Detail == "Fixed, reply, version abc1234");

        var added = await AppFactory.PostAsync(admin, "/admin/changelog", token, new() { ["textEn"] = "The rest timer beeps again.", ["textBg"] = "Таймерът за почивка отново звъни.", ["feedbackId"] = id.ToString() });
        AppFactory.IsRedirectTo(added, "/admin/changelog").Should().BeTrue();
        await using (var db = fixture.CreateContext(null))
        {
            var entry = await db.Changelog.OrderByDescending(c => c.Id).FirstAsync();
            entry.FeedbackId.Should().Be(id);
            AppFactory.IsRedirectTo(await AppFactory.PostAsync(admin, $"/admin/changelog/{entry.Id}/remove", token), "/admin/changelog").Should().BeTrue();
            (await db.Changelog.AnyAsync(c => c.Id == entry.Id)).Should().BeFalse();
        }
    }

    [Fact]
    public async Task Screenshots_are_sandboxed_and_regression_cases_download_as_a_file()
    {
        var (reporter, _) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(reporter, "/feedback/new");
        var form = new MultipartFormDataContent();
        foreach (var (k, v) in FeedbackTests.Report("See the picture"))
        {
            form.Add(new StringContent(v), k);
        }

        form.Add(new StringContent("1"), "screenshotWidth");
        form.Add(new StringContent("1"), "screenshotHeight");
        var file = new ByteArrayContent(FeedbackTests.Png);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(file, "screenshot", "shot");
        var request = new HttpRequestMessage(HttpMethod.Post, "/feedback") { Content = form };
        request.Headers.Add("RequestVerificationToken", token);
        request.Headers.Add("X-Gym-Queue", "1");
        var id = await FeedbackTests.IdOf(await reporter.SendAsync(request));

        var admin = await UnlockedDevAsync(app);
        var shot = await admin.GetAsync($"/admin/feedback/{id}/screenshot");
        shot.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        shot.Headers.GetValues("Content-Security-Policy").Single().Should().StartWith("sandbox");
        (await shot.Content.ReadAsByteArrayAsync()).Should().Equal(FeedbackTests.Png);

        var export = await admin.GetAsync("/admin/parses/export?since=30d");
        export.StatusCode.Should().Be(HttpStatusCode.OK);
        export.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        (await AuditAsync()).Should().Contain(a => a.Action == AuditActions.ExportCases);
    }

    [Fact]
    public async Task The_AI_page_changes_the_shared_matcher_model_and_the_other_account_sees_it_read_only()
    {
        var admin = await UnlockedDevAsync(app);
        try
        {
            // The test app can't reach OpenRouter, so the parser's catalog doesn't load; the matcher's list is fixed.
            (await admin.GetStringAsync("/admin/ai/model/parser")).Should().Contain("be loaded right now");
            (await admin.GetStringAsync("/admin/ai/model/matcher")).Should().Contain("Jev 1.13");
            var token = await AppFactory.TokenAsync(admin, "/admin/ai");
            var pick = await AppFactory.PostAsync(admin, "/admin/ai/model/parser", token, new() { ["modelId"] = "openai/gpt-x" });
            pick.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            AppFactory.IsRedirectTo(await AppFactory.PostAsync(admin, "/admin/ai/model/matcher", token, new() { ["modelId"] = JevFoodMatchDecider.Jev113 }), "/admin/ai")
                .Should().BeTrue();
            (await AuditAsync()).Should().Contain(a => a.Action == AuditActions.ModelSet && a.Target == "matcher" && a.Detail == $"off -> {JevFoodMatchDecider.Jev113}");

            var (other, _) = await app.SignedInClientAsync();
            (await other.GetStringAsync("/settings")).Should().Contain("Food matcher").And.NotContain("/admin/ai");
            (await AppFactory.PostAsync(admin, "/admin/ai/model/nonsense", token, new() { ["modelId"] = "x" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally
        {
            using var scope = app.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().AppSettings.Where(x => x.Name == AiTasks.SettingName(AiTask.Matcher)).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task The_CLI_sets_the_key_from_standard_input_and_lists_roles_and_the_audit()
    {
        var output = new StringWriter();
        try
        {
            (await AiCommand.RunAsync(app.Services, ["ai", "set-key"], output, new StringReader("sk-or-v1-testkey-0123456789abcd\n"))).Should().Be(1, "the test app can't reach OpenRouter, so the test after saving fails");
            output.ToString().Should().Contain("Key saved, ending abcd").And.NotContain("testkey");

            var show = new StringWriter();
            await AiCommand.RunAsync(app.Services, ["ai", "show"], show);
            show.ToString().Should().Contain("saved in the database, ending abcd").And.Contain("Parser:").And.Contain("Matcher: off");

            (await AiCommand.RunAsync(app.Services, ["ai", "set-key"], new StringWriter(), new StringReader("short\n"))).Should().Be(1);
            (await AiCommand.RunAsync(app.Services, ["ai", "clear-key"], new StringWriter())).Should().Be(0);
        }
        finally
        {
            using var scope = app.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().AppSettings.Where(x => x.Name == OpenRouterSettings.KeySettingName).ExecuteDeleteAsync();
        }

        var (email, _) = await app.CreateUserAsync();
        await RoleCommand.RunAsync(app.Services, ["role", "grant", "dev", "--email", email], new StringWriter());
        var roles = new StringWriter();
        (await RoleCommand.RunAsync(app.Services, ["role", "list"], roles)).Should().Be(0);
        roles.ToString().Should().Contain($"{email}  Dev");
        (await RoleCommand.RunAsync(app.Services, ["role", "grant", "admin", "--email", email], new StringWriter())).Should().Be(2, "Dev is the only role");

        var audit = new StringWriter();
        (await AdminCommand.RunAsync(app.Services, ["admin", "audit", "--since", "1h"], audit)).Should().Be(0);
        audit.ToString().Should().Contain("cli  key-set").And.Contain("cli  key-remove").And.Contain($"cli  role-grant  {email}");
    }

    [Fact]
    public async Task An_unlock_and_a_key_change_send_a_Telegram_alert_without_the_key()
    {
        var telegram = new Telegram();
        using var alerting = new AppFactory(fixture,
            settings: new Dictionary<string, string?> { ["Telegram:BotToken"] = "123:secret", ["Telegram:ChatId"] = "42", ["Telegram:BaseUrl"] = "http://telegram.test/" },
            services: s => s.AddHttpClient(GymTracker.Web.Features.Feedback.FeedbackNotifierWorker.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => telegram));
        var (client, email, passkey) = await DevAsync(alerting);
        (await UnlockAsync(client, passkey)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await telegram.NextAsync()).Should().Contain($"admin unlocked by {email}").And.Contain("role revoke dev");

        try
        {
            var token = await AppFactory.TokenAsync(client, "/admin/ai");
            await AppFactory.PostAsync(client, "/admin/ai/key", token, new() { ["apiKey"] = "sk-or-v1-secret-0123456789abcdef" });
            var text = await telegram.NextAsync();
            text.Should().Contain("OpenRouter key was changed from the web").And.NotContain("secret-0123");
        }
        finally
        {
            using var scope = alerting.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().AppSettings.Where(x => x.Name == OpenRouterSettings.KeySettingName).ExecuteDeleteAsync();
        }
    }

    private sealed class Telegram : HttpMessageHandler
    {
        private readonly System.Threading.Channels.Channel<string> texts = System.Threading.Channels.Channel.CreateUnbounded<string>();

        public async Task<string> NextAsync() => await texts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            await texts.Writer.WriteAsync(body["text"]!.GetValue<string>(), ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") };
        }
    }
}
