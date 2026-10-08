using Exchange.Api.Tests.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace Exchange.Api.Tests.Logging;

/// <summary>Spec 1.9: in demo mode nothing goes to Telegram; an error is logged, its alert dropped.</summary>
[Collection(nameof(ProcessEnvironmentGroup))]
public sealed class DemoAlertTests
{
    private const int SettleMilliseconds = 1_500;

    private static readonly Action<ILogger, Exception?> ProbeError =
        LoggerMessage.Define(LogLevel.Error, new EventId(1), "Probe failure in demo mode");

    [Fact]
    public void An_error_in_demo_mode_raises_no_alert()
    {
        var logs = new CapturingSink();
        ProcessEnvironment.With("demo", () =>
        {
            using var factory = new TestAppFactory(TestSettings.Demo(), logs);
            using var client = factory.CreateClient();

            ProbeError(factory.Services.GetRequiredService<ILogger<DemoAlertTests>>(), null);
            Thread.Sleep(SettleMilliseconds); // long enough for the dispatcher to have drained a queued alert
        });

        logs.Text.ShouldContain("Probe failure in demo mode");
        logs.Text.ShouldNotContain("Alert for");
    }
}
