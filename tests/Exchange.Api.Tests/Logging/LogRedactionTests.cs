using Exchange.Api.Logging;
using Exchange.Api.Settings;
using Serilog;
using Shouldly;

namespace Exchange.Api.Tests.Logging;

/// <summary>
/// INV-7: no log output carries a value from the redaction list or a secret pattern - in a named
/// property, nested inside a structure, in the message text, or in an exception - in either format.
/// Secret samples are assembled at run time so this file does not trip INV-12.
/// </summary>
public sealed class LogRedactionTests
{
    private const int TokenTailChars = 35;

    private static readonly string BotToken = "5555555" + ":" + new string('Q', TokenTailChars);
    private static readonly string Jwt = "eyJ" + "hbGciOiJIUzI1NiJ9" + "." + "eyJzdWIiOiIxIn0" + "." + "c2lnbmF0dXJlX3Y";
    private const string DbPassword = "s3cret-db-pass";
    private static readonly string DbUrl = "postgresql://exchange:" + DbPassword + "@db/exchange";
    private const string ConnPassword = "conn-pass-123";
    private static readonly string ConnectionString = "Host=db;Password=" + ConnPassword + ";Database=exchange";
    private const string Iban = "ME25505000012345678951";
    private const string Wallet = "TQn9Y2khEsLJW1ChVWFMSMeRDow5KcbLSE";
    private const string Recipient = "Ivan Petrov";
    private const string Purpose = "rent for October";
    private const string InitData = "query_id=AAHdF6IQ&user=%7B%22id%22%3A1%7D&hash=c501b71e775f74ce10e377dea85a7ea2";
    private const string Cookie = "session=abcdef0123456789";
    private const string ApiKey = "key-0123456789abcdef";

    public static TheoryData<LogFormat> Formats => [LogFormat.Console, LogFormat.Json];

    [Theory]
    [MemberData(nameof(Formats))]
    public void Values_under_secret_names_are_masked(LogFormat format)
    {
        var text = Capture(format, logger => logger.Information(
            "Order {OrderId}: {Iban} {WalletAddress} {RecipientName} {payment_purpose} {InitData} {Authorization} {Cookie} {ApiKey} {DatabaseUrl} {BotToken} {Password} {RequestUri} {ConnectionString}",
            42, Iban, Wallet, Recipient, Purpose, InitData, "Bearer " + Jwt, Cookie, ApiKey, "https://db.example/x", BotToken, "hunter2-pass", "https://example.org/a", "Host=x"));

        ShouldHoldNoSecret(text);
        text.ShouldNotContain("hunter2-pass");
        text.ShouldNotContain("db.example");
        text.ShouldNotContain("example.org/a");
        text.ShouldContain("42");
        text.ShouldContain(LogRedactor.Mask);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Snake_case_names_match_the_list_with_underscores_ignored(LogFormat format)
    {
        var text = Capture(format, logger => logger.Information(
            "Request {init_data} {api_key} {database_url} {bot_token}",
            InitData, ApiKey, "redis://plain-host/0", "plain-value-under-a-token-name"));

        text.ShouldNotContain(InitData);
        text.ShouldNotContain(ApiKey);
        text.ShouldNotContain("plain-host");
        text.ShouldNotContain("plain-value-under-a-token-name");
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Secrets_nested_in_structures_sequences_and_dictionaries_are_masked(LogFormat format)
    {
        var map = new Dictionary<string, string> { ["iban"] = Iban, ["city"] = "Budva" };
        var text = Capture(format, logger => logger.Information(
            "Payload {@Payload} {@Map}",
            new { Details = new { Iban, Amount = "100.00" }, Items = new[] { new { Wallet, Network = "TRC20" } } },
            map));

        ShouldHoldNoSecret(text);
        text.ShouldContain("100.00");
        text.ShouldContain("TRC20");
        text.ShouldContain("Budva");
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Secret_patterns_are_masked_wherever_they_appear(LogFormat format)
    {
        var telegramUri = new Uri("https://api.telegram.org/bot" + BotToken + "/sendMessage");
        var text = Capture(format, logger =>
        {
            logger.Information("Detail {Detail}", "token " + BotToken + " jwt " + Jwt);
            logger.Information("Database {Detail} and {Other}", DbUrl, ConnectionString);
            logger.Information("Calling {Endpoint}", telegramUri);
#pragma warning disable CA2254 // The point of this line: a secret inside the message text itself.
            logger.Information("Literal text with " + BotToken + " inside");
#pragma warning restore CA2254
        });

        ShouldHoldNoSecret(text);
        text.ShouldContain("api.telegram.org/bot" + LogRedactor.Mask);
        text.ShouldContain("postgresql://exchange:" + LogRedactor.Mask + "@db/exchange");
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Secrets_in_an_exception_are_masked_and_the_stack_kept(LogFormat format)
    {
        var text = Capture(format, logger =>
        {
            try
            {
                Fail();
            }
            catch (InvalidOperationException error)
            {
                logger.Error(error, "Operation {Operation} failed", "send-message");
            }
        });

        ShouldHoldNoSecret(text);
        text.ShouldContain(nameof(InvalidOperationException));
        text.ShouldContain(nameof(Fail));
        text.ShouldContain("send-message");
    }

    [Theory]
    [InlineData("12:30 ratio 1:2")]
    [InlineData("https://example.org/path?x=1")]
    [InlineData("Budva, TRC20, 100.00")]
    [InlineData("bot of the week")]
    public void Ordinary_text_is_left_alone(string text) => LogRedactor.MaskText(text).ShouldBe(text);

    [Theory]
    [InlineData("password=abc;", "password=**********;")]
    [InlineData("Pwd=abc ", "Pwd=********** ")]
    [InlineData("https://u:p@host/x", "https://u:**********@host/x")]
    [InlineData("Host=db;Password=\"p;ss word\";Database=x", "Host=db;Password=**********;Database=x")]
    [InlineData("Password='p;ss';", "Password=**********;")]
    public void Credentials_in_text_are_masked(string text, string expected) => LogRedactor.MaskText(text).ShouldBe(expected);

    private static void Fail() =>
        throw new InvalidOperationException("Telegram refused " + BotToken + " for " + DbUrl + " with " + Jwt);

    private static string Capture(LogFormat format, Action<ILogger> write)
    {
        var sink = new CapturingSink(format);
        using (var logger = LoggingSetup.Configure(new LoggerConfiguration(), "Debug", sink, alerts: null).CreateLogger())
        {
            write(logger);
        }

        sink.Events.ShouldNotBeEmpty();
        return sink.Text;
    }

    private static void ShouldHoldNoSecret(string text)
    {
        string[] secrets = [BotToken, Jwt, DbPassword, ConnPassword, Iban, Wallet, Recipient, Purpose, InitData, Cookie, ApiKey];
        foreach (var secret in secrets)
        {
            text.ShouldNotContain(secret);
        }

        // The token's tail alone must not survive either (a partial mask is still a leak).
        text.ShouldNotContain(new string('Q', TokenTailChars));
    }
}
