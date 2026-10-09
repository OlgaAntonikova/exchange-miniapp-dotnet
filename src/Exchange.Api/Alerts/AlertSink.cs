using System.Threading.Channels;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;

namespace Exchange.Api.Alerts;

/// <summary>An alert for the operator chat: one <c>ERROR</c> record, already redacted.</summary>
/// <param name="Fingerprint">Source context and message template: what makes two errors "the same".</param>
/// <param name="Text">The rendered, redacted message with the exception's type.</param>
/// <param name="Timestamp">When the error was logged.</param>
public sealed record Alert(string Fingerprint, string Text, DateTimeOffset Timestamp);

/// <summary>
/// Forwards every <c>ERROR</c> (and above) to the operator chat through a bounded queue, at most
/// once per fingerprint per cooldown (spec 4, Alerting). It sits behind the redacting sink, so it
/// never sees a secret, and it never logs: a dropped alert goes to Serilog's self-log only.
/// </summary>
public sealed class AlertSink : ILogEventSink
{
    /// <summary>Alerts waiting for delivery before new ones are dropped.</summary>
    public const int QueueCapacity = 100;

    /// <summary>An event property whose value is added to the fingerprint, splitting one template into several alerts.</summary>
    public const string KeyProperty = "AlertKey";

    /// <summary>An event property holding the event's own cooldown, in whole seconds.</summary>
    public const string CooldownSecondsProperty = "AlertCooldownSeconds";

    private const string SourceContextProperty = "SourceContext";

    private readonly Channel<Alert> _queue = Channel.CreateBounded<Alert>(new BoundedChannelOptions(QueueCapacity)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
    });

    private readonly Dictionary<string, DateTimeOffset> _lastSent = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private readonly TimeSpan _cooldown;

    /// <summary>Creates the sink with a cooldown per fingerprint.</summary>
    public AlertSink(TimeProvider time, TimeSpan cooldown)
    {
        _time = time;
        _cooldown = cooldown;
    }

    /// <summary>The alerts to deliver, in order.</summary>
    public ChannelReader<Alert> Alerts => _queue.Reader;

    /// <inheritdoc />
    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        if (logEvent.Level < LogEventLevel.Error)
        {
            return;
        }

        var source = logEvent.Properties.TryGetValue(SourceContextProperty, out var value) && value is ScalarValue { Value: string name } ? name : "app";
        var fingerprint = source + " | " + logEvent.MessageTemplate.Text;
        if (logEvent.Properties.TryGetValue(KeyProperty, out var key) && key is ScalarValue { Value: string keyText })
        {
            fingerprint += " | " + keyText;
        }

        // An event may carry its own cooldown (a rate source's outage, spec 1.4); anything but a
        // positive whole number of seconds leaves the default.
        var cooldown = logEvent.Properties.TryGetValue(CooldownSecondsProperty, out var seconds) && seconds is ScalarValue { Value: int own } && own > 0
            ? TimeSpan.FromSeconds(own)
            : _cooldown;
        // Check and record under one lock: concurrent identical errors (a database going down under
        // load) must still raise one alert per cooldown.
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (_lastSent.TryGetValue(fingerprint, out var last) && now - last < cooldown)
            {
                return;
            }

            _lastSent[fingerprint] = now;
        }

        var text = logEvent.RenderMessage(System.Globalization.CultureInfo.InvariantCulture);
        if (logEvent.Exception is not null)
        {
            text += " (" + logEvent.Exception.Message + ")";
        }

        if (!_queue.Writer.TryWrite(new Alert(fingerprint, text, logEvent.Timestamp)))
        {
            SelfLog.WriteLine("Alert queue full; alert dropped for {0}", fingerprint);
        }
    }
}
