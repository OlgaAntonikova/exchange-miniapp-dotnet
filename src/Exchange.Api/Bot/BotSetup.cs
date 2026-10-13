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

    /// <inheritdoc />
    public Task DeleteWebhookAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task<IReadOnlyList<System.Text.Json.JsonElement>> GetUpdatesAsync(long offset, int waitSeconds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<System.Text.Json.JsonElement>>([]);
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

    /// <summary>The service key of the poller's HTTP client, which outlasts a long poll.</summary>
    public const string PollClientKey = "telegram-poll";

    /// <summary>How long beyond the long poll a <c>getUpdates</c> call may take to answer.</summary>
    public const int PollMarginSeconds = 10;

    /// <summary>How long one <c>getUpdates</c> call may take: the long poll and the margin.</summary>
    public const int PollRequestTimeoutSeconds = BotPoller.LongPollSeconds + PollMarginSeconds;

    /// <summary>The client Bot API calls go through (a plain singleton, D-035).</summary>
    public static HttpClient CreateClient(int timeoutSeconds = RequestTimeoutSeconds) => new(RateSetup.CreateHandler())
    {
        Timeout = TimeSpan.FromSeconds(timeoutSeconds),
        MaxResponseContentBufferSize = MaxResponseBytes,
    };

    /// <summary>Registers the bot's services; with <paramref name="poll"/>, also the development poller.</summary>
    public static IServiceCollection AddBot(this IServiceCollection services, bool poll = false)
    {
        if (poll)
        {
            services.AddKeyedSingleton(PollClientKey, (_, _) => CreateClient(PollRequestTimeoutSeconds));
            services.AddHostedService(provider => new BotPoller(
                provider.GetRequiredService<AppSettings>().BotToken is { } token
                    ? new TelegramBotClient(provider.GetRequiredKeyedService<HttpClient>(PollClientKey), token)
                    : new NoBotClient(),
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<AppSettings>(),
                provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<ILogger<BotPoller>>()));
        }

        services.AddKeyedSingleton(ClientKey, (_, _) => CreateClient());
        services.AddSingleton<IBotClient>(provider => provider.GetRequiredService<AppSettings>().BotToken is { } token
            ? new TelegramBotClient(provider.GetRequiredKeyedService<HttpClient>(ClientKey), token)
            : new NoBotClient());
        services.AddSingleton<NotificationOutbox>();
        services.AddSingleton<NotificationDispatcher>();
        services.AddHostedService(provider => provider.GetRequiredService<NotificationDispatcher>());
        services.AddScoped<BotMessages>();
        services.AddScoped<OrderNotifications>();
        services.AddScoped<IBotUpdateHandler, BotUpdates>();
        return services;
    }
}
