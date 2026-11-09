using System.Globalization;
using System.Text.Json;
using Plate.Core.Text;

namespace Plate.Web.Features.Localization;

/// <summary>
/// Looks up user-facing text in the Bulgarian catalogue (Resources/strings.bg.json) when the request's UI culture
/// is Bulgarian. Keys are the English text, so English needs no catalogue and a missing translation shows English.
/// Numbers keep the invariant culture in both languages; only words and dates change.
/// </summary>
public static class FoodNames
{
    /// <summary>The Bulgarian name for a Bulgarian interface when the food has one.</summary>
    public static string DisplayName(this Plate.Core.Domain.FoodItem item) =>
        Localizer.IsBulgarian && !string.IsNullOrEmpty(item.NameBg) ? item.NameBg : item.Name;
}

public class Localizer
{
    public const string CatalogueFile = "Resources/strings.bg.json";
    public static readonly CultureInfo Bulgarian = CultureInfo.GetCultureInfo("bg-BG");

    private readonly IReadOnlyDictionary<string, string> bg;

    public Localizer(IReadOnlyDictionary<string, string> catalogue) => bg = catalogue;

    public static Localizer Load(string contentRoot)
    {
        var path = Path.Combine(contentRoot, CatalogueFile);
        var catalogue = File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? []
            : [];
        return new Localizer(catalogue);
    }

    public static bool IsBulgarian => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "bg";

    /// <summary>The culture for month and day names: Bulgarian, or the invariant (English) names.</summary>
    public static CultureInfo DateCulture => IsBulgarian ? Bulgarian : CultureInfo.InvariantCulture;

    public string this[string key] => IsBulgarian && bg.TryGetValue(key, out var text) ? text : key;

    public string this[string format, params object?[] args] =>
        string.Format(CultureInfo.InvariantCulture, this[format], args);

    /// <summary>Arguments that are themselves LocalText (a food's error inside a sentence) are translated too.</summary>
    public string this[LocalText text] =>
        text.Args.Length == 0
            ? this[text.Format]
            : string.Format(CultureInfo.InvariantCulture, this[text.Format], text.Args.Select(a => a is LocalText inner ? this[inner] : a).ToArray());

    /// <summary>Singular for 1, plural otherwise; the count fills {0}.</summary>
    public string Count(long n, string one, string other) => this[LocalText.Count(n, one, other)];

    /// <summary>Display name for an enum value (meals, muscles, goals): the English name is the key.</summary>
    public string Enum<T>(T value) where T : struct, System.Enum => this[Humanize(value.ToString())];

    /// <summary>"SeatedCableRow" style enum names to "Seated cable row".</summary>
    public static string Humanize(string name) =>
        string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) ? " " + char.ToLowerInvariant(c) : c.ToString()));

    /// <summary>Seeded exercises have English names that are translated; a user's own exercises keep their name.</summary>
    /// <summary>A food entry's name as shown: without a leading amount (older entries kept it), "Quick add" when empty.</summary>
    public string Entry(string description) =>
        string.IsNullOrWhiteSpace(description) ? this["Quick add"] : Plate.Core.Text.FoodText.StripAmount(description);

    /// <summary>
    /// The status shown beside an entry's name: the one saved with it ("boiled", in the language it was logged in), or
    /// raw or cooked as the food's database name says; null when neither says.
    /// </summary>
    public string? Status(string? saved, Plate.Core.Nutrition.FoodState fromFood) =>
        !string.IsNullOrWhiteSpace(saved) ? saved : State(fromFood);

    /// <summary>"raw" or "cooked" in the user's language; null for a food that is neither.</summary>
    public string? State(Plate.Core.Nutrition.FoodState state) => state switch
    {
        Plate.Core.Nutrition.FoodState.Raw => this["raw"],
        Plate.Core.Nutrition.FoodState.Cooked => this["cooked"],
        _ => null,
    };

    /// <summary>An entry's status: its own, or from the linked food when the query loaded it.</summary>
    public string? Status(Plate.Core.Domain.FoodLogEntry entry) =>
        Status(entry.Status, entry.FoodItem is { } food ? Plate.Core.Nutrition.FoodStates.FromName(food.Name, food.NameBg) : default);

    public string Exercise(Plate.Core.Domain.Exercise exercise) =>
        exercise.OwnerUserId is null ? this[exercise.Name] : exercise.Name;

    /// <summary>"45 min" or "1 h 5 min" in the user's language.</summary>
    public string Duration(TimeSpan span) => span.TotalHours >= 1
        ? this["{0} h {1} min", (int)span.TotalHours, span.Minutes]
        : this["{0} min", Math.Max(0, span.Minutes)];

    public IReadOnlyDictionary<string, string> Catalogue => bg;
}
