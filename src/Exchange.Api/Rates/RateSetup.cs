using Exchange.Api.Settings;

namespace Exchange.Api.Rates;

/// <summary>Wires the two rate sources, their caches and the currency routes (spec 1.4, 1.4a).</summary>
public static class RateSetup
{
    /// <summary>The largest rate document read; both sources answer a few kilobytes.</summary>
    public const int MaxResponseBytes = 1024 * 1024;

    /// <summary>Registers <see cref="RateSources"/> with the ECB and Bank of Russia services.</summary>
    public static IServiceCollection AddExchangeRates(this IServiceCollection services)
    {
        services.AddSingleton(provider =>
        {
            var settings = provider.GetRequiredService<AppSettings>();
            var time = provider.GetRequiredService<TimeProvider>();
            var logger = provider.GetRequiredService<ILogger<RateService>>();
            var ecb = new RateService(
                new EcbProvider(CreateClient(settings.RateFetchTimeoutSeconds), settings.EcbRatesUrl, settings.EcbRateMaxEffectiveAgeDays, time),
                settings.EcbRateCacheSeconds,
                settings.EcbRateMaxAgeSeconds,
                settings.RateAlertCooldownSeconds,
                time,
                logger);
            var cbr = new RateService(
                new CbrProvider(CreateClient(settings.RateFetchTimeoutSeconds), settings.CbrRatesUrl, settings.CbrRateMaxEffectiveLeadDays, settings.CbrRateMaxEffectiveAgeDays, time),
                settings.CbrRateCacheSeconds,
                settings.CbrRateMaxAgeSeconds,
                settings.RateAlertCooldownSeconds,
                time,
                logger);
            return new RateSources(RateSources.Routes(ecb, cbr), provider.GetRequiredService<ILogger<RateSources>>());
        });
        return services;
    }

    /// <summary>
    /// A client for one source: <c>RATE_FETCH_TIMEOUT_SECONDS</c> per request, and a body of at most
    /// <see cref="MaxResponseBytes"/>; a longer one fails the fetch as a transport error.
    /// </summary>
    public static HttpClient CreateClient(int timeoutSeconds) => new()
    {
        Timeout = TimeSpan.FromSeconds(timeoutSeconds),
        MaxResponseContentBufferSize = MaxResponseBytes,
    };
}
