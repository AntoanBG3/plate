using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using GymTracker.Web.Cli;
using GymTracker.Web.Data;
using GymTracker.Web.Data.Seed;
using GymTracker.Web.Features;
using GymTracker.Web.Features.Analytics;
using GymTracker.Web.Features.Food;
using GymTracker.Web.Integrations;
using GymTracker.Core.Nutrition;
using GymTracker.Web.Features.Body;
using GymTracker.Web.Features.Profile;
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

builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseNpgsql(config.GetConnectionString("Default") ?? throw new InvalidOperationException("ConnectionStrings:Default is not set.")));

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
builder.Services.AddSingleton(TimeProvider.System);
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

builder.Services.Configure<GeminiOptions>(config.GetSection("Gemini"));
builder.Services.AddHttpClient<GeminiFoodParser>(c =>
{
    c.BaseAddress = new Uri(config["Gemini:BaseUrl"] ?? "https://generativelanguage.googleapis.com/");
    c.Timeout = TimeSpan.FromSeconds(40);
});
builder.Services.AddScoped<GeminiKeyStore>();

// Gemini when a key is set (in Settings or GEMINI_API_KEY), otherwise a simple built-in parser.
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
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(o =>
{
    o.Cookie.Name = "gym.auth";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    o.ExpireTimeSpan = TimeSpan.FromDays(30);
    o.SlidingExpiration = true;
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

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(RateLimits.Login, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = config.GetValue("RateLimits:LoginPerWindow", 10),
            Window = TimeSpan.FromMinutes(5),
            QueueLimit = 0,
        }));
});

builder.Services.AddAuthorization(o => o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

builder.Services.AddControllersWithViews(o =>
{
    o.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
    o.Filters.Add<FeatureExceptionFilter>();
});
builder.Services.AddAntiforgery(o => o.HeaderName = "RequestVerificationToken");

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    await ExerciseSeed.SeedAsync(db, CancellationToken.None);
    await UsdaSeed.SeedAsync(db, UsdaSeed.DefaultPath, CancellationToken.None);
}

if (args.Length > 0 && args[0] == CreateUserCommand.Name)
{
    return await CreateUserCommand.RunAsync(app.Services, args);
}

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
}

app.UseStatusCodePagesWithReExecute("/error/{0}");
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets().AllowAnonymous();
app.MapControllers();

await app.RunAsync();
return 0;

public partial class Program;
