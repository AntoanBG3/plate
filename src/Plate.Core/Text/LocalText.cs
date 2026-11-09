using System.Globalization;

namespace Plate.Core.Text;

/// <summary>
/// A user-facing message as an English format string plus its arguments, so the web layer can show it in
/// the user's language. The format is the lookup key; arguments fill <c>{0}</c>, <c>{1}</c> after translation.
/// </summary>
public sealed record LocalText(string Format, params object[] Args)
{
    /// <summary>The English text, with numbers in the invariant culture.</summary>
    public override string ToString() =>
        Args.Length == 0 ? Format : string.Format(CultureInfo.InvariantCulture, Format, Args);

    public static implicit operator LocalText(string text) => new(text);

    /// <summary>Picks the singular or plural English form; Bulgarian splits counts the same way (1 against the rest).</summary>
    public static LocalText Count(long n, string one, string other) => new(n == 1 ? one : other, n);

    public bool Equals(LocalText? other) =>
        other is not null && Format == other.Format && Args.SequenceEqual(other.Args);

    public override int GetHashCode() => Format.GetHashCode();
}
