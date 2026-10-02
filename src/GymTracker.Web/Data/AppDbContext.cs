using GymTracker.Core.Domain;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options, ICurrentUser currentUser)
    : IdentityDbContext<AppUser>(options)
{
    // Read by the query filters on every query, so each request only sees its own user's rows.
    private string? CurrentUserId => currentUser.UserId;

    public DbSet<UserProfile> Profiles => Set<UserProfile>();
    public DbSet<Exercise> Exercises => Set<Exercise>();
    public DbSet<WorkoutTemplate> Templates => Set<WorkoutTemplate>();
    public DbSet<TemplateExercise> TemplateExercises => Set<TemplateExercise>();
    public DbSet<WorkoutSession> Sessions => Set<WorkoutSession>();
    public DbSet<SessionExercise> SessionExercises => Set<SessionExercise>();
    public DbSet<SetEntry> Sets => Set<SetEntry>();
    public DbSet<BodyweightEntry> Bodyweights => Set<BodyweightEntry>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

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
            e.HasQueryFilter(x => x.Template.UserId == CurrentUserId);
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
            e.HasQueryFilter(x => x.Session.UserId == CurrentUserId);
        });

        builder.Entity<SetEntry>(e =>
        {
            e.HasOne(x => x.SessionExercise).WithMany(se => se.Sets).HasForeignKey(x => x.SessionExerciseId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.WeightKg).HasPrecision(6, 2);
            e.Property(x => x.Rpe).HasPrecision(3, 1);
            e.HasIndex(x => x.ClientId).IsUnique();
            e.HasQueryFilter(x => x.SessionExercise.Session.UserId == CurrentUserId);
        });

        builder.Entity<BodyweightEntry>(e =>
        {
            e.HasOne<AppUser>().WithMany().HasForeignKey(b => b.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(b => b.WeightKg).HasPrecision(6, 2);
            e.HasIndex(b => new { b.UserId, b.Date }).IsUnique();
            e.HasQueryFilter(b => b.UserId == CurrentUserId);
        });
    }
}
