using Shouldly;

namespace Exchange.Api.Tests.Repository;

/// <summary>INV-12: no tracked file holds a secret, and <c>.env.example</c> holds placeholders only.
/// Secret-shaped samples are assembled at run time, so this file does not trip its own scan.</summary>
public sealed class NoSecretsInRepoTests
{
    private const int TokenTailChars = 35;

    private static readonly string SampleBotToken = "123456789" + ":" + new string('A', TokenTailChars);
    private static readonly string SampleJwt = "eyJ" + "hbGciOiJIUzI1NiJ9" + "." + "eyJzdWIiOiIxIn0" + "." + "c2lnbmF0dXJl";
    private static readonly string SamplePem = "-----BEGIN " + "RSA PRIVATE KEY-----";

    [Fact]
    public void No_tracked_file_holds_a_secret()
    {
        var files = SecretScan.Git("ls-files", "-z").Split('\0', StringSplitOptions.RemoveEmptyEntries);
        files.ShouldNotBeEmpty();
        var scanned = 0;

        foreach (var file in files)
        {
            var path = Path.Combine(RepositoryRoot.Path, file);
            if (!File.Exists(path))
            {
                continue; // deleted in the working tree, not yet committed
            }

            var bytes = File.ReadAllBytes(path);
            if (Array.IndexOf(bytes, (byte)0) >= 0)
            {
                continue; // binary
            }

            scanned++;
            SecretScan.Find(System.Text.Encoding.UTF8.GetString(bytes)).ShouldBeEmpty(file);
        }

        scanned.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Env_example_holds_placeholders_only()
    {
        var text = File.ReadAllText(Path.Combine(RepositoryRoot.Path, SecretScan.EnvExampleName));

        text.ShouldContain("BOT_TOKEN=");
        SecretScan.EnvExampleProblems(text).ShouldBeEmpty();
    }

    // --- The scan rejects what it is for (RULE-NEGATIVE-TEST) ---------------------------------------

    [Fact]
    public void A_bot_token_is_found() => SecretScan.Find($"token = \"{SampleBotToken}\"").ShouldBe(["telegram bot token"]);

    [Fact]
    public void A_jwt_is_found() => SecretScan.Find($"Bearer {SampleJwt}").ShouldBe(["JWT"]);

    [Theory]
    [InlineData("RSA ")]
    [InlineData("EC ")]
    [InlineData("OPENSSH ")]
    [InlineData("")]
    public void A_pem_private_key_is_found(string kind) =>
        SecretScan.Find("-----BEGIN " + kind + "PRIVATE KEY-----\nMIIB").ShouldBe(["PEM private key"]);

    [Theory]
    [InlineData("BOT_TOKEN=4242:abc")]
    [InlineData("JWT_SECRET=a-real-looking-secret")]
    [InlineData("PII_ENCRYPTION_KEY=AAAA")]
    [InlineData("DATABASE_URL=postgresql://u:p@h/db")]
    [InlineData("DB_PASSWORD=hunter2")]
    public void A_real_value_for_a_secret_key_in_env_example_is_found(string line) =>
        SecretScan.EnvExampleProblems("# header\n" + line + "\nLOG_FORMAT=console\n").Count.ShouldBe(1);

    [Theory]
    [InlineData("BOT_TOKEN=<token>")]
    [InlineData("BOT_TOKEN=")]
    [InlineData("LOG_FORMAT=console")]
    [InlineData("JWT_TTL_SECONDS=3600")]
    public void Placeholders_and_plain_settings_pass(string line) =>
        SecretScan.EnvExampleProblems(line).ShouldBeEmpty();

    [Fact]
    public void Ordinary_text_is_not_a_secret()
    {
        SecretScan.Find("time 12:30, ratio 1:2, url https://example.org/a:b, BEGIN PUBLIC KEY").ShouldBeEmpty();
        SamplePem.ShouldNotBeEmpty();
        SecretScan.Find(SamplePem).ShouldBe(["PEM private key"]);
    }
}
