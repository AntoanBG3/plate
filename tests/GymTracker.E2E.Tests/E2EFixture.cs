using System.Net;
using System.Net.Sockets;
using GymTracker.Web.Tests;
using Microsoft.Playwright;

namespace GymTracker.E2E.Tests;

/// <summary>
/// The real app on Kestrel at http://localhost (a secure context, so service workers and passkeys work without a trusted
/// certificate), on the test Postgres, with Playwright's Chromium and WebKit. WebKit is the closest engine to iOS Safari.
/// </summary>
public sealed class E2EFixture : IAsyncLifetime
{
    public const string Chromium = "chromium";
    public const string WebKit = "webkit";

    private readonly PostgresFixture db = new();
    private AppFactory? factory;
    private IPlaywright? playwright;
    private readonly Dictionary<string, IBrowser> browsers = [];

    public string BaseUrl { get; private set; } = "";

    public AppFactory App => factory ?? throw new InvalidOperationException("Not started.");

    public PostgresFixture Db => db;

    public async Task InitializeAsync()
    {
        await db.InitializeAsync();

        var port = FreePort();
        // Every test signs in from the same address, so the login and passkey limits are raised for the run.
        factory = new AppFactory(db, settings: new Dictionary<string, string?>
        {
            ["Auth:HostCookies"] = "false",
            ["RateLimits:LoginGlobalPerWindow"] = "10000",
            ["RateLimits:PasskeyOptionsPerWindow"] = "10000",
        });
        factory.UseKestrel(o => o.ListenLocalhost(port));
        factory.StartServer();
        BaseUrl = $"http://localhost:{port}";

        // Downloads the browsers on the first run only; later runs find them in the Playwright cache.
        var exit = Microsoft.Playwright.Program.Main(["install", Chromium, WebKit]);
        if (exit != 0)
        {
            throw new InvalidOperationException($"Playwright browser install failed with exit code {exit}.");
        }

        playwright = await Playwright.CreateAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var browser in browsers.Values)
        {
            await browser.CloseAsync();
        }

        playwright?.Dispose();
        if (factory is not null)
        {
            await factory.DisposeAsync();
        }

        await db.DisposeAsync();
    }

    private async Task<IBrowser> BrowserAsync(string name)
    {
        if (!browsers.TryGetValue(name, out var browser))
        {
            var type = name == WebKit ? playwright!.Webkit : playwright!.Chromium;
            browser = await type.LaunchAsync(new() { Headless = true });
            browsers[name] = browser;
        }

        return browser;
    }

    /// <summary>A phone-sized page (390 px unless given), in English unless <paramref name="locale"/> says otherwise.</summary>
    public async Task<Phone> PhoneAsync(string browserName, int width = 390, string locale = "en-GB")
    {
        var browser = await BrowserAsync(browserName);
        var context = await browser.NewContextAsync(new()
        {
            BaseURL = BaseUrl,
            ViewportSize = new() { Width = width, Height = 844 },
            DeviceScaleFactor = 3,
            // Chromium and WebKit both support mobile emulation (meta viewport, touch events).
            IsMobile = true,
            HasTouch = true,
            Locale = locale,
            ColorScheme = ColorScheme.Dark,
        });
        var page = await context.NewPageAsync();
        return new Phone(this, browserName, context, page);
    }

    /// <summary>A new onboarded account, signed in on a new phone.</summary>
    public async Task<Phone> SignedInPhoneAsync(string browserName, int width = 390, string locale = "en-GB")
    {
        var (email, password) = await App.CreateUserAsync();
        var phone = await PhoneAsync(browserName, width, locale);
        await phone.SignInAsync(email, password);
        phone.Email = email;
        return phone;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

[CollectionDefinition(Name)]
public class E2ECollection : ICollectionFixture<E2EFixture>
{
    public const string Name = "E2E";
}
