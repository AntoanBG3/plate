using System.IO.Compression;
using System.Text;
using FluentAssertions;
using GymTracker.Core.Domain;
using GymTracker.Web.Features.Export;

namespace GymTracker.Web.Tests;

public class CsvTests
{
    [Fact]
    public void Quotes_fields_with_commas_quotes_and_newlines() =>
        new Csv("a", "b").Row("x, y", "say \"hi\"\nthen").ToString()
            .Should().Be("a,b\r\n\"x, y\",\"say \"\"hi\"\"\nthen\"\r\n");

    [Fact]
    public void Writes_numbers_and_dates_in_invariant_culture()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("bg-BG");
        try
        {
            new Csv("n").Row(82.45m, new DateOnly(2026, 10, 2), new DateTimeOffset(2026, 10, 2, 9, 5, 0, TimeSpan.FromHours(3)), null, true)
                .ToString().Should().EndWith("82.45,2026-10-02,2026-10-02T06:05:00Z,,true\r\n");
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("=HYPERLINK(\"x\")", "\"'=HYPERLINK(\"\"x\"\")\"")]
    [InlineData("+1", "'+1")]
    [InlineData("-cmd", "'-cmd")]
    [InlineData("@sum", "'@sum")]
    public void Defuses_text_that_a_spreadsheet_would_run_as_a_formula(string text, string cell) =>
        new Csv("x").Row(text).ToString().Should().Be($"x\r\n{cell}\r\n");

    [Fact]
    public void Leaves_negative_numbers_alone() =>
        new Csv("x").Row(-1.5m).ToString().Should().Be("x\r\n-1.5\r\n");
}

[Collection(PostgresCollection.Name)]
public class ExportTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Zip_has_a_csv_per_kind_of_data_in_utf8_with_bom()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        await s.Bodyweights.SetAsync(new DateOnly(2026, 10, 1), 82.4m, default);
        s.Db.FoodLog.Add(new FoodLogEntry
        {
            UserId = s.UserId, Date = new DateOnly(2026, 10, 1), Meal = Meal.Breakfast, Description = "Баница, 2 парчета",
            Grams = 200, Kcal = 640, ProteinG = 20, CarbsG = 60, FatG = 35, Source = EntrySource.Ai, AiModel = "test", MatchModel = "typesafe/jev-1.13-20260917", CreatedAt = DateTimeOffset.UtcNow,
        });
        await s.Db.SaveChangesAsync();

        var files = Unzip(await new DataExport(s.Db).ZipAsync(default));

        files.Keys.Should().BeEquivalentTo(
            "profile.csv", "bodyweight.csv", "workouts.csv", "sets.csv", "templates.csv",
            "custom_exercises.csv", "food_log.csv", "maintenance_estimates.csv", "weekly_summaries.csv", "feedback.csv", "label_scans.csv");
        files["bodyweight.csv"].Should().Be("date,weight_kg\r\n2026-10-01,82.4\r\n");
        files["food_log.csv"].Should().StartWith("date,meal,description,status,grams,kcal,protein_g,carbs_g,fat_g,fiber_g,saturated_fat_g,source,ai_model,match_model,created_at_utc\r\n");
        files["food_log.csv"].Should().Contain("2026-10-01,Breakfast,\"Баница, 2 парчета\",,200,640,20,60,35,,,Ai,test,typesafe/jev-1.13-20260917,");
    }

    [Fact]
    public async Task Label_scans_are_exported_without_their_photos()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        s.Db.LabelScans.Add(new LabelScan
        {
            UserId = s.UserId, CreatedAt = new DateTimeOffset(2026, 10, 7, 8, 0, 0, TimeSpan.Zero), Model = "test/label", LatencyMs = 1234, Edited = true,
            Values = """{"kcal":62}""", Saved = """{"kcalPer100g":64}""",
            Photos = [new LabelScanPhoto { ContentType = "image/jpeg", Bytes = "PHOTOBYTES"u8.ToArray() }],
        });
        await s.Db.SaveChangesAsync();

        var csv = Unzip(await new DataExport(s.Db).ZipAsync(default))["label_scans.csv"];

        csv.Should().StartWith("created_at_utc,model,latency_ms,error,edited,values,saved\r\n");
        // jsonb comes back reformatted, so the JSON columns are checked by content.
        csv.Should().Contain("2026-10-07T08:00:00Z,test/label,1234,,true,").And.Contain("kcalPer100g").And.NotContain("PHOTOBYTES");
    }

    /// <summary>File name to text, after checking each file starts with the UTF-8 byte order mark.</summary>
    internal static Dictionary<string, string> Unzip(byte[] zipBytes)
    {
        using var zip = new ZipArchive(new MemoryStream(zipBytes));
        return zip.Entries.ToDictionary(e => e.Name, e =>
        {
            using var stream = new MemoryStream();
            e.Open().CopyTo(stream);
            var bytes = stream.ToArray();
            bytes.Take(3).Should().Equal(Encoding.UTF8.Preamble.ToArray());
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        });
    }
}
