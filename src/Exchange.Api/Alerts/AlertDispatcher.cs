using Serilog.Debugging;

namespace Exchange.Api.Alerts;

/// <summary>Delivers an alert to the operator chat.</summary>
public interface IAlertChannel
{
    /// <summary>Sends one alert; throws when it could not.</summary>
    Task SendAsync(Alert alert, CancellationToken cancellationToken);
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
                var reason = error is AlertDeliveryException delivery ? delivery.Reason : error.GetType().Name;
                SelfLog.WriteLine("Alert delivery failed for {0}: {1}", alert.Fingerprint, reason);
            }
        }
    }
}
