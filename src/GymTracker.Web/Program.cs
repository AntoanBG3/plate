using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using GymTracker.Web.Cli;
using GymTracker.Web.Data;
using GymTracker.Web.Data.Seed;
using GymTracker.Web.Features;
using GymTracker.Web.Features.Analytics;
using GymTracker.Web.Features.Diagnostics;
using GymTracker.Web.Features.Food;
using GymTracker.Web.Features.Localization;
using GymTracker.Web.Integrations;
using GymTracker.Core.Nutrition;
using GymTracker.Web.Features.Body;
using GymTracker.Web.Features.Profile;
using GymTracker.Web.Features.Security;
using GymTracker.Web.Features.Training;
using GymTracker.Web.Features.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

// Number inputs always submit "82.5" whatever the phone's locale, so parse and format with the
// invariant culture. Without this a server running under e.g. bg-BG reads "82.5" as invalid.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

builder.WebHost.ConfigureKestrel(k =>
{
    k.AddServerHeader = false;
    // Nothing in the app uploads files; barcode photos are decoded in the browser.
    k.Limits.MaxRequestBodySize = 1024 * 1024;
});

builder.Services.AddSingleton<DbTimingInterceptor>();
builder.Services.AddDbContext<AppDbContext>((sp, o) => o
    .UseNpgsql(config.GetConnectionString("Default") ?? throw new InvalidOperationException("ConnectionStrings:Default is not set."))
    .AddInterceptors(sp.GetRequiredService<DbTimingInterceptor>()));

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(Localizer.Load(builder.Environment.ContentRootPath));
builder.Services.AddScoped<ProfileService>();
builder.Services.AddScoped<ProfileLookup>();
builder.Services.AddScoped<BodyweightService>();
builder.Services.AddScoped<BodyAnalytics>();
builder.Services.AddScoped<TemplateService>();
builder.Services.AddScoped<SessionService>();
builder.Services.AddScoped<ExerciseService>();
builder.Services.AddScoped<TrainingAnalytics>();
builder.Services.AddScoped<FoodItemService>();
builder.Services.AddScoped<FoodMatcher>();
builder.Services.AddScoped<FoodLogService>();
builder.Services.AddScoped<NutritionTargets>();
builder.Services.AddScoped<GymTracker.Web.Features.Export.DataExport>();

builder.Services.Configure<OpenRouterOptions>(config.GetSection("OpenRouter"));
var openRouterUrl = new Uri(config["OpenRouter:BaseUrl"] ?? new OpenRouterOptions().BaseUrl);
void OpenRouterClient(HttpClient c)
{
    c.BaseAddress = openRouterUrl;
    // Optional attribution headers OpenRouter shows in its usage logs.
    c.DefaultRequestHeaders.Add("HTTP-Referer", "https://gym.peychev.uk");
    c.DefaultRequestHeaders.Add("X-Title", "GymTracker");
}
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<OpenRouterModels>(c =>
{
    OpenRouterClient(c);
    c.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddHttpClient<OpenRouterFoodParser>(c =>
{
    OpenRouterClient(c);
    c.Timeout = TimeSpan.FromSeconds(40);
});
builder.Services.AddScoped<OpenRouterSettings>();

// OpenRouter when a key is set (in Settings or OPENROUTER_API_KEY), otherwise a simple built-in parser.
builder.Services.AddScoped<IFoodParser, SwitchingFoodParser>();

builder.Services.AddHttpClient<IFoodDatabase, OpenFoodFactsClient>(c =>
{
    c.BaseAddress = new Uri(config["OpenFoodFacts:BaseUrl"] ?? "https://world.openfoodfacts.org/");
    c.Timeout = TimeSpan.FromSeconds(10);
    // Open Food Facts requires an app name, version and contact.
    var contact = config["OpenFoodFacts:ContactEmail"];
    c.DefaultRequestHeaders.UserAgent.ParseAdd(string.IsNullOrWhiteSpace(contact) ? "GymTracker/1.0" : $"GymTracker/1.0 ({contact})");
});
builder.Services.AddScoped<UserTime>();
builder.Services.AddScoped<UserCreator>();

builder.Services
    .AddIdentity<AppUser, IdentityRole>(o =>
    {
        // Long passwords instead of composition rules.
        o.Password.RequiredLength = 12;
        o.Password.RequireDigit = false;
        o.Password.RequireLowercase = false;
        o.Password.RequireUppercase = false;
        o.Password.RequireNonAlphanumeric = false;
        o.Password.RequiredUniqueChars = 1;

        o.Lockout.AllowedForNewUsers = true;
        o.Lockout.MaxFailedAccessAttempts = 5;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);

        o.User.RequireUniqueEmail = true;
        o.SignIn.RequireConfirmedAccount = false;

        // Version 3 adds the passkeys table (Face ID / Touch ID sign-in).
        o.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders();

// __Host- cookies must be Secure with Path=/ and no Domain; local development runs over plain http.
var hostCookies = !builder.Environment.IsDevelopment();
// The passkey relying party: the public host name in production; unset locally, where the request's host is used.
builder.Services.Configure<IdentityPasskeyOptions>(o =>
{
    if (config["Passkeys:ServerDomain"] is { Length: > 0 } domain)
    {
        o.ServerDomain = domain;
    }
});

builder.Services.ConfigureApplicationCookie(o =>
{
    o.Cookie.Name = hostCookies ? "__Host-gym.auth" : "gym.auth";
    o.Cookie.Path = "/";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.Cookie.SecurePolicy = hostCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
    o.ExpireTimeSpan = TimeSpan.FromDays(30);
    o.SlidingExpiration = true;
    // Every sign-in is persistent, password or passkey: the app is used as an installed PWA and should stay
    // signed in when it is closed. (SignInManager.PasskeySignInAsync on its own issues a session cookie.)
    o.Events.OnSigningIn = context =>
    {
        context.Properties.IsPersistent = true;
        return Task.CompletedTask;
    };
    o.LoginPath = "/account/login";
    o.LogoutPath = "/account/logout";
    o.AccessDeniedPath = "/account/login";

    // htmx and fetch requests get a 401 instead of a redirect, so the page can send the user to log in
    // rather than swapping the login page into a set row.
    o.Events.OnRedirectToLogin = context =>
    {
        if (context.Request.Headers.ContainsKey("HX-Request"))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }

        context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    };
});

// "Log out everywhere" changes the security stamp; other devices notice within this interval.
builder.Services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(5));

var dataProtection = builder.Services.AddDataProtection().SetApplicationName("GymTracker");
if (config["DataProtection:KeysPath"] is { Length: > 0 } keysPath)
{
    // Without persisted keys every container rebuild would invalidate both users' cookies.
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
}

// Behind cloudflared every request arrives from the tunnel container. Trust only it, and take the
// client address from CF-Connecting-IP so login rate limiting sees real clients.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.ForwardedForHeaderName = "CF-Connecting-IP";
    o.RequireHeaderSymmetry = false;
    o.ForwardLimit = 1;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
    foreach (var proxy in (config["ReverseProxy:KnownProxies"] ?? "172.30.0.10").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        o.KnownProxies.Add(IPAddress.Parse(proxy));
    }
});

// Fixed-window limits keyed by client address or by signed-in user. Requests are refused, never queued.
RateLimitPartition<string> PerIp(HttpContext http, int permits, TimeSpan window) =>
    RateLimitPartition.GetFixedWindowLimiter(http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = window, QueueLimit = 0 });
RateLimitPartition<string> PerUser(HttpContext http, int permits, TimeSpan window) =>
    RateLimitPartition.GetFixedWindowLimiter(http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = window, QueueLimit = 0 });

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(RateLimits.Login, http => PerIp(http, config.GetValue("RateLimits:LoginPerWindow", 10), TimeSpan.FromMinutes(5)));

    // Distributed guessing: all sign-in attempts together, whatever the source address.
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
        HttpMethods.IsPost(http.Request.Method) && http.Request.Path.StartsWithSegments("/account/login")
            ? RateLimitPartition.GetSlidingWindowLimiter("login-all", _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = config.GetValue("RateLimits:LoginGlobalPerWindow", 30),
                Window = TimeSpan.FromMinutes(15),
                SegmentsPerWindow = 3,
                QueueLimit = 0,
            })
            : RateLimitPartition.GetNoLimiter("other"));

    // Password and API key changes.
    o.AddPolicy(RateLimits.Sensitive, http => PerUser(http, config.GetValue("RateLimits:SensitivePerWindow", 5), TimeSpan.FromMinutes(15)));
    // Food text parsing spends the shared OpenRouter balance.
    o.AddPolicy(RateLimits.AiParse, http => PerUser(http, config.GetValue("RateLimits:AiParsePerWindow", 30), TimeSpan.FromMinutes(10)));
    // The login page asks for passkey options on every view (autofill), so they get their own budget.
    o.AddPolicy(RateLimits.PasskeyOptions, http => PerIp(http, 30, TimeSpan.FromMinutes(5)));
    o.AddPolicy(RateLimits.CspReport, http => PerIp(http, 20, TimeSpan.FromMinutes(1)));
});

builder.Services.AddAuthorization(o => o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

builder.Services.AddControllersWithViews(o =>
{
    o.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
    o.Filters.Add<FeatureExceptionFilter>();
});
builder.Services.AddAntiforgery(o =>
{
    o.HeaderName = "RequestVerificationToken";
    o.SuppressXFrameOptionsHeader = true; // SecurityHeadersMiddleware sends DENY
    o.Cookie.Name = hostCookies ? "__Host-gym.af" : "gym.af";
    o.Cookie.SecurePolicy = hostCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
});

var app = builder.Build();
app.Logger.LogInformation("GC: server={Server}, latency={Latency}, heap limit={Limit} MiB",
    System.Runtime.GCSettings.IsServerGC, System.Runtime.GCSettings.LatencyMode,
    GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024);

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    await ExerciseSeed.SeedAsync(db, CancellationToken.None);
    await UsdaSeed.SeedAsync(db, UsdaSeed.DefaultPath, CancellationToken.None);
    await BgFoodSeed.SeedAsync(db, BgFoodSeed.DefaultPath, CancellationToken.None);
}

if (args.Length > 0 && args[0] == CreateUserCommand.Name)
{
    return await CreateUserCommand.RunAsync(app.Services, args);
}

app.UseForwardedHeaders();
app.UseMiddleware<ServerTimingMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
}

app.UseStatusCodePagesWithReExecute("/error/{0}");
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
// After authentication, so per-user limits see the signed-in user.
app.UseRateLimiter();
app.UseMiddleware<UserPreferencesMiddleware>();

app.MapStaticAssets().AllowAnonymous();
app.MapControllers();

await app.RunAsync();
return 0;

public partial class Program;
