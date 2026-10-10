using System.Text.Json;
using Exchange.Api.Data;
using Exchange.Api.Locale;
using Exchange.Api.Orders;
using Exchange.Api.Settings;
using Exchange.Domain.Orders;
using Microsoft.EntityFrameworkCore;

namespace Exchange.Api.Bot;

/// <summary>Handles one bot update; the webhook and the poller both call it.</summary>
public interface IBotUpdateHandler
{
    /// <summary>Handles one update.</summary>
    Task HandleAsync(JsonElement update, CancellationToken cancellationToken);
}

/// <summary>
/// The bot's updates (spec 1.5, INV-11): the operator's button presses, from the webhook or the
/// development poller alike. A press counts only when it comes from a Telegram user id in
/// <c>OPERATOR_TELEGRAM_IDS</c> and names a valid <c>order:&lt;id&gt;:&lt;STATUS&gt;</c>; the status
/// then changes through the one transition function as the operator, and the client and the operator
/// are told. Anything else changes nothing: the press is answered, logged at <c>WARNING</c> and
/// dropped. Updates other than button presses are ignored.
/// </summary>
public sealed partial class BotUpdates(OrderTransitions transitions, OrderNotifications notifications, IBotClient bot, AppSettings settings, LocaleTexts texts, ExchangeDbContext db, ILogger<BotUpdates> logger) : IBotUpdateHandler
{
    /// <summary>Handles one update.</summary>
    public async Task HandleAsync(JsonElement update, CancellationToken cancellationToken)
    {
        if (update.ValueKind != JsonValueKind.Object
            || !update.TryGetProperty("callback_query", out var callback) || callback.ValueKind != JsonValueKind.Object)
        {
            LogIgnored(logger);
            return;
        }

        var callbackId = callback.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString()! : string.Empty;
        long? presser = callback.TryGetProperty("from", out var from) && from.ValueKind == JsonValueKind.Object
            && from.TryGetProperty("id", out var fromId) && fromId.ValueKind == JsonValueKind.Number && fromId.TryGetInt64(out var presserId)
            ? presserId
            : null;
        if (presser is not { } operatorId || !settings.OperatorTelegramIds.Contains(operatorId))
        {
            LogRefused(logger, "not_an_operator", presser);
            await AnswerAsync(callbackId, texts.Text(LocaleKeys.PressNotAllowed), cancellationToken).ConfigureAwait(false);
            return;
        }

        var data = callback.TryGetProperty("data", out var raw) && raw.ValueKind == JsonValueKind.String ? raw.GetString() : null;
        if (BotMessages.ParseCallback(data) is not { } press)
        {
            LogRefused(logger, "bad_callback_data", operatorId);
            await AnswerAsync(callbackId, texts.Text(LocaleKeys.PressNotAllowed), cancellationToken).ConfigureAwait(false);
            return;
        }

        var result = await transitions.ChangeAsync(press.OrderId, press.Status, Actor.Operator, operatorId, null, cancellationToken).ConfigureAwait(false);
        if (result.Outcome != TransitionOutcome.Changed)
        {
            LogRefused(logger, result.Outcome == TransitionOutcome.NotFound ? "no_such_order" : "outdated", operatorId);
            await AnswerAsync(callbackId, texts.Text(LocaleKeys.PressOutdated), cancellationToken).ConfigureAwait(false);
            return;
        }

        var order = result.Order!;
        var client = await db.Users.AsNoTracking().SingleAsync(user => user.Id == order.UserId, cancellationToken).ConfigureAwait(false);
        notifications.StatusChanged(order, client);
        await AnswerAsync(callbackId, texts.Text(LocaleKeys.Status(OrderCodes.Of(order.Status))), cancellationToken).ConfigureAwait(false);
    }

    // The spinner on the operator's button stops either way; a failed answer is only logged.
    private async Task AnswerAsync(string callbackId, string notice, CancellationToken cancellationToken)
    {
        try
        {
            await bot.AnswerCallbackAsync(callbackId, notice, cancellationToken).ConfigureAwait(false);
        }
        catch (BotException failure)
        {
            // The stack is safe: a BotException carries a reason code and never the URL.
            LogAnswerFailed(logger, failure, failure.Reason);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Bot update ignored: not a button press")]
    private static partial void LogIgnored(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Button press refused: {Reason} from {PresserId}")]
    private static partial void LogRefused(ILogger logger, string reason, long? presserId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Callback answer failed: {Reason}")]
    private static partial void LogAnswerFailed(ILogger logger, Exception error, string reason);
}
