namespace Exchange.Domain.Money;

/// <summary>
/// The one place money, rates and percentages are rounded (RULE-MONEY-DECIMAL, spec 1.3).
/// </summary>
public static class Quantize
{
    /// <summary>Decimal places of an amount: <c>NUMERIC(18,2)</c>.</summary>
    public const int AmountScaleDigits = 2;

    /// <summary>Decimal places of a rate: <c>NUMERIC(24,12)</c>.</summary>
    public const int RateScaleDigits = 12;

    /// <summary>Decimal places of a percentage: <c>NUMERIC(7,4)</c>.</summary>
    public const int PercentScaleDigits = 4;

    /// <summary>Rounds an amount to its column scale.</summary>
    public static decimal Amount(decimal value) => ToScale(value, AmountScaleDigits);

    /// <summary>Rounds a rate to its column scale.</summary>
    public static decimal Rate(decimal value) => ToScale(value, RateScaleDigits);

    /// <summary>Rounds a percentage to its column scale.</summary>
    public static decimal Percent(decimal value) => ToScale(value, PercentScaleDigits);

    /// <summary>
    /// Rounds <paramref name="value"/> to <paramref name="scaleDigits"/> decimal places, to nearest
    /// with ties toward zero, and returns it with exactly that scale.
    /// </summary>
    /// <remarks>
    /// The framework's toward-zero rounding mode is not this rule: it truncates every value, not
    /// only ties (D-011), so the rounding is done here by hand.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The scale is outside 0..28.</exception>
    /// <exception cref="OverflowException">The value is too large to scale.</exception>
    public static decimal ToScale(decimal value, int scaleDigits)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(scaleDigits);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(scaleDigits, MaxScaleDigits);

        var factor = PowerOfTen(scaleDigits);
        var scaled = value * factor;
        var whole = decimal.Truncate(scaled);
        var remainder = Math.Abs(scaled - whole);
        if (remainder > Half)
        {
            whole += Math.Sign(scaled);
        }

        // Dividing an integer by 10^n can drop trailing zeros (100 / 100 = 1); adding a zero of
        // the target scale restores them, since decimal addition keeps the larger scale.
        return (whole / factor) + new decimal(0, 0, 0, false, (byte)scaleDigits);
    }

    private const int MaxScaleDigits = 28;
    private const decimal Half = 0.5m;
    private const decimal Ten = 10m;

    private static decimal PowerOfTen(int exponent)
    {
        var result = 1m;
        for (var i = 0; i < exponent; i++)
        {
            result *= Ten;
        }

        return result;
    }
}
