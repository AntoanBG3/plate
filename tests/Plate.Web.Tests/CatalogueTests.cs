using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Plate.Core.Domain;
using Plate.Web.Data.Seed;
using Plate.Web.Features.Body;
using Plate.Web.Features.Localization;
using Xunit;
using Xunit.Abstractions;

namespace Plate.Web.Tests;

/// <summary>
/// Every user-facing string must have a Bulgarian translation with the same placeholders. The keys are found by
/// scanning the source for the ways text reaches the user (L["..."], LocalText, validation messages, notices,
/// per-meal phrases, enum and seeded exercise names, script strings).
/// Run with GYM_WRITE_KEYS=1 to write Resources/strings.keys.json, which tools/i18n/translate.mjs translates.
/// </summary>
public partial class CatalogueTests(ITestOutputHelper output)
{
    private static readonly string Repo = RepoPaths.Root;
    private static readonly string Web = RepoPaths.Web;

    private const string Str = "\"((?:[^\"\\\\]|\\\\.)*)\"";

    private static readonly Regex[] Patterns =
    [
        new(@"\bL\[\s*" + Str),                                                     // L["..."] in views and code
        new(@"\bthis\[\s*" + Str),                                                  // the localizer's own phrases
        new(@"\b(?:L|LocalText)\.Count\([^,]+,\s*" + Str + @",\s*" + Str),          // singular and plural
        new(@"\bLocalText\(\s*" + Str),                                             // new LocalText("...")
        new(@"\b(?:ValidationException|Notice|Error|Failed)\(\s*" + Str),            // messages and notices
        new(@"\[Meal\.\w+\]\s*=\s*" + Str),                                         // whole phrases per meal
        new(@"(?:return|\?|:|=>)\s*" + "\"([A-Z][^\"]*[.!?…])\""),                  // sentences returned by rules
    ];

    // Strings that reach the user without a pattern above.
    private static readonly string[] Extra = ["assumed 100 g", "{0} × a serving of {1} g", "{0} × a piece of {1} g", "{0} × a pack of {1} g",
        "No weight per piece for this food, so check the grams.", "The pack size isn't known, so check the grams.", "No serving size for this food, so check the grams.",
        "These values are estimated from the recipe, not read from a label.", "Freestyle", "Dark", "Light", "Match phone", "Entry deleted.", "Exercise removed."];

    // Matched by a pattern but never shown: instructions sent to the model.
    private static readonly string[] Ignore = ["Write assumption in English.", "Reply in English.", "Write in English.", "Read this nutrition label."];

    public static SortedDictionary<string, SortedSet<string>> Extract()
    {
        var keys = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        void Add(string key, string where)
        {
            key = Regex.Unescape(key);
            if (Ignore.Contains(key) || key.Length == 0 || key.All(c => !char.IsLetter(c)))
            {
                return;
            }

            if (!keys.TryGetValue(key, out var set))
            {
                keys[key] = set = new SortedSet<string>(StringComparer.Ordinal);
            }

            set.Add(where);
        }

        var files = Directory.EnumerateFiles(Web, "*.cshtml", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Web, "*.cs", SearchOption.AllDirectories))
            .Concat(Directory.EnumerateFiles(Path.Combine(Repo, "src", "Plate.Core"), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains("Migrations") && !f.Contains($"{Path.DirectorySeparatorChar}Cli{Path.DirectorySeparatorChar}") && !IsAdmin(f));
        foreach (var file in files)
        {
            var where = Path.GetRelativePath(Repo, file);
            foreach (var line in File.ReadLines(file))
            {
                if (line.Contains("Log") && line.Contains("log.") || line.Contains("InvalidOperationException") || line.TrimStart().StartsWith("//") || line.TrimStart().StartsWith("///"))
                {
                    continue;
                }

                foreach (var pattern in Patterns)
                {
                    foreach (Match m in pattern.Matches(line))
                    {
                        for (var g = 1; g < m.Groups.Count; g++)
                        {
                            if (m.Groups[g].Success)
                            {
                                Add(m.Groups[g].Value, where);
                            }
                        }
                    }
                }
            }
        }

        foreach (var key in ScriptText.Keys)
        {
            Add(key, "wwwroot/js");
        }

        foreach (var name in Enum.GetNames<Meal>().Concat(Enum.GetNames<Muscle>()).Concat(Enum.GetNames<Equipment>()).Concat(Enum.GetNames<Goal>()).Concat(Enum.GetNames<ActivityLevel>()))
        {
            Add(Localizer.Humanize(name), "enum");
        }

        foreach (var exercise in ExerciseSeed.All())
        {
            Add(exercise.Name, "exercise");
        }

        foreach (var (_, label) in BodyAnalytics.Ranges)
        {
            Add(label, "range");
        }

        foreach (var key in Extra)
        {
            Add(key, "extra");
        }

        return keys;
    }

    // The admin area is English only: only the developer sees it (SPEC 12). The unlock page is reached from Settings and
    // follows the account's language.
    private static bool IsAdmin(string file)
    {
        var sep = Path.DirectorySeparatorChar;
        if (file.Contains($"{sep}Views{sep}AdminUnlock{sep}"))
        {
            return false;
        }

        return file.Contains($"{sep}Views{sep}Admin") || file.Contains($"{sep}Features{sep}Admin{sep}")
            || (file.Contains($"{sep}Controllers{sep}") && Path.GetFileName(file).StartsWith("Admin", StringComparison.Ordinal));
    }

    [Fact]
    public void Script_strings_are_all_listed_for_translation()
    {
        var used = Directory.EnumerateFiles(Path.Combine(Web, "wwwroot", "js"), "*.js")
            .SelectMany(f => TCall().Matches(File.ReadAllText(f)).Select(m => Regex.Unescape(m.Groups[1].Value)))
            .Distinct()
            .ToList();
        used.Should().NotBeEmpty();
        used.Except(ScriptText.Keys).Should().BeEmpty("every t(\"...\") string must be in ScriptText.Keys so the layout passes its translation");
    }

    [Fact]
    public void Every_string_has_a_Bulgarian_translation_with_the_same_placeholders()
    {
        var keys = Extract();
        if (Environment.GetEnvironmentVariable("GYM_WRITE_KEYS") == "1")
        {
            File.WriteAllText(Path.Combine(Web, "Resources", "strings.keys.json"),
                JsonSerializer.Serialize(keys, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n");
        }

        var bg = Localizer.Load(Web).Catalogue;
        var missing = keys.Keys.Where(k => !bg.ContainsKey(k)).ToList();
        foreach (var key in missing.Take(40))
        {
            output.WriteLine($"missing: {key}  ({string.Join(", ", keys[key])})");
        }

        missing.Should().BeEmpty($"{missing.Count} strings have no Bulgarian translation; run GYM_WRITE_KEYS=1 dotnet test --filter Catalogue, then node tools/i18n/translate.mjs");

        var wrong = bg.Where(kv => keys.ContainsKey(kv.Key) && !Placeholders(kv.Key).SetEquals(Placeholders(kv.Value))).Select(kv => kv.Key).ToList();
        wrong.Should().BeEmpty("a translation must use exactly the placeholders of its English key");
    }

    private static HashSet<string> Placeholders(string s) => Placeholder().Matches(s).Select(m => m.Value).ToHashSet();

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex("\\bt\\(\\s*\"((?:[^\"\\\\]|\\\\.)*)\"")]
    private static partial Regex TCall();
}
