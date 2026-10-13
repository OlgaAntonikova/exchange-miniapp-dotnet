using System.Globalization;
using System.Reflection;
using Exchange.Api.Settings;

namespace Exchange.Api.Logging;

/// <summary>
/// Logs the startup record of spec 4 once the host starts: version, environment, providers with
/// their cache and freshness settings, order limits, and every secret as its mask. URLs are not
/// logged (spec 1.4).
/// </summary>
public sealed partial class StartupRecord(AppSettings settings, ILogger<StartupRecord> logger) : IHostedService
{
    /// <summary>The app's version (informational version of the assembly).</summary>
    public static string Version { get; } =
        typeof(StartupRecord).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!logger.IsEnabled(LogLevel.Information))
        {
            return Task.CompletedTask;
        }

        var limits = string.Join(", ", settings.OrderLimits.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => string.Create(CultureInfo.InvariantCulture, $"{pair.Key} {pair.Value.Min}-{pair.Value.Max}")));
        var secrets = string.Join(", ", new (string Name, Secret? Value)[]
        {
            ("BOT_TOKEN", settings.BotToken),
            ("BOT_WEBHOOK_SECRET", settings.BotWebhookSecret),
            ("JWT_SECRET", settings.JwtSecret),
            ("PII_ENCRYPTION_KEY", settings.PiiEncryptionKey),
            ("DATABASE_URL", settings.DatabaseConnectionString),
        }.Select(secret => $"{secret.Name}={(secret.Value is null ? "unset" : secret.Value.ToString())}"));

        var environment = settings.Environment.ToString().ToLowerInvariant();
        var logFormat = settings.LogFormat.ToString().ToLowerInvariant();
        LogStarted(
            logger,
            Version,
            environment,
            logFormat,
            settings.EcbRateCacheSeconds,
            settings.EcbRateMaxAgeSeconds,
            settings.EcbRateMaxEffectiveAgeDays,
            settings.CbrRateCacheSeconds,
            settings.CbrRateMaxAgeSeconds,
            settings.CbrRateMaxEffectiveLeadDays,
            settings.CbrRateMaxEffectiveAgeDays,
            limits,
            secrets);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message =
        "Exchange Mini App {Version} starting: environment {Environment}, log format {LogFormat}; " +
        "ecb cache {EcbCacheSeconds} s, max age {EcbMaxAgeSeconds} s, effective age {EcbMaxEffectiveAgeDays} d; " +
        "cbr cache {CbrCacheSeconds} s, max age {CbrMaxAgeSeconds} s, lead {CbrMaxEffectiveLeadDays} d, effective age {CbrMaxEffectiveAgeDays} d; " +
        "order limits {OrderLimits}; credentials {Credentials}")]
    private static partial void LogStarted(
        ILogger logger,
        string version,
        string environment,
        string logFormat,
        int ecbCacheSeconds,
        int ecbMaxAgeSeconds,
        int ecbMaxEffectiveAgeDays,
        int cbrCacheSeconds,
        int cbrMaxAgeSeconds,
        int cbrMaxEffectiveLeadDays,
        int cbrMaxEffectiveAgeDays,
        string orderLimits,
        string credentials);
}
