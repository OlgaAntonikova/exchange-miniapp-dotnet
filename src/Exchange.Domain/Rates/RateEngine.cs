using Exchange.Domain.Directions;
using Exchange.Domain.Money;

namespace Exchange.Domain.Rates;

/// <summary>Markup percentages from the reference tables; zero where a component does not apply.</summary>
/// <param name="City">The city's markup (<c>EUR_CASH</c> receive).</param>
/// <param name="BankGive">The sender bank's markup (<c>RUB_WIRE</c> give).</param>
/// <param name="BankReceive">The ATM bank's markup (<c>EUR_ATM</c> receive).</param>
/// <param name="NetworkGive">The USDT network's fee on the give side.</param>
/// <param name="NetworkReceive">The USDT network's fee on the receive side.</param>
public sealed record Markups(decimal City = 0m, decimal BankGive = 0m, decimal BankReceive = 0m, decimal NetworkGive = 0m, decimal NetworkReceive = 0m)
{
    /// <summary>The sum of the components.</summary>
    public decimal Total => City + BankGive + BankReceive + NetworkGive + NetworkReceive;
}

/// <summary>A priced quote (spec 1.3). The computed values - both rates, the total and applied
/// markups and the receive amount - are at their column scales; the inputs are echoed as given.</summary>
/// <param name="Give">What the client gives.</param>
/// <param name="Receive">What the client receives.</param>
/// <param name="AmountGive">The amount given.</param>
/// <param name="BaseRate"><c>mid(to) / mid(from)</c>, at the rate scale.</param>
/// <param name="Markups">The markup components.</param>
/// <param name="TotalMarkupPercent">Their sum.</param>
/// <param name="LoyaltyDiscountPercent">The loyalty discount.</param>
/// <param name="AppliedMarkupPercent"><c>max(total - discount, 0)</c>.</param>
/// <param name="EffectiveRate"><c>base * (1 - applied / 100)</c>, at the rate scale.</param>
/// <param name="AmountReceive"><c>amount * effective</c>, at the amount scale.</param>
public sealed record RateQuote(
    GiveCode Give,
    ReceiveCode Receive,
    decimal AmountGive,
    decimal BaseRate,
    Markups Markups,
    decimal TotalMarkupPercent,
    decimal LoyaltyDiscountPercent,
    decimal AppliedMarkupPercent,
    decimal EffectiveRate,
    decimal AmountReceive);

/// <summary>The inputs cannot produce a quote. <see cref="Reason"/> is a short code.</summary>
public sealed class RateEngineException : Exception
{
    /// <summary>Creates the exception with its reason code.</summary>
    public RateEngineException(string reason)
        : base("no quote: " + reason) => Reason = reason;

    /// <summary>Required by the exception pattern.</summary>
    public RateEngineException()
        : this("unknown")
    {
    }

    /// <summary>Required by the exception pattern.</summary>
    public RateEngineException(string reason, Exception innerException)
        : base("no quote: " + reason, innerException) => Reason = reason;

    /// <summary>Why: <c>same_base</c>, <c>bad_amount</c>, <c>bad_markup</c>, <c>bad_discount</c>,
    /// <c>markup_too_high</c>, <c>no_mid</c>, <c>bad_mid</c>, <c>rate_out_of_range</c> or
    /// <c>rate_rounds_to_zero</c>.</summary>
    public string Reason { get; }
}

/// <summary>
/// The rate engine of spec 1.3: pure decimal arithmetic, no I/O (RULE-MONEY-DECIMAL).
/// <code>
/// base_rate      = mid(to) / mid(from)                       mids: units per one USD
/// applied_markup = max(total_markup - loyalty_discount, 0)   never better than mid-market
/// effective_rate = base_rate * (1 - applied_markup / 100)
/// amount_receive = amount_give * effective_rate
/// </code>
/// Each rate is quantized to its column scale as it is computed, so the rates a quote reports
/// are the ones its amount was computed from (INV-4).
/// </summary>
public static class RateEngine
{
    private const decimal HundredPercent = 100m;

    /// <summary>Prices <paramref name="amountGive"/> from mids in units per one USD.</summary>
    /// <exception cref="RateEngineException">The inputs cannot produce a quote.</exception>
    public static RateQuote Quote(GiveCode give, ReceiveCode receive, decimal amountGive, IReadOnlyDictionary<Currency, decimal> mids, Markups markups, decimal loyaltyDiscountPercent)
    {
        ArgumentNullException.ThrowIfNull(mids);
        ArgumentNullException.ThrowIfNull(markups);
        var giveBase = Directions.Directions.BaseOf(give);
        var receiveBase = Directions.Directions.BaseOf(receive);
        if (giveBase == receiveBase)
        {
            throw new RateEngineException("same_base");
        }

        if (amountGive <= 0m)
        {
            throw new RateEngineException("bad_amount");
        }

        if (markups.City < 0m || markups.BankGive < 0m || markups.BankReceive < 0m || markups.NetworkGive < 0m || markups.NetworkReceive < 0m)
        {
            throw new RateEngineException("bad_markup");
        }

        if (loyaltyDiscountPercent < 0m)
        {
            throw new RateEngineException("bad_discount");
        }

        // Quantized before use, so the applied markup the quote reports is the one its effective
        // rate was computed from (INV-4).
        var total = Quantize.Percent(markups.Total);
        var applied = Quantize.Percent(Math.Max(total - loyaltyDiscountPercent, 0m));
        if (applied >= HundredPercent)
        {
            throw new RateEngineException("markup_too_high");
        }

        decimal baseRate;
        try
        {
            baseRate = Quantize.Rate(Mid(mids, receiveBase) / Mid(mids, giveBase));
        }
        catch (OverflowException overflow)
        {
            // Far beyond any real pair, and beyond NUMERIC(24,12) long before.
            throw new RateEngineException("rate_out_of_range", overflow);
        }

        var effectiveRate = Quantize.Rate(baseRate * (1m - (applied / HundredPercent)));
        if (effectiveRate <= 0m)
        {
            throw new RateEngineException("rate_rounds_to_zero");
        }

        return new RateQuote(
            give,
            receive,
            amountGive,
            baseRate,
            markups,
            total,
            loyaltyDiscountPercent,
            applied,
            effectiveRate,
            Quantize.Amount(amountGive * effectiveRate));
    }

    private static decimal Mid(IReadOnlyDictionary<Currency, decimal> mids, Currency currency)
    {
        if (!mids.TryGetValue(currency, out var mid))
        {
            throw new RateEngineException("no_mid");
        }

        return mid > 0m ? mid : throw new RateEngineException("bad_mid");
    }
}
