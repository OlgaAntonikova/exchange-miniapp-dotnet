using Exchange.Api.Settings;

namespace Exchange.Api.Rates;

/// <summary>Wires the two rate sources, their caches and the currency routes (spec 1.4, 1.4a).</summary>
public static class RateSetup
{
    /// <summary>The largest rate document read; both sources answer a few kilobytes.</summary>
    public const int MaxResponseBytes = 1024 * 1024;

    /// <summary>
    /// How long a pooled connection of a long-lived client is reused before a new one is opened,
    /// so a changed DNS answer is picked up (the handler's default is forever).
    /// </summary>
    public static readonly TimeSpan PooledConnectionLifetime = TimeSpan.FromMinutes(15);

    /// <summary>The handler every long-lived outgoing client uses: pooled connections renewed after <see cref="PooledConnectionLifetime"/>.</summary>
    public static SocketsHttpHandler CreateHandler() => new() { PooledConnectionLifetime = PooledConnectionLifetime };

    /// <summary>
    /// Registers <see cref="RateSources"/> with the ECB and Bank of Russia services. Each source's
    /// client and service are keyed singletons, so the container disposes them with the app.
    /// </summary>
    public static IServiceCollection AddExchangeRates(this IServiceCollection services)
    {
        services.AddKeyedSingleton(EcbProvider.ProviderName, (provider, _) => CreateClient(provider.GetRequiredService<AppSettings>().RateFetchTimeoutSeconds));
        services.AddKeyedSingleton(CbrProvider.ProviderName, (provider, _) => CreateClient(provider.GetRequiredService<AppSettings>().RateFetchTimeoutSeconds));
        services.AddKeyedSingleton(EcbProvider.ProviderName, (provider, _) =>
        {
            var settings = provider.GetRequiredService<AppSettings>();
            var time = provider.GetRequiredService<TimeProvider>();
            return new RateService(
                new EcbProvider(provider.GetRequiredKeyedService<HttpClient>(EcbProvider.ProviderName), settings.EcbRatesUrl, settings.EcbRateMaxEffectiveAgeDays, time),
                settings.EcbRateCacheSeconds,
                settings.EcbRateMaxAgeSeconds,
                settings.RateAlertCooldownSeconds,
                time,
                provider.GetRequiredService<ILogger<RateService>>());
        });
        services.AddKeyedSingleton(CbrProvider.ProviderName, (provider, _) =>
        {
            var settings = provider.GetRequiredService<AppSettings>();
            var time = provider.GetRequiredService<TimeProvider>();
            return new RateService(
                new CbrProvider(provider.GetRequiredKeyedService<HttpClient>(CbrProvider.ProviderName), settings.CbrRatesUrl, settings.CbrRateMaxEffectiveLeadDays, settings.CbrRateMaxEffectiveAgeDays, time),
                settings.CbrRateCacheSeconds,
                settings.CbrRateMaxAgeSeconds,
                settings.RateAlertCooldownSeconds,
                time,
                provider.GetRequiredService<ILogger<RateService>>());
        });
        services.AddSingleton(provider => new RateSources(
            RateSources.Routes(provider.GetRequiredKeyedService<RateService>(EcbProvider.ProviderName), provider.GetRequiredKeyedService<RateService>(CbrProvider.ProviderName)),
            provider.GetRequiredService<ILogger<RateSources>>()));
        return services;
    }

    /// <summary>
    /// A client for one source: <c>RATE_FETCH_TIMEOUT_SECONDS</c> per request, and a body of at most
    /// <see cref="MaxResponseBytes"/>; a longer one fails the fetch as a transport error.
    /// </summary>
    public static HttpClient CreateClient(int timeoutSeconds) => new(CreateHandler())
    {
        Timeout = TimeSpan.FromSeconds(timeoutSeconds),
        MaxResponseContentBufferSize = MaxResponseBytes,
    };
}
