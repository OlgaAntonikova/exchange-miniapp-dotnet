using Exchange.Domain.Directions;

namespace Exchange.Api.Rates;

/// <summary>
/// What one fetch from a source gave: mids in units of each currency per one USD (spec 1.3), when
/// they were fetched, and the moment the source's effective-date window closes, after which the
/// snapshot must not be served however recently it was fetched (RULE-RATE-FRESHNESS).
/// </summary>
/// <param name="Provider">The source's name, as an order's <c>provider</c> records it.</param>
/// <param name="TakenAt">When the snapshot was fetched.</param>
/// <param name="Mids">Units of each currency per one USD.</param>
/// <param name="ValidUntil">The first moment the effective date is outside its window.</param>
public sealed record RateSnapshot(string Provider, DateTimeOffset TakenAt, IReadOnlyDictionary<Currency, decimal> Mids, DateTimeOffset ValidUntil);

/// <summary>A fetch failed. <see cref="Reason"/> is a short code, safe to log: never a URL or a body.</summary>
public sealed class RateProviderException : Exception
{
    /// <summary>Creates the exception with its reason code.</summary>
    public RateProviderException(string reason)
        : base("rate fetch failed: " + reason) => Reason = reason;

    /// <summary>Required by the exception pattern.</summary>
    public RateProviderException()
        : this("unknown")
    {
    }

    /// <summary>Required by the exception pattern; the inner exception must carry no URL or body.</summary>
    public RateProviderException(string reason, Exception innerException)
        : base("rate fetch failed: " + reason, innerException) => Reason = reason;

    /// <summary>Why the fetch failed.</summary>
    public string Reason { get; }
}

/// <summary>One source of mid-market rates (spec 1.4, 1.4a).</summary>
public interface IRateProvider
{
    /// <summary>The source's name: <c>ecb</c> or <c>cbr</c>.</summary>
    string Name { get; }

    /// <summary>Fetches and checks a snapshot.</summary>
    /// <exception cref="RateProviderException">The fetch failed or the answer was refused.</exception>
    Task<RateSnapshot> FetchAsync(CancellationToken cancellationToken);
}
