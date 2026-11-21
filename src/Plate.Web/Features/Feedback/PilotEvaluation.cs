using System.Globalization;
using Plate.Core.Nutrition;
using Plate.Web.Data;
using Plate.Web.Data.Seed;
using Plate.Web.Integrations;
using Microsoft.AspNetCore.Identity;

namespace Plate.Web.Features.Feedback;

/// <summary>
/// <c>feedback pilot</c>: replays the weighed-meal pilot's texts through the live parser and matcher and compares the
/// result with the weighed reference values (reports/weighed-meal-pilot.md, SPEC 11). Nothing is saved; each meal spends
/// one parse of OpenRouter credit, so <c>--check</c> reads the file without calling anything.
/// </summary>
public static class PilotEvaluation
{
    public const string Usage = "Usage: feedback pilot [--file <csv>|-] [--account <email>] [--parser <model>] [--matcher off|jev|<decision model id>] [--no-history] [--check] [--json]";

    public static readonly string[] Columns = ["meal", "eaten_at", "account", "category", "text", "item", "grams", "kcal_100g", "protein_100g", "carbs_100g", "fat_100g", "source", "uncertain", "notes"];

    public static readonly string[] Categories = ["counted", "staple", "homemade", "loose"];

    private static readonly string[] Valued = ["--file", "--account", "--parser", "--matcher"];
    private static readonly string[] Flags = ["--no-history", "--check", "--json"];
    private static readonly TimeZoneInfo Sofia = TimeZoneInfo.FindSystemTimeZoneById("Europe/Sofia");

    /// <param name="args">The command's arguments, starting with "pilot".</param>
    public static async Task<int> RunAsync(IServiceProvider services, string[] args, TextWriter o, TextReader input, bool json, CancellationToken ct)
    {
        for (var n = 1; n < args.Length; n++)
        {
            if (args[n] is "--help" or "-h" or "help")
            {
                o.WriteLine(Usage);
                o.WriteLine("Compares what Plate gives for each pilot meal's text with the weighed values. Each meal spends one parse of OpenRouter credit; --check only reads the file.");
                return 0;
            }

            if (Valued.Contains(args[n]) && n + 1 < args.Length)
            {
                n++;
            }
            else if (!Flags.Contains(args[n]))
            {
                Console.Error.WriteLine(Valued.Contains(args[n]) ? $"{args[n]} needs a value; nothing was run." : $"Unknown option \"{args[n]}\" for pilot; nothing was run.");
                Console.Error.WriteLine(Usage);
                return 2;
            }
        }

        var path = Option(args, "--file") ?? "-";
        var lines = path == "-" ? ReadAll(input) : File.ReadAllLines(path).ToList();
        var (meals, errors) = Read(lines, Option(args, "--account"));
        foreach (var error in errors)
        {
            Console.Error.WriteLine(error);
        }

        if (errors.Count > 0)
        {
            Console.Error.WriteLine("Nothing was run; fix the file first.");
            return 2;
        }

        var users = services.GetRequiredService<UserManager<AppUser>>();
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var email in meals.Select(m => m.Account!).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (await users.FindByEmailAsync(email) is not { } user)
            {
                Console.Error.WriteLine($"No account with the email {email}; nothing was run.");
                return 2;
            }

            ids[email] = user.Id;
        }

        o.WriteLine($"{meals.Count} meals ({string.Join(", ", meals.GroupBy(m => m.Category).Select(g => $"{g.Count()} {g.Key}"))}), {meals.Count(m => m.Uncertain)} with uncertain items.");
        if (args.Contains("--check"))
        {
            return 0;
        }

        if (await services.GetRequiredService<OpenRouterSettings>().GetKeyAsync(ct) is null)
        {
            Console.Error.WriteLine("No OpenRouter key is set, so there is nothing to compare against.");
            return 1;
        }

        var evaluator = services.GetRequiredService<ParseEvaluator>();
        var models = await evaluator.ModelsAsync(Option(args, "--parser"), Option(args, "--matcher"), ct);
        var history = !args.Contains("--no-history");
        var results = new List<(WeighedResult Result, IReadOnlyList<ConfirmItemLine> Plate, string? Error)>();
        foreach (var meal in meals)
        {
            var (parsed, items) = await evaluator.ReplayAsync(ids[meal.Account!], meal.Text, meal.EatenAt ?? DateTimeOffset.UtcNow, history, models, ct);
            var result = new WeighedResult(meal, MealNutrients.Of(items.Select(i => (i.Grams, i.Per100))));
            var lines2 = items.Select(i => new ConfirmItemLine(i.Description, i.Grams, i.FoodItemId, i.MatchedName)).ToList();
            results.Add((result, lines2, parsed.Error?.ToString()));
            if (!json)
            {
                o.WriteLine(MealText(result, lines2, parsed.Error?.ToString()));
            }
        }

        var summary = WeighedMeals.Summarize(results.Select(r => r.Result).ToList());
        if (json)
        {
            Cli.FeedbackCommand.WriteJson(o, new
            {
                parserModel = models.Parser, matcherModel = models.Matcher, history, summary.Overall, summary.ByCategory, uncertain = summary.Uncertain,
                meals = results.Select(r => new { r.Result.Meal.Id, r.Result.Meal.Category, r.Result.Meal.Text, reference = r.Result.Meal.Reference, plate = r.Result.Plate, r.Result.Meal.Uncertain, items = r.Plate, error = r.Error }),
            });
            return 0;
        }

        o.WriteLine($"Parser {models.Parser}, matcher {models.Matcher}, {(history ? "with the account's history" : "without history")}.");
        o.WriteLine("Overall: " + SummaryText(summary.Overall) + (summary.Uncertain > 0 ? $" {summary.Uncertain} meals with uncertain items are left out." : ""));
        foreach (var (category, s) in summary.ByCategory)
        {
            o.WriteLine($"  {category}: " + SummaryText(s));
        }

        o.WriteLine("Largest misses: " + string.Join(", ", summary.LargestMisses.Select(r => $"meal {r.Meal.Id} {Signed(r.KcalError)} kcal")));
        o.WriteLine("Cost is not reported here; check the OpenRouter activity page.");
        return 0;
    }

    /// <summary>One row Plate gave for a meal.</summary>
    public record ConfirmItemLine(string Description, decimal Grams, int? FoodItemId, string? MatchedName);

    /// <summary>
    /// Reads the pilot CSV: one row per weighed food, rows of a meal sharing its <c>meal</c> id, the meal's text, time,
    /// account and category on any of its rows. Meals whose id starts with "example" are the template's and skipped.
    /// Returns every problem found, by line, so a file is fixed in one go.
    /// </summary>
    public static (List<WeighedMeal> Meals, List<string> Errors) Read(IReadOnlyList<string> lines, string? account)
    {
        var errors = new List<string>();
        if (lines.Count == 0)
        {
            return ([], ["The file is empty."]);
        }

        var header = UsdaSeed.ParseCsvLine(lines[0]).Select(h => h.Trim().ToLowerInvariant()).ToList();
        var missing = Columns.Where(c => !header.Contains(c)).ToList();
        if (missing.Count > 0)
        {
            return ([], [$"Line 1: missing columns {string.Join(", ", missing)}."]);
        }

        var rows = new List<(int Line, Dictionary<string, string> Fields)>();
        for (var n = 1; n < lines.Count; n++)
        {
            if (lines[n].Trim().Length == 0)
            {
                continue;
            }

            var f = UsdaSeed.ParseCsvLine(lines[n]);
            rows.Add((n + 1, header.Select((h, i) => (h, v: i < f.Count ? f[i].Trim() : "")).ToDictionary(x => x.h, x => x.v)));
        }

        var meals = new List<WeighedMeal>();
        foreach (var group in rows.Where(r => r.Fields["meal"].Length > 0 && !r.Fields["meal"].StartsWith("example", StringComparison.OrdinalIgnoreCase))
                     .GroupBy(r => r.Fields["meal"]))
        {
            string? First(string column) => group.Select(r => r.Fields[column]).FirstOrDefault(v => v.Length > 0);
            var at = group.First().Line;
            var text = First("text");
            var category = First("category")?.ToLowerInvariant();
            var who = First("account") ?? account;
            if (text is null) errors.Add($"Line {at}: meal {group.Key} has no text.");
            if (category is null || !Categories.Contains(category)) errors.Add($"Line {at}: meal {group.Key} needs a category: {string.Join(", ", Categories)}.");
            if (who is null) errors.Add($"Line {at}: meal {group.Key} has no account; add one or pass --account.");
            DateTimeOffset? eatenAt = null;
            if (First("eaten_at") is { } time)
            {
                if ((time.Contains('+') || time.EndsWith('Z')) && DateTimeOffset.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
                {
                    eatenAt = exact;
                }
                else if (DateTime.TryParseExact(time, ["yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
                {
                    eatenAt = new DateTimeOffset(local, Sofia.GetUtcOffset(local));
                }
                else
                {
                    errors.Add($"Line {at}: eaten_at \"{time}\" isn't a time like 2026-11-22 13:30.");
                }
            }

            var items = new List<WeighedItem>();
            foreach (var (line, f) in group)
            {
                decimal? Num(string column, decimal max)
                {
                    var ok = decimal.TryParse(f[column].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0 && v <= max;
                    if (!ok) errors.Add($"Line {line}: {column} \"{f[column]}\" isn't a number from 0 to {max}.");
                    return ok ? v : null;
                }

                if (f["item"].Length == 0)
                {
                    errors.Add($"Line {line}: no item name.");
                    continue;
                }

                var (grams, kcal, protein, carbs, fat) = (Num("grams", FoodMath.MaxGrams), Num("kcal_100g", 900), Num("protein_100g", 100), Num("carbs_100g", 100), Num("fat_100g", 100));
                if (grams is null || kcal is null || protein is null || carbs is null || fat is null)
                {
                    continue;
                }

                items.Add(new WeighedItem(f["item"], grams.Value, new Per100g(kcal.Value, protein.Value, carbs.Value, fat.Value),
                    f["source"].Length == 0 ? null : f["source"], f["uncertain"].ToLowerInvariant() is "yes" or "y" or "true" or "1" or "да"));
            }

            if (text is not null && category is not null && Categories.Contains(category) && who is not null)
            {
                meals.Add(new WeighedMeal(group.Key, eatenAt, who, category, text, items));
            }
        }

        if (meals.Count == 0 && errors.Count == 0)
        {
            errors.Add("No meals in the file (rows whose meal id starts with \"example\" are skipped).");
        }

        return (meals, errors);
    }

    public static string MealText(WeighedResult r, IReadOnlyList<ConfirmItemLine> plate, string? error)
    {
        var w = r.Meal.Reference;
        var lines = new List<string>
        {
            $"meal {r.Meal.Id,-4} {r.Meal.Category,-8} weighed {Num(w.Kcal)} kcal, Plate {Num(r.Plate.Kcal)} ({Signed(r.KcalError)}); protein {Signed(r.Plate.Protein - w.Protein)} g, carbs {Signed(r.Plate.Carbs - w.Carbs)} g, fat {Signed(r.Plate.Fat - w.Fat)} g"
                + (r.Meal.Uncertain ? " [uncertain]" : "") + $"  \"{Clip(r.Meal.Text)}\"" + (error is null ? "" : $"  [{error}]"),
        };
        lines.AddRange(r.Meal.Items.Select(i => $"    weighed {Num(i.Grams)} g {i.Name}" + (i.Source is null ? "" : $" ({i.Source})")));
        lines.AddRange(plate.Select(i => $"    Plate   {Num(i.Grams)} g {i.Description} → " + (i.FoodItemId is { } id ? $"{i.MatchedName} #{id}" : "AI estimate")));
        return string.Join('\n', lines);
    }

    private static string SummaryText(NutrientAgreement s) =>
        s.Meals == 0 ? "no meals."
            : $"{s.Meals} meals, kcal off by {Num(s.MeanAbsKcal)} on average (median {Num(s.MedianAbsKcal)}, bias {Signed(s.MeanSignedKcal)}), protein {Num(s.MeanAbsProtein)} g, carbs {Num(s.MeanAbsCarbs)} g, fat {Num(s.MeanAbsFat)} g.";

    private static string Num(decimal v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    private static string Signed(decimal v) => (v >= 0 ? "+" : "") + Num(v);

    private static string Clip(string text) => text.Length > 60 ? text[..60] + "…" : text;

    private static List<string> ReadAll(TextReader input)
    {
        var lines = new List<string>();
        while (input.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines;
    }

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
