using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Plate.Core.Text;

namespace Plate.Web.Tests;

/// <summary>
/// House rules for the Bulgarian text (SPEC 8, Phase 11): numbers with a dot like everywhere else in the app, units in
/// Bulgarian, the informal "ти" throughout, no Latin look-alike letters inside Cyrillic words, and no translations
/// left over for strings the app no longer has.
/// </summary>
public partial class BulgarianCatalogueTests
{
    private static readonly Dictionary<string, string> Bg = Read("strings.bg.json");

    private static Dictionary<string, string> Read(string file) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(Path.Combine(RepoPaths.Web, "Resources", file)))!
            .ToDictionary(e => e.Key, e => e.Value.ValueKind == JsonValueKind.String ? e.Value.GetString()! : "");

    private static List<string> Offenders(Regex pattern) =>
        Bg.Where(e => pattern.IsMatch(e.Value)).Select(e => $"{e.Key} => {e.Value}").ToList();

    [Fact]
    public void Every_translation_is_for_a_string_the_app_still_has() =>
        Bg.Keys.Except(Read("strings.keys.json").Keys).Should().BeEmpty("stale translations are pruned (regenerate keys with GYM_WRITE_KEYS=1)");

    [Fact]
    public void Decimals_use_a_dot() =>
        Offenders(DecimalComma()).Should().BeEmpty();

    [Fact]
    public void Units_are_in_Bulgarian() =>
        Offenders(EnglishUnit()).Should().BeEmpty();

    // The formal pronoun is capitalised ("Вие", "Ви", "Ваш"); a lowercase "ви" is the plural, as in "за двама ви". The
    // formal verb forms the app would use are listed: a general "-ете" rule would also catch "разчете" and "дисковете".
    [Fact]
    public void The_app_says_ти_not_Вие() =>
        Offenders(FormalPronoun()).Concat(Offenders(FormalVerb())).Should().BeEmpty();

    [Fact]
    public void No_word_mixes_Latin_and_Cyrillic_letters() =>
        Bg.Where(e => MixedScript.HasMixedWord(e.Value)).Select(e => e.Value).Should().BeEmpty();

    [GeneratedRegex(@"\d,\d")]
    private static partial Regex DecimalComma();

    [GeneratedRegex(@"\b(kcal|kg)\b")]
    private static partial Regex EnglishUnit();

    [GeneratedRegex(@"(?<![А-Яа-я])(Вие|Ви|Ваш|Ваша|Ваше|Ваши)(?![А-Яа-я])")]
    private static partial Regex FormalPronoun();

    [GeneratedRegex(@"(?i)(?<![а-я])(имате|въведете|оставете|задайте|променете|получите|изберете|натиснете|опитайте|запишете|добавете|проверете|потвърдете|премахнете|можете|искате|влезте)(?![а-я])")]
    private static partial Regex FormalVerb();
}
