using System.Collections.Concurrent;
using Exchange.Api.Logging;
using Exchange.Api.Settings;
using Serilog.Core;
using Serilog.Events;

namespace Exchange.Api.Tests.Logging;

/// <summary>An output that keeps every event and its text in a format, for tests to read.</summary>
public sealed class CapturingSink : ILogEventSink
{
    private readonly ConcurrentQueue<(LogEvent Event, string Text)> _entries = new();
    private readonly Serilog.Formatting.ITextFormatter _formatter;

    public CapturingSink(LogFormat format = LogFormat.Console) => _formatter = LoggingSetup.CreateFormatter(format);

    public IReadOnlyList<LogEvent> Events => [.. _entries.Select(entry => entry.Event)];

    public string Text => string.Concat(_entries.Select(entry => entry.Text));

    public void Emit(LogEvent logEvent)
    {
        using var writer = new StringWriter();
        _formatter.Format(logEvent, writer);
        _entries.Enqueue((logEvent, writer.ToString()));
    }
}
