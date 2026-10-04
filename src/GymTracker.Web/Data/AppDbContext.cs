using GymTracker.Core.Domain;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options, ICurrentUser currentUser)
    : IdentityDbContext<AppUser>(options)
{
    /// <summary>Query filter names on the soft-deleted entities. Undo ignores only SoftDelete, never Owner.</summary>
    public const string OwnerFilter = "Owner";
    public const string SoftDeleteFilter = "SoftDelete";

    // Read by the query filters on every query, so each request only sees its own user's rows.
    private string? CurrentUserId => currentUser.UserId;

    public DbSet<UserProfile> Profiles => Set<UserProfile>();
    public DbSet<Exercise> Exercises => Set<Exercise>();
    public DbSet<WorkoutTemplate> Templates => Set<WorkoutTemplate>();
    public DbSet<TemplateExercise> TemplateExercises => Set<TemplateExercise>();
    public DbSet<WorkoutSession> Sessions => Set<WorkoutSession>();
    public DbSet<SessionExercise> SessionExercises => Set<SessionExercise>();
    public DbSet<SetEntry> Sets => Set<SetEntry>();
    public DbSet<UserExerciseSettings> ExerciseSettings => Set<UserExerciseSettings>();
    public DbSet<BodyweightEntry> Bodyweights => Set<BodyweightEntry>();
    public DbSet<FoodItem> FoodItems => Set<FoodItem>();
    public DbSet<FoodLogEntry> FoodLog => Set<FoodLogEntry>();
    public DbSet<SavedMeal> SavedMeals => Set<SavedMeal>();
    public DbSet<SavedMealItem> SavedMealItems => Set<SavedMealItem>();
    public DbSet<MaintenanceEstimate> MaintenanceEstimates => Set<MaintenanceEstimate>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<ParseRun> ParseRuns => Set<ParseRun>();
    public DbSet<Feedback> Feedback => Set<Feedback>();
    public DbSet<FeedbackAttachment> FeedbackAttachments => Set<FeedbackAttachment>();
    public DbSet<ErrorEvent> ErrorEvents => Set<ErrorEvent>();
    public DbSet<ChangelogEntry> Changelog => Set<ChangelogEntry>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Fuzzy name matching for food search and for resolving parsed text to database foods.
        builder.HasPostgresExtension("pg_trgm");

        builder.Entity<UserProfile>(e =>
        {
            e.HasKey(p => p.UserId);
            e.HasOne<AppUser>().WithOne().HasForeignKey<UserProfile>(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(p => p.DisplayName).HasMaxLength(100);
            e.Property(p => p.TimeZoneId).HasMaxLength(64);
            e.Property(p => p.Sex).HasConversion<string>().HasMaxLength(10);
            e.Property(p => p.Goal).HasConversion<string>().HasMaxLength(10);
            e.Property(p => p.TargetRatePercentPerWeek).HasPrecision(4, 2);
            e.Property(p => p.ProteinGramsPerKg).HasPrecision(4, 2);
            e.Property(p => p.DefaultWeightIncrementKg).HasPrecision(6, 2);
            e.Property(p => p.BarWeightKg).HasPrecision(5, 2);
            e.Property(p => p.Theme).HasConversion<string>().HasMaxLength(10);
            e.Property(p => p.Language).HasConversion<string>().HasMaxLength(12);
            e.HasQueryFilter(p => p.UserId == CurrentUserId);
        });

        builder.Entity<Exercise>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.PrimaryMuscle).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Equipment).HasConversion<string>().HasMaxLength(20);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => x.OwnerUserId == null || x.OwnerUserId == CurrentUserId);
        });

        builder.Entity<WorkoutTemplate>(e =>
        {
            e.Property(t => t.Name).HasMaxLength(100);
            e.HasOne<AppUser>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(t => new { t.UserId, t.SortOrder });
            e.HasQueryFilter(t => t.UserId == CurrentUserId);
        });

        builder.Entity<TemplateExercise>(e =>
        {
            e.HasOne(x => x.Template).WithMany(t => t.Exercises).HasForeignKey(x => x.TemplateId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Exercise).WithMany().HasForeignKey(x => x.ExerciseId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.WeightIncrementKg).HasPrecision(6, 2);
            e.HasQueryFilter(OwnerFilter, x => x.Template.UserId == CurrentUserId);
            e.HasQueryFilter(SoftDeleteFilter, x => x.DeletedAt == null);
        });

        builder.Entity<WorkoutSession>(e =>
        {
            e.HasOne<AppUser>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(s => s.Template).WithMany().HasForeignKey(s => s.TemplateId).OnDelete(DeleteBehavior.SetNull);
            e.Property(s => s.Notes).HasMaxLength(2000);
            e.Ignore(s => s.IsActive);
            e.HasIndex(s => new { s.UserId, s.StartedAt });

            // At most one active session per user.
            e.HasIndex(s => s.UserId).IsUnique().HasFilter("\"EndedAt\" IS NULL").HasDatabaseName("IX_Sessions_OneActivePerUser");
            e.HasQueryFilter(s => s.UserId == CurrentUserId);
        });

        builder.Entity<SessionExercise>(e =>
        {
            e.HasOne(x => x.Session).WithMany(s => s.Exercises).HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Exercise).WithMany().HasForeignKey(x => x.ExerciseId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.WeightIncrementKg).HasPrecision(6, 2);
            e.HasQueryFilter(OwnerFilter, x => x.Session.UserId == CurrentUserId);
            e.HasQueryFilter(SoftDeleteFilter, x => x.DeletedAt == null);
        });

        builder.Entity<SetEntry>(e =>
        {
            e.HasOne(x => x.SessionExercise).WithMany(se => se.Sets).HasForeignKey(x => x.SessionExerciseId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.WeightKg).HasPrecision(6, 2);
            e.Property(x => x.Rpe).HasPrecision(3, 1);
            e.HasIndex(x => x.ClientId).IsUnique();
            e.HasQueryFilter(OwnerFilter, x => x.SessionExercise.Session.UserId == CurrentUserId);

            // Also hidden while its exercise is removed, so history, volume and export skip it.
            e.HasQueryFilter(SoftDeleteFilter, x => x.DeletedAt == null && x.SessionExercise.DeletedAt == null);
        });

        builder.Entity<UserExerciseSettings>(e =>
        {
            e.HasKey(x => new { x.UserId, x.ExerciseId });
            e.Property(x => x.Note).HasMaxLength(UserExerciseSettings.MaxNoteLength);
            e.Property(x => x.BarWeightKg).HasPrecision(5, 2);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Exercise>().WithMany().HasForeignKey(x => x.ExerciseId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => x.UserId == CurrentUserId);
        });

        builder.Entity<BodyweightEntry>(e =>
        {
            e.HasOne<AppUser>().WithMany().HasForeignKey(b => b.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(b => b.WeightKg).HasPrecision(6, 2);
            e.HasIndex(b => new { b.UserId, b.Date }).IsUnique();
            e.HasQueryFilter(b => b.UserId == CurrentUserId);
        });

        builder.Entity<FoodItem>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.NameBg).HasMaxLength(200);
            e.Property(x => x.Brand).HasMaxLength(200);
            e.Property(x => x.Barcode).HasMaxLength(32);
            e.Property(x => x.Source).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.KcalPer100g).HasPrecision(7, 2);
            e.Property(x => x.ProteinPer100g).HasPrecision(6, 2);
            e.Property(x => x.CarbsPer100g).HasPrecision(6, 2);
            e.Property(x => x.FatPer100g).HasPrecision(6, 2);
            e.Property(x => x.DefaultServingGrams).HasPrecision(7, 1);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.Barcode).IsUnique().HasFilter("\"Barcode\" IS NOT NULL");
            e.HasIndex(x => x.Name).HasMethod("gin").HasOperators("gin_trgm_ops");
            e.HasIndex(x => x.NameBg).HasMethod("gin").HasOperators("gin_trgm_ops");
            e.HasQueryFilter(x => x.OwnerUserId == null || x.OwnerUserId == CurrentUserId);
        });

        builder.Entity<FoodLogEntry>(e =>
        {
            e.Property(x => x.Description).HasMaxLength(200);
            e.Property(x => x.Meal).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Source).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.AiModel).HasMaxLength(100);
            e.Property(x => x.MatchModel).HasMaxLength(100);
            e.Property(x => x.Grams).HasPrecision(7, 1);
            e.Property(x => x.Kcal).HasPrecision(7, 1);
            e.Property(x => x.ProteinG).HasPrecision(6, 1);
            e.Property(x => x.CarbsG).HasPrecision(6, 1);
            e.Property(x => x.FatG).HasPrecision(6, 1);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.FoodItem).WithMany().HasForeignKey(x => x.FoodItemId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<ParseRun>().WithMany().HasForeignKey(x => x.ParseRunId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => new { x.UserId, x.Date });
            e.HasQueryFilter(OwnerFilter, x => x.UserId == CurrentUserId);
            e.HasQueryFilter(SoftDeleteFilter, x => x.DeletedAt == null);
        });

        builder.Entity<SavedMeal>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(SavedMeal.MaxNameLength);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.UserId);
            e.HasQueryFilter(x => x.UserId == CurrentUserId);
        });

        builder.Entity<SavedMealItem>(e =>
        {
            e.HasOne(x => x.SavedMeal).WithMany(m => m.Items).HasForeignKey(x => x.SavedMealId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<FoodItem>().WithMany().HasForeignKey(x => x.FoodItemId).OnDelete(DeleteBehavior.SetNull);
            e.Property(x => x.Description).HasMaxLength(200);
            e.Property(x => x.Source).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Grams).HasPrecision(7, 1);
            e.Property(x => x.Kcal).HasPrecision(7, 1);
            e.Property(x => x.ProteinG).HasPrecision(6, 1);
            e.Property(x => x.CarbsG).HasPrecision(6, 1);
            e.Property(x => x.FatG).HasPrecision(6, 1);
            e.HasQueryFilter(x => x.SavedMeal.UserId == CurrentUserId);
        });

        // Shared by all accounts, so no user filter.
        builder.Entity<AppSetting>(e =>
        {
            e.HasKey(x => x.Name);
            e.Property(x => x.Name).HasMaxLength(64);
            e.Property(x => x.Value).HasMaxLength(2000);
            e.Property(x => x.UpdatedByUserId).HasMaxLength(450);
        });

        builder.Entity<MaintenanceEstimate>(e =>
        {
            e.Property(x => x.Method).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.TrendWeightKg).HasPrecision(6, 2);
            e.Property(x => x.LoggingCompleteness).HasPrecision(4, 3);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.UserId, x.CalculatedOn }).IsUnique();
            e.HasQueryFilter(x => x.UserId == CurrentUserId);
        });

        // Feedback (SPEC 11). JSON snapshots are stored as jsonb strings and read only by the CLI and the services.
        builder.Entity<ParseRun>(e =>
        {
            e.Property(x => x.Text).HasMaxLength(1000);
            e.Property(x => x.AiModel).HasMaxLength(100);
            e.Property(x => x.MatchModel).HasMaxLength(100);
            e.Property(x => x.ParserError).HasMaxLength(500);
            e.Property(x => x.Parsed).HasColumnType("jsonb");
            e.Property(x => x.Resolved).HasColumnType("jsonb");
            e.Property(x => x.Saved).HasColumnType("jsonb");
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.HasIndex(x => x.CreatedAt);
            e.HasQueryFilter(x => x.UserId == CurrentUserId);
        });

        builder.Entity<Feedback>(e =>
        {
            e.Property(x => x.Source).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Area).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.Rating).HasConversion<string>().HasMaxLength(10);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Message).HasMaxLength(Core.Domain.Feedback.MaxMessageLength);
            e.Property(x => x.Reply).HasMaxLength(Core.Domain.Feedback.MaxReplyLength);
            e.Property(x => x.FixedInVersion).HasMaxLength(40);
            e.Property(x => x.Context).HasColumnType("jsonb");
            e.Ignore(x => x.HasUnreadReply);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.ParseRun).WithMany().HasForeignKey(x => x.ParseRunId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<ErrorEvent>().WithMany().HasForeignKey(x => x.ErrorEventId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Attachment).WithOne(a => a.Feedback).HasForeignKey<FeedbackAttachment>(a => a.FeedbackId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.ClientId).IsUnique();
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.HasIndex(x => x.Status);
            e.HasQueryFilter(x => x.UserId == CurrentUserId);
        });

        builder.Entity<FeedbackAttachment>(e =>
        {
            e.Property(x => x.ContentType).HasMaxLength(20);
            e.HasIndex(x => x.FeedbackId).IsUnique();
            e.HasQueryFilter(x => x.Feedback.UserId == CurrentUserId);
        });

        // Not user data: written for anyone, read only by the CLI.
        builder.Entity<ErrorEvent>(e =>
        {
            e.Property(x => x.Fingerprint).HasMaxLength(16);
            e.Property(x => x.Origin).HasConversion<string>().HasMaxLength(10);
            e.Property(x => x.Type).HasMaxLength(200);
            e.Property(x => x.Message).HasMaxLength(1000);
            e.Property(x => x.Path).HasMaxLength(300);
            e.Property(x => x.Stack).HasMaxLength(4000);
            e.Property(x => x.UserId).HasMaxLength(450);
            e.Property(x => x.LastTraceId).HasMaxLength(100);
            e.Property(x => x.FirstVersion).HasMaxLength(40);
            e.Property(x => x.LastVersion).HasMaxLength(40);
            e.HasIndex(x => x.Fingerprint).IsUnique();
            e.HasIndex(x => x.LastSeenAt);
        });

        // Shared by both accounts.
        builder.Entity<ChangelogEntry>(e =>
        {
            e.Property(x => x.TextEn).HasMaxLength(ChangelogEntry.MaxTextLength);
            e.Property(x => x.TextBg).HasMaxLength(ChangelogEntry.MaxTextLength);
            e.Property(x => x.Version).HasMaxLength(40);
            e.HasOne<Feedback>().WithMany().HasForeignKey(x => x.FeedbackId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => x.CreatedAt);
        });
    }
}
