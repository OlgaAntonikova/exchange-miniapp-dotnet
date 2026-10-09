using Exchange.Api.Rates;
using Exchange.Api.Tests.Logging;
using Exchange.Domain.Directions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Shouldly;

namespace Exchange.Api.Tests.Rates;

/// <summary>
/// Spec 1.4a and INV-5: each currency is served by its own source, so a stale RUB refuses only what
/// needs RUB; the provider of a quote is every source it used, joined by "+" in alphabetical order,
/// and its time the oldest of their snapshots.
/// </summary>
public sealed class RateSourcesTests
{
    private const int CacheSeconds = 60;
    private const int MaxAgeSeconds = 3600;

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero));
    private readonly CapturingSink _logs = new();

    [Fact]
    public async Task A_direction_needing_both_sources_gets_both_providers_and_the_older_time()
    {
        var cbr = new FakeRateProvider("cbr", _time).Succeeds(new() { [Currency.Rub] = 90m });
        var ecb = new FakeRateProvider("ecb", _time).Succeeds(new() { [Currency.Usd] = 1m, [Currency.Eur] = 0.9m });
        var sources = Sources(ecb, cbr);
        var cbrTakenAt = _time.GetUtcNow();
        await sources.MidsForAsync([Currency.Rub], TestContext.Current.CancellationToken); // the cbr snapshot is now the older
        _time.Advance(TimeSpan.FromSeconds(10));

        var mids = await sources.MidsForAsync([Currency.Rub, Currency.Eur], TestContext.Current.CancellationToken);

        mids.Mids.ShouldBe(new Dictionary<Currency, decimal> { [Currency.Rub] = 90m, [Currency.Eur] = 0.9m }, ignoreOrder: true);
        mids.Provider.ShouldBe("cbr+ecb");
        mids.TakenAt.ShouldBe(cbrTakenAt);
    }

    [Fact]
    public async Task A_direction_needing_one_source_names_that_source_and_fetches_it_once()
    {
        var ecb = new FakeRateProvider("ecb", _time).Succeeds(new() { [Currency.Usd] = 1m, [Currency.Eur] = 0.9m });
        var cbr = new FakeRateProvider("cbr", _time);

        var mids = await Sources(ecb, cbr).MidsForAsync([Currency.Usd, Currency.Eur], TestContext.Current.CancellationToken);

        mids.Provider.ShouldBe("ecb");
        ecb.Calls.ShouldBe(1);
        cbr.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_stale_rouble_refuses_rouble_directions_only()
    {
        var ecb = new FakeRateProvider("ecb", _time).Succeeds(new() { [Currency.Usd] = 1m, [Currency.Eur] = 0.9m });
        var cbr = new FakeRateProvider("cbr", _time).Fails("http_503");
        var sources = Sources(ecb, cbr);

        await Should.ThrowAsync<RatesUnavailableException>(() => sources.MidsForAsync([Currency.Rub, Currency.Eur], TestContext.Current.CancellationToken));
        (await sources.MidsForAsync([Currency.Usd, Currency.Eur], TestContext.Current.CancellationToken)).Provider.ShouldBe("ecb");
    }

    [Fact]
    public async Task A_currency_with_no_source_is_unavailable_and_logged_as_an_error()
    {
        var ecb = new FakeRateProvider("ecb", _time).Succeeds(new() { [Currency.Usd] = 1m, [Currency.Eur] = 0.9m });
        var sources = new RateSources(new Dictionary<Currency, RateService> { [Currency.Usd] = Service(ecb), [Currency.Eur] = Service(ecb) }, Logger<RateSources>());

        var refusal = await Should.ThrowAsync<RatesUnavailableException>(() => sources.MidsForAsync([Currency.Rub, Currency.Eur], TestContext.Current.CancellationToken));

        refusal.Reason.ShouldBe("no_source");
        _logs.Events.ShouldContain(e => e.Level == LogEventLevel.Error);
    }

    [Fact]
    public async Task A_source_that_lacks_a_routed_currency_is_unavailable_and_logged_as_an_error()
    {
        var ecb = new FakeRateProvider("ecb", _time).Succeeds(new() { [Currency.Usd] = 1m }); // no EUR
        var cbr = new FakeRateProvider("cbr", _time);

        var refusal = await Should.ThrowAsync<RatesUnavailableException>(() => Sources(ecb, cbr).MidsForAsync([Currency.Usd, Currency.Eur], TestContext.Current.CancellationToken));

        refusal.Reason.ShouldBe("currency_missing");
        _logs.Events.ShouldContain(e => e.Level == LogEventLevel.Error);
    }

    [Fact]
    public async Task Asking_for_no_currency_is_a_programming_error() =>
        await Should.ThrowAsync<ArgumentException>(() => Sources(new FakeRateProvider("ecb", _time), new FakeRateProvider("cbr", _time)).MidsForAsync([], TestContext.Current.CancellationToken));

    [Fact]
    public void The_routes_of_spec_1_4a_send_usd_and_eur_to_the_ecb_and_rub_to_the_bank_of_russia()
    {
        var ecb = Service(new FakeRateProvider("ecb", _time));
        var cbr = Service(new FakeRateProvider("cbr", _time));

        var routes = RateSources.Routes(ecb, cbr);

        routes.ShouldBe(new Dictionary<Currency, RateService> { [Currency.Usd] = ecb, [Currency.Eur] = ecb, [Currency.Rub] = cbr }, ignoreOrder: true);
    }

    private RateSources Sources(FakeRateProvider ecb, FakeRateProvider cbr) =>
        new(RateSources.Routes(Service(ecb), Service(cbr)), Logger<RateSources>());

    private RateService Service(FakeRateProvider provider) =>
        new(provider, CacheSeconds, MaxAgeSeconds, 900, _time, NullLogger<RateService>.Instance);

    private Microsoft.Extensions.Logging.ILogger<T> Logger<T>()
    {
#pragma warning disable CA2000 // the factory lives as long as the test's sources
        var factory = new SerilogLoggerFactory(new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(_logs).CreateLogger(), dispose: true);
#pragma warning restore CA2000
        return factory.CreateLogger<T>();
    }
}
