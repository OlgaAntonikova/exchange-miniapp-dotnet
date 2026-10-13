using Exchange.Domain.Directions;

namespace Exchange.Api.Rates;

/// <summary>The mids one quote needs, and where they came from.</summary>
/// <param name="Mids">Units of each needed currency per one USD.</param>
/// <param name="Provider">Every source used, joined by <c>+</c> in alphabetical order.</param>
/// <param name="TakenAt">The oldest of their snapshot times.</param>
public sealed record SourcedMids(IReadOnlyDictionary<Currency, decimal> Mids, string Provider, DateTimeOffset TakenAt);

/// <summary>
/// Which source serves which currency (spec 1.4a), and the mids for one quote. A quote asks only
/// for the currencies its direction needs, so a stale RUB refuses RUB directions and nothing else.
/// </summary>
public sealed partial class RateSources(IReadOnlyDictionary<Currency, RateService> routes, ILogger<RateSources> logger)
{
    private const string ProviderSeparator = "+";

    /// <summary>The routes of spec 1.4a: USD and EUR from the ECB, RUB from the Bank of Russia.</summary>
    public static IReadOnlyDictionary<Currency, RateService> Routes(RateService ecb, RateService cbr) => new Dictionary<Currency, RateService>
    {
        [Currency.Usd] = ecb,
        [Currency.Eur] = ecb,
        [Currency.Rub] = cbr,
    };

    /// <summary>The source services, each once, in name order.</summary>
    public IReadOnlyList<RateService> Services => [.. routes.Values.Distinct().OrderBy(service => service.ProviderName, StringComparer.Ordinal)];

    /// <summary>Mids for <paramref name="currencies"/>, each from its own source.</summary>
    /// <exception cref="RatesUnavailableException">A needed currency has no fresh rate.</exception>
    public async Task<SourcedMids> MidsForAsync(IEnumerable<Currency> currencies, CancellationToken cancellationToken)
    {
        var needed = currencies.Distinct().Order().ToList();
        if (needed.Count == 0)
        {
            throw new ArgumentException("at least one currency is needed", nameof(currencies));
        }

        var unrouted = needed.Where(currency => !routes.ContainsKey(currency)).ToList();
        if (unrouted.Count > 0)
        {
            // A direction asked for a currency no source serves: a configuration fault.
            LogRoutingFailed(logger, "no_source", string.Join(", ", unrouted));
            throw new RatesUnavailableException("no_source");
        }

        // Each source once, however many of the needed currencies it serves.
        var snapshots = new Dictionary<RateService, RateSnapshot>();
        foreach (var service in needed.Select(currency => routes[currency]).Distinct())
        {
            snapshots[service] = await service.CurrentAsync(cancellationToken).ConfigureAwait(false);
        }

        var mids = new Dictionary<Currency, decimal>();
        foreach (var currency in needed)
        {
            if (!snapshots[routes[currency]].Mids.TryGetValue(currency, out var mid))
            {
                LogRoutingFailed(logger, "currency_missing", currency.ToString());
                throw new RatesUnavailableException("currency_missing");
            }

            mids[currency] = mid;
        }

        return new SourcedMids(
            mids,
            string.Join(ProviderSeparator, snapshots.Values.Select(snapshot => snapshot.Provider).Order(StringComparer.Ordinal)),
            snapshots.Values.Min(snapshot => snapshot.TakenAt));
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Rate routing failed: {Reason} for {Currency}")]
    private static partial void LogRoutingFailed(ILogger logger, string reason, string currency);
}
