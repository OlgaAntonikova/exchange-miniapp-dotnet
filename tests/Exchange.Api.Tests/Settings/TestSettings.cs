namespace Exchange.Api.Tests.Settings;

/// <summary>
/// A valid production-shaped environment for tests. Secret-shaped values are assembled at run time
/// so that no literal in the repository matches a secret pattern (INV-12).
/// </summary>
internal static class TestSettings
{
    private const int TokenTailChars = 35;
    private const int JwtSecretChars = 40;
    private const int PiiKeyBytes = 32;
    private const byte PiiKeyFill = 7;

    public static string BotToken { get; } = "4242" + ":" + new string('A', TokenTailChars);

    public static string JwtSecret { get; } = new('j', JwtSecretChars);

    public static string PiiKey { get; } = Convert.ToBase64String(Enumerable.Repeat(PiiKeyFill, PiiKeyBytes).ToArray());

    public static string DatabaseUrl { get; } = "postgresql://exchange:" + "dbpass" + "@localhost:5432/exchange";

    /// <summary>A fresh, valid environment that a test may change.</summary>
    public static Dictionary<string, string?> Production() => new()
    {
        ["BOT_TOKEN"] = BotToken,
        ["BOT_WEBHOOK_SECRET"] = "webhook_secret-1",
        ["JWT_SECRET"] = JwtSecret,
        ["PII_ENCRYPTION_KEY"] = PiiKey,
        ["DATABASE_URL"] = DatabaseUrl,
        ["OPERATOR_CHAT_ID"] = "-1001234",
        ["OPERATOR_TELEGRAM_IDS"] = "111, 222",
    };

    /// <summary>A fresh, valid demo environment.</summary>
    public static Dictionary<string, string?> Demo() => new()
    {
        ["DATABASE_URL"] = "postgresql://postgres@db:5432/exchange",
    };
}
