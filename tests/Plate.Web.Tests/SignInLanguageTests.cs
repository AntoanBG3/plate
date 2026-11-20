using System.Net;
using FluentAssertions;

namespace Plate.Web.Tests;

/// <summary>A phone set to Bulgarian with no language cookie (a new device, or a new host) gets the sign-in page in Bulgarian.</summary>
public class SignInLanguageTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task The_sign_in_page_is_in_Bulgarian_on_a_Bulgarian_phone_without_cookies()
    {
        await using var app = new AppFactory(fixture);
        var client = app.NewClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("bg-BG");

        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/account/login"));

        html.Should().Contain("Вход с ключ за достъп").And.Contain("Парола");
    }
}
