using System.Net;
using System.Reflection;
using FluentAssertions;
using GymTracker.Core.Domain;
using GymTracker.Core.Onboarding;
using GymTracker.Web.Controllers;
using GymTracker.Web.Data;
using GymTracker.Web.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GymTracker.Web.Tests;

/// <summary>A setup chat model that answers from a script and records what it was asked.</summary>
public sealed class ScriptedAssistant : IOnboardingAssistant
{
    private readonly Queue<OnboardingFields> script = new();

    public bool Available { get; set; } = true;
    public List<OnboardingContext> Asked { get; } = [];

    public ScriptedAssistant Then(OnboardingFields fields)
    {
        script.Enqueue(fields);
        return this;
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(Available);

    public Task<OnboardingTurn> NextAsync(OnboardingContext context, CancellationToken ct)
    {
        Asked.Add(context);
        var fields = script.Count > 0 ? script.Dequeue() : OnboardingFields.None;
        return Task.FromResult(new OnboardingTurn($"Reply {Asked.Count}", fields, ["Male", "Female"], null, "test/model"));
    }
}

public class OnboardingTests(PostgresFixture db) : IDisposable, IClassFixture<PostgresFixture>
{
    private readonly ScriptedAssistant assistant = new();
    private AppFactory? app;

    private AppFactory App => app ??= new AppFactory(db, services: s => s.AddScoped<IOnboardingAssistant>(_ => assistant));

    public void Dispose() => app?.Dispose();

    private static readonly OnboardingFields Everything = new(
        Sex: Sex.Male, BirthYear: 1996, HeightCm: 180, WeightKg: 80, ActivityLevel: ActivityLevel.LowActive, Goal: Goal.Maintain);

    private readonly Dictionary<HttpClient, string> emails = [];

    private async Task<HttpClient> NewUserAsync()
    {
        var (email, password) = await App.CreateUserAsync(onboarded: false);
        var client = App.NewClient();
        await AppFactory.LoginAsync(client, email, password);
        emails[client] = email;
        return client;
    }

    private async Task<(UserProfile Profile, int Chats, List<BodyweightEntry> Weights)> StoredAsync(HttpClient client)
    {
        using var scope = App.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var id = await data.Users.Where(u => u.Email == emails[client]).Select(u => u.Id).SingleAsync();
        var profile = await data.Profiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.UserId == id);
        var chats = await data.OnboardingChats.IgnoreQueryFilters().CountAsync(c => c.UserId == id);
        var weights = await data.Bodyweights.IgnoreQueryFilters().AsNoTracking().Where(b => b.UserId == id).ToListAsync();
        return (profile, chats, weights);
    }

    private static Task<HttpResponseMessage> PostFormAsync(HttpClient client, string path, string token, Dictionary<string, string>? fields = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(fields ?? []) };
        request.Headers.Add("RequestVerificationToken", token);
        return client.SendAsync(request);
    }

    private static Dictionary<string, string> SummaryForm(string height = "180") => new()
    {
        ["displayName"] = "Ivan",
        ["sex"] = "Male",
        ["birthYear"] = "1996",
        ["heightCm"] = height,
        ["weightKg"] = "80",
        ["activityLevel"] = "LowActive",
        ["goal"] = "Maintain",
        ["proteinGramsPerKg"] = "2",
        ["timeZoneId"] = "Europe/London",
        ["returnUrl"] = "/progress",
    };

    [Fact]
    public async Task A_new_user_is_sent_to_the_splash_with_the_page_they_wanted()
    {
        var client = await NewUserAsync();

        var today = await client.GetAsync("/today");
        today.Headers.Location!.OriginalString.Should().Be("/welcome");
        var progress = await client.GetAsync("/progress?range=4w");
        progress.Headers.Location!.OriginalString.Should().Be("/welcome?returnUrl=%2Fprogress%3Frange%3D4w");

        var splash = await client.GetStringAsync("/welcome");
        splash.Should().Contain("set you up").And.Contain("answers to an AI model through OpenRouter");
    }

    [Fact]
    public async Task Partials_posts_and_exempt_pages_are_never_redirected()
    {
        var client = await NewUserAsync();

        var htmx = new HttpRequestMessage(HttpMethod.Get, "/today");
        htmx.Headers.Add("HX-Request", "true");
        (await client.SendAsync(htmx)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/feedback/new")).StatusCode.Should().Be(HttpStatusCode.OK);

        var token = await AppFactory.TokenAsync(client, "/welcome");
        var weight = await AppFactory.PostAsync(client, "/today/weight", token, new() { ["weightKg"] = "80" });
        weight.StatusCode.Should().NotBe(HttpStatusCode.BadRequest);
        weight.Headers.Location?.OriginalString.Should().NotStartWith("/welcome");
    }

    [Fact]
    public async Task An_onboarded_user_goes_straight_to_their_page()
    {
        var (client, _) = await App.SignedInClientAsync();

        (await client.GetAsync("/today")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_chat_keeps_values_inside_the_limits_and_tells_the_model_what_it_refused()
    {
        assistant.Then(OnboardingFields.None).Then(new OnboardingFields(Sex: Sex.Male, HeightCm: 300));
        var client = await NewUserAsync();
        var token = await AppFactory.TokenAsync(client, "/welcome");

        (await PostFormAsync(client, "/welcome/start", token)).Headers.Location!.OriginalString.Should().Be("/welcome");
        await PostFormAsync(client, "/welcome/message", token, new() { ["text"] = "I'm a man, 300 cm tall" });
        await PostFormAsync(client, "/welcome/message", token, new() { ["text"] = "Sorry, 180" });

        assistant.Asked.Should().HaveCount(3);
        assistant.Asked[1].History.Select(l => l.Text).Should().Equal("Reply 1", "I'm a man, 300 cm tall");
        assistant.Asked[2].Draft.Sex.Should().Be(Sex.Male);
        assistant.Asked[2].Draft.HeightCm.Should().BeNull();
        assistant.Asked[2].Rejected.Should().ContainSingle().Which.Field.Should().Be(OnboardingField.Height);

        var page = await client.GetStringAsync("/welcome");
        page.Should().Contain("I&#x27;m a man, 300 cm tall").And.Contain("Reply 3").And.Contain("data-quick-reply");
        page.Should().NotContain("Your summary", "height, weight and more are still missing");
    }

    [Fact]
    public async Task A_complete_draft_shows_the_summary_and_saving_sets_up_the_profile()
    {
        assistant.Then(OnboardingFields.None).Then(Everything);
        var client = await NewUserAsync();
        var token = await AppFactory.TokenAsync(client, "/welcome");
        await PostFormAsync(client, "/welcome/start", token);
        await PostFormAsync(client, "/welcome/message", token, new() { ["text"] = "30, man, 180 cm, 80 kg, desk job but 10k steps, maintain" });

        var page = await client.GetStringAsync("/welcome");
        page.Should().Contain("Your summary").And.Contain(">2946</span>", "NASEM 2023 low active, male, 30 y, 180 cm, 80 kg");

        var finish = await PostFormAsync(client, "/welcome/finish", token, SummaryForm());

        finish.Headers.Location!.OriginalString.Should().Be("/progress");
        var (profile, chats, weights) = await StoredAsync(client);
        profile.OnboardedAt.Should().NotBeNull();
        profile.ActivityLevel.Should().Be(ActivityLevel.LowActive);
        profile.DisplayName.Should().Be("Ivan");
        profile.TimeZoneId.Should().Be("Europe/London");
        chats.Should().Be(0);
        weights.Should().ContainSingle().Which.WeightKg.Should().Be(80);
        (await client.GetAsync("/today")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetStringAsync("/today")).Should().Contain("2946");
    }

    [Fact]
    public async Task A_summary_outside_the_limits_saves_nothing()
    {
        var client = await NewUserAsync();
        var token = await AppFactory.TokenAsync(client, "/welcome");

        var finish = await AppFactory.PostAsync(client, "/welcome/finish", token, SummaryForm(height: "300"));

        finish.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var (profile, _, weights) = await StoredAsync(client);
        profile.OnboardedAt.Should().BeNull();
        profile.HeightCm.Should().BeNull();
        weights.Should().BeEmpty();
    }

    [Fact]
    public async Task Skipping_ends_the_setup_without_changing_the_profile()
    {
        var client = await NewUserAsync();
        var token = await AppFactory.TokenAsync(client, "/welcome");

        var skip = await PostFormAsync(client, "/welcome/skip", token, new() { ["returnUrl"] = "/train" });

        skip.Headers.Location!.OriginalString.Should().Be("/train");
        var (profile, _, _) = await StoredAsync(client);
        profile.OnboardedAt.Should().NotBeNull();
        profile.Sex.Should().BeNull();
        (await client.GetAsync("/today")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Without_a_model_the_page_is_the_form()
    {
        assistant.Available = false;
        var client = await NewUserAsync();

        var page = await client.GetStringAsync("/welcome");

        page.Should().Contain("Your profile").And.Contain("action=\"/welcome/finish\"").And.NotContain("set you up");
    }

    [Fact]
    public async Task One_users_chat_is_not_shown_to_another()
    {
        var a = await NewUserAsync();
        var token = await AppFactory.TokenAsync(a, "/welcome");
        await PostFormAsync(a, "/welcome/start", token);
        await PostFormAsync(a, "/welcome/message", token, new() { ["text"] = "secret of user A" });

        var b = await NewUserAsync();
        var page = await b.GetStringAsync("/welcome");

        page.Should().NotContain("secret of user A").And.Contain("set you up");
    }

    [Fact]
    public async Task Overlong_messages_are_refused()
    {
        var client = await NewUserAsync();
        var token = await AppFactory.TokenAsync(client, "/welcome");

        var response = await AppFactory.PostAsync(client, "/welcome/message", token, new() { ["text"] = new string('a', 501) });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public void Chat_messages_and_starts_are_rate_limited()
    {
        foreach (var action in new[] { nameof(OnboardingController.Message), nameof(OnboardingController.Start) })
        {
            typeof(OnboardingController).GetMethod(action)!.GetCustomAttribute<EnableRateLimitingAttribute>()!.PolicyName
                .Should().Be(RateLimits.AiOnboarding);
        }
    }

    [Fact]
    public void The_summary_card_shows_auto_protein_and_the_macro_ranges()
    {
        var draft = new OnboardingDraft
        {
            Name = "Ivan", Sex = Sex.Male, BirthYear = 1996, HeightCm = 180, WeightKg = 80,
            ActivityLevel = ActivityLevel.Inactive, Goal = Goal.Cut, RatePercentPerWeek = 0.5m,
        };

        var preview = GymTracker.Web.Features.Onboarding.OnboardingService.Preview(draft, new UserProfile(), new DateOnly(2026, 10, 5))!;

        preview.Macros!.ProteinMinG.Should().Be(130);
        preview.ProteinG.Should().Be(175, "Auto on a cut is 2.2 g/kg of 80 kg");
        preview.Macros.FatMinG.Should().BeLessThan(preview.Macros.FatMaxG);
    }
}
