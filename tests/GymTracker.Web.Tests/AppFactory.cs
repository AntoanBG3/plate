using System.Net;
using System.Text.RegularExpressions;
using GymTracker.Web.Data;
using GymTracker.Web.Features.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace GymTracker.Web.Tests;

/// <summary>The real app on the test Postgres, in a non-Development environment like production.</summary>
public sealed class AppFactory(PostgresFixture db, int loginLimit = 1000, IDictionary<string, string?>? settings = null, Action<IServiceCollection>? services = null)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        // Outside Development the host doesn't load the static web assets manifest, which maps the compressed copies
        // in obj/; without it every request with Accept-Encoding gets an empty 200 (browsers always send one).
        builder.UseStaticWebAssets();
        builder.UseSetting("ConnectionStrings:Default", db.ConnectionString);
        builder.UseSetting("RateLimits:LoginPerWindow", loginLimit.ToString());
        // Never reach the real OpenRouter from tests: nothing listens on the discard port.
        builder.UseSetting("OpenRouter:BaseUrl", "http://127.0.0.1:9/");
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            builder.UseSetting(key, value);
        }

        if (services is not null)
        {
            builder.ConfigureTestServices(services);
        }
    }

    public static WebApplicationFactoryClientOptions HttpsClientOptions => new()
    {
        // https so the Secure auth cookie is sent back.
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
        HandleCookies = true,
    };

    public HttpClient NewClient() => CreateClient(HttpsClientOptions);

    /// <summary>A new user, signed in on a new client.</summary>
    public async Task<(HttpClient Client, string Email)> SignedInClientAsync()
    {
        var (email, password) = await CreateUserAsync();
        var client = NewClient();
        await LoginAsync(client, email, password);
        return (client, email);
    }

    /// <summary>A new account. Unless <paramref name="onboarded"/> is false it has done the setup chat, so pages load directly.</summary>
    public async Task<(string Email, string Password)> CreateUserAsync(bool onboarded = true)
    {
        var email = $"{Guid.NewGuid():N}@test.local";
        const string password = "correct horse battery";
        using var scope = Services.CreateScope();
        var errors = await scope.ServiceProvider.GetRequiredService<UserCreator>().CreateAsync(email, "Test", password, default);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" ", errors));
        }

        if (onboarded)
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var id = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
            await db.Profiles.IgnoreQueryFilters().Where(p => p.UserId == id)
                .ExecuteUpdateAsync(p => p.SetProperty(x => x.OnboardedAt, DateTimeOffset.UtcNow));
        }

        return (email, password);
    }

    public static async Task<string> TokenAsync(HttpClient client, string path)
    {
        var html = await client.GetStringAsync(path);
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");
        if (!match.Success)
        {
            match = Regex.Match(html, "\"RequestVerificationToken\": \"([^\"]+)\"");
        }

        return match.Success ? match.Groups[1].Value : throw new InvalidOperationException($"No antiforgery token on {path}.");
    }

    public static async Task<HttpResponseMessage> LoginAsync(HttpClient client, string email, string password, string? returnUrl = null)
    {
        var token = await TokenAsync(client, "/account/login");
        var fields = new Dictionary<string, string>
        {
            ["email"] = email,
            ["password"] = password,
            ["__RequestVerificationToken"] = token,
        };
        if (returnUrl is not null)
        {
            fields["returnUrl"] = returnUrl;
        }

        return await client.PostAsync("/account/login", new FormUrlEncodedContent(fields));
    }

    public static Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string token, Dictionary<string, string>? fields = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(fields ?? []) };
        request.Headers.Add("RequestVerificationToken", token);
        request.Headers.Add("HX-Request", "true");
        return client.SendAsync(request);
    }

    public static bool IsRedirectTo(HttpResponseMessage response, string pathPrefix) =>
        response.StatusCode == HttpStatusCode.Redirect && response.Headers.Location?.OriginalString.StartsWith(pathPrefix) == true;
}
