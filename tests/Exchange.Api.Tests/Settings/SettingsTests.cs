using Exchange.Api.Settings;
using Exchange.Api.Tests.Repository;
using Shouldly;

namespace Exchange.Api.Tests.Settings;

/// <summary>INV-8 and spec 1.11: the app refuses to start on a missing, malformed or placeholder
/// setting, naming the variable and never the value; demo mode reads no secret (spec 1.9).</summary>
public sealed class SettingsTests
{
    private static readonly byte[] DemoKeyBytes = Enumerable.Range(0, SettingsLoader.KeyLengthBytes).Select(i => (byte)i).ToArray();

    [Fact]
    public void A_valid_environment_loads_with_defaults_for_plain_settings()
    {
        var settings = Load(TestSettings.Production());

        settings.Environment.ShouldBe(AppEnvironment.Production);
        settings.BotToken!.Reveal().ShouldBe(TestSettings.BotToken);
        settings.OperatorChatId.ShouldBe(-1001234);
        settings.OperatorTelegramIds.ShouldBe(new HashSet<long> { 111, 222 });
        settings.LogFormat.ShouldBe(LogFormat.Console);
        settings.LogLevel.ShouldBe("Information");
        settings.ServeHost.ShouldBe("127.0.0.1");
        settings.EcbRatesUrl.ShouldBe(new Uri("https://www.ecb.europa.eu/stats/eurofxref/eurofxref-daily.xml"));
        settings.CbrRatesUrl.ShouldBe(new Uri("https://www.cbr.ru/scripts/XML_daily.asp"));
        settings.EcbRateMaxAgeSeconds.ShouldBe(129_600);
        settings.CbrRateMaxEffectiveAgeDays.ShouldBe(15);
        settings.OrderLimits["RUB_WIRE"].ShouldBe(new OrderLimit(10_000m, 1_000_000m));
        settings.OrderLimits["USDT"].ShouldBe(new OrderLimit(100m, 10_000m));
        settings.InitDataMaxAgeSeconds.ShouldBe(86_400);
        settings.JwtTtlSeconds.ShouldBe(3600);
    }

    [Theory]
    [InlineData("BOT_TOKEN")]
    [InlineData("BOT_WEBHOOK_SECRET")]
    [InlineData("JWT_SECRET")]
    [InlineData("PII_ENCRYPTION_KEY")]
    [InlineData("DATABASE_URL")]
    [InlineData("OPERATOR_CHAT_ID")]
    [InlineData("OPERATOR_TELEGRAM_IDS")]
    public void A_missing_required_setting_is_refused_by_name(string name)
    {
        var environment = TestSettings.Production();
        environment.Remove(name);

        Refusal(environment).Problems.ShouldBe([$"{name} is missing"]);
    }

    [Fact]
    public void Every_placeholder_of_env_example_is_refused_by_name()
    {
        var example = DotEnvFile.Parse(File.ReadAllText(Path.Combine(RepositoryRoot.Path, ".env.example")));
        var environment = example.ToDictionary(pair => pair.Key, pair => (string?)pair.Value);

        var problems = Refusal(environment).Problems;

        string[] required = ["BOT_TOKEN", "BOT_WEBHOOK_SECRET", "JWT_SECRET", "PII_ENCRYPTION_KEY", "DATABASE_URL", "OPERATOR_CHAT_ID", "OPERATOR_TELEGRAM_IDS"];
        problems.ShouldBe(required.Select(name => $"{name} is still the .env.example placeholder").ToList(), ignoreOrder: true);
    }

    [Theory]
    [InlineData("BOT_TOKEN", "12345", "BOT_TOKEN is not shaped")]
    [InlineData("BOT_TOKEN", "abc:defghijklmnopqrstuvwxyz0123456789", "BOT_TOKEN is not shaped")]
    [InlineData("BOT_WEBHOOK_SECRET", "has space", "BOT_WEBHOOK_SECRET must be")]
    [InlineData("JWT_SECRET", "short-key", "JWT_SECRET is shorter than 32 characters")]
    [InlineData("PII_ENCRYPTION_KEY", "c2hvcnQ=", "PII_ENCRYPTION_KEY is not base64 of exactly 32 bytes")]
    [InlineData("PII_ENCRYPTION_KEY", "not base64 at all!!", "PII_ENCRYPTION_KEY is not base64 of exactly 32 bytes")]
    [InlineData("DATABASE_URL", "mysql://u:p@host/db", "DATABASE_URL is not a postgresql:// URL")]
    [InlineData("OPERATOR_CHAT_ID", "abc", "OPERATOR_CHAT_ID is not a Telegram chat id")]
    [InlineData("OPERATOR_CHAT_ID", "0", "OPERATOR_CHAT_ID is not a Telegram chat id")]
    [InlineData("OPERATOR_TELEGRAM_IDS", "111,abc", "OPERATOR_TELEGRAM_IDS must be")]
    [InlineData("OPERATOR_TELEGRAM_IDS", "-5", "OPERATOR_TELEGRAM_IDS must be")]
    [InlineData("LOG_FORMAT", "xml", "LOG_FORMAT must be")]
    [InlineData("LOG_LEVEL", "Verbose", "LOG_LEVEL must be")]
    [InlineData("SERVE_HOST", "example.com", "SERVE_HOST must be")]
    [InlineData("SERVE_HOST", "1", "SERVE_HOST must be")]
    [InlineData("SERVE_HOST", "127.1", "SERVE_HOST must be")]
    [InlineData("JWT_TTL_SECONDS", "0", "JWT_TTL_SECONDS must be a positive whole number")]
    [InlineData("ECB_RATE_MAX_AGE_SECONDS", "-1", "ECB_RATE_MAX_AGE_SECONDS must be a positive whole number")]
    [InlineData("CBR_RATE_CACHE_SECONDS", "1.5", "CBR_RATE_CACHE_SECONDS must be a positive whole number")]
    [InlineData("ECB_RATES_URL", "ftp://example.org/x", "ECB_RATES_URL must be an absolute http or https URL")]
    [InlineData("CBR_RATES_URL", "not a url", "CBR_RATES_URL must be an absolute http or https URL")]
    [InlineData("USDT_ORDER_MIN", "0", "USDT_ORDER_MIN must be a positive amount")]
    [InlineData("USDT_ORDER_MIN", "1e2", "USDT_ORDER_MIN must be a positive amount")]
    [InlineData("USDT_ORDER_MIN", "100.123", "USDT_ORDER_MIN must be a positive amount")]
    [InlineData("USDT_ORDER_MIN", "10000", "USDT_ORDER_MIN must be below USDT_ORDER_MAX")]
    [InlineData("RUB_WIRE_ORDER_MAX", "5000", "RUB_WIRE_ORDER_MIN must be below RUB_WIRE_ORDER_MAX")]
    public void A_malformed_setting_is_refused_by_name(string name, string value, string expected)
    {
        var environment = TestSettings.Production();
        environment[name] = value;

        var problems = Refusal(environment).Problems;

        problems.Count.ShouldBe(1);
        problems[0].ShouldStartWith(expected);
    }

    [Theory]
    [InlineData("SERVE_HOST", "0.0.0.0")]
    [InlineData("SERVE_HOST", "::1")]
    [InlineData("SERVE_HOST", "localhost")]
    [InlineData("USDT_ORDER_MIN", "100.5")]
    [InlineData("USDT_ORDER_MAX", "10000.25")]
    public void A_valid_value_passes(string name, string value)
    {
        var environment = TestSettings.Production();
        environment[name] = value;

        Should.NotThrow(() => Load(environment));
    }

    [Fact]
    public void A_webhook_secret_longer_than_telegram_allows_is_refused()
    {
        var environment = TestSettings.Production();
        environment["BOT_WEBHOOK_SECRET"] = new string('w', SettingsLoader.WebhookSecretMaxChars + 1);

        Refusal(environment).Problems.ShouldBe(["BOT_WEBHOOK_SECRET must be 1-256 characters of A-Z a-z 0-9 _ -"]);
    }

    [Fact]
    public void Every_problem_is_reported_at_once()
    {
        var environment = TestSettings.Production();
        environment.Remove("BOT_TOKEN");
        environment["JWT_SECRET"] = "short";
        environment["LOG_FORMAT"] = "xml";

        Refusal(environment).Problems.Count.ShouldBe(3);
    }

    [Fact]
    public void A_refusal_never_carries_the_value()
    {
        var environment = TestSettings.Production();
        var malformedToken = "999" + ":" + "tooshort-but-secret";
        var shortJwt = "jwt-secret-value";
        var badUrl = "postgresql://user:" + "dbpassword-value" + "@host/db?password=x";
        environment["BOT_TOKEN"] = malformedToken;
        environment["JWT_SECRET"] = shortJwt;
        environment["DATABASE_URL"] = badUrl;

        var refusal = Refusal(environment);

        refusal.Problems.Count.ShouldBe(3);
        foreach (var value in new[] { malformedToken, shortJwt, "dbpassword-value", "tooshort-but-secret" })
        {
            refusal.Message.ShouldNotContain(value);
            refusal.ToString().ShouldNotContain(value);
        }
    }

    [Fact]
    public void Secrets_print_masked_even_inside_the_settings_record()
    {
        var settings = Load(TestSettings.Production());

        settings.JwtSecret.ToString().ShouldBe(Secret.Mask);
        $"{settings.DatabaseConnectionString}".ShouldBe(Secret.Mask);
        settings.ToString().ShouldBe(typeof(AppSettings).FullName);
        DatabaseUrl.Convert(TestSettings.DatabaseUrl).ToString().ShouldNotContain("dbpass");
    }

    // --- Demo mode (spec 1.9) ----------------------------------------------------------------------

    [Fact]
    public void Demo_mode_needs_only_a_password_less_database_url_and_generates_its_keys()
    {
        var settings = Load(TestSettings.Demo(), AppEnvironment.Demo);

        settings.Environment.ShouldBe(AppEnvironment.Demo);
        settings.BotToken.ShouldBeNull();
        settings.BotWebhookSecret.ShouldBeNull();
        settings.OperatorChatId.ShouldBeNull();
        settings.OperatorTelegramIds.ShouldBeEmpty();
        settings.JwtSecret.Reveal().ShouldBe(Convert.ToBase64String(DemoKeyBytes));
        settings.PiiEncryptionKey.Reveal().ShouldBe(Convert.ToBase64String(DemoKeyBytes));
    }

    [Fact]
    public void Demo_keys_come_from_the_random_source_on_every_start()
    {
        var calls = 0;
        var first = SettingsLoader.Load(AppEnvironment.Demo, Read(TestSettings.Demo()), length => { calls++; return new byte[length]; });

        calls.ShouldBe(2);
        Convert.FromBase64String(first.JwtSecret.Reveal()).Length.ShouldBe(SettingsLoader.KeyLengthBytes);
        Convert.FromBase64String(first.PiiEncryptionKey.Reveal()).Length.ShouldBe(SettingsLoader.KeyLengthBytes);
    }

    [Theory]
    [InlineData("BOT_TOKEN")]
    [InlineData("BOT_WEBHOOK_SECRET")]
    [InlineData("JWT_SECRET")]
    [InlineData("PII_ENCRYPTION_KEY")]
    [InlineData("PGPASSWORD")]
    [InlineData("PGPASSFILE")]
    public void Demo_mode_refuses_a_production_secret(string name)
    {
        var environment = TestSettings.Demo();
        environment[name] = "anything";

        Refusal(environment, AppEnvironment.Demo).Problems.ShouldBe([$"{name} must not be set in demo mode"]);
    }

    [Fact]
    public void A_production_secret_outside_demo_mode_is_not_refused_for_being_set()
    {
        var environment = TestSettings.Production();
        environment["PGPASSWORD"] = "anything";

        Should.NotThrow(() => Load(environment));
    }

    [Fact]
    public void Demo_mode_refuses_a_database_url_with_a_password()
    {
        var environment = TestSettings.Demo();
        environment["DATABASE_URL"] = TestSettings.DatabaseUrl;

        Refusal(environment, AppEnvironment.Demo).Problems.ShouldBe(["DATABASE_URL must carry no password in demo mode"]);
    }

    [Fact]
    public void Demo_mode_refuses_a_password_in_the_database_url_query()
    {
        var environment = TestSettings.Demo();
        environment["DATABASE_URL"] = "postgresql://postgres@db/exchange?password=x";

        Refusal(environment, AppEnvironment.Demo).Problems.ShouldBe(["DATABASE_URL has an unsupported query parameter; only sslmode is allowed"]);
    }

    [Fact]
    public void Demo_mode_still_requires_a_database_url()
    {
        var environment = TestSettings.Demo();
        environment.Remove("DATABASE_URL");

        Refusal(environment, AppEnvironment.Demo).Problems.ShouldBe(["DATABASE_URL is missing"]);
    }

    [Fact]
    public void App_env_in_the_configuration_is_ignored()
    {
        // APP_ENV is resolved from the process by AppEnvironments, never read by the loader (D-017).
        var environment = TestSettings.Production();
        environment["APP_ENV"] = "demo";

        Load(environment).Environment.ShouldBe(AppEnvironment.Production);
    }

    private static AppSettings Load(Dictionary<string, string?> environment, AppEnvironment appEnvironment = AppEnvironment.Production) =>
        SettingsLoader.Load(appEnvironment, Read(environment), _ => DemoKeyBytes);

    private static SettingsException Refusal(Dictionary<string, string?> environment, AppEnvironment appEnvironment = AppEnvironment.Production) =>
        Should.Throw<SettingsException>(() => Load(environment, appEnvironment));

    private static Func<string, string?> Read(Dictionary<string, string?> environment) =>
        key => environment.TryGetValue(key, out var value) ? value : null;
}
