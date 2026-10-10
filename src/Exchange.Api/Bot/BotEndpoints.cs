using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Exchange.Api.Auth;
using Exchange.Api.Settings;

namespace Exchange.Api.Bot;

/// <summary>
/// <c>POST /api/bot/webhook</c> (spec 1.10, INV-11): where Telegram delivers the bot's updates. The
/// call must carry the secret registered with <c>setWebhook</c> in
/// <c>X-Telegram-Bot-Api-Secret-Token</c>, compared in constant time; without it, or with another
/// value, the answer is the 401 of INV-21 and nothing changes. Every accepted call is answered 200,
/// whatever the update held, so Telegram does not deliver it again. Not mapped in demo mode (spec 1.9).
/// </summary>
public static partial class BotEndpoints
{
    /// <summary>The header Telegram sends the webhook secret in.</summary>
    public const string SecretHeader = "X-Telegram-Bot-Api-Secret-Token";

    /// <summary>The largest update read; an update with a button press is a few kilobytes.</summary>
    public const int MaxUpdateBytes = 64 * 1024;

    private const string LoggerCategory = "Exchange.Api.Bot";

    /// <summary>Maps the webhook, outside demo mode only.</summary>
    public static IEndpointRouteBuilder MapBotEndpoints(this IEndpointRouteBuilder app, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.BotWebhookSecret is not { } secret)
        {
            return app;
        }

        app.MapPost("/api/bot/webhook", async (HttpContext http, IBotUpdateHandler updates) =>
        {
            var sent = http.Request.Headers[SecretHeader].ToString();
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(sent), Encoding.UTF8.GetBytes(secret.Reveal())))
            {
                return AuthEndpoints.Unauthorized("bad_webhook_secret");
            }

            var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(LoggerCategory);
            if (await AuthEndpoints.ReadCappedAsync(http.Request.Body, MaxUpdateBytes, http.RequestAborted).ConfigureAwait(false) is not { } body)
            {
                LogUnreadable(logger, "too_long");
                return Accepted();
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                LogUnreadable(logger, "malformed");
                return Accepted();
            }

            using (document)
            {
                await updates.HandleAsync(document.RootElement, http.RequestAborted).ConfigureAwait(false);
            }

            return Accepted();
        });
        return app;
    }

    private static IResult Accepted() => Results.Ok(new Dictionary<string, bool> { ["ok"] = true });

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Bot update unreadable: {Reason}")]
    private static partial void LogUnreadable(ILogger logger, string reason);
}
