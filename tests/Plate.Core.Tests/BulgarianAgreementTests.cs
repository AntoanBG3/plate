using FluentAssertions;
using Plate.Core.Text;

namespace Plate.Core.Tests;

public class BulgarianAgreementTests
{
    [Theory]
    // The ending of the first word: a consonant is masculine, -а/-я feminine, -о/-е neuter, -и plural.
    [InlineData("Ориз", BulgarianForm.Masculine)]
    [InlineData("Ориз басмати", BulgarianForm.Masculine)]
    [InlineData("Хляб пълнозърнест", BulgarianForm.Masculine)]
    [InlineData("Чай", BulgarianForm.Masculine)]
    [InlineData("Паста със сол", BulgarianForm.Feminine)]
    [InlineData("Червена леща", BulgarianForm.Feminine)]
    [InlineData("Сьомга", BulgarianForm.Feminine)]
    [InlineData("Пица", BulgarianForm.Feminine)]
    [InlineData("Пилешко филе", BulgarianForm.Neuter)]
    [InlineData("Яйце", BulgarianForm.Neuter)]
    [InlineData("Кафе", BulgarianForm.Neuter)]
    [InlineData("Картофи", BulgarianForm.Plural)]
    [InlineData("Спагети", BulgarianForm.Plural)]
    [InlineData("Овесени ядки", BulgarianForm.Plural)]
    // An adjective in -ски is the same for a masculine noun and for a plural, so the noun after it decides.
    [InlineData("Пилешки гърди без кожа", BulgarianForm.Plural)]
    [InlineData("Пилешки дроб", BulgarianForm.Masculine)]
    [InlineData("Свински врат", BulgarianForm.Masculine)]
    [InlineData("Телешка кайма", BulgarianForm.Feminine)]
    // Words whose ending misleads.
    [InlineData("Яйца", BulgarianForm.Plural)]
    [InlineData("Пилешки филета", BulgarianForm.Plural)]
    [InlineData("Кюфтета", BulgarianForm.Plural)]
    [InlineData("Плодове", BulgarianForm.Plural)]
    [InlineData("Сол", BulgarianForm.Feminine)]
    [InlineData("Захар", BulgarianForm.Feminine)]
    [InlineData("Киви", BulgarianForm.Neuter)]
    [InlineData("Галета", BulgarianForm.Feminine)]
    [InlineData("Панчета", BulgarianForm.Feminine)]
    [InlineData("Пилешки бедра", BulgarianForm.Plural)]
    [InlineData("Свински ребра", BulgarianForm.Plural)]
    [InlineData("Пилешки крила", BulgarianForm.Plural)]
    [InlineData("Пилешки сърца", BulgarianForm.Plural)]
    // Other adjectives that read the same for a masculine noun and a plural.
    [InlineData("Говежди стек", BulgarianForm.Masculine)]
    [InlineData("Говежди кюфтета", BulgarianForm.Plural)]
    [InlineData("Овчи кашкавал", BulgarianForm.Masculine)]
    [InlineData("Пачи гърди", BulgarianForm.Plural)]
    public void The_form_comes_from_the_name(string name, BulgarianForm form) =>
        BulgarianAgreement.FormOf(name).Should().Be(form);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Skyr Milbona")]
    [InlineData("200")]
    public void A_name_without_a_Bulgarian_word_has_no_form(string? name) =>
        BulgarianAgreement.FormOf(name).Should().BeNull();

    [Theory]
    [InlineData("сготвено", "Ориз", "сготвен")]
    [InlineData("сготвено", "Паста", "сготвена")]
    [InlineData("сготвено", "Пилешко филе", "сготвено")]
    [InlineData("сготвено", "Картофи", "сготвени")]
    [InlineData("сурово", "Пилешки гърди", "сурови")]
    [InlineData("сурово", "Свински врат", "суров")]
    [InlineData("сурово", "Сьомга", "сурова")]
    [InlineData("варена", "Ориз", "варен")]
    [InlineData("варен", "Паста със сол", "варена")]
    [InlineData("печени", "Пиле", "печено")]
    [InlineData("пържено", "Яйца", "пържени")]
    [InlineData("задушена", "Броколи", "задушени")]
    [InlineData("пушено", "Скумрия", "пушена")]
    [InlineData("замразено", "Грах", "замразен")]
    [InlineData("сушено", "Сини сливи", "сушени")]
    [InlineData("варено и осолено", "Ориз", "варен и осолен")]
    [InlineData("грилована", "Пилешко филе", "гриловано")]
    [InlineData("опушено", "Свински врат", "опушен")]
    [InlineData("панирован", "Пилешки крила", "панировани")]
    public void A_state_word_agrees_with_the_name(string status, string name, string agreed) =>
        BulgarianAgreement.Agree(status, name).Should().Be(agreed);

    [Theory]
    // Phrases with no ending to change, English, unknown words and names with no form are left as they are.
    [InlineData("на скара", "Пилешко филе")]
    [InlineData("на пара", "Ориз")]
    [InlineData("raw", "Ориз")]
    [InlineData("boiled", "Chicken breast")]
    [InlineData("сготвено", "Skyr")]
    [InlineData("сготвено", "")]
    [InlineData("домашно", "Ориз")]
    public void Anything_else_is_left_alone(string status, string name) =>
        BulgarianAgreement.Agree(status, name).Should().Be(status);

    [Fact]
    public void A_capital_stays_a_capital() =>
        BulgarianAgreement.Agree("Сготвено", "Ориз").Should().Be("Сготвен");
}
