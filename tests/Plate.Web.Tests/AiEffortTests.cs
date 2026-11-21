using System.Net;
using FluentAssertions;
using Plate.Web.Cli;
using Plate.Web.Data;
using Plate.Web.Features;
using Plate.Web.Features.Admin;
using Plate.Web.Integrations;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Plate.Web.Tests;

/// <summary>The reasoning effort per chat task (SPEC 12): what the catalog offers, what is sent, and the admin page and CLI that set it.</summary>
public class AiEffortTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static OpenRouterSettings Store(UserScope s, StubOpenRouter stub) =>
        new(s.Db, new FixedCurrentUser(s.UserId), new EphemeralDataProtectionProvider(), Options.Create(new OpenRouterOptions { Model = "openai/gpt-x" }), stub.Catalog(),
            TimeProvider.System, NullLogger<OpenRouterSettings>.Instance);

    [Fact]
    public async Task The_catalog_lists_each_models_efforts_highest_first()
    {
        var models = (await new StubOpenRouter().Catalog().ListAsync(default))!;

        models.Single(m => m.Id == "google/gemini-3.5-flash-lite").Efforts.Should().Equal("high", "medium", "low", "minimal");
        models.Single(m => m.Id == "openai/gpt-x").Efforts.Should().Equal(["high", "low", "none"], "reasoning is optional, so none is one");
        AiTasks.MatcherOff.Efforts.Should().BeNullOrEmpty();
    }

    [Theory]
    [InlineData("low", "low")]
    [InlineData(null, "none")]
    [InlineData("medium", "none")] // not one this model takes: its lowest is sent instead
    public async Task The_picked_effort_is_sent_when_the_model_takes_it(string? picked, string sent)
    {
        var stub = new StubOpenRouter();

        (await stub.Parser().TestAsync("sk-or-v1-key", "openai/gpt-x", default, picked)).Should().BeNull();
        stub.SentBody!["reasoning"]!["effort"]!.GetValue<string>().Should().Be(sent);

        await stub.Namer().NameAsync([new Plate.Core.Nutrition.NameRequest("banana", "Bananas, raw", null, Plate.Core.Nutrition.FoodState.Raw)], "sk-or-v1-key", "openai/gpt-x", default, picked);
        stub.SentBody!["reasoning"]!["effort"]!.GetValue<string>().Should().Be(sent);
        await stub.Onboarding().TestAsync("sk-or-v1-key", "openai/gpt-x", default, picked);
        stub.SentBody!["reasoning"]!["effort"]!.GetValue<string>().Should().Be(sent);
    }

    [Fact]
    public async Task An_effort_is_saved_only_when_the_tasks_model_takes_it()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var store = Store(s, new StubOpenRouter());
        try
        {
            (await store.GetEffortAsync(AiTask.Parser, default)).Should().BeNull("the lowest is the default");
            await store.SetEffortAsync(AiTask.Parser, "LOW", default);
            (await store.GetEffortAsync(AiTask.Parser, default)).Should().Be("low");
            (await store.GetEffortAsync(AiTask.Weekly, default)).Should().BeNull("each task has its own");

            await store.Invoking(x => x.SetEffortAsync(AiTask.Parser, "medium", default)).Should().ThrowAsync<ValidationException>();
            await store.Invoking(x => x.SetEffortAsync(AiTask.Matcher, "low", default)).Should().ThrowAsync<ValidationException>();

            await store.SetEffortAsync(AiTask.Parser, "lowest", default);
            (await store.GetEffortAsync(AiTask.Parser, default)).Should().BeNull();
            (await s.Db.AppSettings.AnyAsync(x => x.Name == AiTasks.EffortSettingName(AiTask.Parser))).Should().BeFalse();
        }
        finally
        {
            await s.Db.AppSettings.Where(x => x.Name.StartsWith("OpenRouter:")).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task The_admin_page_sets_the_effort_runs_the_check_with_it_and_shows_it()
    {
        var stub = new StubOpenRouter();
        await using var app = new AppFactory(fixture,
            settings: new Dictionary<string, string?> { ["OpenRouter:ApiKey"] = "sk-or-v1-test-0123456789abcdef", ["OpenRouter:Model"] = "openai/gpt-x" },
            services: s =>
            {
                s.AddHttpClient<OpenRouterModels>().ConfigurePrimaryHttpMessageHandler(() => stub);
                s.AddHttpClient<OpenRouterFoodParser>().ConfigurePrimaryHttpMessageHandler(() => stub);
            });
        try
        {
            var admin = await AdminTests.UnlockedDevAsync(app);
            var page = WebUtility.HtmlDecode(await admin.GetStringAsync("/admin/ai/model/parser"));
            page.Should().Contain("Reasoning effort").And.Contain("Lowest (none), the default").And.Contain("Now sending: none");
            var token = await AppFactory.TokenAsync(admin, "/admin/ai/model/parser");

            var set = await AppFactory.PostAsync(admin, "/admin/ai/effort/parser", token, new() { ["effort"] = "low" });
            AppFactory.IsRedirectTo(set, "/admin/ai/model/parser").Should().BeTrue();
            stub.SentBody!["reasoning"]!["effort"]!.GetValue<string>().Should().Be("low", "saving runs the parser's check with the new effort");
            WebUtility.HtmlDecode(await admin.GetStringAsync("/admin/ai/model/parser")).Should().Contain("Now sending: low");
            WebUtility.HtmlDecode(await admin.GetStringAsync("/admin/ai")).Should().Contain("effort low");

            // A real parse uses it too.
            var (user, _) = await app.SignedInClientAsync();
            var userToken = await AppFactory.TokenAsync(user, "/food/add");
            await AppFactory.PostAsync(user, "/food/parse", userToken, new() { ["date"] = "2026-11-21", ["meal"] = "Lunch", ["text"] = "1 banana" });
            stub.SentBody!["reasoning"]!["effort"]!.GetValue<string>().Should().Be("low");

            (await AppFactory.PostAsync(admin, "/admin/ai/effort/parser", token, new() { ["effort"] = "medium" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await AppFactory.PostAsync(admin, "/admin/ai/effort/matcher", token, new() { ["effort"] = "low" })).StatusCode.Should().Be(HttpStatusCode.NotFound);

            AppFactory.IsRedirectTo(await AppFactory.PostAsync(admin, "/admin/ai/effort/parser", token, new() { ["effort"] = "lowest" }), "/admin/ai/model/parser").Should().BeTrue();
            stub.SentBody!["reasoning"]!["effort"]!.GetValue<string>().Should().Be("none");

            using var scope = app.Services.CreateScope();
            var audits = await scope.ServiceProvider.GetRequiredService<AppDbContext>().AdminAudit.AsNoTracking().Where(a => a.Action == AuditActions.EffortSet).OrderBy(a => a.Id).Select(a => a.Detail).ToListAsync();
            audits.TakeLast(2).Should().Equal("lowest -> low", "low -> lowest");
        }
        finally
        {
            using var scope = app.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().AppSettings.Where(x => x.Name.StartsWith("OpenRouter:")).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task The_CLI_sets_and_shows_the_effort()
    {
        var stub = new StubOpenRouter();
        await using var app = new AppFactory(fixture,
            settings: new Dictionary<string, string?> { ["OpenRouter:ApiKey"] = "sk-or-v1-test-0123456789abcdef", ["OpenRouter:Model"] = "openai/gpt-x" },
            services: s =>
            {
                s.AddHttpClient<OpenRouterModels>().ConfigurePrimaryHttpMessageHandler(() => stub);
                s.AddHttpClient<OpenRouterFoodParser>().ConfigurePrimaryHttpMessageHandler(() => stub);
            });
        try
        {
            var output = new StringWriter();
            (await AiCommand.RunAsync(app.Services, ["ai", "set-effort", "parser", "low"], output)).Should().Be(0);
            output.ToString().Should().Contain("Parser effort is now low.").And.Contain("Parser: works.");
            stub.SentBody!["reasoning"]!["effort"]!.GetValue<string>().Should().Be("low");

            var show = new StringWriter();
            await AiCommand.RunAsync(app.Services, ["ai", "show"], show);
            show.ToString().Should().Contain("Parser: openai/gpt-x, effort low of high/low/none");

            (await AiCommand.RunAsync(app.Services, ["ai", "set-effort", "matcher", "low"], new StringWriter())).Should().Be(1);
        }
        finally
        {
            using var scope = app.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().AppSettings.Where(x => x.Name.StartsWith("OpenRouter:")).ExecuteDeleteAsync();
        }
    }
}
