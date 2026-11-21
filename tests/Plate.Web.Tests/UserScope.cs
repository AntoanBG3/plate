using Microsoft.Extensions.Caching.Memory;
using Plate.Web.Data;
using Plate.Web.Features.Body;
using Plate.Web.Features.Profile;
using Plate.Web.Features.Training;

namespace Plate.Web.Tests;

/// <summary>The services as one user sees them, sharing one DbContext like a request scope.</summary>
public sealed class UserScope : IAsyncDisposable
{
    private readonly MemoryCache cache = new(new MemoryCacheOptions());

    public UserScope(PostgresFixture fixture, string userId)
    {
        UserId = userId;
        Db = fixture.CreateContext(userId);
        var currentUser = new FixedCurrentUser(userId);
        Templates = new TemplateService(Db, currentUser, TimeProvider.System);
        Sessions = new SessionService(Db, currentUser, new ProfileLookup(Db), TimeProvider.System);
        Bodyweights = new BodyweightService(Db, currentUser);
        Exercises = new ExerciseService(Db, currentUser);
        ExerciseSettings = new ExerciseSettingsService(Db, currentUser);
        FoodDatabase = new FakeFoodDatabase();
        Foods = new Plate.Web.Features.Food.FoodItemService(Db, currentUser, FoodDatabase);
        Repeat = new Plate.Web.Features.Food.RepeatLoggingService(Db, currentUser, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<Plate.Web.Features.Food.RepeatLoggingService>.Instance);
        Matcher = new Plate.Web.Features.Food.FoodMatcher(Foods, MatchDecider, Repeat);
        FoodLog = new Plate.Web.Features.Food.FoodLogService(Db, currentUser, TimeProvider.System);
        SavedMeals = new Plate.Web.Features.Food.SavedMealService(Db, currentUser, TimeProvider.System);
        Undo = new Plate.Web.Features.Undo.UndoService(Db, TimeProvider.System);
        ParseRuns = new Plate.Web.Features.Feedback.ParseRunService(Db, currentUser, new Plate.Web.Integrations.FakeFoodParser(), Matcher, Namer, TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Plate.Web.Features.Feedback.ParseRunService>.Instance, cache, Repeat);
    }

    public Plate.Web.Features.Feedback.ParseRunService ParseRuns { get; }

    public FakeFoodDatabase FoodDatabase { get; }

    /// <summary>Makes no decision unless a test scripts one, so the matcher behaves as without Jev.</summary>
    public StubMatchDecider MatchDecider { get; } = new();

    /// <summary>Names nothing unless a test scripts it, so rows keep their database names as without a key.</summary>
    public StubFoodNamer Namer { get; } = new();
    public Plate.Web.Features.Food.FoodItemService Foods { get; }
    public Plate.Web.Features.Food.RepeatLoggingService Repeat { get; }
    public Plate.Web.Features.Food.FoodMatcher Matcher { get; }
    public Plate.Web.Features.Food.FoodLogService FoodLog { get; }
    public Plate.Web.Features.Food.SavedMealService SavedMeals { get; }
    public Plate.Web.Features.Undo.UndoService Undo { get; }

    public string UserId { get; }
    public AppDbContext Db { get; }
    public TemplateService Templates { get; }
    public SessionService Sessions { get; }
    public BodyweightService Bodyweights { get; }
    public ExerciseService Exercises { get; }
    public ExerciseSettingsService ExerciseSettings { get; }

    public ValueTask DisposeAsync()
    {
        cache.Dispose();
        return Db.DisposeAsync();
    }
}
