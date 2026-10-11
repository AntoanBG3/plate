using System.Text.RegularExpressions;
using FluentAssertions;

namespace GymTracker.Web.Tests;

public class ServerTimingTests(PostgresFixture db) : IDisposable, IClassFixture<PostgresFixture>
{
    private readonly AppFactory app = new(db);

    public void Dispose() => app.Dispose();

    [Fact]
    public async Task Pages_report_app_and_database_time()
    {
        var (client, _) = await app.SignedInClientAsync();

        var response = await client.GetAsync("/today");

        var header = string.Join(",", response.Headers.GetValues("Server-Timing"));
        header.Should().MatchRegex(@"app;dur=\d+(\.\d+)?");
        Regex.Match(header, "db;dur=[0-9.]+;desc=\"(\\d+) queries\"").Success.Should().BeTrue(header);
    }

    [Fact]
    public async Task Today_page_stays_within_its_query_budget()
    {
        var (client, _) = await app.SignedInClientAsync();
        await client.GetAsync("/today"); // the first load may store the initial maintenance estimate

        var response = await client.GetAsync("/today");

        var header = string.Join(",", response.Headers.GetValues("Server-Timing"));
        var queries = int.Parse(Regex.Match(header, "desc=\"(\\d+) queries\"").Groups[1].Value);
        // 10 before the weekly summary (SPEC 6.5), which adds its stored-summary lookup and the AI settings read.
        queries.Should().BeLessThanOrEqualTo(12, header);
    }
}
