using Exchange.Api.Logging;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;
using Shouldly;

namespace Exchange.Api.Tests.Logging;

/// <summary>D-021: an output that throws does not keep the event from the outputs after it.</summary>
public sealed class RedactingSinkTests
{
    [Fact]
    public void A_failing_output_does_not_starve_the_next_one()
    {
        var capture = new CapturingSink();
        using var sink = new RedactingSink(new ThrowingSink(), capture);

        sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Error, null, new MessageTemplateParser().Parse("Still delivered"), []));

        capture.Events.ShouldHaveSingleItem().MessageTemplate.Text.ShouldBe("Still delivered");
    }

    private sealed class ThrowingSink : ILogEventSink
    {
        public void Emit(LogEvent logEvent) => throw new IOException("console closed");
    }
}
