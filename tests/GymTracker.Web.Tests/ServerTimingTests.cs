using System.Text.RegularExpressions;
using FluentAssertions;

namespace GymTracker.Web.Tests;

[Collection(PostgresCollection.Name)]
public class ServerTimingTests(PostgresFixture db) : IDisposable
{
    private readonly AppFactory app = new(db);

    public void Dispose() => app.Dispose();

    [Fact]
    public async Task Pages_report_app_and_database_time()
    {
        var (email, password) = await app.CreateUserAsync();
        var client = app.NewClient();
        await AppFactory.LoginAsync(client, email, password);

        var response = await client.GetAsync("/today");

        var header = string.Join(",", response.Headers.GetValues("Server-Timing"));
        header.Should().MatchRegex(@"app;dur=\d+(\.\d+)?");
        Regex.Match(header, "db;dur=[0-9.]+;desc=\"(\\d+) queries\"").Success.Should().BeTrue(header);
    }
}
