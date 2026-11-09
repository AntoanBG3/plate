using Microsoft.Extensions.DependencyInjection;
using Plate.Web.Tests;
using Microsoft.Playwright;

namespace Plate.E2E.Tests;

/// <summary>
/// The real app on Kestrel at http://localhost (a secure context, so service workers and passkeys work without a trusted
/// certificate), on the test Postgres, with Playwright's Chromium and WebKit. WebKit is the closest engine to iOS Safari.
/// Each test class gets its own fixture, with its own database and apps, so classes run in parallel; the browsers are
/// shared by the whole run and every phone is a new browser context.
/// </summary>
public sealed class E2EFixture : IAsyncLifetime
{
    public const string Chromium = "chromium";
    public const string WebKit = "webkit";

    private static readonly Lazy<Task<IPlaywright>> Playwright = new(StartPlaywrightAsync);
    private static readonly Dictionary<string, Task<IBrowser>> Browsers = [];

    private readonly PostgresFixture db = new();
    private AppFactory? factory;
    private AppFactory? aiFactory;

    public string BaseUrl { get; private set; } = "";

    /// <summary>A second app on the same database whose AI models are stubs (AiStubs.cs), as if a key were set.</summary>
    public string AiBaseUrl { get; private set; } = "";

    public AppFactory App => factory ?? throw new InvalidOperationException("Not started.");

    public PostgresFixture Db => db;

    public async Task InitializeAsync()
    {
        await db.InitializeAsync();

        // Every test signs in from the same address, so the login, passkey and join limits are raised for the run.
        var settings = new Dictionary<string, string?>
        {
            ["Auth:HostCookies"] = "false",
            ["RateLimits:LoginGlobalPerWindow"] = "10000",
            ["RateLimits:PasskeyOptionsPerWindow"] = "10000",
            ["RateLimits:JoinPerWindow"] = "10000",
        };
        factory = new AppFactory(db, settings: settings);
        BaseUrl = StartOnLoopback(factory);

        aiFactory = new AppFactory(db, settings: settings, services: s =>
        {
            s.AddScoped<Plate.Core.Nutrition.IFoodParser, StubFoodParser>();
            s.AddScoped<Plate.Core.Nutrition.IFoodNamer, StubFoodNamer>();
            s.AddScoped<Plate.Core.Onboarding.IOnboardingAssistant, StubOnboardingAssistant>();
            s.AddScoped<Plate.Core.Summary.IWeeklySummaryWriter, StubWeeklySummaryWriter>();
            s.AddHttpContextAccessor();
            s.AddScoped<Plate.Core.Nutrition.ILabelReader, StubLabelReader>();
            // Barcode misses for label photos; never the real Open Food Facts from a test.
            s.AddSingleton<Plate.Core.Nutrition.IFoodDatabase, Plate.Web.Tests.FakeFoodDatabase>();
        });
        AiBaseUrl = StartOnLoopback(aiFactory);
    }

    private static async Task<IPlaywright> StartPlaywrightAsync()
    {
        // Downloads the browsers on the first run only; later runs find them in the Playwright cache.
        var exit = Microsoft.Playwright.Program.Main(["install", Chromium, WebKit]);
        if (exit != 0)
        {
            throw new InvalidOperationException($"Playwright browser install failed with exit code {exit}.");
        }

        var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        // The browsers outlive every fixture; close them with the test run so no browser process is left behind.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            Task<IBrowser>[] launched;
            lock (Browsers)
            {
                launched = [.. Browsers.Values];
            }

            foreach (var browser in launched.Where(b => b.IsCompletedSuccessfully))
            {
                browser.Result.CloseAsync().Wait(TimeSpan.FromSeconds(5));
            }

            playwright.Dispose();
        };
        return playwright;
    }

    public async Task DisposeAsync()
    {
        if (factory is not null)
        {
            await factory.DisposeAsync();
        }

        if (aiFactory is not null)
        {
            await aiFactory.DisposeAsync();
        }

        await db.DisposeAsync();
    }

    private static Task<IBrowser> BrowserAsync(string name)
    {
        lock (Browsers)
        {
            if (!Browsers.TryGetValue(name, out var browser))
            {
                browser = LaunchAsync(name);
                Browsers[name] = browser;
            }

            return browser;
        }
    }

    private static async Task<IBrowser> LaunchAsync(string name)
    {
        var playwright = await Playwright.Value;
        var type = name == WebKit ? playwright.Webkit : playwright.Chromium;
        return await type.LaunchAsync(new() { Headless = true });
    }

    /// <summary>A phone-sized page (390 px unless given), in English unless <paramref name="locale"/> says otherwise.</summary>
    public async Task<Phone> PhoneAsync(string browserName, int width = 390, string locale = "en-GB", bool ai = false)
    {
        var browser = await BrowserAsync(browserName);
        var context = await browser.NewContextAsync(new()
        {
            BaseURL = ai ? AiBaseUrl : BaseUrl,
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
    public async Task<Phone> SignedInPhoneAsync(string browserName, int width = 390, string locale = "en-GB", bool ai = false)
    {
        var (email, password) = await App.CreateUserAsync();
        var phone = await PhoneAsync(browserName, width, locale, ai);
        await phone.SignInAsync(email, password);
        phone.Email = email;
        return phone;
    }

    private static string StartOnLoopback(AppFactory app)
    {
        app.UseKestrel(0);
        app.StartServer();
        // Keep localhost as the browser origin for the passkey tests.
        return new UriBuilder(app.ClientOptions.BaseAddress) { Host = "localhost" }.Uri.AbsoluteUri;
    }
}

