using Serilog.Debugging;

namespace Exchange.Api.Alerts;

/// <summary>Delivers an alert to the operator chat.</summary>
public interface IAlertChannel
{
    /// <summary>Sends one alert; throws when it could not.</summary>
    Task SendAsync(Alert alert, CancellationToken cancellationToken);
}

/// <summary>
/// Until the Telegram client exists (PHASE-3), an alert is recorded as an <c>INFO</c> line naming
/// what would have been sent (spec 7, PHASE-0).
/// </summary>
public sealed partial class LogOnlyAlertChannel(ILogger<LogOnlyAlertChannel> logger) : IAlertChannel
{
    /// <inheritdoc />
    public Task SendAsync(Alert alert, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(alert);
        LogWouldSend(logger, alert.Fingerprint);
        return Task.CompletedTask;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Alert for {Fingerprint} is ready for the operator chat; delivery arrives with the Telegram client")]
    private static partial void LogWouldSend(ILogger logger, string fingerprint);
}

/// <summary>
/// Drains the alert queue off the request path. A failed delivery goes to Serilog's self-log
/// (stderr) only, never back into logging, which would alert about alerting (spec 4).
/// </summary>
public sealed class AlertDispatcher(AlertSink sink, IAlertChannel channel) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var alert in sink.Alerts.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await channel.SendAsync(alert, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                SelfLog.WriteLine("Alert delivery failed for {0}: {1}", alert.Fingerprint, error.GetType().Name);
            }
        }
    }
}
