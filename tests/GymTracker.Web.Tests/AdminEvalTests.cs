using System.Net;
using System.Reflection;
using FluentAssertions;
using GymTracker.Core.Nutrition;
using GymTracker.Web.Controllers;
using GymTracker.Web.Features.Admin;
using GymTracker.Web.Features.Feedback;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace GymTracker.Web.Tests;

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

    [Fact]
    public async Task One_evaluation_runs_at_a_time_in_the_background_with_its_limit_capped()
    {
        var evaluator = new HeldEvaluator();
        using var app = new AppFactory(fixture,
            settings: new Dictionary<string, string?> { ["OpenRouter:ApiKey"] = "sk-or-v1-test-0123456789abcdef" },
            services: s => s.AddScoped<IParseEvaluator>(_ => evaluator));
        var admin = await AdminTests.UnlockedDevAsync(app);
        var token = await AppFactory.TokenAsync(admin, "/admin/ai/eval");

        var start = await AppFactory.PostAsync(admin, "/admin/ai/eval", token, new() { ["limit"] = "99", ["since"] = "30d", ["matcher"] = "jev" });
        AppFactory.IsRedirectTo(start, "/admin/ai/eval").Should().BeTrue();

        // The run starts on a background thread; wait for its first progress report.
        var running = await StatusAsync(admin, s => s.Contains("1 of 3"));
        running.Should().Contain("Running").And.Contain("1 of 3 parses").And.Contain("hx-trigger=\"every 3s\"");
        (await AppFactory.PostAsync(admin, "/admin/ai/eval", token, new() { ["limit"] = "5" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        evaluator.Last!.Limit.Should().Be(EvalJobs.MaxLimit);
        evaluator.Last.MatcherModel.Should().Be("jev");
        evaluator.Last.Since.Should().BeCloseTo(DateTimeOffset.UtcNow.AddDays(-30), TimeSpan.FromMinutes(1));

        evaluator.Release.SetResult();
        var done = await StatusAsync(admin, s => !s.Contains("Running"));
        done.Should().Contain("Total: food 2/2 (100%)").And.Contain("run 7").And.NotContain("hx-trigger", "polling stops when the run ends");
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
