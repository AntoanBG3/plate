using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using GymTracker.Web.Cli;
using GymTracker.Web.Data;
using GymTracker.Web.Data.Seed;
using GymTracker.Web.Features;
using GymTracker.Web.Features.Admin;
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

// The CLI commands print to stdout for piping over SSH (feedback attachments are raw bytes), so their logs go to stderr.
if (args.Length > 0 && args[0] is FeedbackCommand.Name or RoleCommand.Name or AiCommand.Name or AdminCommand.Name
    or InviteCommand.Name or ResetPasswordCommand.Name or DeleteUserCommand.Name)
{
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    // A console-specific rule wins over the general levels in appsettings.
    builder.Logging.AddFilter<Microsoft.Extensions.Logging.Console.ConsoleLoggerProvider>(null, LogLevel.Warning);
}

builder.WebHost.ConfigureKestrel(k =>
{
    k.AddServerHeader = false;
    // Barcode photos are decoded in the browser; feedback screenshots are shrunk there first (SPEC 11).
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
builder.Services.AddScoped<ExerciseSettingsService>();
builder.Services.AddScoped<TrainingAnalytics>();
builder.Services.AddScoped<FoodItemService>();
builder.Services.AddScoped<FoodMatcher>();
builder.Services.AddScoped<FoodLogService>();
builder.Services.AddScoped<SavedMealService>();
builder.Services.AddScoped<GymTracker.Web.Features.Feedback.ParseRunService>();
builder.Services.AddScoped<GymTracker.Web.Features.Feedback.FeedbackService>();
builder.Services.AddScoped<GymTracker.Web.Features.Feedback.FeedbackSnapshots>();
builder.Services.AddScoped<GymTracker.Web.Features.Feedback.FeedbackAdmin>();
builder.Services.AddScoped<GymTracker.Web.Features.Feedback.Changelog>();
builder.Services.AddScoped<GymTracker.Web.Features.Feedback.ModelAccuracyReport>();
builder.Services.AddScoped<GymTracker.Web.Features.Feedback.LabelScanReport>();
builder.Services.AddScoped<GymTracker.Web.Features.Feedback.IParseEvaluator, GymTracker.Web.Features.Feedback.ParseEvaluator>();
builder.Services.Configure<GymTracker.Web.Features.Feedback.TelegramOptions>(config.GetSection("Telegram"));
builder.Services.AddSingleton<GymTracker.Web.Features.Feedback.FeedbackNotifier>();
builder.Services.AddSingleton<GymTracker.Web.Features.Feedback.IFeedbackNotifier>(sp => sp.GetRequiredService<GymTracker.Web.Features.Feedback.FeedbackNotifier>());
builder.Services.AddHostedService<GymTracker.Web.Features.Feedback.FeedbackNotifierWorker>();
builder.Services.AddSingleton<GymTracker.Web.Features.Feedback.ErrorRecorder>();
builder.Services.AddExceptionHandler<GymTracker.Web.Features.Feedback.ErrorCaptureHandler>();
// The bot token is part of Telegram's URL, so this client must not log request URLs.
builder.Services.AddHttpClient(GymTracker.Web.Features.Feedback.FeedbackNotifierWorker.HttpClientName).RemoveAllLoggers();
builder.Services.AddScoped<GymTracker.Web.Features.Undo.UndoService>();
builder.Services.AddHostedService<GymTracker.Web.Features.Undo.SoftDeletePurge>();
builder.Services.AddScoped<NutritionTargets>();
builder.Services.AddScoped<GymTracker.Web.Features.Export.DataExport>();

// The admin area (SPEC 12).
builder.Services.AddScoped<AdminAuditLog>();
builder.Services.AddScoped<InviteAdmin>();
builder.Services.AddScoped<UserDeleter>();
builder.Services.AddScoped<AiAdmin>();
builder.Services.AddSingleton<EvalJobs>();
builder.Services.Configure<CloudflareAccessOptions>(config.GetSection("Admin:CloudflareAccess"));
builder.Services.AddSingleton<CloudflareAccess>();
builder.Services.AddHttpClient(CloudflareAccess.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddScoped<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, AdminHandler>();
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler, AdminResultHandler>();

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
builder.Services.AddHttpClient<JevFoodMatchDecider>(c =>
{
    OpenRouterClient(c);
    // The decider cancels each call after 3 s itself; this is only a backstop.
    c.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddHttpClient<OpenRouterOnboardingAssistant>(c =>
{
    OpenRouterClient(c);
    c.Timeout = TimeSpan.FromSeconds(40);
});
builder.Services.AddHttpClient<OpenRouterWeeklySummaryWriter>(c =>
{
    OpenRouterClient(c);
    c.Timeout = TimeSpan.FromSeconds(50);
});
builder.Services.AddHttpClient<OpenRouterLabelReader>(c =>
{
    OpenRouterClient(c);
    c.Timeout = TimeSpan.FromSeconds(50);
});
builder.Services.AddScoped<OpenRouterSettings>();

// OpenRouter when a key is set (in Settings or OPENROUTER_API_KEY), otherwise a simple built-in parser.
builder.Services.AddScoped<IFoodParser, SwitchingFoodParser>();
// The matcher model picked in Settings when a key is set; otherwise the database ranking alone.
builder.Services.AddScoped<IFoodMatchDecider, SwitchingMatchDecider>();
// The setup chat's model when a key is set and it isn't off; otherwise the setup page shows only the form.
builder.Services.AddScoped<GymTracker.Core.Onboarding.IOnboardingAssistant, SwitchingOnboardingAssistant>();
builder.Services.AddScoped<GymTracker.Web.Features.Onboarding.OnboardingService>();
// The weekly model when a key is set and it isn't off; otherwise Today has no weekly summary.
builder.Services.AddScoped<GymTracker.Core.Summary.IWeeklySummaryWriter, SwitchingWeeklySummaryWriter>();
builder.Services.AddScoped<GymTracker.Web.Features.Summary.WeeklySummaryService>();
// The label model when a key is set and it isn't off; otherwise label photos are hidden (SPEC 6.6).
builder.Services.AddScoped<ILabelReader, SwitchingLabelReader>();
builder.Services.AddScoped<GymTracker.Web.Features.Food.LabelScanService>();

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
builder.Services.AddScoped<InviteService>();

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

// __Host- cookies must be Secure with Path=/ and no Domain; local development runs over plain http. The browser
// tests also run over http://localhost (no trusted certificate), so they turn this off with Auth:HostCookies.
var hostCookies = config.GetValue("Auth:HostCookies", !builder.Environment.IsDevelopment());
// The passkey relying party: the public host name in production; unset locally, where the request's host is used.
builder.Services.Configure<IdentityPasskeyOptions>(o =>
{
    if (config["Passkeys:ServerDomain"] is { Length: > 0 } domain)
    {
        o.ServerDomain = domain;
    }

    // Face ID / Touch ID every time, not just possession of the device: the admin unlock relies on it (SPEC 12).
    o.UserVerificationRequirement = "required";
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

// The admin unlock: a second, short-lived cookie (SPEC 12).
builder.Services.AddAuthentication().AddCookie(AdminUnlock.Scheme, o => AdminUnlock.Configure(o, hostCookies));

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
    // Setup chat messages spend it too (SPEC 6.4).
    o.AddPolicy(RateLimits.AiOnboarding, http => PerUser(http, config.GetValue("RateLimits:AiOnboardingPerWindow", 40), TimeSpan.FromMinutes(10)));
    // A failing weekly model is retried on Today visits; this caps how often (SPEC 6.5).
    o.AddPolicy(RateLimits.AiWeekly, http => PerUser(http, config.GetValue("RateLimits:AiWeeklyPerWindow", 6), TimeSpan.FromHours(1)));
    // Each label photo spends the shared OpenRouter balance too (SPEC 6.6).
    o.AddPolicy(RateLimits.AiLabel, http => PerUser(http, config.GetValue("RateLimits:AiLabelPerWindow", 20), TimeSpan.FromMinutes(10)));
    // The login page asks for passkey options on every view (autofill), so they get their own budget.
    o.AddPolicy(RateLimits.PasskeyOptions, http => PerIp(http, config.GetValue("RateLimits:PasskeyOptionsPerWindow", 30), TimeSpan.FromMinutes(5)));
    o.AddPolicy(RateLimits.CspReport, http => PerIp(http, 20, TimeSpan.FromMinutes(1)));
    // The invite page, GET and POST together (SPEC 9). Tokens are unguessable; this only caps noise.
    o.AddPolicy(RateLimits.Join, http => PerIp(http, config.GetValue("RateLimits:JoinPerWindow", 10), TimeSpan.FromMinutes(15)));
    // Reports and parse ratings, and browser error reports (SPEC 11).
    o.AddPolicy(RateLimits.Feedback, http => PerUser(http, config.GetValue("RateLimits:FeedbackPerWindow", 30), TimeSpan.FromMinutes(10)));
    o.AddPolicy(RateLimits.ClientError, http => PerUser(http, config.GetValue("RateLimits:ClientErrorPerWindow", 20), TimeSpan.FromMinutes(10)));
    o.AddPolicy(RateLimits.Admin, http => PerUser(http, config.GetValue("RateLimits:AdminPerWindow", 60), TimeSpan.FromMinutes(10)));
    // An unlock takes two requests (options, then the assertion), so 10 allow five attempts.
    o.AddPolicy(RateLimits.AdminUnlock, http => PerUser(http, config.GetValue("RateLimits:AdminUnlockPerWindow", 10), TimeSpan.FromMinutes(15)));
});

builder.Services.AddAuthorization(o =>
{
    o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    AdminPolicy.Add(o);
});

builder.Services.AddControllersWithViews(o =>
{
    o.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
    o.Filters.Add<FeatureExceptionFilter>();
    o.ModelBinderProviders.Insert(0, new DecimalModelBinderProvider());
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
    await DataFixups.RunAsync(db, CancellationToken.None);
}

if (args.Length > 0 && args[0] == CreateUserCommand.Name)
{
    return await CreateUserCommand.RunAsync(app.Services, args);
}

if (args.Length > 0 && args[0] == FeedbackCommand.Name)
{
    return await FeedbackCommand.RunAsync(app.Services, args);
}

if (args.Length > 0 && args[0] == RoleCommand.Name)
{
    return await RoleCommand.RunAsync(app.Services, args);
}

if (args.Length > 0 && args[0] == AiCommand.Name)
{
    return await AiCommand.RunAsync(app.Services, args);
}

if (args.Length > 0 && args[0] == AdminCommand.Name)
{
    return await AdminCommand.RunAsync(app.Services, args);
}

if (args.Length > 0 && args[0] == InviteCommand.Name)
{
    return await InviteCommand.RunAsync(app.Services, args);
}

if (args.Length > 0 && args[0] == ResetPasswordCommand.Name)
{
    return await ResetPasswordCommand.RunAsync(app.Services, args);
}

if (args.Length > 0 && args[0] == DeleteUserCommand.Name)
{
    return await DeleteUserCommand.RunAsync(app.Services, args);
}

if (!app.Services.GetRequiredService<CloudflareAccess>().Available)
{
    app.Logger.LogWarning("The admin area is off: Admin:CloudflareAccess:TeamDomain and Audience are not set (SPEC 12).");
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
// After preferences, which has already loaded the profile this checks.
app.UseMiddleware<GymTracker.Web.Features.Onboarding.OnboardingRedirectMiddleware>();

app.MapStaticAssets().AllowAnonymous();
app.MapControllers();

await app.RunAsync();
return 0;

public partial class Program;
