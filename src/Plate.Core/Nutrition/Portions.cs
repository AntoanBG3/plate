using System.Globalization;
using Plate.Core.Text;

namespace Plate.Core.Nutrition;

/// <summary>A food's weights for counted amounts; null where the source doesn't give one.</summary>
/// <param name="Piece">One piece, as sold or as the label counts it ("2 biscuits (30 g)" is 15 g).</param>
/// <param name="Pack">A whole pack's net weight.</param>
/// <param name="Serving">The food's serving, which can be several pieces, so it never stands in for a piece.</param>
public record UnitWeights(decimal? Piece, decimal? Pack, decimal? Serving);

/// <summary>Where a row's grams came from.</summary>
public enum PortionBasis
{
    /// <summary>The parser's estimate (or the account's usual amount, set later).</summary>
    Parser,

    /// <summary>A weight or volume the text gives.</summary>
    Stated,

    /// <summary>A count times the matched food's weight for that unit.</summary>
    Count,
}

/// <param name="Note">Says how the grams were reached, or that they are a guess to check; null when the parser's own assumption says it.</param>
public record Portion(decimal Grams, PortionBasis Basis, LocalText? Note);

/// <summary>
/// The grams for a parsed food once it has a database food (SPEC 6.1 step 4). A weight the text gives wins; a count
/// uses the food's weight for what it counts (a piece, a pack or a serving); without that weight the parser's estimate
/// stands and the row says to check it.
/// </summary>
public static class Portions
{
    // Note formats; localisation keys, listed in CatalogueTests.Extra.
    public const string PieceNote = "{0} × a piece of {1} g";
    public const string PackNote = "{0} × a pack of {1} g";
    public const string ServingNote = "{0} × a serving of {1} g";
    public const string NoPieceNote = "No weight per piece for this food, so check the grams.";
    public const string NoPackNote = "The pack size isn't known, so check the grams.";
    public const string NoServingNote = "No serving size for this food, so check the grams.";

    public static readonly string[] Notes = [PieceNote, PackNote, ServingNote, NoPieceNote, NoPackNote, NoServingNote];

    public static Portion For(ParsedFoodItem item, UnitWeights food)
    {
        if (item.GramsStated || item.Units is not { } units || units <= 0)
        {
            return new Portion(item.Grams, item.GramsStated ? PortionBasis.Stated : PortionBasis.Parser, null);
        }

        var unit = item.CountOf ?? CountUnit.Piece;
        var weight = unit switch
        {
            CountUnit.Pack => food.Pack,
            CountUnit.Serving => food.Serving,
            _ => food.Piece,
        };
        if (weight is { } w and > 0)
        {
            var format = unit switch { CountUnit.Pack => PackNote, CountUnit.Serving => ServingNote, _ => PieceNote };
            return new Portion(Math.Round(units * w, 1), PortionBasis.Count, new LocalText(format, Num(units), Num(w)));
        }

        // The parser's assumption already says how it guessed; otherwise say the grams are a guess to check.
        var missing = unit switch { CountUnit.Pack => NoPackNote, CountUnit.Serving => NoServingNote, _ => NoPieceNote };
        return new Portion(item.Grams, PortionBasis.Parser, string.IsNullOrWhiteSpace(item.Assumption) ? new LocalText(missing) : null);
    }

    private static string Num(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
