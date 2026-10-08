namespace Exchange.Api.Settings;

/// <summary>
/// Every setting of spec 1.11, read once at startup by <see cref="SettingsLoader"/>. Secrets are
/// <see cref="Secret"/>s. In demo mode the Telegram settings are absent (null) and the JWT and PII
/// keys are generated for this process (spec 1.9). A class, not a record: a generated
/// <c>ToString</c> would list every member, URLs included, wherever the object is formatted.
/// </summary>
public sealed class AppSettings
{
    /// <summary><c>APP_ENV</c>.</summary>
    public required AppEnvironment Environment { get; init; }

    /// <summary><c>BOT_TOKEN</c>; null in demo mode.</summary>
    public required Secret? BotToken { get; init; }

    /// <summary><c>BOT_WEBHOOK_SECRET</c>; null in demo mode.</summary>
    public required Secret? BotWebhookSecret { get; init; }

    /// <summary><c>JWT_SECRET</c>, or a per-process key in demo mode.</summary>
    public required Secret JwtSecret { get; init; }

    /// <summary><c>PII_ENCRYPTION_KEY</c> (base64 of 32 bytes), or a per-process key in demo mode.</summary>
    public required Secret PiiEncryptionKey { get; init; }

    /// <summary>The Npgsql connection string built from <c>DATABASE_URL</c> (D-010).</summary>
    public required Secret DatabaseConnectionString { get; init; }

    /// <summary><c>OPERATOR_CHAT_ID</c>; null in demo mode.</summary>
    public required long? OperatorChatId { get; init; }

    /// <summary><c>OPERATOR_TELEGRAM_IDS</c>; empty in demo mode.</summary>
    public required IReadOnlySet<long> OperatorTelegramIds { get; init; }

    /// <summary><c>LOG_FORMAT</c>.</summary>
    public required LogFormat LogFormat { get; init; }

    /// <summary><c>LOG_LEVEL</c>: one of <see cref="SettingsLoader.LogLevels"/>.</summary>
    public required string LogLevel { get; init; }

    /// <summary><c>SERVE_HOST</c>: an IP address or <c>localhost</c>.</summary>
    public required string ServeHost { get; init; }

    /// <summary><c>ALERT_COOLDOWN_SECONDS</c>.</summary>
    public required int AlertCooldownSeconds { get; init; }

    /// <summary><c>RATE_ALERT_COOLDOWN_SECONDS</c>.</summary>
    public required int RateAlertCooldownSeconds { get; init; }

    /// <summary><c>RATE_FETCH_TIMEOUT_SECONDS</c>.</summary>
    public required int RateFetchTimeoutSeconds { get; init; }

    /// <summary><c>ECB_RATES_URL</c>.</summary>
    public required Uri EcbRatesUrl { get; init; }

    /// <summary><c>ECB_RATE_CACHE_SECONDS</c>.</summary>
    public required int EcbRateCacheSeconds { get; init; }

    /// <summary><c>ECB_RATE_MAX_AGE_SECONDS</c>.</summary>
    public required int EcbRateMaxAgeSeconds { get; init; }

    /// <summary><c>ECB_RATE_MAX_EFFECTIVE_AGE_DAYS</c>.</summary>
    public required int EcbRateMaxEffectiveAgeDays { get; init; }

    /// <summary><c>CBR_RATES_URL</c>.</summary>
    public required Uri CbrRatesUrl { get; init; }

    /// <summary><c>CBR_RATE_CACHE_SECONDS</c>.</summary>
    public required int CbrRateCacheSeconds { get; init; }

    /// <summary><c>CBR_RATE_MAX_AGE_SECONDS</c>.</summary>
    public required int CbrRateMaxAgeSeconds { get; init; }

    /// <summary><c>CBR_RATE_MAX_EFFECTIVE_LEAD_DAYS</c>.</summary>
    public required int CbrRateMaxEffectiveLeadDays { get; init; }

    /// <summary><c>CBR_RATE_MAX_EFFECTIVE_AGE_DAYS</c>.</summary>
    public required int CbrRateMaxEffectiveAgeDays { get; init; }

    /// <summary><c>&lt;CODE&gt;_ORDER_MIN</c> / <c>_MAX</c> per give code (spec 1.5), inclusive.</summary>
    public required IReadOnlyDictionary<string, OrderLimit> OrderLimits { get; init; }

    /// <summary><c>INIT_DATA_MAX_AGE_SECONDS</c>.</summary>
    public required int InitDataMaxAgeSeconds { get; init; }

    /// <summary><c>JWT_TTL_SECONDS</c>.</summary>
    public required int JwtTtlSeconds { get; init; }
}

/// <summary>The inclusive amount range of one give currency, in that currency.</summary>
/// <param name="Min">The smallest amount accepted.</param>
/// <param name="Max">The largest amount accepted.</param>
public sealed record OrderLimit(decimal Min, decimal Max);
