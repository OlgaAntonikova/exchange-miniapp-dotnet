using Microsoft.Extensions.Configuration.Memory;

namespace Exchange.Api.Settings;

/// <summary>
/// Reads a <c>.env</c> file: <c>KEY=VALUE</c> lines, <c>#</c> comments and blank lines ignored, one
/// pair of matching surrounding quotes removed. Used in development only (D-017).
/// </summary>
public static class DotEnvFile
{
    /// <summary>The pairs of a <c>.env</c> text; a later key wins. A malformed line is reported by number.</summary>
    /// <exception cref="FormatException">A line is neither blank, a comment nor <c>KEY=VALUE</c>.</exception>
    public static IReadOnlyDictionary<string, string> Parse(string text)
    {
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf('=', StringComparison.Ordinal);
            var key = separator > 0 ? line[..separator].Trim() : string.Empty;
            if (key.Length == 0 || key.Any(char.IsWhiteSpace))
            {
                // The line number only: the line may hold a secret.
                throw new FormatException($".env line {index + 1} is not KEY=VALUE");
            }

            pairs[key] = Unquote(line[(separator + 1)..].Trim());
        }

        return pairs;
    }

    /// <summary>
    /// Adds the pairs of a <c>.env</c> text below every existing configuration source, so the process
    /// environment wins (D-017). <c>APP_ENV</c> is refused: it is read from the process only.
    /// </summary>
    /// <exception cref="SettingsException">The text sets <c>APP_ENV</c>.</exception>
    public static void AddBelow(IConfigurationBuilder configuration, string text)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var pairs = Parse(text);
        if (pairs.ContainsKey(AppEnvironments.VariableName))
        {
            throw new SettingsException($"{AppEnvironments.VariableName} is read from the process environment only; remove it from .env");
        }

        configuration.Sources.Insert(0, new MemoryConfigurationSource { InitialData = pairs! });
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && (value[0] is '"' or '\'') && value[^1] == value[0] ? value[1..^1] : value;
}
