using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Exchange.Api.Tests.Repository;

/// <summary>
/// The checks of INV-12 (tracked files) and INV-19 (history), as pure functions over text so that
/// each can be shown to reject a planted secret (RULE-NEGATIVE-TEST). Findings name the pattern
/// and the place, never the matched text.
/// </summary>
internal static partial class SecretScan
{
    /// <summary>The only <c>.env</c>-named file the repository may hold.</summary>
    public const string EnvExampleName = ".env.example";

    private const string EnvFileName = ".env";

    private static readonly string[] SecretKeyMarkers = ["TOKEN", "SECRET", "PASSWORD", "KEY", "DATABASE_URL"];

    /// <summary>The names of the secret patterns found in a text.</summary>
    public static IReadOnlyList<string> Find(string text)
    {
        var found = new List<string>();
        if (BotTokenPattern().IsMatch(text))
        {
            found.Add("telegram bot token");
        }

        if (JwtPattern().IsMatch(text))
        {
            found.Add("JWT");
        }

        if (PemPrivateKeyPattern().IsMatch(text))
        {
            found.Add("PEM private key");
        }

        return found;
    }

    /// <summary>Secret-named keys of <c>.env.example</c> whose value is not empty or a <c>&lt;placeholder&gt;</c>.</summary>
    public static IReadOnlyList<string> EnvExampleProblems(string text) =>
        [.. text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#') && line.Contains('=', StringComparison.Ordinal))
            .Select(line => (Key: line[..line.IndexOf('=', StringComparison.Ordinal)].Trim(), Value: line[(line.IndexOf('=', StringComparison.Ordinal) + 1)..].Trim()))
            .Where(pair => SecretKeyMarkers.Any(marker => pair.Key.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            .Where(pair => pair.Value.Length > 0 && !(pair.Value.StartsWith('<') && pair.Value.EndsWith('>')))
            .Select(pair => pair.Key)];

    /// <summary>
    /// Commits of a <c>git log -p --format=commit:%H</c> text whose added lines carry a secret, as
    /// "<c>hash: pattern</c>".
    /// </summary>
    public static IReadOnlyList<string> HistoryFindings(string log)
    {
        var findings = new List<string>();
        var commit = "(unknown)";
        foreach (var line in log.Split('\n'))
        {
            if (line.StartsWith("commit:", StringComparison.Ordinal))
            {
                commit = line["commit:".Length..].Trim();
            }
            // A file header is exactly "+++ b/<path>" or "+++ /dev/null"; any other '+' line is added
            // content, including content that itself starts with "++".
            else if (line.StartsWith('+') && !line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                findings.AddRange(Find(line).Select(pattern => $"{commit}: {pattern}"));
            }
        }

        return [.. findings.Distinct()];
    }

    /// <summary>Paths (from <c>git log --diff-filter=A --name-only</c>) that are a <c>.env</c> file.</summary>
    public static IReadOnlyList<string> EnvFiles(string names) =>
        [.. names.Split('\n').Select(name => name.Trim()).Where(name => name.Length > 0).Where(IsEnvFile).Distinct()];

    /// <summary>Runs git in the repository and returns its standard output.</summary>
    /// <exception cref="InvalidOperationException">git failed.</exception>
    public static string Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = RepositoryRoot.Path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("git did not start");
        var errors = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0
            ? output
            : throw new InvalidOperationException($"git {string.Join(' ', arguments)} exited {process.ExitCode}: {errors.Result}");
    }

    private static bool IsEnvFile(string path)
    {
        var name = Path.GetFileName(path);
        return name == EnvFileName || (name.StartsWith(EnvFileName + ".", StringComparison.Ordinal) && name != EnvExampleName);
    }

    // A numeric bot id, a colon, and at least 30 token characters (NR-4).
    [GeneratedRegex(@"\b[0-9]{3,}:[A-Za-z0-9_-]{30,}")]
    private static partial Regex BotTokenPattern();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}")]
    private static partial Regex JwtPattern();

    [GeneratedRegex(@"-----BEGIN (?:[A-Z]+ )*PRIVATE KEY-----")]
    private static partial Regex PemPrivateKeyPattern();
}
