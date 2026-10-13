using Exchange.Api.Alerts;
using Exchange.Api.Logging;
using Exchange.Api.Tests.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog.Events;
using Shouldly;

namespace Exchange.Api.Tests.Logging;

/// <summary>Spec 4 in the real host: the startup record (version, configuration, every secret masked, no
/// URL) and the request line, both through the app's own output.</summary>
public sealed class StartupRecordTests
{
    [Fact]
    public void The_host_logs_its_version_and_configuration_without_a_secret()
    {
        var logs = new CapturingSink();
        using (var factory = new TestAppFactory(TestSettings.Production(), logs))
        using (var client = factory.CreateClient())
        {
            client.ShouldNotBeNull();
        }

        var record = logs.Events.Single(e => e.MessageTemplate.Text.StartsWith("Exchange Mini App", StringComparison.Ordinal));
        record.Level.ShouldBe(LogEventLevel.Information);
        var text = record.RenderMessage();
        text.ShouldContain(StartupRecord.Version);
        text.ShouldContain("\"test\"");
        text.ShouldContain("max age 129600 s");
        text.ShouldContain("RUB_WIRE 10000-1000000");
        text.ShouldContain("JWT_SECRET=" + LogRedactor.Mask);
        text.ShouldContain("DATABASE_URL=" + LogRedactor.Mask);
        foreach (var secret in new[] { TestSettings.BotToken, TestSettings.JwtSecret, TestSettings.PiiKey, "dbpass" })
        {
            logs.Text.ShouldNotContain(secret);
        }

        logs.Text.ShouldNotContain("ecb.europa.eu");
        logs.Text.ShouldNotContain("cbr.ru");
    }

    [Fact]
    public async Task A_request_to_the_app_is_logged_with_its_route_and_request_id()
    {
        var logs = new CapturingSink();
        using var factory = new TestAppFactory(TestSettings.Production(), logs);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/healthz", UriKind.Relative), TestContext.Current.CancellationToken);

        var requestId = response.Headers.GetValues(RequestLogging.RequestIdHeader).ShouldHaveSingleItem();
        var line = logs.Events.Single(e => e.MessageTemplate.Text.StartsWith("HTTP ", StringComparison.Ordinal));
        line.RenderMessage().ShouldMatch("^HTTP \"GET\" \"/healthz\" responded 200 in [0-9]+ ms$");
        line.Properties[RequestLogging.RequestIdProperty].ToString().ShouldBe("\"" + requestId + "\"");
    }

    private const int AlertWaitMilliseconds = 10_000;
    private const int AlertPollMilliseconds = 50;

    private static readonly Action<ILogger, Exception?> ProbeError =
        LoggerMessage.Define(LogLevel.Error, new EventId(1), "Probe failure for the alert path");

    [Fact]
    public async Task An_error_in_the_app_reaches_the_alert_channel()
    {
        var channel = new RecordingAlertChannel();
        using var factory = new TestAppFactory(TestSettings.Production(), services: services => services.AddSingleton<IAlertChannel>(channel));
        using var client = factory.CreateClient();

        ProbeError(factory.Services.GetRequiredService<ILogger<StartupRecordTests>>(), null);

        (await WaitForAsync(() => !channel.Alerts.IsEmpty)).ShouldBeTrue("the alert dispatcher never delivered the error");
        channel.Alerts.ShouldContain(alert => alert.Text.Contains("Probe failure for the alert path", StringComparison.Ordinal));
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition)
    {
        for (var waited = 0; waited < AlertWaitMilliseconds; waited += AlertPollMilliseconds)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(AlertPollMilliseconds, TestContext.Current.CancellationToken);
        }

        return condition();
    }
}

/// <summary>An alert channel that keeps what it was given.</summary>
internal sealed class RecordingAlertChannel : IAlertChannel
{
    public System.Collections.Concurrent.ConcurrentQueue<Alert> Alerts { get; } = new();

    public Task SendAsync(Alert alert, CancellationToken cancellationToken)
    {
        Alerts.Enqueue(alert);
        return Task.CompletedTask;
    }
}
