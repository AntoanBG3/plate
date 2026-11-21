using System.Net;
using System.Reflection;
using FluentAssertions;
using Plate.Core.Nutrition;
using Plate.Web.Cli;
using Plate.Web.Controllers;
using Plate.Web.Features.Admin;
using Plate.Web.Features.Feedback;
using Plate.Web.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Plate.Web.Tests;

/// <summary>The admin area's background evaluation, and the guard that keeps admin services behind the admin policy (SPEC 12).</summary>
public class AdminEvalTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    /// <summary>Holds each run until the test lets it finish, reporting one case of three on the way.</summary>
    private sealed class HeldEvaluator : IParseEvaluator
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public EvalRequest? Last { get; private set; }

        public async Task<EvalReport> RunAsync(EvalRequest request, Action<EvalCaseResult, int, int>? onCase, CancellationToken ct)
        {
            Last = request;
            var first = new EvalCaseResult(7, "2 eggs", new AgreementResult(2, 2, 1, 0), 0, null, []);
            onCase?.Invoke(first, 1, 3);
            await Release.Task.WaitAsync(ct);
            return new EvalReport("openai/test", "off", new AgreementResult(2, 2, 1, 0), 0, [first]);
        }
    }

    /// <summary>Counts runs and reports one case.</summary>
    private sealed class CountingEvaluator(EvalCaseResult result) : IParseEvaluator
    {
        public int Runs { get; private set; }

        public Task<EvalReport> RunAsync(EvalRequest request, Action<EvalCaseResult, int, int>? onCase, CancellationToken ct)
        {
            Runs++;
            onCase?.Invoke(result, 1, 1);
            return Task.FromResult(new EvalReport("openai/test", "off", result.Score, result.RepeatedWrongFoods, [result]));
        }
    }

    private static readonly EvalCaseResult CottageCheese = new(19, "200g cottage cheese", new AgreementResult(1, 0, 1, 0), 0, null,
        [new EvalFresh("cottage cheese", 200, 5, "Извара")],
        [new EvalFresh("cottage cheese", 200, 2591, "Котидж сирене, 2% масленост")]);

    private AppFactory EvalApp(IParseEvaluator evaluator) => new(fixture,
        settings: new Dictionary<string, string?> { ["OpenRouter:ApiKey"] = "sk-or-v1-test-0123456789abcdef" },
        services: s => s.AddScoped(_ => evaluator));

    // "feedback eval --help" once ran a full evaluation, which spends OpenRouter credit.
    [Theory]
    [InlineData(new[] { "--help" }, 0)]
    [InlineData(new[] { "--limt", "5" }, 2)]
    [InlineData(new[] { "--limit" }, 2)]
    public async Task Eval_runs_nothing_for_help_or_an_option_it_does_not_know(string[] options, int exit)
    {
        var evaluator = new CountingEvaluator(CottageCheese);
        using var app = EvalApp(evaluator);
        var output = new StringWriter();

        (await FeedbackCommand.RunAsync(app.Services, ["feedback", "eval", .. options], output)).Should().Be(exit);

        evaluator.Runs.Should().Be(0);
        if (exit == 0)
        {
            output.ToString().Should().Contain("spends OpenRouter credit");
        }
    }

    [Fact]
    public async Task Eval_shows_what_was_saved_beside_what_it_gives_now_for_a_case_that_disagrees()
    {
        var evaluator = new CountingEvaluator(CottageCheese);
        using var app = EvalApp(evaluator);
        var output = new StringWriter();

        (await FeedbackCommand.RunAsync(app.Services, ["feedback", "eval", "--limit", "1", "--flagged"], output)).Should().Be(0);

        evaluator.Runs.Should().Be(1);
        output.ToString().Should().Contain("    saved 200 g cottage cheese → Котидж сирене, 2% масленост #2591")
            .And.Contain("    now   200 g cottage cheese → Извара #5");
    }

    [Fact]
    public async Task One_evaluation_runs_at_a_time_in_the_background_with_its_limit_capped()
    {
        var evaluator = new HeldEvaluator();
        using var app = new AppFactory(fixture,
            settings: new Dictionary<string, string?> { ["OpenRouter:ApiKey"] = "sk-or-v1-test-0123456789abcdef" },
            services: s =>
            {
                s.AddScoped<IParseEvaluator>(_ => evaluator);
                s.AddHttpClient<OpenRouterModels>().ConfigurePrimaryHttpMessageHandler(() => new StubOpenRouter());
            });
        var admin = await AdminTests.UnlockedDevAsync(app);
        (await admin.GetStringAsync("/admin/ai/eval")).Should().Contain("value=\"typesafe/jev-1.14\"", "the matcher choices are OpenRouter's decision models");
        var token = await AppFactory.TokenAsync(admin, "/admin/ai/eval");
        (await AppFactory.PostAsync(admin, "/admin/ai/eval", token, new() { ["matcher"] = "made/up" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var start = await AppFactory.PostAsync(admin, "/admin/ai/eval", token, new() { ["limit"] = "99", ["since"] = "30d", ["matcher"] = "typesafe/jev-1.14" });
        AppFactory.IsRedirectTo(start, "/admin/ai/eval").Should().BeTrue();

        // The run starts on a background thread; wait for its first progress report.
        var running = await StatusAsync(admin, s => s.Contains("1 of 3"));
        running.Should().Contain("Running").And.Contain("1 of 3 parses").And.Contain("hx-trigger=\"every 3s\"");
        (await AppFactory.PostAsync(admin, "/admin/ai/eval", token, new() { ["limit"] = "5" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        evaluator.Last!.Limit.Should().Be(EvalJobs.MaxLimit);
        evaluator.Last.MatcherModel.Should().Be("typesafe/jev-1.14");
        evaluator.Last.Since.Should().BeCloseTo(DateTimeOffset.UtcNow.AddDays(-30), TimeSpan.FromMinutes(1));

        evaluator.Release.SetResult();
        var done = await StatusAsync(admin, s => !s.Contains("Running"));
        done.Should().Contain("Agreement with the saved log: food 2/2 (100%)").And.Contain("run 7").And.NotContain("hx-trigger", "polling stops when the run ends");
    }

    private static async Task<string> StatusAsync(HttpClient admin, Func<string, bool> until)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var html = await admin.GetStringAsync("/admin/ai/eval/status");
            if (until(html) || DateTime.UtcNow > deadline)
            {
                return html;
            }

            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task An_unknown_parser_model_or_no_key_is_refused_before_anything_runs()
    {
        var evaluator = new HeldEvaluator();
        using var app = new AppFactory(fixture, services: s => s.AddScoped<IParseEvaluator>(_ => evaluator));
        var admin = await AdminTests.UnlockedDevAsync(app);
        var token = await AppFactory.TokenAsync(admin, "/admin/ai/eval");
        (await AppFactory.PostAsync(admin, "/admin/ai/eval", token, new() { ["limit"] = "5" })).StatusCode.Should().Be(HttpStatusCode.BadRequest, "no key is set");
        evaluator.Last.Should().BeNull();
    }

    [Fact]
    public void Every_admin_route_has_the_admin_policy_and_only_admin_controllers_take_admin_services()
    {
        using var app = new AppFactory(fixture);
        var endpoints = app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        var admin = endpoints.Where(e => e.RoutePattern.RawText?.TrimStart('/').StartsWith("admin", StringComparison.OrdinalIgnoreCase) == true).ToList();
        admin.Should().NotBeEmpty();
        foreach (var e in admin)
        {
            var policies = e.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).ToList();
            policies.Should().Contain(p => p == AdminPolicy.Name || p == AdminPolicy.Unlock, e.RoutePattern.RawText);
            e.Metadata.GetMetadata<IAllowAnonymous>().Should().BeNull(e.RoutePattern.RawText);
        }

        admin.Where(e => e.Metadata.GetOrderedMetadata<IAuthorizeData>().All(a => a.Policy != AdminPolicy.Name))
            .Select(e => e.Metadata.GetMetadata<ControllerActionDescriptor>()!.ControllerTypeInfo.AsType())
            .Distinct().Should().Equal([typeof(AdminUnlockController)], "only the unlock itself may go without an unlock");

        // FeedbackAdmin and LabelScanReport read every account's data; AiAdmin and EvalJobs change shared settings and spend credit.
        Type[] adminServices = [typeof(FeedbackAdmin), typeof(LabelScanReport), typeof(AiAdmin), typeof(EvalJobs), typeof(IParseEvaluator)];
        var controllers = endpoints.Select(e => e.Metadata.GetMetadata<ControllerActionDescriptor>()?.ControllerTypeInfo.AsType()).OfType<Type>().Distinct();
        foreach (var controller in controllers)
        {
            var takes = controller.GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType);
            if (takes.Intersect(adminServices).Any())
            {
                controller.Name.Should().StartWith("Admin");
                controller.GetCustomAttributes<AuthorizeAttribute>().Select(a => a.Policy).Should().Contain(AdminPolicy.Name, controller.Name);
            }
        }
    }
}
