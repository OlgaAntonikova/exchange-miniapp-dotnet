using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Exchange.Api.Settings;

/// <summary>
/// Reads and validates every setting of spec 1.11 in one pass, collecting all problems before it
/// refuses (INV-8). Each problem names its variable and never its value (RULE-SECRETS).
/// </summary>
public static partial class SettingsLoader
{
    /// <summary>Production secrets: required outside demo mode, refused inside it (spec 1.9, D-013).</summary>
    public static readonly IReadOnlyList<string> ProductionSecrets =
        ["BOT_TOKEN", "BOT_WEBHOOK_SECRET", "JWT_SECRET", "PII_ENCRYPTION_KEY"];

    /// <summary>Accepted <c>LOG_LEVEL</c> values.</summary>
    public static readonly IReadOnlyList<string> LogLevels = ["Debug", "Information", "Warning", "Error"];

    /// <summary>The give codes that carry order limits (spec 1.1, 1.5).</summary>
    public static readonly IReadOnlyList<string> GiveCodes = ["RUB_WIRE", "USD_CASH", "EUR_CASH", "USDT"];

    /// <summary>Length of a generated demo key and of the PII key, in bytes (AES-256).</summary>
    public const int KeyLengthBytes = 32;

    /// <summary>The shortest accepted <c>JWT_SECRET</c>, in characters.</summary>
    public const int JwtSecretMinChars = 32;

    /// <summary>The longest accepted <c>BOT_WEBHOOK_SECRET</c>, in characters (Telegram's limit).</summary>
    public const int WebhookSecretMaxChars = 256;

    private const string DefaultLogLevel = "Information";
    private const string DefaultServeHost = "127.0.0.1";
    private const string LocalhostName = "localhost";
    private const int DefaultAlertCooldownSeconds = 300;
    private const int DefaultRateAlertCooldownSeconds = 900;
    private const int DefaultRateFetchTimeoutSeconds = 10;
    private const string DefaultEcbRatesUrl = "https://www.ecb.europa.eu/stats/eurofxref/eurofxref-daily.xml";
    private const int DefaultEcbRateCacheSeconds = 3600;
    private const int DefaultEcbRateMaxAgeSeconds = 129_600;
    private const int DefaultEcbRateMaxEffectiveAgeDays = 5;
    private const string DefaultCbrRatesUrl = "https://www.cbr.ru/scripts/XML_daily.asp";
    private const int DefaultCbrRateCacheSeconds = 3600;
    private const int DefaultCbrRateMaxAgeSeconds = 129_600;
    private const int DefaultCbrRateMaxEffectiveLeadDays = 1;
    private const int DefaultCbrRateMaxEffectiveAgeDays = 15;
    private const int DefaultInitDataMaxAgeSeconds = 86_400;
    private const int DefaultJwtTtlSeconds = 3600;

    private static readonly Dictionary<string, (decimal Min, decimal Max)> DefaultOrderLimits = new()
    {
        ["RUB_WIRE"] = (10_000m, 1_000_000m),
        ["USD_CASH"] = (100m, 10_000m),
        ["EUR_CASH"] = (100m, 10_000m),
        ["USDT"] = (100m, 10_000m),
    };

    /// <summary>
    /// Variables demo mode refuses besides the production secrets: they would let a demo reach a
    /// password-protected database (spec 1.9, D-016).
    /// </summary>
    public static readonly IReadOnlyList<string> DemoRefusedDatabaseVariables = ["PGPASSWORD", "PGPASSFILE"];

    /// <summary>Loads the settings with keys from the system's random number generator.</summary>
    public static AppSettings Load(AppEnvironment environment, Func<string, string?> read) =>
        Load(environment, read, RandomNumberGenerator.GetBytes);

    /// <summary>
    /// Loads the settings for <paramref name="environment"/> (resolved by
    /// <see cref="AppEnvironments"/>, never read here); <paramref name="randomBytes"/> makes the
    /// demo keys.
    /// </summary>
    /// <exception cref="SettingsException">Any setting is missing or invalid; every problem is listed.</exception>
    public static AppSettings Load(AppEnvironment environment, Func<string, string?> read, Func<int, byte[]> randomBytes)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(randomBytes);
        var problems = new List<string>();
        var reader = new Reader(read, problems);
        var demo = environment == AppEnvironment.Demo;

        Secret? botToken = null, webhookSecret = null, jwtSecret = null, piiKey = null;
        long? operatorChatId = null;
        IReadOnlySet<long> operatorIds = new HashSet<long>();
        if (demo)
        {
            foreach (var name in ProductionSecrets.Concat(DemoRefusedDatabaseVariables).Where(name => reader.IsSet(name)))
            {
                problems.Add($"{name} must not be set in demo mode");
            }

            jwtSecret = new Secret(System.Convert.ToBase64String(randomBytes(KeyLengthBytes)));
            piiKey = new Secret(System.Convert.ToBase64String(randomBytes(KeyLengthBytes)));
        }
        else
        {
            botToken = reader.Secret("BOT_TOKEN", value => BotTokenPattern().IsMatch(value) ? null : "is not shaped <numeric id>:<at least 30 characters>");
            webhookSecret = reader.Secret("BOT_WEBHOOK_SECRET", value => WebhookSecretPattern().IsMatch(value) && value.Length <= WebhookSecretMaxChars ? null : $"must be 1-{WebhookSecretMaxChars} characters of A-Z a-z 0-9 _ -");
            jwtSecret = reader.Secret("JWT_SECRET", value => value.Length >= JwtSecretMinChars ? null : $"is shorter than {JwtSecretMinChars} characters");
            piiKey = reader.Secret("PII_ENCRYPTION_KEY", value => IsBase64OfLength(value, KeyLengthBytes) ? null : $"is not base64 of exactly {KeyLengthBytes} bytes");
            operatorChatId = reader.ChatId("OPERATOR_CHAT_ID");
            operatorIds = reader.UserIds("OPERATOR_TELEGRAM_IDS");
        }

        var connectionString = reader.DatabaseUrl(demo);

        var settings = new AppSettings
        {
            Environment = environment,
            BotToken = botToken,
            BotWebhookSecret = webhookSecret,
            JwtSecret = jwtSecret ?? Placeholder,
            PiiEncryptionKey = piiKey ?? Placeholder,
            DatabaseConnectionString = connectionString ?? Placeholder,
            OperatorChatId = operatorChatId,
            OperatorTelegramIds = operatorIds,
            LogFormat = reader.LogFormat(),
            LogLevel = reader.LogLevel(),
            ServeHost = reader.ServeHost(),
            AlertCooldownSeconds = reader.Positive("ALERT_COOLDOWN_SECONDS", DefaultAlertCooldownSeconds),
            RateAlertCooldownSeconds = reader.Positive("RATE_ALERT_COOLDOWN_SECONDS", DefaultRateAlertCooldownSeconds),
            RateFetchTimeoutSeconds = reader.Positive("RATE_FETCH_TIMEOUT_SECONDS", DefaultRateFetchTimeoutSeconds),
            EcbRatesUrl = reader.HttpUrl("ECB_RATES_URL", DefaultEcbRatesUrl),
            EcbRateCacheSeconds = reader.Positive("ECB_RATE_CACHE_SECONDS", DefaultEcbRateCacheSeconds),
            EcbRateMaxAgeSeconds = reader.Positive("ECB_RATE_MAX_AGE_SECONDS", DefaultEcbRateMaxAgeSeconds),
            EcbRateMaxEffectiveAgeDays = reader.Positive("ECB_RATE_MAX_EFFECTIVE_AGE_DAYS", DefaultEcbRateMaxEffectiveAgeDays),
            CbrRatesUrl = reader.HttpUrl("CBR_RATES_URL", DefaultCbrRatesUrl),
            CbrRateCacheSeconds = reader.Positive("CBR_RATE_CACHE_SECONDS", DefaultCbrRateCacheSeconds),
            CbrRateMaxAgeSeconds = reader.Positive("CBR_RATE_MAX_AGE_SECONDS", DefaultCbrRateMaxAgeSeconds),
            CbrRateMaxEffectiveLeadDays = reader.Positive("CBR_RATE_MAX_EFFECTIVE_LEAD_DAYS", DefaultCbrRateMaxEffectiveLeadDays),
            CbrRateMaxEffectiveAgeDays = reader.Positive("CBR_RATE_MAX_EFFECTIVE_AGE_DAYS", DefaultCbrRateMaxEffectiveAgeDays),
            OrderLimits = reader.OrderLimits(),
            InitDataMaxAgeSeconds = reader.Positive("INIT_DATA_MAX_AGE_SECONDS", DefaultInitDataMaxAgeSeconds),
            JwtTtlSeconds = reader.Positive("JWT_TTL_SECONDS", DefaultJwtTtlSeconds),
        };

        return problems.Count > 0 ? throw new SettingsException(problems) : settings;
    }

    // Stands in for a refused secret while the remaining settings are still checked; the settings
    // object holding it is never returned.
    private static readonly Secret Placeholder = new("refused");

    private static bool IsBase64OfLength(string value, int lengthBytes)
    {
        var buffer = new byte[lengthBytes + 1];
        return System.Convert.TryFromBase64String(value, buffer, out var written) && written == lengthBytes;
    }

    // NR-4: Telegram documents the shape only by example; kept loose so a real token is never refused.
    [GeneratedRegex("^[0-9]+:[A-Za-z0-9_-]{30,}$")]
    private static partial Regex BotTokenPattern();

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex WebhookSecretPattern();

    // An amount with at most two decimals: limits are compared with NUMERIC(18,2) amounts.
    [GeneratedRegex("^[0-9]+(\\.[0-9]{1,2})?$")]
    private static partial Regex PlainDecimalPattern();

    private sealed class Reader(Func<string, string?> read, List<string> problems)
    {
        public string? Value(string name)
        {
            var value = read(name);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        public bool IsSet(string name) => Value(name) is not null;

        public Secret? Secret(string name, Func<string, string?> shapeProblem)
        {
            var value = Value(name);
            if (value is null)
            {
                problems.Add($"{name} is missing");
                return null;
            }

            if (IsPlaceholder(value))
            {
                problems.Add($"{name} is still the .env.example placeholder");
                return null;
            }

            var problem = shapeProblem(value);
            if (problem is not null)
            {
                problems.Add($"{name} {problem}");
                return null;
            }

            return new Secret(value);
        }

        public Secret? DatabaseUrl(bool demo)
        {
            const string name = "DATABASE_URL";
            var value = Value(name);
            if (value is null)
            {
                problems.Add($"{name} is missing");
                return null;
            }

            if (IsPlaceholder(value))
            {
                problems.Add($"{name} is still the .env.example placeholder");
                return null;
            }

            var result = Settings.DatabaseUrl.Convert(value);
            if (result.Problem is not null)
            {
                problems.Add($"{name} {result.Problem}");
                return null;
            }

            if (demo && result.HasPassword)
            {
                problems.Add($"{name} must carry no password in demo mode");
                return null;
            }

            return result.ConnectionString;
        }

        public long? ChatId(string name)
        {
            var value = Required(name);
            if (value is null)
            {
                return null;
            }

            if (long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id) && id != 0)
            {
                return id;
            }

            problems.Add($"{name} is not a Telegram chat id");
            return null;
        }

        public HashSet<long> UserIds(string name)
        {
            var value = Required(name);
            if (value is null)
            {
                return new HashSet<long>();
            }

            var ids = new HashSet<long>();
            foreach (var part in value.Split(','))
            {
                if (long.TryParse(part.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0)
                {
                    ids.Add(id);
                }
                else
                {
                    problems.Add($"{name} must be comma-separated positive Telegram user ids");
                    return new HashSet<long>();
                }
            }

            return ids;
        }

        public LogFormat LogFormat()
        {
            var value = Value("LOG_FORMAT");
            if (value is null)
            {
                return Settings.LogFormat.Console;
            }

            if (value.Equals("console", StringComparison.OrdinalIgnoreCase))
            {
                return Settings.LogFormat.Console;
            }

            if (value.Equals("json", StringComparison.OrdinalIgnoreCase))
            {
                return Settings.LogFormat.Json;
            }

            problems.Add("LOG_FORMAT must be console or json");
            return Settings.LogFormat.Console;
        }

        public string LogLevel()
        {
            var value = Value("LOG_LEVEL");
            if (value is null)
            {
                return DefaultLogLevel;
            }

            var level = LogLevels.FirstOrDefault(level => level.Equals(value, StringComparison.OrdinalIgnoreCase));
            if (level is null)
            {
                problems.Add("LOG_LEVEL must be one of " + string.Join(", ", LogLevels));
                return DefaultLogLevel;
            }

            return level;
        }

        public string ServeHost()
        {
            var value = Value("SERVE_HOST") ?? DefaultServeHost;
            // Canonical form only: IPAddress.TryParse also takes "1" (0.0.0.1) and "127.1".
            if (value.Equals(LocalhostName, StringComparison.OrdinalIgnoreCase)
                || (IPAddress.TryParse(value, out var address) && address.ToString() == value))
            {
                return value;
            }

            problems.Add("SERVE_HOST must be an IP address or localhost");
            return DefaultServeHost;
        }

        public int Positive(string name, int defaultValue)
        {
            var value = Value(name);
            if (value is null)
            {
                return defaultValue;
            }

            if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0)
            {
                return number;
            }

            problems.Add($"{name} must be a positive whole number");
            return defaultValue;
        }

        public Uri HttpUrl(string name, string defaultValue)
        {
            var value = Value(name) ?? defaultValue;
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
            {
                return uri;
            }

            problems.Add($"{name} must be an absolute http or https URL");
            return new Uri(defaultValue);
        }

        public Dictionary<string, OrderLimit> OrderLimits()
        {
            var limits = new Dictionary<string, OrderLimit>();
            foreach (var code in GiveCodes)
            {
                var (defaultMin, defaultMax) = DefaultOrderLimits[code];
                var min = PositiveDecimal($"{code}_ORDER_MIN", defaultMin);
                var max = PositiveDecimal($"{code}_ORDER_MAX", defaultMax);
                if (min >= max)
                {
                    problems.Add($"{code}_ORDER_MIN must be below {code}_ORDER_MAX");
                }

                limits[code] = new OrderLimit(min, max);
            }

            return limits;
        }

        private decimal PositiveDecimal(string name, decimal defaultValue)
        {
            var value = Value(name);
            if (value is null)
            {
                return defaultValue;
            }

            if (PlainDecimalPattern().IsMatch(value)
                && decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)
                && number > 0)
            {
                return number;
            }

            problems.Add($"{name} must be a positive amount like 100 or 100.50");
            return defaultValue;
        }

        private string? Required(string name)
        {
            var value = Value(name);
            if (value is null)
            {
                problems.Add($"{name} is missing");
                return null;
            }

            if (IsPlaceholder(value))
            {
                problems.Add($"{name} is still the .env.example placeholder");
                return null;
            }

            return value;
        }

        private static bool IsPlaceholder(string value) => value.StartsWith('<') && value.EndsWith('>');
    }
}
