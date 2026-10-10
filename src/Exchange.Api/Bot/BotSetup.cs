using Exchange.Api.Rates;
using Exchange.Api.Settings;

namespace Exchange.Api.Bot;

/// <summary>A bot client for an app with no bot (demo mode): it is never reached, since demo deliveries are suppressed first.</summary>
public sealed class NoBotClient : IBotClient
{
    /// <inheritdoc />
    public Task SendMessageAsync(OutgoingMessage message, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task AnswerCallbackAsync(string callbackQueryId, string? notice, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Wires the bot client, the message builder, the notification queue and its dispatcher.</summary>
public static class BotSetup
{
    /// <summary>How long one Bot API call may take, in seconds.</summary>
    public const int RequestTimeoutSeconds = 10;

    /// <summary>The largest Bot API answer read.</summary>
    public const int MaxResponseBytes = 64 * 1024;

    /// <summary>The service key of the bot's HTTP client.</summary>
    public const string ClientKey = "telegram-bot";

    /// <summary>The client Bot API calls go through (a plain singleton, D-035).</summary>
    public static HttpClient CreateClient() => new(RateSetup.CreateHandler())
    {
        Timeout = TimeSpan.FromSeconds(RequestTimeoutSeconds),
        MaxResponseContentBufferSize = MaxResponseBytes,
    };

    /// <summary>Registers the bot's services.</summary>
    public static IServiceCollection AddBot(this IServiceCollection services)
    {
        services.AddKeyedSingleton(ClientKey, (_, _) => CreateClient());
        services.AddSingleton<IBotClient>(provider => provider.GetRequiredService<AppSettings>().BotToken is { } token
            ? new TelegramBotClient(provider.GetRequiredKeyedService<HttpClient>(ClientKey), token)
            : new NoBotClient());
        services.AddSingleton<NotificationOutbox>();
        services.AddSingleton<NotificationDispatcher>();
        services.AddHostedService(provider => provider.GetRequiredService<NotificationDispatcher>());
        services.AddScoped<BotMessages>();
        services.AddScoped<OrderNotifications>();
        return services;
    }
}
