using Exchange.Api.Settings;

namespace Exchange.Api.Bot;

/// <summary>
/// Development only (spec 7, PHASE-4): receives the bot's updates by long polling instead of the
/// webhook, which needs a public HTTPS address a development machine does not have. Started with
/// <c>dotnet run -- poll</c> beside the app, it removes any registered webhook first - Telegram
/// refuses <c>getUpdates</c> while one is set - so it must never run against the production bot,
/// and it refuses demo mode, which talks to no Telegram (spec 1.9). Each update goes, in a scope of
/// its own, to the same <see cref="BotUpdates"/> the webhook uses.
/// </summary>
public sealed partial class BotPoller(IBotClient bot, IServiceScopeFactory scopes, AppSettings settings, TimeProvider time, ILogger<BotPoller> logger) : BackgroundService
{
    /// <summary>The command-line argument that starts the poller with the app.</summary>
    public const string Command = "poll";

    /// <summary>How long Telegram holds a <c>getUpdates</c> call open when nothing is waiting.</summary>
    public const int LongPollSeconds = 25;

    /// <summary>The pause after a failed <c>getUpdates</c>.</summary>
    public static readonly TimeSpan PauseAfterFailure = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (settings.Environment == AppEnvironment.Demo)
        {
            throw new InvalidOperationException("the bot poller does not run in demo mode");
        }

        return base.StartAsync(cancellationToken);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await bot.DeleteWebhookAsync(stoppingToken).ConfigureAwait(false);
        LogStarted(logger);
        var offset = 0L;
        while (!stoppingToken.IsCancellationRequested)
        {
            offset = await PollOnceAsync(offset, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>One <c>getUpdates</c> round; returns the next offset.</summary>
    public async Task<long> PollOnceAsync(long offset, CancellationToken stoppingToken)
    {
        IReadOnlyList<System.Text.Json.JsonElement> updates;
        try
        {
            updates = await bot.GetUpdatesAsync(offset, LongPollSeconds, stoppingToken).ConfigureAwait(false);
        }
        catch (BotException failure)
        {
            LogPollFailed(logger, failure, failure.Reason);
            await Task.Delay(PauseAfterFailure, time, stoppingToken).ConfigureAwait(false);
            return offset;
        }

        foreach (var update in updates)
        {
            var updateId = update.TryGetProperty("update_id", out var id) && id.TryGetInt64(out var value) ? value : offset;

            // Moved past before handling: an update that keeps failing is not fetched forever.
            offset = Math.Max(offset, updateId + 1);
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IBotUpdateHandler>().HandleAsync(update, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // One bad update must not stop the poller.
                LogUpdateFailed(logger, error, updateId);
            }
        }

        return offset;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Bot polling started")]
    private static partial void LogStarted(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Bot polling failed: {Reason}")]
    private static partial void LogPollFailed(ILogger logger, Exception error, string reason);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Bot update {UpdateId} failed")]
    private static partial void LogUpdateFailed(ILogger logger, Exception error, long updateId);
}
