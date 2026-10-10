using Exchange.Api.Rates;
using Exchange.Domain.Directions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Exchange.Api.Tests.Rates;

/// <summary>Rate sources for an app under test: 1 USD = 0.9 EUR = 90 RUB unless a source is told to fail.</summary>
internal static class FakeSources
{
    public const int CacheSeconds = 60;
    public const int MaxAgeSeconds = 3600;
    public const int AlertCooldownSeconds = 900;

    // More scripted answers than any test makes fetches.
    private const int ScriptedAnswers = 1000;

    public static readonly Dictionary<Currency, decimal> EcbMids = new() { [Currency.Usd] = 1m, [Currency.Eur] = 0.9m };
    public static readonly Dictionary<Currency, decimal> CbrMids = new() { [Currency.Rub] = 90m };

    /// <summary>Replaces the app's rate sources; a failing source never had a good snapshot.</summary>
    public static Action<IServiceCollection> Replace(bool ecbWorks = true, bool cbrWorks = true) => services =>
    {
        var time = TimeProvider.System;
        var ecb = new FakeRateProvider("ecb", time);
        var cbr = new FakeRateProvider("cbr", time);
        for (var i = 0; i < ScriptedAnswers; i++)
        {
            _ = ecbWorks ? ecb.Succeeds(EcbMids) : ecb.Fails("http_503");
            _ = cbrWorks ? cbr.Succeeds(CbrMids) : cbr.Fails("http_503");
        }

        services.AddSingleton(new RateSources(RateSources.Routes(Service(ecb, time), Service(cbr, time)), NullLogger<RateSources>.Instance));
    };

#pragma warning disable CA2000 // owned by the sources, which live as long as the app under test
    private static RateService Service(FakeRateProvider provider, TimeProvider time) =>
        new(provider, CacheSeconds, MaxAgeSeconds, AlertCooldownSeconds, time, NullLogger<RateService>.Instance);
#pragma warning restore CA2000
}
