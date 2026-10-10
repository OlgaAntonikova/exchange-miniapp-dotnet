using Exchange.Api.Auth;
using Exchange.Api.Data;
using Exchange.Api.Locale;
using Exchange.Api.Pricing;
using Exchange.Domain.Directions;
using Exchange.Domain.Money;

namespace Exchange.Api.Rates;

/// <summary>
/// <c>POST /api/rate</c> (spec 1.10): a live quote with its markup breakdown, every value computed
/// by the server (RULE-SERVER-AUTHORITY). A request breaking the rules is the 422 of INV-21 naming
/// its fields; a direction with no fresh rate is the 503 with the localized message, never a
/// number (RULE-RATE-FRESHNESS).
/// </summary>
public static class RateEndpoints
{
    /// <summary>A quote's markups in percent: the stored components and the derived totals.</summary>
    /// <param name="City">The city's markup.</param>
    /// <param name="BankGive">The sender bank's markup.</param>
    /// <param name="BankReceive">The ATM bank's markup.</param>
    /// <param name="NetworkGive">The give-side network fee.</param>
    /// <param name="NetworkReceive">The receive-side network fee.</param>
    /// <param name="Total">Their sum.</param>
    /// <param name="LoyaltyDiscount">The loyalty discount.</param>
    /// <param name="Applied">The markup applied.</param>
    public sealed record MarkupOut(decimal City, decimal BankGive, decimal BankReceive, decimal NetworkGive, decimal NetworkReceive, decimal Total, decimal LoyaltyDiscount, decimal Applied);

    /// <summary>A quote, as <c>POST /api/rate</c> returns it.</summary>
    /// <param name="Give">The give code.</param>
    /// <param name="Receive">The receive code.</param>
    /// <param name="AmountGive">The amount given.</param>
    /// <param name="AmountReceive">The amount received.</param>
    /// <param name="BaseRate">The mid-market rate.</param>
    /// <param name="EffectiveRate">The rate after markups.</param>
    /// <param name="MarkupPercent">The markup breakdown.</param>
    /// <param name="Provider">The rate sources used.</param>
    /// <param name="RateTakenAt">The oldest of their snapshot times.</param>
    public sealed record QuoteOut(string Give, string Receive, decimal AmountGive, decimal AmountReceive, decimal BaseRate, decimal EffectiveRate, MarkupOut MarkupPercent, string Provider, DateTimeOffset RateTakenAt);

    /// <summary>Maps <c>POST /api/rate</c>.</summary>
    public static IEndpointRouteBuilder MapRateEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/rate", async (HttpContext http, Pricer pricer, LocaleTexts texts, ExchangeDbContext db) =>
        {
            // A valid signature on a user who does not exist is no identity, as for every other
            // route that needs a token (RULE-SERVER-AUTHORITY).
            var (user, refusal) = await AuthEndpoints.CallerAsync(http, db).ConfigureAwait(false);
            if (user is null)
            {
                return refusal!;
            }

            try
            {
                using var body = await PricingRefusal.ReadBodyAsync(http.Request, http.RequestAborted).ConfigureAwait(false);
                var input = PricingRequest.Parse(body.RootElement);
                var priced = await pricer.PriceAsync(input, input.ReferenceIds.Keys, Purpose.Quote, http.RequestAborted).ConfigureAwait(false);
                return Results.Ok(Out(priced));
            }
            catch (PricingException refused)
            {
                return PricingRefusal.Result(http, texts, refused, "Quote");
            }
        }).RequireAuthorization();
        return app;
    }

    private static QuoteOut Out(Priced priced)
    {
        var quote = priced.Quote;
        var markups = quote.Markups;
        return new QuoteOut(
            Codes.Of(quote.Give),
            Codes.Of(quote.Receive),
            Quantize.Amount(quote.AmountGive),
            quote.AmountReceive,
            quote.BaseRate,
            quote.EffectiveRate,
            // The components come from NUMERIC(7,4) columns or are a zero at that scale, and the
            // totals are quantized by the engine, so every percentage already has its scale.
            new MarkupOut(
                markups.City,
                markups.BankGive,
                markups.BankReceive,
                markups.NetworkGive,
                markups.NetworkReceive,
                quote.TotalMarkupPercent,
                quote.LoyaltyDiscountPercent,
                quote.AppliedMarkupPercent),
            priced.Sourced.Provider,
            priced.Sourced.TakenAt);
    }

}
