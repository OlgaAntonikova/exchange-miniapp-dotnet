using System.Globalization;
using System.Net;
using Exchange.Api.Locale;
using Exchange.Api.Rates;
using Exchange.Api.Settings;

namespace Exchange.Api.Alerts;

/// <summary>An alert could not be delivered. <see cref="Reason"/> is a short code: never the URL, which holds the bot token.</summary>
public sealed class AlertDeliveryException : Exception
{
    /// <summary>Creates the exception with its reason code.</summary>
    public AlertDeliveryException(string reason)
        : base("alert delivery failed: " + reason) => Reason = reason;

    /// <summary>Required by the exception pattern.</summary>
    public AlertDeliveryException()
        : this("unknown")
    {
    }

    /// <summary>Required by the exception pattern; the inner exception must carry no URL.</summary>
    public AlertDeliveryException(string reason, Exception innerException)
        : base("alert delivery failed: " + reason, innerException) => Reason = reason;

    /// <summary>Why: <c>http_&lt;status&gt;</c>, <c>transport_&lt;type&gt;</c> or <c>timeout</c>.</summary>
    public string Reason { get; }
}

/// <summary>Delivers alerts to the operator chat through the Telegram Bot API's <c>sendMessage</c> (spec 4, Alerting).</summary>
public sealed class TelegramAlertChannel(HttpClient client, Secret botToken, long chatId, LocaleTexts texts) : IAlertChannel
{
    /// <summary>The Bot API's address.</summary>
    public static readonly Uri TelegramApiUrl = new("https://api.telegram.org");

    /// <summary>Telegram's limit for one message's text, in characters.</summary>
    public const int MessageMaxChars = 4096;

    /// <summary>What ends a text cut to the limit.</summary>
    public const string TruncationMark = "\n[truncated]";

    private const string TimeoutReason = "timeout";

    /// <inheritdoc />
    public async Task SendAsync(Alert alert, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(alert);
        var message = texts.Text(LocaleKeys.AlertHeader) + "\n" + alert.Text;
        var text = message.Length <= MessageMaxChars ? message : Cut(message) + TruncationMark;
        var url = new Uri(TelegramApiUrl, "/bot" + botToken.Reveal() + "/sendMessage");
        var body = new Dictionary<string, object> { ["chat_id"] = chatId, ["text"] = text, ["disable_web_page_preview"] = true };

        string failure;
        try
        {
            using var response = await client.PostAsJsonAsync(url, body, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                return;
            }

            failure = "http_" + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient reports its own timeout as a cancellation the caller did not ask for.
            failure = TimeoutReason;
        }
        catch (HttpRequestException error)
        {
            // Not chained: the transport's message quotes the URL, and the URL holds the bot token.
            failure = "transport_" + error.GetType().Name;
        }

        throw new AlertDeliveryException(failure);
    }

    // The text before the mark, never ending inside a surrogate pair, which would leave a lone half.
    private static string Cut(string text)
    {
        var length = MessageMaxChars - TruncationMark.Length;
        return char.IsHighSurrogate(text[length - 1]) ? text[..(length - 1)] : text[..length];
    }
}

/// <summary>Demo mode sends nothing to Telegram; its alerts are dropped before they are queued (spec 1.9).</summary>
public sealed class NoAlertChannel : IAlertChannel
{
    /// <inheritdoc />
    public Task SendAsync(Alert alert, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Wires the alert channel: Telegram outside demo mode, nothing in it (spec 1.9).</summary>
public static class TelegramAlertSetup
{
    /// <summary>How long one delivery may take, in seconds.</summary>
    public const int RequestTimeoutSeconds = 10;

    /// <summary>The service key of the alert channel's HTTP client.</summary>
    public const string ClientKey = "telegram-alerts";

    /// <summary>The largest Bot API answer read; <c>sendMessage</c> answers well under a kilobyte.</summary>
    public const int MaxResponseBytes = 64 * 1024;

    /// <summary>The client deliveries go through.</summary>
    public static HttpClient CreateClient() => new(RateSetup.CreateHandler())
    {
        Timeout = TimeSpan.FromSeconds(RequestTimeoutSeconds),
        MaxResponseContentBufferSize = MaxResponseBytes,
    };

    /// <summary>Registers <see cref="IAlertChannel"/>: Telegram when a bot and an operator chat are configured.</summary>
    public static IServiceCollection AddAlertChannel(this IServiceCollection services)
    {
        services.AddKeyedSingleton(ClientKey, (_, _) => CreateClient());
        services.AddSingleton<IAlertChannel>(provider =>
        {
            var settings = provider.GetRequiredService<AppSettings>();
            return settings is { BotToken: { } token, OperatorChatId: { } chat }
                ? new TelegramAlertChannel(provider.GetRequiredKeyedService<HttpClient>(ClientKey), token, chat, provider.GetRequiredService<LocaleTexts>())
                : new NoAlertChannel();
        });
        return services;
    }
}
