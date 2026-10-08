using Exchange.Domain.Money;
using Shouldly;

namespace Exchange.Domain.Tests.Money;

/// <summary>
/// INV-14, D-011: rounding is to nearest with ties toward zero. Every expected value is computed by
/// hand; the rows around 1.239 and 1.0051 are the ones <c>MidpointRounding.ToZero</c> gets wrong.
/// </summary>
public sealed class QuantizeTests
{
    [Theory]
    [InlineData("1.005", "1.00")] // exact tie: toward zero
    [InlineData("1.0050000000", "1.00")] // exact tie with trailing zeros
    [InlineData("1.0051", "1.01")] // just above a tie: away from zero
    [InlineData("1.0049", "1.00")] // just below a tie
    [InlineData("1.239", "1.24")] // not a tie: nearest, which truncation would get wrong
    [InlineData("1.235", "1.23")]
    [InlineData("1.2350000000000000000000001", "1.24")] // the smallest step above a tie
    [InlineData("0.005", "0.00")]
    [InlineData("0.0051", "0.01")]
    [InlineData("0.004", "0.00")]
    [InlineData("2.5", "2.50")] // fewer places than the scale: padded
    [InlineData("10", "10.00")]
    [InlineData("0", "0.00")]
    [InlineData("1.23", "1.23")] // already at scale
    [InlineData("9999999999999999.995", "9999999999999999.99")] // largest amount, a tie
    [InlineData("9999999999999999.996", "10000000000000000.00")]
    [InlineData("-1.005", "-1.00")] // ties toward zero on the negative side too
    [InlineData("-1.0051", "-1.01")]
    [InlineData("-1.239", "-1.24")]
    public void Amount_rounds_to_nearest_with_ties_toward_zero(string input, string expected)
    {
        var result = Quantize.Amount(Parse(input));

        result.ShouldBe(Parse(expected));
        result.Scale.ShouldBe((byte)Quantize.AmountScaleDigits);
        Format(result).ShouldBe(expected);
    }

    [Theory]
    [InlineData("0.0123456789125", "0.012345678912")] // tie at the 13th place: toward zero
    [InlineData("0.01234567891251", "0.012345678913")]
    [InlineData("98.7654321098765", "98.765432109876")] // a tie again
    [InlineData("98.76543210987651", "98.765432109877")]
    [InlineData("1", "1.000000000000")]
    public void Rate_rounds_to_twelve_places(string input, string expected)
    {
        var result = Quantize.Rate(Parse(input));

        Format(result).ShouldBe(expected);
        result.Scale.ShouldBe((byte)Quantize.RateScaleDigits);
    }

    [Theory]
    [InlineData("0.5", "0.5000")]
    [InlineData("1.23455", "1.2345")]
    [InlineData("1.234551", "1.2346")]
    public void Percent_rounds_to_four_places(string input, string expected)
    {
        var result = Quantize.Percent(Parse(input));

        Format(result).ShouldBe(expected);
        result.Scale.ShouldBe((byte)Quantize.PercentScaleDigits);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(29)]
    public void ToScale_rejects_a_scale_decimal_cannot_hold(int scaleDigits)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => Quantize.ToScale(1m, scaleDigits));
    }

    [Fact]
    public void ToScale_rejects_a_value_too_large_to_scale()
    {
        Should.Throw<OverflowException>(() => Quantize.ToScale(decimal.MaxValue, Quantize.AmountScaleDigits));
    }

    private static decimal Parse(string text) =>
        decimal.Parse(text, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture);

    private static string Format(decimal value) =>
        value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
