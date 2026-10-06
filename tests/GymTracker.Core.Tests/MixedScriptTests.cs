using FluentAssertions;
using GymTracker.Core.Text;

namespace GymTracker.Core.Tests;

/// <summary>Food names scraped with Latin look-alike letters inside Cyrillic words, or the reverse (Phase 11).</summary>
public class MixedScriptTests
{
    [Theory]
    [InlineData("Консeрвиран печен боб", "Консервиран печен боб")] // Latin e in a Cyrillic word
    [InlineData("Картофен чипc", "Картофен чипс")]
    [InlineData("Рафаелоp", "Рафаелор")]
    [InlineData("Златна селекция кравe кашкавал", "Златна селекция краве кашкавал")]
    [InlineData("Мousse strawberry banana", "Mousse strawberry banana")] // Cyrillic М in a Latin word
    public void A_look_alike_letter_takes_the_alphabet_of_the_rest_of_its_word(string text, string expected) =>
        MixedScript.Normalize(text).Should().Be(expected);

    [Theory]
    [InlineData("Кисело мляко 2% Danone")] // the two alphabets in separate words are fine
    [InlineData("Bananas, raw")]
    [InlineData("Боб чорба")]
    [InlineData("Coca-Cola Zero")]
    public void Words_in_one_alphabet_are_left_alone(string text) =>
        MixedScript.Normalize(text).Should().Be(text);

    [Fact]
    public void Detects_mixed_words() =>
        (MixedScript.HasMixedWord("чипc"), MixedScript.HasMixedWord("чипс Lay's")).Should().Be((true, false));
}
