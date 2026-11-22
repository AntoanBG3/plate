using System.Net;
using FluentAssertions;
using Plate.Core.Domain;
using Plate.Web.Data;
using Plate.Web.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Plate.Web.Tests;

/// <summary>Each OpenRouter call an account makes is recorded with its cost, never its text (SPEC 11).</summary>
public class AiUsageTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string BananaWithUsage = """
        {"model":"openai/gpt-x-20261101","choices":[{"message":{"role":"assistant","content":"{\"items\":[{\"nameEn\":\"banana\",\"nameOriginal\":\"1 banana\",\"grams\":120,\"state\":\"na\",\"preparation\":null,\"assumption\":null,\"kcalPer100g\":89,\"proteinPer100g\":1.1,\"carbsPer100g\":23,\"fatPer100g\":0.3}]}"}}],
         "usage":{"prompt_tokens":812,"completion_tokens":95,"cost":0.0004211}}
        """;

    [Fact]
    public async Task A_parse_records_its_call_with_tokens_and_cost()
    {
        var stub = new StubOpenRouter(completion: BananaWithUsage);
        await using var app = new AppFactory(fixture,
            settings: new Dictionary<string, string?> { ["OpenRouter:ApiKey"] = "sk-or-v1-test-0123456789abcdef", ["OpenRouter:Model"] = "openai/gpt-x" },
            services: s =>
            {
                s.AddHttpClient<OpenRouterModels>().ConfigurePrimaryHttpMessageHandler(() => stub);
                s.AddHttpClient<OpenRouterFoodParser>().ConfigurePrimaryHttpMessageHandler(() => stub);
            });
        var (user, email) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(user, "/food/add");

        var parsed = await AppFactory.PostAsync(user, "/food/parse", token, new() { ["date"] = "2026-11-21", ["meal"] = "Lunch", ["text"] = "1 banana" });
        // Check foods lists the banana: the parser still read the answer after the recorder had read it.
        WebUtility.HtmlDecode(await parsed.Content.ReadAsStringAsync()).Should().Contain("Check foods").And.Contain("1 banana");

        await using var db = fixture.CreateContext(null);
        var userId = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        var calls = await db.AiCalls.IgnoreQueryFilters().Where(c => c.UserId == userId).ToListAsync();
        var call = calls.Single(c => c.Task == "parser");
        // The namer's client isn't stubbed, so its call fails: recorded as failed, with no cost.
        calls.Where(c => c.Task != "parser").Should().OnlyContain(c => c.Task == "namer" && c.Failed && c.Cost == null);
        call.Model.Should().Be("openai/gpt-x-20261101", "the model OpenRouter answered with");
        (call.InputTokens, call.OutputTokens, call.Cost, call.Failed).Should().Be((812, 95, 0.0004211m, false));
    }

    private static AiUsageRecorder Recorder(IServiceProvider services, string? userId, HttpMessageHandler inner) =>
        new("matcher", new FixedCurrentUser(userId), services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, NullLogger<AiUsageRecorder>.Instance)
        {
            InnerHandler = inner,
        };

    private static HttpRequestMessage Post() => new(HttpMethod.Post, "http://openrouter.test/api/alpha/decisions")
    {
        Content = new StringContent("""{"model":"typesafe/jev-1.13","state":{"text":"secret meal words"}}"""),
    };

    [Fact]
    public async Task Decision_usage_is_read_and_the_answer_stays_readable()
    {
        await using var app = new AppFactory(fixture);
        var userId = await fixture.CreateUserAsync();
        using var client = new HttpClient(Recorder(app.Services, userId, new Answer(HttpStatusCode.OK,
            """{"model":"typesafe/jev-1.13","decisions":[],"usage":{"input_tokens":2686,"output_tokens":3,"cost":0.0002686}}""")));

        var response = await client.SendAsync(Post());

        (await response.Content.ReadAsStringAsync()).Should().Contain("decisions");
        await using var db = fixture.CreateContext(null);
        var call = await db.AiCalls.IgnoreQueryFilters().SingleAsync(c => c.UserId == userId);
        (call.Task, call.Model, call.InputTokens, call.OutputTokens, call.Cost, call.Failed).Should().Be(("matcher", "typesafe/jev-1.13", 2686, 3, 0.0002686m, false));
    }

    [Fact]
    public async Task A_failed_call_is_recorded_without_cost_and_still_fails()
    {
        await using var app = new AppFactory(fixture);
        var userId = await fixture.CreateUserAsync();

        using (var client = new HttpClient(Recorder(app.Services, userId, new Answer(HttpStatusCode.BadGateway, "upstream down"))))
        {
            (await client.SendAsync(Post())).StatusCode.Should().Be(HttpStatusCode.BadGateway);
        }

        using (var client = new HttpClient(Recorder(app.Services, userId, new Answer(HttpStatusCode.OK, "", Throw: true))))
        {
            await client.Invoking(c => c.SendAsync(Post())).Should().ThrowAsync<HttpRequestException>();
        }

        await using var db = fixture.CreateContext(null);
        var calls = await db.AiCalls.IgnoreQueryFilters().Where(c => c.UserId == userId).ToListAsync();
        calls.Should().HaveCount(2).And.OnlyContain(c => c.Failed && c.Cost == null && c.Model == "typesafe/jev-1.13");
    }

    [Fact]
    public async Task Nothing_is_recorded_without_a_user_or_for_a_model_list()
    {
        await using var app = new AppFactory(fixture);
        var userId = await fixture.CreateUserAsync();
        await using var before = fixture.CreateContext(null);
        var count = await before.AiCalls.IgnoreQueryFilters().CountAsync();

        using (var client = new HttpClient(Recorder(app.Services, null, new Answer(HttpStatusCode.OK, """{"usage":{"cost":1}}"""))))
        {
            await client.SendAsync(Post());
        }

        using (var client = new HttpClient(Recorder(app.Services, userId, new Answer(HttpStatusCode.OK, """{"data":[]}"""))))
        {
            await client.GetAsync("http://openrouter.test/api/v1/models");
        }

        await using var db = fixture.CreateContext(null);
        (await db.AiCalls.IgnoreQueryFilters().CountAsync()).Should().Be(count);
    }

    [Fact]
    public async Task Calls_older_than_180_days_are_removed()
    {
        var userId = await fixture.CreateUserAsync();
        var now = DateTimeOffset.UtcNow;
        await using (var db = fixture.CreateContext(null))
        {
            db.AiCalls.AddRange(
                new AiCall { UserId = userId, At = now.AddDays(-181), Task = "parser" },
                new AiCall { UserId = userId, At = now.AddDays(-179), Task = "parser" });
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext(null))
        {
            await Plate.Web.Features.Undo.SoftDeletePurge.PurgeFeedbackAsync(db, now, default);
            (await db.AiCalls.IgnoreQueryFilters().Where(c => c.UserId == userId).Select(c => c.At).ToListAsync())
                .Should().ContainSingle().Which.Should().BeCloseTo(now.AddDays(-179), TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task The_digest_shows_each_accounts_activity_and_ai_cost_without_contents()
    {
        await using var app = new AppFactory(fixture);
        var userId = await fixture.CreateUserAsync();
        var now = DateTimeOffset.UtcNow;
        string name;
        await using (var db = fixture.CreateContext(null))
        {
            var profile = await db.Profiles.IgnoreQueryFilters().SingleAsync(p => p.UserId == userId);
            name = profile.DisplayName = "Digest " + Guid.NewGuid().ToString("N")[..6];
            db.FoodLog.AddRange(
                new FoodLogEntry { UserId = userId, Date = DateOnly.FromDateTime(now.UtcDateTime), Description = "secret soup", Grams = 300, Kcal = 200, Source = EntrySource.Barcode, CreatedAt = now.AddHours(-1) },
                new FoodLogEntry { UserId = userId, Date = DateOnly.FromDateTime(now.UtcDateTime), Description = "secret bread", Grams = 50, Kcal = 120, Source = EntrySource.Search, CreatedAt = now.AddDays(-2) },
                new FoodLogEntry { UserId = userId, Date = DateOnly.FromDateTime(now.UtcDateTime), Description = "deleted", Grams = 50, Kcal = 120, Source = EntrySource.Search, CreatedAt = now, DeletedAt = now });
            db.AiCalls.AddRange(
                new AiCall { UserId = userId, At = now.AddHours(-1), Task = "parser", Cost = 0.0003m },
                new AiCall { UserId = userId, At = now.AddHours(-1), Task = "parser", Cost = 0.0002m },
                new AiCall { UserId = userId, At = now.AddHours(-1), Task = "matcher", Failed = true },
                new AiCall { UserId = userId, At = now.AddDays(-30), Task = "label", Cost = 0.01m });
            await db.SaveChangesAsync();
        }

        var digest = new StringWriter();
        (await Plate.Web.Cli.FeedbackCommand.RunAsync(app.Services, ["feedback", "digest", "--since", "7d"], digest)).Should().Be(0);

        var line = digest.ToString().Split('\n').Single(l => l.StartsWith($"  {name}:"));
        line.Should().Be($"  {name}: 2 days active; food 2 (Barcode 1, Search 1); 0 sets; 0 weigh-ins; AI 3 calls $0.0005 (parser 2 $0.0005, matcher 1 (1 failed) $0.0000)");
        digest.ToString().Should().NotContain("secret", "the digest gives counts, never what was logged");
    }

    private sealed class Answer(HttpStatusCode status, string body, bool Throw = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Throw ? throw new HttpRequestException("no route") : Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }
}
