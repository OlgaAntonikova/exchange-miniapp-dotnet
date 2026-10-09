using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Exchange.Api.Settings;

namespace Exchange.Api.Auth;

/// <summary>The Telegram user an <c>initData</c> speaks for.</summary>
/// <param name="Id">Telegram's user id.</param>
/// <param name="FirstName">The user's first name.</param>
/// <param name="Username">The user's username, if any.</param>
public sealed record TelegramUser(long Id, string FirstName, string? Username);

/// <summary>An <c>initData</c> Telegram signed for this bot, recently.</summary>
/// <param name="User">Who it speaks for.</param>
/// <param name="AuthDate">When Telegram issued it, in Unix seconds.</param>
public sealed record ValidatedInitData(TelegramUser User, long AuthDate);

/// <summary>An <c>initData</c> was refused. <see cref="Reason"/> is a short code, safe to log; the
/// message never carries the input or the bot token.</summary>
public sealed class InitDataException : Exception
{
    /// <summary>Creates the exception with its reason code.</summary>
    public InitDataException(string reason)
        : base("initData refused: " + reason) => Reason = reason;

    /// <summary>Required by the exception pattern.</summary>
    public InitDataException()
        : this("unknown")
    {
    }

    /// <summary>Required by the exception pattern; the inner exception must carry no input.</summary>
    public InitDataException(string reason, Exception innerException)
        : base("initData refused: " + reason, innerException) => Reason = reason;

    /// <summary>Why: <c>too_long</c>, <c>malformed</c>, <c>duplicate_field</c>, <c>missing_hash</c>,
    /// <c>bad_signature</c>, <c>missing_auth_date</c>, <c>bad_auth_date</c>, <c>from_the_future</c>,
    /// <c>expired</c>, <c>missing_user</c> or <c>bad_user</c>.</summary>
    public string Reason { get; }
}

/// <summary>
/// Validates Telegram Mini App <c>initData</c> (INV-1, INV-2, RULE-SERVER-AUTHORITY), the method of
/// Telegram's "Validating data received via the Mini App".
/// </summary>
public static class InitDataValidator
{
    /// <summary>Real initData is well under 2 KB; anything far beyond it is refused before parsing.</summary>
    public const int MaxInitDataChars = 8192;

    /// <summary>How far into the future an <c>auth_date</c> may lie before it is forged (INV-2).</summary>
    public const int InitDataMaxClockSkewSeconds = 60;

    /// <summary>The user in <paramref name="raw"/> if Telegram signed it for this bot within <paramref name="maxAgeSeconds"/>.</summary>
    /// <exception cref="InitDataException">Refused, with a reason code.</exception>
    public static ValidatedInitData Validate(string raw, Secret botToken, int maxAgeSeconds, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(botToken);
        if (raw.Length > MaxInitDataChars)
        {
            throw new InitDataException("too_long");
        }

        var fields = Parse(raw);
        if (!fields.Remove(HashField, out var receivedHash) || receivedHash.Length == 0)
        {
            throw new InitDataException("missing_hash");
        }

        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(receivedHash), Encoding.ASCII.GetBytes(ExpectedHash(fields, botToken))))
        {
            throw new InitDataException("bad_signature");
        }

        var authDate = AuthDate(fields);
        var nowSeconds = now.ToUnixTimeSeconds();
        if (authDate > nowSeconds + InitDataMaxClockSkewSeconds)
        {
            throw new InitDataException("from_the_future");
        }

        if (nowSeconds - authDate > maxAgeSeconds)
        {
            throw new InitDataException("expired");
        }

        return new ValidatedInitData(User(fields), authDate);
    }

    private const string HashField = "hash";
    private const string AuthDateField = "auth_date";
    private const string UserField = "user";
    private const char FirstNonAsciiChar = (char)0x80;
    private const int EscapeLengthChars = 3;

    private static readonly byte[] SecretKeyLabel = Encoding.UTF8.GetBytes("WebAppData");

    // A strict application/x-www-form-urlencoded parse: every segment is key=value with a key, every
    // escape is %XX, the decoded bytes are UTF-8, and no key appears twice (which copy was signed
    // would be ambiguous). A failure names no part of the input.
    private static Dictionary<string, string> Parse(string raw)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var segment in raw.Split('&'))
        {
            var separator = segment.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                throw new InitDataException("malformed");
            }

            var key = Decode(segment[..separator]);
            var value = Decode(segment[(separator + 1)..]);
            if (!fields.TryAdd(key, value))
            {
                throw new InitDataException("duplicate_field");
            }
        }

        return fields;
    }

    private static string Decode(string text)
    {
        var bytes = new List<byte>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '+')
            {
                bytes.Add((byte)' ');
            }
            else if (c == '%')
            {
                if (i + EscapeLengthChars > text.Length
                    || !byte.TryParse(text.AsSpan(i + 1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var escaped))
                {
                    throw new InitDataException("malformed");
                }

                bytes.Add(escaped);
                i += 2;
            }
            else if (c < FirstNonAsciiChar)
            {
                bytes.Add((byte)c);
            }
            else
            {
                // Telegram percent-encodes everything beyond ASCII; a raw character is not its initData.
                throw new InitDataException("malformed");
            }
        }

        try
        {
            return StrictUtf8.GetString([.. bytes]);
        }
        catch (DecoderFallbackException)
        {
            // Not chained: the decoder's message can quote the offending bytes of the input.
            throw new InitDataException("malformed");
        }
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // Telegram's method: every field but hash, as key=value lines sorted by key; the secret is
    // HMAC-SHA256 of the bot token under "WebAppData"; the hash is the lower-case hex HMAC of the lines.
    private static string ExpectedHash(Dictionary<string, string> fields, Secret botToken)
    {
        var dataCheckString = string.Join('\n', fields.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value));
        var secret = HMACSHA256.HashData(SecretKeyLabel, Encoding.UTF8.GetBytes(botToken.Reveal()));
        return Convert.ToHexStringLower(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(dataCheckString)));
    }

    private static long AuthDate(Dictionary<string, string> fields)
    {
        if (!fields.TryGetValue(AuthDateField, out var text))
        {
            throw new InitDataException("missing_auth_date");
        }

        return text.Length > 0 && text.All(char.IsAsciiDigit) && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? seconds
            : throw new InitDataException("bad_auth_date");
    }

    private static TelegramUser User(Dictionary<string, string> fields)
    {
        if (!fields.TryGetValue(UserField, out var json))
        {
            throw new InitDataException("missing_user");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            // Not chained: the parser's message can quote the user object.
            throw new InitDataException("bad_user");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var userId) || userId <= 0
                || !root.TryGetProperty("first_name", out var firstName) || firstName.ValueKind != JsonValueKind.String)
            {
                throw new InitDataException("bad_user");
            }

            string? username = null;
            if (root.TryGetProperty("username", out var name) && name.ValueKind != JsonValueKind.Null)
            {
                username = name.ValueKind == JsonValueKind.String ? name.GetString() : throw new InitDataException("bad_user");
            }

            return new TelegramUser(userId, firstName.GetString()!, username);
        }
    }
}
