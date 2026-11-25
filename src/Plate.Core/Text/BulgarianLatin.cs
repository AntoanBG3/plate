using System.Text;
using System.Text.RegularExpressions;

namespace Plate.Core.Text;

/// <summary>
/// Bulgarian is often typed in Latin letters, in the official transliteration ("shopska salata", "kyufte") or the
/// informal one ("mlqko", "kebap4eta", "banica"). Product names in the database are Cyrillic only, so "boza" meets
/// nothing (QA 2026-11-24 M4). This gives one Cyrillic reading of such text. Some letters can't be told apart ("a" may
/// be "ъ", "u" may be "ъ"), so the reading is a best guess for a trigram search, which forgives a letter or two.
/// </summary>
public static partial class BulgarianLatin
{
    // Longest first, so "sht" wins over "sh" and "s".
    private static readonly (string Latin, string Cyrillic)[] Groups =
    [
        ("sht", "щ"), ("sh", "ш"), ("ch", "ч"), ("zh", "ж"), ("ts", "ц"), ("kh", "х"),
        ("yu", "ю"), ("iu", "ю"), ("ya", "я"), ("yo", "йо"),
    ];

    private static readonly Dictionary<char, string> Letters = new()
    {
        ['a'] = "а", ['b'] = "б", ['c'] = "ц", ['d'] = "д", ['e'] = "е", ['f'] = "ф", ['g'] = "г", ['h'] = "х",
        ['i'] = "и", ['j'] = "ж", ['k'] = "к", ['l'] = "л", ['m'] = "м", ['n'] = "н", ['o'] = "о", ['p'] = "п",
        ['q'] = "я", ['r'] = "р", ['s'] = "с", ['t'] = "т", ['u'] = "у", ['v'] = "в", ['w'] = "в", ['x'] = "х",
        ['z'] = "з",
        // Informal: the digits that look like the letter.
        ['4'] = "ч", ['6'] = "ш",
    };

    private const string Vowels = "aeiouy";

    /// <summary>
    /// The Cyrillic reading of text written only in Latin letters; null when it has any Cyrillic letter (it is already
    /// Bulgarian, or mixed) or no Latin letter at all.
    /// </summary>
    public static string? ToCyrillic(string text)
    {
        if (text.Any(IsCyrillic) || !text.Any(IsLatin))
        {
            return null;
        }

        return Token().Replace(text.ToLowerInvariant(), m => Word(m.Value));
    }

    private static string Word(string word)
    {
        // An amount ("200g", "1.5l", "3") keeps its digits: only a 4 or 6 among letters is a letter.
        if (Amount().IsMatch(word) || !word.Any(IsLatin))
        {
            return word;
        }

        var result = new StringBuilder(word.Length);
        for (var i = 0; i < word.Length;)
        {
            var group = Groups.FirstOrDefault(g => string.CompareOrdinal(word, i, g.Latin, 0, g.Latin.Length) == 0);
            if (group.Latin is not null)
            {
                result.Append(group.Cyrillic);
                i += group.Latin.Length;
                continue;
            }

            var c = word[i];
            if (c == 'y')
            {
                // "chay", "yaytsa": next to a vowel it is "й"; between consonants the informal "ъ" ("sys").
                var nextToVowel = i > 0 && Vowels.Contains(word[i - 1]) || i + 1 < word.Length && Vowels.Contains(word[i + 1]);
                result.Append(nextToVowel ? "й" : "ъ");
            }
            else
            {
                result.Append(Letters.GetValueOrDefault(c, c.ToString()));
            }

            i++;
        }

        return result.ToString();
    }

    private static bool IsCyrillic(char c) => c is >= 'Ѐ' and <= 'ӿ';

    private static bool IsLatin(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Token();

    [GeneratedRegex(@"^\d+([.,]\d+)?(g|gr|kg|ml|l|x)?$")]
    private static partial Regex Amount();
}
