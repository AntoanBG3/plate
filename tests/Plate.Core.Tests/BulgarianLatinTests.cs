using FluentAssertions;
using Plate.Core.Text;

namespace Plate.Core.Tests;

public class BulgarianLatinTests
{
    [Theory]
    [InlineData("boza", "боза")]
    [InlineData("Boza", "боза")]
    [InlineData("banica sus sirene", "баница сус сирене")]
    [InlineData("shopska salata", "шопска салата")]
    [InlineData("kiselo mlqko", "кисело мляко")]
    [InlineData("kebap4eta", "кебапчета")]
    [InlineData("6okolad", "шоколад")]
    [InlineData("kyufte", "кюфте")]
    [InlineData("kiufte", "кюфте")]
    [InlineData("chorba", "чорба")]
    [InlineData("shtrudel", "щрудел")]
    [InlineData("zhito", "жито")]
    [InlineData("palachinka s med", "палачинка с мед")]
    [InlineData("chay", "чай")]
    [InlineData("yaytsa", "яйца")]
    [InlineData("hlyab", "хляб")]
    [InlineData("omlet ot 3 qica", "омлет от 3 яица")]
    [InlineData("musaka 300g", "мусака 300g")]
    public void Reads_Latin_Bulgarian_in_Cyrillic(string latin, string cyrillic) =>
        BulgarianLatin.ToCyrillic(latin).Should().Be(cyrillic);

    [Theory]
    [InlineData("боза")]
    [InlineData("boza с мед")]
    [InlineData("200")]
    [InlineData("")]
    public void Text_with_Cyrillic_or_no_Latin_letters_has_no_reading(string text) =>
        BulgarianLatin.ToCyrillic(text).Should().BeNull();
}
