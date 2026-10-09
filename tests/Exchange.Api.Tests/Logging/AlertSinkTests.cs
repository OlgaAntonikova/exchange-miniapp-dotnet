using Exchange.Api.Alerts;
using Microsoft.Extensions.Time.Testing;
using Serilog.Events;
using Serilog.Parsing;
using Shouldly;

namespace Exchange.Api.Tests.Logging;

/// <summary>Spec 4, Alerting: every ERROR reaches the operator queue, once per fingerprint per cooldown.</summary>
public sealed class AlertSinkTests
{
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(300);
    private static readonly MessageTemplateParser Parser = new();

    [Theory]
    [InlineData(LogEventLevel.Error)]
    [InlineData(LogEventLevel.Fatal)]
    public void An_error_or_worse_is_queued(LogEventLevel level)
    {
        var sink = new AlertSink(new FakeTimeProvider(), Cooldown);

        sink.Emit(Event(level, "Rate fetch failed for {Provider}", "ecb"));

        sink.Alerts.TryRead(out var alert).ShouldBeTrue();
        alert!.Text.ShouldBe("Rate fetch failed for \"ecb\"");
        alert.Fingerprint.ShouldBe("Exchange.Api.Rates | Rate fetch failed for {Provider}");
    }

    [Theory]
    [InlineData(LogEventLevel.Warning)]
    [InlineData(LogEventLevel.Information)]
    public void Anything_below_error_is_not(LogEventLevel level)
    {
        var sink = new AlertSink(new FakeTimeProvider(), Cooldown);

        sink.Emit(Event(level, "Retrying {Provider}", "ecb"));

        sink.Alerts.TryRead(out _).ShouldBeFalse();
    }

    [Fact]
    public void The_same_error_alerts_again_only_after_the_cooldown()
    {
        var time = new FakeTimeProvider();
        var sink = new AlertSink(time, Cooldown);

        sink.Emit(Event(LogEventLevel.Error, "Rate fetch failed for {Provider}", "ecb"));
        time.Advance(Cooldown - TimeSpan.FromSeconds(1));
        sink.Emit(Event(LogEventLevel.Error, "Rate fetch failed for {Provider}", "cbr")); // same template: same fingerprint
        time.Advance(TimeSpan.FromSeconds(1));
        sink.Emit(Event(LogEventLevel.Error, "Rate fetch failed for {Provider}", "ecb"));

        Drain(sink).Count.ShouldBe(2);
    }

    [Fact]
    public void Concurrent_identical_errors_raise_one_alert()
    {
        const int Writers = 64;
        var sink = new AlertSink(new FakeTimeProvider(), Cooldown);
        using var start = new Barrier(Writers);

        // Dedicated threads, all released at once: a thread-pool loop could not fill the barrier.
        var threads = Enumerable.Range(0, Writers).Select(_ => new Thread(() =>
        {
            start.SignalAndWait();
            sink.Emit(Event(LogEventLevel.Error, "Database unreachable at {Operation}", "orders"));
        })).ToList();
        threads.ForEach(thread => thread.Start());
        threads.ForEach(thread => thread.Join());

        Drain(sink).Count.ShouldBe(1);
    }

    [Fact]
    public void Different_errors_alert_independently()
    {
        var sink = new AlertSink(new FakeTimeProvider(), Cooldown);

        sink.Emit(Event(LogEventLevel.Error, "Rate fetch failed for {Provider}", "ecb"));
        sink.Emit(Event(LogEventLevel.Error, "Order {OrderId} notification failed", 7));

        Drain(sink).Count.ShouldBe(2);
    }

    [Fact]
    public void A_full_queue_drops_instead_of_blocking()
    {
        var sink = new AlertSink(new FakeTimeProvider(), Cooldown);

        for (var i = 0; i <= AlertSink.QueueCapacity; i++)
        {
            sink.Emit(Event(LogEventLevel.Error, "Failure number " + i, null));
        }

        Drain(sink).Count.ShouldBe(AlertSink.QueueCapacity);
    }

    [Fact]
    public void An_alert_key_splits_one_template_into_separate_fingerprints()
    {
        var sink = new AlertSink(new FakeTimeProvider(), Cooldown);

        sink.Emit(Event(LogEventLevel.Error, "Rate fetch failed for {Provider}", "ecb", (AlertSink.KeyProperty, "ecb")));
        sink.Emit(Event(LogEventLevel.Error, "Rate fetch failed for {Provider}", "cbr", (AlertSink.KeyProperty, "cbr")));
        sink.Emit(Event(LogEventLevel.Error, "Rate fetch failed for {Provider}", "ecb", (AlertSink.KeyProperty, "ecb")));

        var alerts = Drain(sink);
        alerts.Select(alert => alert.Fingerprint).ShouldBe(
        [
            "Exchange.Api.Rates | Rate fetch failed for {Provider} | ecb",
            "Exchange.Api.Rates | Rate fetch failed for {Provider} | cbr",
        ]);
    }

    [Fact]
    public void An_event_may_carry_its_own_cooldown()
    {
        var time = new FakeTimeProvider();
        var sink = new AlertSink(time, Cooldown);
        const int RateCooldownSeconds = 900;

        sink.Emit(Event(LogEventLevel.Error, "Rate fetch failed for {Provider}", "ecb", (AlertSink.CooldownSecondsProperty, RateCooldownSeconds)));
        time.Advance(Cooldown); // the default cooldown has passed, the event's own has not
        sink.Emit(Event(LogEventLevel.Error, "Rate fetch failed for {Provider}", "ecb", (AlertSink.CooldownSecondsProperty, RateCooldownSeconds)));
        time.Advance(TimeSpan.FromSeconds(RateCooldownSeconds) - Cooldown);
        sink.Emit(Event(LogEventLevel.Error, "Rate fetch failed for {Provider}", "ecb", (AlertSink.CooldownSecondsProperty, RateCooldownSeconds)));

        Drain(sink).Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("900")] // a string is not a number of seconds
    [InlineData(-5)]
    [InlineData(0)]
    public void A_cooldown_that_is_not_a_positive_integer_falls_back_to_the_default(object value)
    {
        var time = new FakeTimeProvider();
        var sink = new AlertSink(time, Cooldown);

        sink.Emit(Event(LogEventLevel.Error, "Rate fetch failed for {Provider}", "ecb", (AlertSink.CooldownSecondsProperty, value)));
        time.Advance(Cooldown - TimeSpan.FromSeconds(1));
        sink.Emit(Event(LogEventLevel.Error, "Rate fetch failed for {Provider}", "ecb", (AlertSink.CooldownSecondsProperty, value)));
        time.Advance(TimeSpan.FromSeconds(1));
        sink.Emit(Event(LogEventLevel.Error, "Rate fetch failed for {Provider}", "ecb", (AlertSink.CooldownSecondsProperty, value)));

        Drain(sink).Count.ShouldBe(2);
    }

    private static List<Alert> Drain(AlertSink sink)
    {
        var alerts = new List<Alert>();
        while (sink.Alerts.TryRead(out var alert))
        {
            alerts.Add(alert);
        }

        return alerts;
    }

    private static LogEvent Event(LogEventLevel level, string template, object? value, params (string Name, object Value)[] extra)
    {
        var parsed = Parser.Parse(template);
        var properties = new List<LogEventProperty> { new("SourceContext", new ScalarValue("Exchange.Api.Rates")) };
        var name = parsed.Tokens.OfType<PropertyToken>().FirstOrDefault()?.PropertyName;
        if (name is not null)
        {
            properties.Add(new LogEventProperty(name, new ScalarValue(value)));
        }

        properties.AddRange(extra.Select(property => new LogEventProperty(property.Name, new ScalarValue(property.Value))));
        return new LogEvent(DateTimeOffset.UtcNow, level, null, parsed, properties);
    }
}
