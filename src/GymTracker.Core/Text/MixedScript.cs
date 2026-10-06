using System.Text;
using System.Text.RegularExpressions;

namespace GymTracker.Core.Text;

/// <summary>
/// Scraped product names sometimes have a Latin letter that looks Cyrillic inside a Cyrillic word ("чипc"), or the
/// reverse ("Мousse"). They look right but don't match a search typed on a keyboard. In a word that mixes the two,
/// each look-alike takes the alphabet most of the word's letters are in.
/// </summary>
public static partial class MixedScript
{
    private const string Latin = "aeopcxyAEOPCXBKMHT";
    private const string Cyrillic = "аеорсхуАЕОРСХВКМНТ";

    public static bool HasMixedWord(string text) => Words().Matches(text).Any(m => IsMixed(m.Value));

    public static string Normalize(string text) =>
        Words().Replace(text, m => IsMixed(m.Value) ? Fix(m.Value) : m.Value);

    private static bool IsMixed(string word) => word.Any(IsCyrillic) && word.Any(IsLatin);

    private static string Fix(string word)
    {
        var toCyrillic = word.Count(IsCyrillic) >= word.Count(IsLatin);
        var (from, to) = toCyrillic ? (Latin, Cyrillic) : (Cyrillic, Latin);
        var result = new StringBuilder(word.Length);
        foreach (var c in word)
        {
            var i = from.IndexOf(c);
            result.Append(i >= 0 ? to[i] : c);
        }

        return result.ToString();
    }

    private static bool IsCyrillic(char c) => c is >= 'Ѐ' and <= 'ӿ';

    private static bool IsLatin(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    [GeneratedRegex(@"\p{L}+")]
    private static partial Regex Words();
}
