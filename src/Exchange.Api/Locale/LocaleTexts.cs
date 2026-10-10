using System.Text.Json;
using System.Text.RegularExpressions;

namespace Exchange.Api.Locale;

/// <summary>The keys of <c>wwwroot/locale/ru.json</c> the server uses (spec 1.7); each must exist at startup.</summary>
public static class LocaleKeys
{
    /// <summary>The 503 <c>message</c> of spec 1.10.</summary>
    public const string RatesUnavailable = "rates_unavailable";

    /// <summary>Every key the server reads.</summary>
    public static IReadOnlyList<string> All { get; } = [RatesUnavailable];
}

/// <summary>The locale file could not be used. The message names the key or placeholder, never a text.</summary>
public sealed class LocaleException : Exception
{
    /// <summary>Creates the exception.</summary>
    public LocaleException(string message)
        : base(message)
    {
    }

    /// <summary>Required by the exception pattern.</summary>
    public LocaleException()
    {
    }

    /// <summary>Required by the exception pattern.</summary>
    public LocaleException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The user-facing texts, read once from the one locale file the page and the server share
/// (spec 1.7, the RULE-ENGLISH-ONLY exception), with a small <c>{name}</c> placeholder formatter.
/// </summary>
public sealed partial class LocaleTexts
{
    /// <summary>The web root, which holds the file and is copied beside the assembly.</summary>
    public const string WebRootDirectory = "wwwroot";

    /// <summary>The file, relative to the web root.</summary>
    public const string RelativePath = "locale/ru.json";

    private readonly Dictionary<string, string> _texts;

    private LocaleTexts(Dictionary<string, string> texts) => _texts = texts;

    /// <summary>Parses <paramref name="json"/> and checks that every one of <paramref name="requiredKeys"/> is there.</summary>
    /// <exception cref="LocaleException">Malformed, or a required key is missing.</exception>
    public static LocaleTexts Load(string json, IEnumerable<string> requiredKeys)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(requiredKeys);
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowDuplicateProperties = false });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new LocaleException("the locale file is not a JSON object");
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String || property.Value.GetString() is not { Length: > 0 } text)
                {
                    throw new LocaleException("locale key " + property.Name + " is not a non-empty string");
                }

                texts[property.Name] = text;
            }
        }
        catch (JsonException)
        {
            throw new LocaleException("the locale file is not valid JSON");
        }

        var missing = requiredKeys.Where(key => !texts.ContainsKey(key)).ToList();
        return missing.Count == 0
            ? new LocaleTexts(texts)
            : throw new LocaleException("locale keys missing: " + string.Join(", ", missing));
    }

    /// <summary>The text for <paramref name="key"/> with its placeholders filled from <paramref name="values"/>.</summary>
    /// <exception cref="LocaleException">The key is unknown, or the placeholders and the values differ.</exception>
    public string Text(string key, IReadOnlyDictionary<string, string>? values = null)
    {
        if (!_texts.TryGetValue(key, out var text))
        {
            throw new LocaleException("locale key missing: " + key);
        }

        values ??= new Dictionary<string, string>();
        var placeholders = Placeholder().Matches(text).Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        if (!placeholders.SetEquals(values.Keys))
        {
            throw new LocaleException("locale key " + key + " has placeholders that differ from its values");
        }

        // One pass over the template, so a filled value is never read as a placeholder.
        return Placeholder().Replace(text, match => values[match.Groups[1].Value]);
    }

    [GeneratedRegex(@"\{([a-z_]+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();
}
