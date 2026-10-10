using System.Globalization;
using System.Net;
using System.Text.Json;
using Exchange.Api.Settings;

namespace Exchange.Api.Bot;

/// <summary>An inline button: its label and the data Telegram sends back when it is pressed.</summary>
/// <param name="Text">The label.</param>
/// <param name="CallbackData">The callback data, at most 64 bytes.</param>
public sealed record BotButton(string Text, string CallbackData);

/// <summary>A message the bot sends: its chat, its text and its buttons (one per row).</summary>
/// <param name="ChatId">The chat.</param>
/// <param name="Text">The text; it may carry the client's details and is never logged.</param>
/// <param name="Buttons">The inline buttons, one per row; empty for none.</param>
public sealed record OutgoingMessage(long ChatId, string Text, IReadOnlyList<BotButton> Buttons);

/// <summary>A Bot API call failed. <see cref="Reason"/> is a short code: never the URL, which holds the bot token.</summary>
public sealed class BotException : Exception
{
    /// <summary>Creates the exception with its reason code.</summary>
    public BotException(string reason)
        : base("bot call failed: " + reason) => Reason = reason;

    /// <summary>Required by the exception pattern.</summary>
    public BotException()
        : this("unknown")
    {
    }

    /// <summary>Required by the exception pattern; the inner exception must carry no URL.</summary>
    public BotException(string reason, Exception innerException)
        : base("bot call failed: " + reason, innerException) => Reason = reason;

    /// <summary>Why: <c>http_&lt;status&gt;</c>, <c>transport_&lt;type&gt;</c>, <c>timeout</c> or <c>not_ok</c>.</summary>
    public string Reason { get; }
}

/// <summary>The Bot API calls the app makes.</summary>
public interface IBotClient
{
    /// <summary>Sends a message.</summary>
    /// <exception cref="BotException">Telegram did not accept it.</exception>
    Task SendMessageAsync(OutgoingMessage message, CancellationToken cancellationToken);

    /// <summary>Answers a button press, optionally with a short notice shown to the presser.</summary>
    /// <exception cref="BotException">Telegram did not accept it.</exception>
    Task AnswerCallbackAsync(string callbackQueryId, string? notice, CancellationToken cancellationToken);

    /// <summary>Removes the registered webhook, which Telegram requires before <c>getUpdates</c>.</summary>
    /// <exception cref="BotException">Telegram did not accept it.</exception>
    Task DeleteWebhookAsync(CancellationToken cancellationToken);

    /// <summary>The button presses from <paramref name="offset"/> on, waiting up to <paramref name="waitSeconds"/> for one.</summary>
    /// <exception cref="BotException">Telegram did not accept it.</exception>
    Task<IReadOnlyList<JsonElement>> GetUpdatesAsync(long offset, int waitSeconds, CancellationToken cancellationToken);
}

/// <summary>
/// The Bot API over plain JSON POSTs (spec 6.2 forbids <c>Telegram.Bot</c>). Only a 200 answer with
/// <c>"ok": true</c> counts; every failure is a reason code, and the transport's exception is not
/// chained, since its message quotes the URL and the URL holds the bot token.
/// </summary>
public sealed class TelegramBotClient(HttpClient client, Secret botToken) : IBotClient
{
    /// <summary>The Bot API's address.</summary>
    public static readonly Uri TelegramApiUrl = new("https://api.telegram.org");

    private const string TimeoutReason = "timeout";

    /// <inheritdoc />
    public Task SendMessageAsync(OutgoingMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var body = new Dictionary<string, object> { ["chat_id"] = message.ChatId, ["text"] = message.Text, ["disable_web_page_preview"] = true };
        if (message.Buttons.Count > 0)
        {
            body["reply_markup"] = new Dictionary<string, object>
            {
                ["inline_keyboard"] = message.Buttons.Select(button => new[] { new Dictionary<string, string> { ["text"] = button.Text, ["callback_data"] = button.CallbackData } }).ToArray(),
            };
        }

        return CallAsync("sendMessage", body, cancellationToken);
    }

    /// <inheritdoc />
    public Task AnswerCallbackAsync(string callbackQueryId, string? notice, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object> { ["callback_query_id"] = callbackQueryId };
        if (notice is not null)
        {
            body["text"] = notice;
        }

        return CallAsync("answerCallbackQuery", body, cancellationToken);
    }

    /// <inheritdoc />
    public Task DeleteWebhookAsync(CancellationToken cancellationToken) =>
        CallAsync("deleteWebhook", new Dictionary<string, object> { ["drop_pending_updates"] = false }, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<JsonElement>> GetUpdatesAsync(long offset, int waitSeconds, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object> { ["offset"] = offset, ["timeout"] = waitSeconds, ["allowed_updates"] = new[] { "callback_query" } };
        var result = await CallAsync("getUpdates", body, cancellationToken).ConfigureAwait(false);
        return result.ValueKind == JsonValueKind.Array ? [.. result.EnumerateArray()] : throw new BotException("not_ok");
    }

    private async Task<JsonElement> CallAsync(string method, Dictionary<string, object> body, CancellationToken cancellationToken)
    {
        var url = new Uri(TelegramApiUrl, "/bot" + botToken.Reveal() + "/" + method);
        string failure;
        try
        {
            using var response = await client.PostAsJsonAsync(url, body, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                failure = "http_" + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                var answer = await response.Content.ReadFromJsonAsync<BotAnswer>(cancellationToken).ConfigureAwait(false);
                if (answer is { Ok: true })
                {
                    return answer.Result;
                }

                failure = "not_ok";
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            failure = TimeoutReason;
        }
        catch (HttpRequestException error)
        {
            // Not chained: the transport's message quotes the URL, which holds the bot token.
            failure = "transport_" + error.GetType().Name;
        }
        catch (System.Text.Json.JsonException)
        {
            failure = "not_ok";
        }

        throw new BotException(failure);
    }

    private sealed record BotAnswer(bool Ok, JsonElement Result);
}
