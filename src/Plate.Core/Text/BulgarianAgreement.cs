using System.Text.RegularExpressions;

namespace Plate.Core.Text;

/// <summary>Gender and number of a Bulgarian noun phrase, which an adjective beside it has to match.</summary>
public enum BulgarianForm { Masculine, Feminine, Neuter, Plural }

/// <summary>
/// Makes a food's status chip agree with its Bulgarian name: "Ориз · сготвен", "Паста · сготвена", "Картофи · сготвени".
/// The fallback status is the catalogue's neuter "сготвено", and the AI namer writes its status without seeing the
/// grammar of the name, so both can disagree. Only known state adjectives are changed, by their ending.
/// </summary>
public static partial class BulgarianAgreement
{
    // Masculine forms; the others add -а, -о or -и. Statuses the database names give (FoodNaming, FoodStates).
    private static readonly string[] StateStems =
    [
        "суров", "сух", "готвен", "сготвен", "варен", "сварен", "неварен", "печен", "изпечен", "запечен", "препечен",
        "опечен", "пържен", "изпържен", "задушен", "пушен", "сушен", "изсушен", "замразен", "размразен", "осолен",
        "маринован", "грилован", "опушен", "изпушен", "панирован", "консервиран", "бланширан", "пасиран", "обработен", "преработен",
    ];

    // Nouns whose ending says the wrong thing.
    private static readonly Dictionary<string, BulgarianForm> Exceptions = new()
    {
        ["яйца"] = BulgarianForm.Plural, ["крилца"] = BulgarianForm.Plural, ["зърна"] = BulgarianForm.Plural,
        ["семена"] = BulgarianForm.Plural, ["бедра"] = BulgarianForm.Plural, ["ребра"] = BulgarianForm.Plural,
        ["ребърца"] = BulgarianForm.Plural, ["крила"] = BulgarianForm.Plural, ["сърца"] = BulgarianForm.Plural,
        ["сол"] = BulgarianForm.Feminine, ["мас"] = BulgarianForm.Feminine, ["захар"] = BulgarianForm.Feminine,
        ["галета"] = BulgarianForm.Feminine, ["крокета"] = BulgarianForm.Feminine, ["панчета"] = BulgarianForm.Feminine,
        ["брускета"] = BulgarianForm.Feminine,
        ["киви"] = BulgarianForm.Neuter, ["уиски"] = BulgarianForm.Neuter, ["суши"] = BulgarianForm.Neuter,
        ["чили"] = BulgarianForm.Neuter,
    };

    // Adjectives that read the same for a masculine noun and a plural: "пилешки дроб", "пилешки гърди", "говежди стек".
    private static readonly string[] SharedFormEndings = ["ски", "шки", "жки", "чки", "цки", "жди"];

    private static readonly HashSet<string> SharedFormWords = ["овчи", "кози", "пачи", "гъши"];

    [GeneratedRegex(@"\p{L}+")]
    private static partial Regex Word();

    private static bool IsCyrillic(string word) => word.All(c => c is >= 'Ѐ' and <= 'ӿ');

    /// <summary>
    /// The form of a food name from its head noun: the first word, or the word after a leading adjective that is the same
    /// for a masculine noun and a plural ("пилешки дроб", "пилешки гърди"). Null when the name doesn't
    /// start with a Bulgarian word.
    /// </summary>
    public static BulgarianForm? FormOf(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var words = Word().Matches(name.Split(',')[0]).Select(m => m.Value.ToLowerInvariant()).ToList();
        if (words.Count == 0 || !IsCyrillic(words[0]))
        {
            return null;
        }

        var head = words[0];
        if (words.Count > 1 && IsCyrillic(words[1]) && (SharedFormWords.Contains(head) || SharedFormEndings.Any(head.EndsWith)))
        {
            head = words[1];
        }

        if (Exceptions.TryGetValue(head, out var form))
        {
            return form;
        }

        // Plurals of -е nouns (филе, кюфте) and of masculine nouns in -ове/-еве (плодове).
        if (head.Length > 4 && (head.EndsWith("ета") || head.EndsWith("ове") || head.EndsWith("еве")))
        {
            return BulgarianForm.Plural;
        }

        return head[^1] switch
        {
            'а' or 'я' => BulgarianForm.Feminine,
            'о' or 'е' or 'ю' or 'у' => BulgarianForm.Neuter,
            'и' => BulgarianForm.Plural,
            _ => BulgarianForm.Masculine,
        };
    }

    /// <summary>
    /// <paramref name="status"/> with each state adjective in it changed to agree with <paramref name="name"/>; as it is
    /// when it has none or the name has no Bulgarian form.
    /// </summary>
    public static string Agree(string status, string? name)
    {
        if (FormOf(name) is not { } form)
        {
            return status;
        }

        var ending = form switch { BulgarianForm.Feminine => "а", BulgarianForm.Neuter => "о", BulgarianForm.Plural => "и", _ => "" };
        return Word().Replace(status, m =>
        {
            var lower = m.Value.ToLowerInvariant();
            var stem = StateStems.FirstOrDefault(s => lower == s || (lower.Length == s.Length + 1 && lower.StartsWith(s, StringComparison.Ordinal) && lower[^1] is 'а' or 'о' or 'и'));
            if (stem is null)
            {
                return m.Value;
            }

            var agreed = stem + ending;
            return char.IsUpper(m.Value[0]) ? char.ToUpperInvariant(agreed[0]) + agreed[1..] : agreed;
        });
    }
}
