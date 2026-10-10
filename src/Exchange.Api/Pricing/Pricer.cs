using Exchange.Api.Data;
using Exchange.Api.Rates;
using Exchange.Domain.Directions;
using Exchange.Domain.Money;
using Exchange.Domain.Orders;
using Exchange.Domain.Rates;
using Microsoft.EntityFrameworkCore;

namespace Exchange.Api.Pricing;

/// <summary>A priced request: the engine's quote and the mids it came from.</summary>
/// <param name="Quote">The quote.</param>
/// <param name="Sourced">The mids, their provider and their time.</param>
public sealed record Priced(RateQuote Quote, SourcedMids Sourced);

/// <summary>
/// The one pricing path a quote and an order both take, so an order is priced exactly as its quote
/// was (RULE-SERVER-AUTHORITY, INV-4): the direction and its fields (spec 1.1), each named
/// reference row (it exists, is active and fits its side), fresh mids from the sources the direction
/// needs (RULE-RATE-FRESHNESS), then the engine (spec 1.3).
/// </summary>
public sealed class Pricer(ExchangeDbContext db, RateSources sources)
{
    /// <summary>The kind of a direction whose sides share a base currency.</summary>
    public const string InvalidDirection = "invalid_direction";

    /// <summary>The kind of a receive amount that rounds to zero.</summary>
    public const string AmountTooSmall = "amount_too_small";

    /// <summary>The kind of a direction with no fresh rate.</summary>
    public const string RatesUnavailable = "rates_unavailable";

    // Spec 1.1: what each side's reference row must be.
    private const string RubCurrency = "RUB";
    private const string EurCurrency = "EUR";
    private const string RussiaCountry = "RU";
    private const string MontenegroCountry = "ME";

    // No loyalty discount until PHASE-6 (spec 1.6).
    private static readonly decimal NoLoyaltyDiscountPercent = Quantize.Percent(0m);
    private static readonly decimal NoMarkupPercent = Quantize.Percent(0m);

    /// <summary>Checks the direction and its fields, loads the markups, gets fresh mids and prices.</summary>
    /// <param name="input">The parsed request.</param>
    /// <param name="provided">The names of the extra fields present in the request.</param>
    /// <param name="purpose">A quote or an order.</param>
    /// <param name="cancellationToken">The request's cancellation.</param>
    /// <exception cref="PricingException">The request cannot be priced.</exception>
    public async Task<Priced> PriceAsync(PricingInput input, IEnumerable<string> provided, Purpose purpose, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (Directions.Check(input.Give, input.Receive, provided, purpose) is { } problem)
        {
            throw problem.SameBase
                ? new PricingException(InvalidDirection, [PricingRequest.FieldPrefix + "give", PricingRequest.FieldPrefix + "receive"])
                : new PricingException(PricingRequest.InvalidRequest, [.. problem.Missing.Concat(problem.Unexpected).Select(field => PricingRequest.FieldPrefix + field)]);
        }

        var markups = await MarkupsAsync(input.ReferenceIds, cancellationToken).ConfigureAwait(false);
        SourcedMids sourced;
        try
        {
            sourced = await sources.MidsForAsync([Directions.BaseOf(input.Give), Directions.BaseOf(input.Receive)], cancellationToken).ConfigureAwait(false);
        }
        catch (RatesUnavailableException)
        {
            throw new PricingException(RatesUnavailable, []);
        }

        // The direction and the amount are valid here, so an engine refusal is the server's fault
        // (markups adding up to 100 %, a broken mid): it reaches the top-level handler as a 500.
        var quote = RateEngine.Quote(input.Give, input.Receive, input.AmountGive, sourced.Mids, markups, NoLoyaltyDiscountPercent);
        return quote.AmountReceive > 0m
            ? new Priced(quote, sourced)
            : throw new PricingException(AmountTooSmall, [PricingRequest.FieldPrefix + "amount_give"]);
    }

    private async Task<Markups> MarkupsAsync(IReadOnlyDictionary<string, int> ids, CancellationToken cancellationToken)
    {
        var bad = new List<string>();

        async Task<decimal> Markup(string field, Func<int, Task<decimal?>> lookup)
        {
            if (!ids.TryGetValue(field, out var id))
            {
                return NoMarkupPercent;
            }

            if (await lookup(id).ConfigureAwait(false) is { } markup)
            {
                return markup;
            }

            bad.Add(PricingRequest.FieldPrefix + field);
            return NoMarkupPercent;
        }

        var city = await Markup(DetailFields.CityId, async id => await db.Cities.AsNoTracking()
            .Where(c => c.Id == id && c.IsActive && c.Country == MontenegroCountry)
            .Select(c => (decimal?)c.MarkupPercent).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        var bankGive = await Markup(DetailFields.BankGiveId, id => BankMarkupAsync(id, BankChannel.Wire, RubCurrency, RussiaCountry, cancellationToken)).ConfigureAwait(false);
        var bankReceive = await Markup(DetailFields.BankReceiveId, id => BankMarkupAsync(id, BankChannel.Atm, EurCurrency, MontenegroCountry, cancellationToken)).ConfigureAwait(false);
        var networkGive = await Markup(DetailFields.NetworkGiveId, id => NetworkFeeAsync(id, cancellationToken)).ConfigureAwait(false);
        var networkReceive = await Markup(DetailFields.NetworkReceiveId, id => NetworkFeeAsync(id, cancellationToken)).ConfigureAwait(false);

        return bad.Count == 0
            ? new Markups(city, bankGive, bankReceive, networkGive, networkReceive)
            : throw new PricingException(PricingRequest.InvalidRequest, bad);
    }

    private Task<decimal?> BankMarkupAsync(int id, BankChannel channel, string currency, string country, CancellationToken cancellationToken) =>
        db.Banks.AsNoTracking()
            .Where(b => b.Id == id && b.IsActive && b.Channel == channel && b.Currency == currency && b.Country == country)
            .Select(b => (decimal?)b.MarkupPercent).SingleOrDefaultAsync(cancellationToken);

    private Task<decimal?> NetworkFeeAsync(int id, CancellationToken cancellationToken) =>
        db.UsdtNetworks.AsNoTracking()
            .Where(n => n.Id == id && n.IsActive)
            .Select(n => (decimal?)n.FeePercent).SingleOrDefaultAsync(cancellationToken);
}
