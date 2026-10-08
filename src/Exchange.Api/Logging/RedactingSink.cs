using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;

namespace Exchange.Api.Logging;

/// <summary>
/// The one entrance to every log output: each event is redacted here and only then handed on
/// (spec 4). Nothing downstream ever sees an unredacted event.
/// </summary>
public sealed class RedactingSink : ILogEventSink, IDisposable
{
    private readonly ILogEventSink[] _outputs;

    /// <summary>Redacts every event, then writes it to each output.</summary>
    public RedactingSink(params ILogEventSink[] outputs) => _outputs = outputs;

    /// <inheritdoc />
    public void Emit(LogEvent logEvent)
    {
        var redacted = LogRedactor.Redact(logEvent);
        foreach (var output in _outputs)
        {
            try
            {
                output.Emit(redacted);
            }
            catch (Exception error)
            {
                // An output that fails must not take the alert (or the console) down with it; the
                // failure goes to Serilog's self-log, since logging it would re-enter this sink.
                SelfLog.WriteLine("Log output {0} failed: {1}", output.GetType().Name, error.GetType().Name);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var output in _outputs.OfType<IDisposable>())
        {
            output.Dispose();
        }
    }
}
