using GymTracker.Web.Data;
using GymTracker.Web.Features.Body;
using GymTracker.Web.Features.Profile;
using GymTracker.Web.Features.Training;

namespace GymTracker.Web.Tests;

/// <summary>The services as one user sees them, sharing one DbContext like a request scope.</summary>
public sealed class UserScope : IAsyncDisposable
{
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
        Foods = new GymTracker.Web.Features.Food.FoodItemService(Db, currentUser, FoodDatabase);
        Matcher = new GymTracker.Web.Features.Food.FoodMatcher(Foods, MatchDecider);
        Repeat = new GymTracker.Web.Features.Food.RepeatLoggingService(Db, currentUser, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<GymTracker.Web.Features.Food.RepeatLoggingService>.Instance);
        FoodLog = new GymTracker.Web.Features.Food.FoodLogService(Db, currentUser, TimeProvider.System);
        SavedMeals = new GymTracker.Web.Features.Food.SavedMealService(Db, currentUser, TimeProvider.System);
        Undo = new GymTracker.Web.Features.Undo.UndoService(Db, TimeProvider.System);
        ParseRuns = new GymTracker.Web.Features.Feedback.ParseRunService(Db, currentUser, new GymTracker.Web.Integrations.FakeFoodParser(), Matcher, Namer, TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GymTracker.Web.Features.Feedback.ParseRunService>.Instance);
    }

    public GymTracker.Web.Features.Feedback.ParseRunService ParseRuns { get; }

    public FakeFoodDatabase FoodDatabase { get; }

    /// <summary>Makes no decision unless a test scripts one, so the matcher behaves as without Jev.</summary>
    public StubMatchDecider MatchDecider { get; } = new();

    /// <summary>Names nothing unless a test scripts it, so rows keep their database names as without a key.</summary>
    public StubFoodNamer Namer { get; } = new();
    public GymTracker.Web.Features.Food.FoodItemService Foods { get; }
    public GymTracker.Web.Features.Food.RepeatLoggingService Repeat { get; }
    public GymTracker.Web.Features.Food.FoodMatcher Matcher { get; }
    public GymTracker.Web.Features.Food.FoodLogService FoodLog { get; }
    public GymTracker.Web.Features.Food.SavedMealService SavedMeals { get; }
    public GymTracker.Web.Features.Undo.UndoService Undo { get; }

    public string UserId { get; }
    public AppDbContext Db { get; }
    public TemplateService Templates { get; }
    public SessionService Sessions { get; }
    public BodyweightService Bodyweights { get; }
    public ExerciseService Exercises { get; }
    public ExerciseSettingsService ExerciseSettings { get; }

    public ValueTask DisposeAsync() => Db.DisposeAsync();
}
