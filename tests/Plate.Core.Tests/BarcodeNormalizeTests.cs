using FluentAssertions;
using Plate.Core.Nutrition;

namespace Plate.Core.Tests;

/// <summary>The barcode types SPEC 7 lists: EAN-13, EAN-8, UPC-A and UPC-E, typed or scanned.</summary>
public class BarcodeNormalizeTests
{
    [Theory]
    [InlineData("04252614", "042100005264")] // UPC-E, last digit 1: manufacturer 421, product 526
    [InlineData("01234505", "012000003455")] // last digit 5: manufacturer 12345, product 5
    [InlineData("01234531", "012300000451")] // last digit 3
    [InlineData("01234543", "012340000053")] // last digit 4
    public void A_upc_e_code_is_expanded_to_its_upc_a(string upcE, string upcA)
    {
        var normalized = Barcodes.Normalize(upcE);
        normalized.Should().Be(upcA);
        Barcodes.IsValid(normalized).Should().BeTrue();
    }

    [Theory]
    [InlineData("96385074")] // a valid EAN-8 stays as it is
    [InlineData("4006381333931")] // EAN-13
    [InlineData("036000291452")] // UPC-A
    public void Other_codes_are_kept(string code) => Barcodes.Normalize(code).Should().Be(code);

    [Fact]
    public void Spaces_and_dashes_are_dropped() => Barcodes.Normalize(" 4006381-333931 ").Should().Be("4006381333931");

    [Fact]
    public void A_wrong_check_digit_stays_invalid() => Barcodes.IsValid(Barcodes.Normalize("12345678")).Should().BeFalse();
}
