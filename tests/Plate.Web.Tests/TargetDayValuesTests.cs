using System.Text.Json;
using FluentAssertions;
using Plate.Core.Nutrition;
using Plate.Web.Features.Food;

namespace Plate.Web.Tests;

/// <summary>NutritionTargetDay rows are never rewritten, so the stored JSON is a format, not a by-product of Core records.</summary>
public class TargetDayValuesTests
{
    private static readonly TargetDayValues Day = new(2500,
        new GoalTarget(2060, 175, false, new MacroTarget(80.5m, 130, 175, 55, 80, 180, 250, 30, 23, 40, true)), 80.5m, 1800);

    private const string Json = """
        {"maintenanceKcal":2500,"kcal":2060,"proteinG":175,"floorApplied":false,"floorKcal":1800,"trendWeightKg":80.5,
        "macros":{"referenceKg":80.5,"proteinMinG":130,"proteinAimG":175,"fatMinG":55,"fatMaxG":80,"carbsMinG":180,
        "carbsMaxG":250,"fiberMinG":30,"saturatedFatMaxG":23,"mealProteinG":40,"proteinLowered":true}}
        """;

    [Fact]
    public void The_stored_format_is_fixed()
    {
        JsonDocument.Parse(Day.ToJson()).RootElement.GetRawText()
            .Should().Be(JsonDocument.Parse(Json.Replace("\n", "")).RootElement.GetRawText());
    }

    [Fact]
    public void A_stored_day_reads_back_with_every_value()
    {
        TargetDayValues.Read(Json).Should().Be(Day);
        TargetDayValues.Read(Day.ToJson()).Should().Be(Day);
    }

    [Fact]
    public void A_day_without_a_calorie_target_keeps_its_nulls()
    {
        var day = new TargetDayValues(2500, new GoalTarget(null, 160, false), null, 1500);
        TargetDayValues.Read(day.ToJson()).Should().Be(day);
    }

    [Fact]
    public void A_row_missing_a_stored_value_fails_instead_of_reading_as_zero()
    {
        var act = () => TargetDayValues.Read("""{"kcal":2060,"proteinG":175,"floorApplied":false,"floorKcal":1800}""");
        act.Should().Throw<JsonException>();
    }
}
