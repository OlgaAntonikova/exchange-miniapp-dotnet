using Exchange.Api.Alerts;
using Exchange.Api.Rates;
using Exchange.Api.Tests.Logging;
using Exchange.Domain.Directions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Shouldly;

namespace Exchange.Api.Tests.Rates;

/// <summary>
/// RULE-RATE-FRESHNESS and INV-5, per source: the cache, the maximum age since the last good
/// fetch, the effective-date window carried by the snapshot, and what is logged. A stale or
/// missing rate is refused, never served.
/// </summary>
public sealed class RateFreshnessTests
{
    private const int CacheSeconds = 60;
    private const int MaxAgeSeconds = 3600;
    private const int AlertCooldownSeconds = 900;

    private static readonly Dictionary<Currency, decimal> EcbMids = new() { [Currency.Usd] = 1m, [Currency.Eur] = 0.9m };

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero));
    private readonly CapturingSink _logs = new();

    [Fact]
    public async Task A_snapshot_is_fetched_once_per_cache_period()
    {
        var provider = new FakeRateProvider("ecb", _time).Succeeds(EcbMids).Succeeds(EcbMids);
        var service = Service(provider);

        await CurrentAsync(service);
        _time.Advance(TimeSpan.FromSeconds(CacheSeconds - 1));
        await CurrentAsync(service);
        provider.Calls.ShouldBe(1);

        _time.Advance(TimeSpan.FromSeconds(1));
        await CurrentAsync(service);
        provider.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task A_failed_fetch_serves_the_last_good_snapshot_and_logs_an_error()
    {
        var provider = new FakeRateProvider("ecb", _time).Succeeds(EcbMids).Fails("http_503");
        var service = Service(provider);
        var first = await CurrentAsync(service);

        _time.Advance(TimeSpan.FromSeconds(CacheSeconds));
        var second = await CurrentAsync(service);

        second.ShouldBeSameAs(first);
        var error = _logs.Events.Where(e => e.Level == LogEventLevel.Error).ShouldHaveSingleItem();
        Scalar(error, "Provider").ShouldBe("ecb");
        Scalar(error, "Reason").ShouldBe("http_503");
        error.Properties.ShouldContainKey("DurationMs");
    }

    [Fact]
    public async Task A_failure_alerts_per_provider_with_the_rate_alert_cooldown()
    {
        var provider = new FakeRateProvider("cbr", _time).Fails("timeout");

        await Should.ThrowAsync<RatesUnavailableException>(() => CurrentAsync(Service(provider)));

        var error = _logs.Events.Where(e => e.Level == LogEventLevel.Error).ShouldHaveSingleItem();
        Scalar(error, AlertSink.KeyProperty).ShouldBe("cbr");
        Scalar(error, AlertSink.CooldownSecondsProperty).ShouldBe(AlertCooldownSeconds);
    }

    [Fact]
    public async Task A_failed_fetch_is_not_retried_before_the_cache_period_passes()
    {
        var provider = new FakeRateProvider("ecb", _time).Succeeds(EcbMids).Fails("http_503").Succeeds(EcbMids);
        var service = Service(provider);
        await CurrentAsync(service);
        _time.Advance(TimeSpan.FromSeconds(CacheSeconds));
        await CurrentAsync(service); // fails, serves the last good

        _time.Advance(TimeSpan.FromSeconds(CacheSeconds - 1));
        await CurrentAsync(service);

        provider.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task A_recovered_source_starts_a_new_cache_period()
    {
        var provider = new FakeRateProvider("ecb", _time).Fails("http_503").Succeeds(EcbMids).Succeeds(EcbMids);
        var service = Service(provider);
        await Should.ThrowAsync<RatesUnavailableException>(() => CurrentAsync(service));
        _time.Advance(TimeSpan.FromSeconds(CacheSeconds));
        await CurrentAsync(service); // recovers

        _time.Advance(TimeSpan.FromSeconds(CacheSeconds - 1));
        await CurrentAsync(service);

        provider.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task A_successful_fetch_is_logged_at_information()
    {
        await CurrentAsync(Service(new FakeRateProvider("ecb", _time).Succeeds(EcbMids)));

        var fetch = _logs.Events.Where(e => e.Level == LogEventLevel.Information).ShouldHaveSingleItem();
        Scalar(fetch, "Provider").ShouldBe("ecb");
        fetch.Properties.ShouldContainKey("DurationMs");
        _logs.Events.ShouldNotContain(e => e.Level >= LogEventLevel.Warning);
    }

    [Theory]
    [InlineData(MaxAgeSeconds, true)]
    [InlineData(MaxAgeSeconds + 1, false)]
    public async Task The_last_good_snapshot_is_served_until_its_maximum_age(int ageSeconds, bool served)
    {
        var provider = new FakeRateProvider("ecb", _time).Succeeds(EcbMids);
        for (var i = 0; i < 100; i++)
        {
            provider.Fails("http_503");
        }

        var service = Service(provider);
        await CurrentAsync(service);

        _time.Advance(TimeSpan.FromSeconds(ageSeconds));

        if (served)
        {
            (await CurrentAsync(service)).Provider.ShouldBe("ecb");
        }
        else
        {
            (await Should.ThrowAsync<RatesUnavailableException>(() => CurrentAsync(service))).Reason.ShouldBe("too_old");
            Scalar(_logs.Events.Last(e => e.Level == LogEventLevel.Warning), "Provider").ShouldBe("ecb");
        }
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    public async Task A_snapshot_is_refused_from_the_moment_its_effective_date_window_closes(int secondsFromClose, bool served)
    {
        var validFor = TimeSpan.FromSeconds(CacheSeconds / 2);
        var service = Service(new FakeRateProvider("ecb", _time).Succeeds(EcbMids, validFor));
        await CurrentAsync(service);

        _time.Advance(validFor + TimeSpan.FromSeconds(secondsFromClose)); // within the cache period: no new fetch

        if (served)
        {
            (await CurrentAsync(service)).Provider.ShouldBe("ecb");
        }
        else
        {
            (await Should.ThrowAsync<RatesUnavailableException>(() => CurrentAsync(service))).Reason.ShouldBe("past_validity");
        }
    }

    [Fact]
    public async Task With_no_snapshot_ever_fetched_the_rate_is_unavailable()
    {
        var service = Service(new FakeRateProvider("ecb", _time).Fails("malformed_xml"));

        var refusal = await Should.ThrowAsync<RatesUnavailableException>(() => CurrentAsync(service));

        refusal.Reason.ShouldBe("no_snapshot");
        Scalar(_logs.Events.Single(e => e.Level == LogEventLevel.Warning), "Provider").ShouldBe("ecb");
    }

    [Fact]
    public async Task A_cancelled_caller_does_not_use_up_the_cache_period()
    {
        // The caller gives up while the fetch is in flight: no attempt is recorded, so the next
        // request fetches at once instead of finding no snapshot for a whole cache period.
        var provider = new FakeRateProvider("ecb", _time)
            .Waits(Task.FromCanceled<RateSnapshot>(new CancellationToken(canceled: true)))
            .Succeeds(EcbMids);
        var service = Service(provider);

        await Should.ThrowAsync<OperationCanceledException>(() => CurrentAsync(service));
        (await CurrentAsync(service)).Provider.ShouldBe("ecb");

        provider.Calls.ShouldBe(2);
        _logs.Events.ShouldNotContain(e => e.Level >= LogEventLevel.Error);
    }

    [Fact]
    public async Task Concurrent_callers_share_one_fetch()
    {
        var answer = new TaskCompletionSource<RateSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeRateProvider("ecb", _time).Waits(answer.Task);
        var service = Service(provider);

        var callers = Enumerable.Range(0, 8).Select(_ => CurrentAsync(service)).ToList();
        answer.SetResult(new RateSnapshot("ecb", _time.GetUtcNow(), EcbMids, _time.GetUtcNow().AddDays(1)));
        await Task.WhenAll(callers);

        provider.Calls.ShouldBe(1);
    }

    private RateService Service(FakeRateProvider provider)
    {
#pragma warning disable CA2000 // the factory lives as long as the test's service
        var factory = new SerilogLoggerFactory(new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(_logs).CreateLogger(), dispose: true);
#pragma warning restore CA2000
        return new RateService(provider, CacheSeconds, MaxAgeSeconds, AlertCooldownSeconds, _time, factory.CreateLogger<RateService>());
    }

    private static Task<RateSnapshot> CurrentAsync(RateService service) => service.CurrentAsync(TestContext.Current.CancellationToken);

    private static object? Scalar(LogEvent logEvent, string name) => ((ScalarValue)logEvent.Properties[name]).Value;
}
