using Serilog.Core;

namespace Exchange.Api.Logging;

/// <summary>
/// Where formatted logs go: the console (and in development the rolling file) by default. A test
/// host registers its own to read what the app logged; redaction still runs before it.
/// </summary>
/// <param name="Sink">The output sink; it receives redacted events only.</param>
public sealed record LogOutput(ILogEventSink Sink);
