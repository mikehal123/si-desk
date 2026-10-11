using System.Globalization;

namespace Sovereign.Desktop.Core;

/// <summary>
/// Exact money as integer minor units. Binary floating point is forbidden for stored amounts.
/// </summary>
public readonly record struct Money(string Currency, long MinorUnits)
{
    public string Currency { get; } = string.IsNullOrWhiteSpace(Currency)
        ? throw new ArgumentException("Currency is required.", nameof(Currency))
        : Currency;

    public long MinorUnits { get; } = MinorUnits;

    public static Money UsdCents(long cents) => new("USD", cents);

    public string Format(CultureInfo? culture = null)
    {
        culture ??= CultureInfo.InvariantCulture;
        var major = MinorUnits / 100m;
        return string.Create(culture, $"{Currency} {major:0.00}");
    }

    public override string ToString() => Format();
}
