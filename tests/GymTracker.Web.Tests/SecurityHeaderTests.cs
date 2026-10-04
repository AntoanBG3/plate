using System.Net;
using FluentAssertions;

namespace GymTracker.Web.Tests;

[Collection(PostgresCollection.Name)]
public class SecurityHeaderTests(PostgresFixture db)
{
    [Theory]
    [InlineData("/account/login")]
    [InlineData("/css/app.css")]
    public async Task Responses_carry_the_security_headers(string path)
    {
        using var app = new AppFactory(db, settings: new Dictionary<string, string?> { ["Security:CspEnforce"] = "true" });
        var response = await app.NewClient().GetAsync(path);

        var h = response.Headers;
        h.GetValues("Content-Security-Policy").Single().Should().Contain("script-src 'self' 'wasm-unsafe-eval'").And.Contain("frame-ancestors 'none'");
        h.GetValues("X-Content-Type-Options").Single().Should().Be("nosniff");
        h.GetValues("Referrer-Policy").Single().Should().Be("same-origin");
        h.GetValues("Permissions-Policy").Single().Should().Contain("camera=(self)");
        h.GetValues("X-Frame-Options").Single().Should().Be("DENY");
        h.GetValues("Strict-Transport-Security").Single().Should().StartWith("max-age=31536000");
        h.Contains("Server").Should().BeFalse();
    }

    [Fact]
    public async Task Csp_is_report_only_until_enforced()
    {
        using var app = new AppFactory(db);
        var response = await app.NewClient().GetAsync("/account/login");

        response.Headers.Contains("Content-Security-Policy").Should().BeFalse();
        response.Headers.GetValues("Content-Security-Policy-Report-Only").Single().Should().Contain("report-uri /csp-report");
    }

    [Fact]
    public async Task Csp_reports_are_accepted_without_login_or_token()
    {
        using var app = new AppFactory(db);
        var content = new StringContent("{\"csp-report\":{\"violated-directive\":\"script-src\"}}", System.Text.Encoding.UTF8, "application/csp-report");

        var response = await app.NewClient().PostAsync("/csp-report", content);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Unknown_hosts_are_rejected()
    {
        using var app = new AppFactory(db, settings: new Dictionary<string, string?> { ["AllowedHosts"] = "localhost" });
        var request = new HttpRequestMessage(HttpMethod.Get, "/account/login");
        request.Headers.Host = "evil.example";

        (await app.NewClient().SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
