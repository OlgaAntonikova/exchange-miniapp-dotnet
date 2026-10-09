using Exchange.Api.Alerts;

namespace Exchange.Api.Rates;

/// <summary>No snapshot fresh enough to quote from (RULE-RATE-FRESHNESS). <see cref="Reason"/> is a short code.</summary>
public sealed class RatesUnavailableException : Exception
{
    /// <summary>Creates the exception with its reason code.</summary>
    public RatesUnavailableException(string reason)
        : base("rates unavailable: " + reason) => Reason = reason;

    /// <summary>Required by the exception pattern.</summary>
    public RatesUnavailableException()
        : this("unknown")
    {
    }

    /// <summary>Required by the exception pattern.</summary>
    public RatesUnavailableException(string reason, Exception innerException)
        : base("rates unavailable: " + reason, innerException) => Reason = reason;

    /// <summary>Why: <c>no_snapshot</c>, <c>too_old</c>, <c>past_validity</c>, <c>no_source</c> or <c>currency_missing</c>.</summary>
    public string Reason { get; }
}

/// <summary>
/// One source's cache and freshness guard (spec 1.4, RULE-RATE-FRESHNESS, INV-5). A new snapshot
/// is fetched at most once per cache period, whether the last fetch worked or not, so a dead
/// source is not hit by every request. A failed fetch is logged at ERROR - which alerts the
/// operator, at most once per <c>RATE_ALERT_COOLDOWN_SECONDS</c> per source - and the last good
/// snapshot is served while it is younger than the maximum age and inside its effective-date
/// window; after that the rate is unavailable, never a number.
/// </summary>
public sealed partial class RateService(IRateProvider provider, int cacheSeconds, int maxAgeSeconds, int alertCooldownSeconds, TimeProvider time, ILogger<RateService> logger) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RateSnapshot? _lastGood;
    private DateTimeOffset? _lastAttempt;

    /// <summary>The source's name.</summary>
    public string ProviderName => provider.Name;

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    /// <summary>The last good snapshot if it is still fresh, fetching a new one once per cache period.</summary>
    /// <exception cref="RatesUnavailableException">No fresh snapshot.</exception>
    public async Task<RateSnapshot> CurrentAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = time.GetUtcNow();
            if (_lastAttempt is not { } last || now - last >= TimeSpan.FromSeconds(cacheSeconds))
            {
                await FetchAsync(now, cancellationToken).ConfigureAwait(false);
            }

            return FreshOrRefuse(time.GetUtcNow());
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task FetchAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        try
        {
            _lastGood = await provider.FetchAsync(cancellationToken).ConfigureAwait(false);
            _lastAttempt = now;
            var durationMs = ElapsedMs(started);
            LogFetched(logger, provider.Name, durationMs);
        }
        catch (RateProviderException failure)
        {
            _lastAttempt = now;
            var durationMs = ElapsedMs(started);
            // The provider is the alert key: an ECB outage and a Bank of Russia outage are two
            // alerts, each on the rate alert cooldown. The stack is safe: a provider failure
            // carries a reason code and never chains the transport's exception, which quotes the URL.
            using (logger.BeginScope(new Dictionary<string, object>
            {
                [AlertSink.KeyProperty] = provider.Name,
                [AlertSink.CooldownSecondsProperty] = alertCooldownSeconds,
            }))
            {
                LogFetchFailed(logger, failure, provider.Name, failure.Reason, durationMs);
            }
        }

        // A cancelled caller leaves no attempt behind, so the next request fetches at once.
    }

    private RateSnapshot FreshOrRefuse(DateTimeOffset now)
    {
        if (_lastGood is not { } snapshot)
        {
            LogRefused(logger, provider.Name, "no_snapshot");
            throw new RatesUnavailableException("no_snapshot");
        }

        if (now >= snapshot.ValidUntil)
        {
            LogRefused(logger, provider.Name, "past_validity");
            throw new RatesUnavailableException("past_validity");
        }

        if (now - snapshot.TakenAt > TimeSpan.FromSeconds(maxAgeSeconds))
        {
            LogRefused(logger, provider.Name, "too_old");
            throw new RatesUnavailableException("too_old");
        }

        return snapshot;
    }

    private long ElapsedMs(long started) => (long)time.GetElapsedTime(started).TotalMilliseconds;

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Rate fetch {Provider} ok in {DurationMs} ms")]
    private static partial void LogFetched(ILogger logger, string provider, long durationMs);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Rate fetch {Provider} failed: {Reason} after {DurationMs} ms")]
    private static partial void LogFetchFailed(ILogger logger, Exception failure, string provider, string reason, long durationMs);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Quote refused: {Provider} rate unavailable ({Reason})")]
    private static partial void LogRefused(ILogger logger, string provider, string reason);
}
