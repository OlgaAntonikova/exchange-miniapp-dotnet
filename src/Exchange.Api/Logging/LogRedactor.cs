using System.Text.RegularExpressions;
using Serilog.Events;
using Serilog.Parsing;

namespace Exchange.Api.Logging;

/// <summary>
/// Removes secrets and personal data from a log event before any sink sees it (RULE-LOGGING,
/// RULE-SECRETS, spec 4, INV-7). Callers never mask anything themselves. Two layers: a value under a
/// secret-sounding name is masked whole, at any depth; every remaining text - string values, the
/// message template, the exception - has secret patterns masked.
/// </summary>
public static partial class LogRedactor
{
    /// <summary>What a redacted value becomes.</summary>
    public const string Mask = Settings.Secret.Mask;

    private static readonly MessageTemplateParser TemplateParser = new();

    /// <summary>A copy of <paramref name="logEvent"/> with every secret masked.</summary>
    public static LogEvent Redact(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        var templateText = MaskText(logEvent.MessageTemplate.Text);
        var template = templateText == logEvent.MessageTemplate.Text ? logEvent.MessageTemplate : TemplateParser.Parse(templateText);
        var properties = logEvent.Properties.Select(property => new LogEventProperty(property.Key, RedactNamed(property.Key, property.Value)));
        var exception = logEvent.Exception is null ? null : new RedactedException(logEvent.Exception);
        return new LogEvent(logEvent.Timestamp, logEvent.Level, exception, template, properties, logEvent.TraceId ?? default, logEvent.SpanId ?? default);
    }

    /// <summary><paramref name="text"/> with every secret pattern masked.</summary>
    public static string MaskText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var masked = BotPathPattern().Replace(text, Mask);
        masked = BotTokenPattern().Replace(masked, Mask);
        masked = JwtPattern().Replace(masked, Mask);
        masked = UrlPasswordPattern().Replace(masked, Mask);
        return KeyValuePasswordPattern().Replace(masked, Mask);
    }

    /// <summary>Whether a property name is on the redaction list (case and underscores ignored).</summary>
    public static bool IsSecretName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return SecretNamePattern().IsMatch(name.Replace("_", string.Empty, StringComparison.Ordinal));
    }

    private static LogEventPropertyValue RedactNamed(string name, LogEventPropertyValue value) =>
        IsSecretName(name) ? new ScalarValue(Mask) : RedactValue(value);

    private static LogEventPropertyValue RedactValue(LogEventPropertyValue value) => value switch
    {
        ScalarValue scalar => RedactScalar(scalar),
        StructureValue structure => new StructureValue(
            structure.Properties.Select(property => new LogEventProperty(property.Name, RedactNamed(property.Name, property.Value))),
            structure.TypeTag),
        SequenceValue sequence => new SequenceValue(sequence.Elements.Select(RedactValue)),
        DictionaryValue dictionary => new DictionaryValue(dictionary.Elements.Select(element =>
            new KeyValuePair<ScalarValue, LogEventPropertyValue>(
                RedactScalar(element.Key),
                element.Key.Value is string key && IsSecretName(key) ? new ScalarValue(Mask) : RedactValue(element.Value)))),
        _ => value,
    };

    // Numbers, booleans, dates and the like cannot hold a secret; anything else (a string, a Uri,
    // an object rendered by its ToString) is checked as text.
    private static ScalarValue RedactScalar(ScalarValue scalar)
    {
        if (scalar.Value is null or bool or char or Enum or DateTime or DateTimeOffset or TimeSpan or Guid
            or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
        {
            return scalar;
        }

        var text = scalar.Value.ToString() ?? string.Empty;
        var masked = MaskText(text);
        return masked == text ? scalar : new ScalarValue(masked);
    }

    // The redaction list of spec 4, matched against the name with case ignored and '_' removed.
    [GeneratedRegex("token|secret|password|authorization|initdata|hash|iban|wallet|recipient|purpose|apikey|cookie|connection|databaseurl|url|uri", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretNamePattern();

    // The token in a Bot API path: /bot<token>/method.
    [GeneratedRegex("(?<=/bot)[^/\\s\"'?#]+", RegexOptions.CultureInvariant)]
    private static partial Regex BotPathPattern();

    // A Telegram bot token anywhere: a numeric bot id, a colon, at least 30 token characters (NR-4).
    [GeneratedRegex("\\b[0-9]{3,}:[A-Za-z0-9_-]{30,}", RegexOptions.CultureInvariant)]
    private static partial Regex BotTokenPattern();

    [GeneratedRegex("\\beyJ[A-Za-z0-9_-]{5,}\\.[A-Za-z0-9_-]{5,}\\.[A-Za-z0-9_-]{5,}", RegexOptions.CultureInvariant)]
    private static partial Regex JwtPattern();

    // The password of scheme://user:password@host.
    [GeneratedRegex("(?<=[A-Za-z][A-Za-z0-9+.-]*://[^:/@\\s]+:)[^@\\s/]+(?=@)", RegexOptions.CultureInvariant)]
    private static partial Regex UrlPasswordPattern();

    // password=... or pwd=... in a connection string or a query, quoted (as a connection-string
    // builder writes a value holding ';') or not.
    [GeneratedRegex("(?<=\\b(?:password|pwd)=)(?:\"[^\"]*\"|'[^']*'|[^;\\s\"&]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeyValuePasswordPattern();
}
