using System.Globalization;
using Exchange.Domain.Directions;
using Exchange.Domain.Money;
using Exchange.Domain.Rates;
using Shouldly;
using DirectionTable = Exchange.Domain.Directions.Directions;

namespace Exchange.Domain.Tests.Rates;

/// <summary>
/// Spec 1.3 (RULE-TEST-FIRST): the rate engine. Every expected value is computed by hand from
/// mids of 1 USD = 0.9 EUR = 90 RUB unless a row says otherwise; rates carry 12 decimals, amounts
/// 2, percentages 4, and the receive amount rounds to nearest with ties toward zero (NR-3).
/// </summary>
public sealed class RateEngineTests
{
    private static readonly decimal[] MatrixAmounts = [0.01m, 1m, 123.45m, 10_000m, 1_000_000m];

    private static readonly Dictionary<Currency, decimal> Mids = new()
    {
        [Currency.Usd] = 1m,
        [Currency.Eur] = 0.9m,
        [Currency.Rub] = 90m,
    };

    [Fact]
    public void Roubles_to_euro_cash_carry_the_city_and_the_sender_bank_markups()
    {
        var quote = Quote(GiveCode.RubWire, ReceiveCode.EurCash, 10_000m, new Markups(City: 0.5m, BankGive: 0.5m));

        Text(quote.BaseRate).ShouldBe("0.010000000000"); // 0.9 / 90
        Text(quote.TotalMarkupPercent).ShouldBe("1.0000");
        Text(quote.AppliedMarkupPercent).ShouldBe("1.0000");
        Text(quote.EffectiveRate).ShouldBe("0.009900000000"); // 0.01 * 0.99
        Text(quote.AmountReceive).ShouldBe("99.00");
    }

    [Fact]
    public void Dollars_to_an_atm_carry_the_atm_bank_markup()
    {
        var quote = Quote(GiveCode.UsdCash, ReceiveCode.EurAtm, 100m, new Markups(BankReceive: 0.5m));

        Text(quote.BaseRate).ShouldBe("0.900000000000");
        Text(quote.EffectiveRate).ShouldBe("0.895500000000"); // 0.9 * 0.995
        Text(quote.AmountReceive).ShouldBe("89.55");
    }

    [Fact]
    public void Euro_to_usdt_rounds_the_base_rate_to_its_scale_before_the_markup()
    {
        var quote = Quote(GiveCode.EurCash, ReceiveCode.Usdt, 100m, new Markups(NetworkReceive: 0.3m));

        Text(quote.BaseRate).ShouldBe("1.111111111111"); // 1 / 0.9
        Text(quote.EffectiveRate).ShouldBe("1.107777777778"); // 1.111111111111 * 0.997 = 1.107777777777667
        Text(quote.AmountReceive).ShouldBe("110.78"); // 110.7777777778
    }

    [Fact]
    public void Usdt_to_a_wire_carries_the_network_fee_on_the_give_side()
    {
        var quote = Quote(GiveCode.Usdt, ReceiveCode.EurWire, 1000m, new Markups(NetworkGive: 1.0m));

        Text(quote.EffectiveRate).ShouldBe("0.891000000000");
        Text(quote.AmountReceive).ShouldBe("891.00");
    }

    [Fact]
    public void Roubles_to_usdt_add_the_bank_and_the_network()
    {
        var quote = Quote(GiveCode.RubWire, ReceiveCode.Usdt, 10_000m, new Markups(BankGive: 0.5m, NetworkReceive: 0.4m));

        Text(quote.BaseRate).ShouldBe("0.011111111111"); // 1 / 90
        Text(quote.EffectiveRate).ShouldBe("0.011011111111"); // 0.011111111111 * 0.991 = 0.011011111111001
        Text(quote.AmountReceive).ShouldBe("110.11");
    }

    [Theory]
    [InlineData("0.9", "90.04")] // 100.05 * 0.9 = 90.045, an exact tie: toward zero
    [InlineData("0.9001", "90.06")] // 90.055005, just above a tie
    [InlineData("0.8999", "90.03")] // 90.034995, just below a tie
    public void The_receive_amount_rounds_ties_toward_zero(string eurPerUsd, string expected)
    {
        var mids = new Dictionary<Currency, decimal>(Mids) { [Currency.Eur] = decimal.Parse(eurPerUsd, CultureInfo.InvariantCulture) };

        var quote = RateEngine.Quote(GiveCode.UsdCash, ReceiveCode.EurWire, 100.05m, mids, new Markups(), 0m);

        Text(quote.AmountReceive).ShouldBe(expected);
    }

    [Theory]
    [InlineData("3", "0.0000", "90.00")] // a discount above the markup never beats mid-market
    [InlineData("1", "0.0000", "90.00")] // exactly the markup
    [InlineData("0.4", "0.6000", "89.46")] // 0.9 * 0.994
    [InlineData("0", "1.0000", "89.10")]
    public void The_loyalty_discount_lowers_the_markup_but_never_below_zero(string discount, string applied, string amount)
    {
        var quote = RateEngine.Quote(GiveCode.UsdCash, ReceiveCode.EurCash, 100m, Mids, new Markups(City: 1.0m), decimal.Parse(discount, CultureInfo.InvariantCulture));

        Text(quote.TotalMarkupPercent).ShouldBe("1.0000");
        Text(quote.AppliedMarkupPercent).ShouldBe(applied);
        Text(quote.AmountReceive).ShouldBe(amount);
    }

    [Fact]
    public void A_quote_echoes_its_inputs()
    {
        var markups = new Markups(City: 0.5m);

        var quote = RateEngine.Quote(GiveCode.UsdCash, ReceiveCode.EurCash, 100m, Mids, markups, 0.25m);

        quote.Give.ShouldBe(GiveCode.UsdCash);
        quote.Receive.ShouldBe(ReceiveCode.EurCash);
        quote.AmountGive.ShouldBe(100m);
        quote.Markups.ShouldBe(markups);
        quote.LoyaltyDiscountPercent.ShouldBe(0.25m);
    }

    // The matrix: every direction of spec 1.1, at several amounts, prices from the quantized rates
    // it reports, so an order's stored rate reproduces its amount (INV-4).
    public static TheoryData<GiveCode, ReceiveCode, decimal> Matrix() =>
        [.. DirectionTable.All.SelectMany(d => MatrixAmounts.Select(amount => (d.Give, d.Receive, amount)))];

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Every_direction_prices_from_the_rates_it_reports(GiveCode give, ReceiveCode receive, decimal amount)
    {
        var markups = new Markups(City: 0.5m, BankGive: 0.5m, BankReceive: 0.5m, NetworkGive: 0.3m, NetworkReceive: 0.4m);

        var quote = RateEngine.Quote(give, receive, amount, Mids, markups, 0m);

        quote.BaseRate.ShouldBe(Quantize.Rate(Mids[DirectionTable.BaseOf(receive)] / Mids[DirectionTable.BaseOf(give)]));
        quote.EffectiveRate.ShouldBe(Quantize.Rate(quote.BaseRate * (1m - (quote.AppliedMarkupPercent / 100m))));
        quote.AmountReceive.ShouldBe(Quantize.Amount(amount * quote.EffectiveRate));
        Text(quote.BaseRate).Split('.')[1].Length.ShouldBe(Quantize.RateScaleDigits);
        Text(quote.EffectiveRate).Split('.')[1].Length.ShouldBe(Quantize.RateScaleDigits);
        Text(quote.AmountReceive).Split('.')[1].Length.ShouldBe(Quantize.AmountScaleDigits);
        Text(quote.AppliedMarkupPercent).Split('.')[1].Length.ShouldBe(Quantize.PercentScaleDigits);
    }

    // --- Refusals ---------------------------------------------------------------------------------

    // Every pair of spec 1.1 that is not a direction: the same base on both sides (USDT is USD, NR-1).
    public static TheoryData<GiveCode, ReceiveCode> SameBasePairs() =>
        [.. Enum.GetValues<GiveCode>().SelectMany(give => Enum.GetValues<ReceiveCode>().Select(receive => (give, receive)))
            .Where(pair => !DirectionTable.All.Contains(new Direction(pair.give, pair.receive)))];

    [Fact]
    public void There_are_six_same_base_pairs() => SameBasePairs().Count.ShouldBe(6);

    [Theory]
    [MemberData(nameof(SameBasePairs))]
    public void A_same_base_direction_is_refused(GiveCode give, ReceiveCode receive) =>
        Reason(() => RateEngine.Quote(give, receive, 100m, Mids, new Markups(), 0m)).ShouldBe("same_base");

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void A_non_positive_amount_is_refused(string amount) =>
        Reason(() => RateEngine.Quote(GiveCode.UsdCash, ReceiveCode.EurCash, decimal.Parse(amount, CultureInfo.InvariantCulture), Mids, new Markups(), 0m)).ShouldBe("bad_amount");

    [Theory]
    [InlineData(nameof(Markups.City))]
    [InlineData(nameof(Markups.BankGive))]
    [InlineData(nameof(Markups.BankReceive))]
    [InlineData(nameof(Markups.NetworkGive))]
    [InlineData(nameof(Markups.NetworkReceive))]
    public void A_negative_markup_component_is_refused(string component)
    {
        var markups = component switch
        {
            nameof(Markups.City) => new Markups(City: -0.1m),
            nameof(Markups.BankGive) => new Markups(BankGive: -0.1m),
            nameof(Markups.BankReceive) => new Markups(BankReceive: -0.1m),
            nameof(Markups.NetworkGive) => new Markups(NetworkGive: -0.1m),
            _ => new Markups(NetworkReceive: -0.1m),
        };

        // Each negative component is offset by another, positive one, so the total stays positive
        // and only the component check refuses.
        var offset = component == nameof(Markups.City) ? markups with { BankGive = 1m } : markups with { City = 1m };
        offset.Total.ShouldBePositive();
        Reason(() => RateEngine.Quote(GiveCode.UsdCash, ReceiveCode.EurCash, 100m, Mids, offset, 0m)).ShouldBe("bad_markup");
    }

    [Fact]
    public void A_negative_discount_is_refused() =>
        Reason(() => RateEngine.Quote(GiveCode.UsdCash, ReceiveCode.EurCash, 100m, Mids, new Markups(), -1m)).ShouldBe("bad_discount");

    [Theory]
    [InlineData("100")]
    [InlineData("150")]
    public void A_markup_that_leaves_nothing_to_pay_out_is_refused(string city) =>
        Reason(() => RateEngine.Quote(GiveCode.UsdCash, ReceiveCode.EurCash, 100m, Mids, new Markups(City: decimal.Parse(city, CultureInfo.InvariantCulture)), 0m)).ShouldBe("markup_too_high");

    [Fact]
    public void A_markup_just_under_a_hundred_percent_still_quotes() =>
        Text(RateEngine.Quote(GiveCode.UsdCash, ReceiveCode.EurCash, 100m, Mids, new Markups(City: 99.9999m), 0m).EffectiveRate).ShouldBe("0.000000900000");

    [Fact]
    public void A_missing_mid_is_refused()
    {
        var mids = new Dictionary<Currency, decimal>(Mids);
        mids.Remove(Currency.Rub);

        Reason(() => RateEngine.Quote(GiveCode.RubWire, ReceiveCode.EurCash, 100m, mids, new Markups(), 0m)).ShouldBe("no_mid");
    }

    [Theory]
    [InlineData(Currency.Rub, "0")]
    [InlineData(Currency.Rub, "-90")]
    [InlineData(Currency.Eur, "0")]
    public void A_non_positive_mid_is_refused(Currency currency, string mid)
    {
        var mids = new Dictionary<Currency, decimal>(Mids) { [currency] = decimal.Parse(mid, CultureInfo.InvariantCulture) };

        Reason(() => RateEngine.Quote(GiveCode.RubWire, ReceiveCode.EurCash, 100m, mids, new Markups(), 0m)).ShouldBe("bad_mid");
    }

    [Fact]
    public void A_rate_that_rounds_to_zero_is_refused()
    {
        var mids = new Dictionary<Currency, decimal>(Mids) { [Currency.Rub] = 1_000_000_000_000_000m }; // 1 / 10^15 rounds to 0 at 12 places

        Reason(() => RateEngine.Quote(GiveCode.RubWire, ReceiveCode.Usdt, 100m, mids, new Markups(), 0m)).ShouldBe("rate_rounds_to_zero");
    }

    [Fact]
    public void A_rate_too_large_for_any_scale_is_refused()
    {
        var mids = new Dictionary<Currency, decimal>(Mids) { [Currency.Usd] = 0.00000000000000000001m }; // 0.9 / 10^-20 overflows at 12 places

        Reason(() => RateEngine.Quote(GiveCode.UsdCash, ReceiveCode.EurCash, 100m, mids, new Markups(), 0m)).ShouldBe("rate_out_of_range");
    }

    [Fact]
    public void Markups_beyond_the_percentage_scale_price_from_the_applied_markup_the_quote_reports()
    {
        // 0.00004 + 0.00004 = 0.00008, reported as 0.0001; the effective rate must use 0.0001:
        // 0.9 * (1 - 0.000001) = 0.8999991, where 0.00008 would give 0.89999928.
        var quote = RateEngine.Quote(GiveCode.UsdCash, ReceiveCode.EurAtm, 100m, Mids, new Markups(City: 0.00004m, BankReceive: 0.00004m), 0m);

        Text(quote.AppliedMarkupPercent).ShouldBe("0.0001");
        Text(quote.EffectiveRate).ShouldBe("0.899999100000");
    }

    [Fact]
    public void An_unrelated_mid_is_not_needed()
    {
        var mids = new Dictionary<Currency, decimal> { [Currency.Usd] = 1m, [Currency.Eur] = 0.9m };

        Text(RateEngine.Quote(GiveCode.UsdCash, ReceiveCode.EurCash, 100m, mids, new Markups(), 0m).AmountReceive).ShouldBe("90.00");
    }

    private static RateQuote Quote(GiveCode give, ReceiveCode receive, decimal amount, Markups markups) =>
        RateEngine.Quote(give, receive, amount, Mids, markups, 0m);

    private static string Reason(Func<RateQuote> quote) => Should.Throw<RateEngineException>(quote).Reason;

    private static string Text(decimal value) => value.ToString(CultureInfo.InvariantCulture);
}
