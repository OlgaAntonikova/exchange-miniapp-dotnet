using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Exchange.Api.Json;

/// <summary>The JSON contract of spec 1.10: snake_case names, money as strings (RULE-MONEY-DECIMAL).</summary>
public static class ApiJson
{
    /// <summary>Applies the contract to a serializer's options.</summary>
    public static void Configure(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        options.NumberHandling = JsonNumberHandling.Strict;
        options.Converters.Add(new MoneyJsonConverter());
    }
}

/// <summary>
/// Money, rates and percentages travel as JSON strings at their own scale (<c>"0.5000"</c>), never
/// as numbers. Reading takes a plain decimal string only; a JSON number is refused (D-007), so no
/// binary floating point ever stands between the client and the value.
/// </summary>
public sealed partial class MoneyJsonConverter : JsonConverter<decimal>
{
    /// <inheritdoc />
    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("money must be a JSON string");
        }

        // Exact or refused: the value must print back as the text it came from, so no digit is
        // rounded away, nothing overflows, and no spelling but the canonical one gets in.
        var text = reader.GetString() ?? string.Empty;
        if (!PlainDecimalPattern().IsMatch(text)
            || !decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            || value.ToString(CultureInfo.InvariantCulture) != text)
        {
            throw new JsonException("money must be a plain decimal string that a decimal holds exactly");
        }

        return value;
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        // decimal keeps its scale, so the invariant text carries the column's scale ("0.5000").
        writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
    }

    // \A and \z: unlike ^ and $, nothing - not even a final newline - may follow the digits.
    [GeneratedRegex("\\A-?[0-9]+(\\.[0-9]+)?\\z", RegexOptions.CultureInvariant)]
    private static partial Regex PlainDecimalPattern();
}
