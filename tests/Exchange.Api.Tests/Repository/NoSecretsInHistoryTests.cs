using Shouldly;

namespace Exchange.Api.Tests.Repository;

/// <summary>INV-19: no commit in the history adds a secret or a <c>.env</c> file. The repository
/// becomes public with its history, so a secret committed once and deleted later still leaks.</summary>
public sealed class NoSecretsInHistoryTests
{
    private const int TokenTailChars = 35;

    [Fact]
    public void The_history_is_complete()
    {
        // A shallow clone hides old commits; the check would pass without having looked (CI fetches
        // the full history for this reason).
        SecretScan.Git("rev-parse", "--is-shallow-repository").Trim().ShouldBe("false");
    }

    [Fact]
    public void No_commit_adds_a_secret()
    {
        // -m shows merge commits' changes too; --no-renames shows a renamed file's content as added.
        var log = SecretScan.Git("log", "--all", "-p", "-m", "--no-renames", "--no-color", "--no-ext-diff", "--format=commit:%H");

        log.ShouldContain("commit:");
        SecretScan.HistoryFindings(log).ShouldBeEmpty();
    }

    [Fact]
    public void No_commit_adds_an_env_file()
    {
        // --no-renames: a file renamed to .env counts as added under its new name.
        var names = SecretScan.Git("log", "--all", "-m", "--no-renames", "--diff-filter=A", "--name-only", "--format=");

        names.ShouldContain("docs/SPEC.md"); // the scan saw real history
        SecretScan.EnvFiles(names).ShouldBeEmpty();
    }

    // --- The scan rejects what it is for (RULE-NEGATIVE-TEST) ---------------------------------------

    [Fact]
    public void A_secret_added_in_an_old_commit_is_found_by_commit()
    {
        var token = "987654321" + ":" + new string('B', TokenTailChars);
        var log = string.Join('\n',
            "commit:aaa111",
            "+++ b/src/Clean.cs",
            "+var clean = 1;",
            "commit:bbb222",
            "+++ b/src/Leak.cs",
            $"+var token = \"{token}\";",
            "commit:ccc333",
            $"-var token = \"{token}\";");

        SecretScan.HistoryFindings(log).ShouldBe(["bbb222: telegram bot token"]);
    }

    [Fact]
    public void Added_content_that_starts_with_plus_signs_is_scanned()
    {
        var token = "987654321" + ":" + new string('C', TokenTailChars);
        var log = string.Join('\n', "commit:eee555", "+++ b/notes.md", $"+++{token}");

        SecretScan.HistoryFindings(log).ShouldBe(["eee555: telegram bot token"]);
    }

    [Fact]
    public void A_removed_line_and_a_file_header_are_not_findings()
    {
        var jwt = "eyJ" + "abcdefgh" + "." + "ijklmnop" + "." + "qrstuvwx";
        var log = string.Join('\n', "commit:ddd444", $"-{jwt}", $"+++ b/{jwt}.txt");

        SecretScan.HistoryFindings(log).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(".env")]
    [InlineData("src/Exchange.Api/.env")]
    [InlineData(".env.local")]
    [InlineData(".env.production")]
    public void An_added_env_file_is_found(string path) =>
        SecretScan.EnvFiles("README.md\n" + path + "\n.env.example\n").ShouldBe([path]);

    [Theory]
    [InlineData(".env.example")]
    [InlineData("docs/env.md")]
    [InlineData("my.env")]
    public void Other_names_pass(string path) => SecretScan.EnvFiles(path).ShouldBeEmpty();
}
