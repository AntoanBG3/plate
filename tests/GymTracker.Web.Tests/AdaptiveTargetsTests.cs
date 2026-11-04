using FluentAssertions;
using GymTracker.Core.Domain;
using GymTracker.Web.Data;
using GymTracker.Web.Features.Body;
using GymTracker.Web.Features.Food;
using GymTracker.Web.Features.Profile;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Tests;

public class AdaptiveTargetsTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    // Noon UTC is mid-afternoon in Sofia, so the user's day is the same date.
    private static readonly DateOnly Today = new(2026, 9, 1);

    private sealed class Clock(DateOnly day) : TimeProvider
    {
        public DateOnly Day { get; set; } = day;

        public override DateTimeOffset GetUtcNow() => new(Day.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero);
    }

    private sealed record Setup(UserScope Scope, Clock Clock, NutritionTargets Targets, ProfileService Profiles) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Scope.DisposeAsync();
    }

    private async Task<Setup> SetupAsync(int? statedMaintenance = 2500, Goal goal = Goal.Maintain, decimal rate = 0)
    {
        var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var clock = new Clock(Today);
        var profiles = new ProfileService(s.Db, clock);
        await profiles.UpdateAsync(p =>
        {
            p.InitialMaintenanceKcal = statedMaintenance;
            p.Sex = Sex.Male;
            p.Goal = goal;
            p.TargetRatePercentPerWeek = rate;
        }, default);
        return new Setup(s, clock, new NutritionTargets(s.Db, new FixedCurrentUser(s.UserId), profiles, s.Bodyweights, clock), profiles);
    }

    /// <summary>Weigh-ins and food for each of the given days before today.</summary>
    private static async Task LogDaysAsync(UserScope s, int days, decimal kg, decimal kcal)
    {
        for (var d = days; d >= 1; d--)
        {
            var date = Today.AddDays(-d);
            s.Db.Bodyweights.Add(new BodyweightEntry { UserId = s.UserId, Date = date, WeightKg = kg });
            s.Db.FoodLog.Add(new FoodLogEntry
            {
                UserId = s.UserId, Date = date, Meal = Meal.Lunch, Description = "Food", Grams = 100,
                Kcal = kcal, Source = EntrySource.Manual, CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        await s.Db.SaveChangesAsync();
    }

    [Fact]
    public async Task Without_logs_the_target_is_the_prior_marked_initial()
    {
        await using var x = await SetupAsync(statedMaintenance: 2500);

        var targets = await x.Targets.ForTodayAsync(default);

        targets.Kcal.Should().Be(2500);
        targets.Estimate!.Method.Should().Be(MaintenanceMethod.Initial);
        targets.ProteinG.Should().BeNull("there is no weigh-in yet");
        targets.Missing.Should().Be("Log your weight to get a protein target.");
    }

    [Fact]
    public async Task Three_weeks_of_logs_give_an_adaptive_estimate_that_is_stored_and_reused_for_a_week()
    {
        await using var x = await SetupAsync(statedMaintenance: 2500);
        await LogDaysAsync(x.Scope, 21, 80m, 2300m);

        var first = await x.Targets.ForTodayAsync(default);

        // Stable weight at 2300 kcal: 0.7 * 2500 + 0.3 * 2300 = 2440.
        first.Estimate!.Method.Should().Be(MaintenanceMethod.Adaptive);
        first.Estimate.EstimatedTdeeKcal.Should().Be(2440);
        first.Estimate.DaysOfData.Should().Be(21);
        first.Kcal.Should().Be(2440);
        first.ProteinG.Should().Be(145, "Auto protein on Maintain is 1.8 g/kg of 80 kg");
        first.Macros!.ProteinMinG.Should().Be(130);
        first.TrendWeightKg.Should().Be(80m);

        x.Clock.Day = Today.AddDays(6);
        (await x.Targets.ForTodayAsync(default)).Estimate!.Id.Should().Be(first.Estimate.Id, "a second call within 7 days reuses it");

        x.Clock.Day = Today.AddDays(7);
        var next = await x.Targets.ForTodayAsync(default);
        next.Estimate!.Id.Should().NotBe(first.Estimate.Id);
        next.Estimate.Method.Should().Be(MaintenanceMethod.Initial, "nothing was logged in the last week");
        next.Estimate.EstimatedTdeeKcal.Should().Be(2440, "the last adaptive estimate is carried forward");

        (await x.Scope.Db.MaintenanceEstimates.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task A_cut_below_the_floor_is_raised_to_it()
    {
        await using var x = await SetupAsync(statedMaintenance: 2000, goal: Goal.Cut, rate: 1.0m);
        await x.Scope.Bodyweights.SetAsync(Today, 100m, default);

        var targets = await x.Targets.ForTodayAsync(default);

        targets.Kcal.Should().Be(1800);
        targets.FloorApplied.Should().BeTrue();
        targets.FloorKcal.Should().Be(1800);
    }

    [Fact]
    public async Task Profile_edits_apply_at_once_and_update_the_stored_targets()
    {
        await using var x = await SetupAsync(statedMaintenance: 2500);
        await x.Scope.Bodyweights.SetAsync(Today, 80m, default);
        await x.Targets.ForTodayAsync(default);

        await x.Profiles.UpdateAsync(p => p.InitialMaintenanceKcal = 2700, default);
        (await x.Targets.ForTodayAsync(default)).Kcal.Should().Be(2700, "before any adaptive estimate, the prior follows the profile");

        await x.Profiles.UpdateAsync(p => { p.Goal = Goal.Bulk; p.TargetRatePercentPerWeek = 0.25m; }, default);
        var bulk = await x.Targets.ForTodayAsync(default);
        bulk.Kcal.Should().Be(2920); // 80 kg * 0.25% = 0.2 kg/week = 220 kcal/day
        (await x.Scope.Db.MaintenanceEstimates.SingleAsync()).TargetKcal.Should().Be(2920);
    }

    [Fact]
    public async Task The_estimated_prior_follows_the_activity_level()
    {
        await using var x = await SetupAsync(statedMaintenance: null);
        await x.Profiles.UpdateAsync(p => { p.HeightCm = 180; p.BirthYear = 1996; }, default);
        await x.Scope.Bodyweights.SetAsync(Today, 80m, default);

        (await x.Targets.ForTodayAsync(default)).Kcal.Should().Be(2726, "no activity level counts as inactive (NASEM 2023, male, 30 y, 180 cm, 80 kg)");

        await x.Profiles.UpdateAsync(p => p.ActivityLevel = ActivityLevel.LowActive, default);
        var targets = await x.Targets.ForTodayAsync(default);
        targets.Kcal.Should().Be(2946);
        targets.Estimate!.Method.Should().Be(MaintenanceMethod.Initial);
    }

    [Fact]
    public async Task Goal_and_profile_edits_do_not_rewrite_past_targets_or_the_chart()
    {
        await using var x = await SetupAsync();
        await x.Scope.Bodyweights.SetAsync(Today, 80, default);
        var before = await x.Targets.ForTodayAsync(default);

        x.Clock.Day = Today.AddDays(1);
        await x.Profiles.UpdateAsync(p =>
        {
            p.Goal = Goal.Cut;
            p.TargetRatePercentPerWeek = 0.5m;
            p.Sex = Sex.Female;
            p.ProteinGramsPerKg = 2.4m;
        }, default);
        var current = await x.Targets.ForTodayAsync(default);
        await x.Targets.ForTodayAsync(default); // The same day updates its snapshot, rather than adding duplicates.
        var past = (await x.Targets.ForDateAsync(Today, default)).Targets!;

        current.Kcal.Should().Be(2060);
        current.Estimate!.TargetKcal.Should().Be(current.Kcal);
        past.Kcal.Should().Be(before.Kcal);
        past.Macros.Should().Be(before.Macros);
        past.FloorKcal.Should().Be(1800);
        (await x.Scope.Db.MaintenanceEstimates.SingleAsync()).TargetKcal.Should().Be(2500);
        (await x.Scope.Db.NutritionTargetDays.CountAsync()).Should().Be(2);

        var chart = await new BodyAnalytics(x.Scope.Db, x.Scope.Bodyweights).CaloriesAsync(x.Clock.Day, default);
        chart[^2].TargetKcal.Should().Be(2500);
        chart[^1].TargetKcal.Should().Be(2060);
        chart[^2].TargetProteinG.Should().Be(before.ProteinG);
    }

    [Fact]
    public async Task A_new_prior_does_not_change_yesterdays_maintenance_or_the_weekly_cadence()
    {
        await using var x = await SetupAsync();
        var initial = await x.Targets.ForTodayAsync(default);
        x.Clock.Day = Today.AddDays(1);
        await x.Profiles.UpdateAsync(p => p.InitialMaintenanceKcal = 2700, default);

        var current = await x.Targets.ForTodayAsync(default);
        current.Kcal.Should().Be(2700);
        current.Estimate!.TargetKcal.Should().Be(current.Kcal);
        current.Estimate!.Id.Should().Be(initial.Estimate!.Id);
        (await x.Scope.Db.MaintenanceEstimates.SingleAsync()).EstimatedTdeeKcal.Should().Be(2500);
        var past = (await x.Targets.ForDateAsync(Today, default)).Targets!;
        past.Kcal.Should().Be(2500);
        past.Estimate!.EstimatedTdeeKcal.Should().Be(2500);
    }

    [Fact]
    public async Task Days_before_snapshots_keep_their_macros_and_stop_following_profile_edits()
    {
        await using var x = await SetupAsync();
        await x.Scope.Bodyweights.SetAsync(Today.AddDays(-7), 80, default);
        x.Scope.Db.MaintenanceEstimates.Add(new MaintenanceEstimate
        {
            UserId = x.Scope.UserId, CalculatedOn = Today.AddDays(-7), EstimatedTdeeKcal = 2500,
            TargetKcal = 2060, TargetProteinG = 175, Method = MaintenanceMethod.Initial,
        });
        await x.Scope.Db.SaveChangesAsync();

        // Opening a past day first, as from a link, captures the earlier days with the profile as it is now.
        var past = (await x.Targets.ForDateAsync(Today.AddDays(-1), default)).Targets!;
        past.Kcal.Should().Be(2060);
        past.ProteinG.Should().Be(175);
        // The profile now aims for 145 g (Maintain, 1.8 g/kg); the day keeps the 175 g it recorded, and carbs fit it.
        past.Macros.Should().BeEquivalentTo(new { ProteinAimG = 175, FatMinG = 45, FatMaxG = 80, CarbsMinG = 160, CarbsMaxG = 240 });
        (await x.Scope.Db.NutritionTargetDays.CountAsync()).Should().Be(7);

        await x.Profiles.UpdateAsync(p => p.ProteinGramsPerKg = 2.4m, default);
        var after = (await x.Targets.ForDateAsync(Today.AddDays(-1), default)).Targets!;
        after.Macros.Should().Be(past.Macros);

        var chart = await new BodyAnalytics(x.Scope.Db, x.Scope.Bodyweights).CaloriesAsync(Today.AddDays(-1), default);
        chart[^1].TargetKcal.Should().Be(2060);
        chart[^1].TargetProteinG.Should().Be(175);
    }

    [Fact]
    public async Task A_settings_save_captures_earlier_days_before_changing_the_profile()
    {
        await using var x = await SetupAsync();
        x.Scope.Db.MaintenanceEstimates.Add(new MaintenanceEstimate
        {
            UserId = x.Scope.UserId, CalculatedOn = Today.AddDays(-3), EstimatedTdeeKcal = 2500,
            TargetKcal = 2500, TargetProteinG = 160, Method = MaintenanceMethod.Initial,
        });
        await x.Scope.Db.SaveChangesAsync();

        await x.Targets.CaptureHistoryAsync(default);
        await x.Profiles.UpdateAsync(p => p.Goal = Goal.Cut, default);

        var past = (await x.Targets.ForDateAsync(Today.AddDays(-2), default)).Targets!;
        past.Kcal.Should().Be(2500);
        (await x.Scope.Db.NutritionTargetDays.CountAsync()).Should().Be(3);
        await x.Targets.CaptureHistoryAsync(default); // Only once per user.
        (await x.Scope.Db.NutritionTargetDays.CountAsync()).Should().Be(3);
    }

    [Fact]
    public async Task An_unchanged_day_is_not_written_again()
    {
        await using var x = await SetupAsync();
        await x.Scope.Bodyweights.SetAsync(Today, 80, default);
        await x.Targets.ForTodayAsync(default);
        var xmin = await XminAsync(x);

        await x.Targets.ForTodayAsync(default);
        (await XminAsync(x)).Should().Be(xmin);

        await x.Profiles.UpdateAsync(p => p.ProteinGramsPerKg = 2.4m, default);
        await x.Targets.ForTodayAsync(default);
        (await XminAsync(x)).Should().NotBe(xmin);
    }

    // The row's transaction id changes whenever the row is written, even with the same values.
    private static Task<long> XminAsync(Setup x) => x.Scope.Db.Database
        .SqlQuery<long>($"""SELECT xmin::text::bigint AS "Value" FROM "NutritionTargetDays" WHERE "UserId" = {x.Scope.UserId}""")
        .SingleAsync();

    [Fact]
    public async Task Estimates_are_private_to_each_user()
    {
        await using var a = await SetupAsync();
        await a.Targets.ForTodayAsync(default);

        await using var b = new UserScope(fixture, await fixture.CreateUserAsync());
        (await b.Db.MaintenanceEstimates.CountAsync()).Should().Be(0);
        (await b.Db.NutritionTargetDays.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Calorie_chart_shows_each_days_intake_against_the_target_in_force()
    {
        await using var x = await SetupAsync(statedMaintenance: 2500);
        await LogDaysAsync(x.Scope, 3, 80m, 2300m);
        await x.Targets.ForTodayAsync(default);

        var days = await new BodyAnalytics(x.Scope.Db, x.Scope.Bodyweights).CaloriesAsync(Today, default);

        days.Should().HaveCount(28);
        days[^1].Date.Should().Be(Today);
        days[^2].Kcal.Should().Be(2300);
        days[^2].TargetKcal.Should().BeNull("the first estimate is from today");
        days[^1].TargetKcal.Should().Be(2500);
        days[^1].Kcal.Should().BeNull();

        var weight = await new BodyAnalytics(x.Scope.Db, x.Scope.Bodyweights).WeightAsync("4w", Today, default);
        weight.Points.Should().HaveCount(4);
        weight.Points.Should().OnlyContain(p => p.TrendKg == 80m);
        (await new BodyAnalytics(x.Scope.Db, x.Scope.Bodyweights).WeightAsync("bogus", Today, default)).Range.Should().Be("12w");
    }
}
