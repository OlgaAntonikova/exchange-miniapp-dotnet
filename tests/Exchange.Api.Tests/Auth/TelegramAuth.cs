using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Shouldly;

namespace Exchange.Api.Tests.Auth;

/// <summary>
/// What a client of the auth endpoints does, written from Telegram's documentation and RFC 7519,
/// independently of the app: sign an initData as Telegram would, sign in with it, and take a JWT
/// apart or forge one. Tokens are assembled at run time, so no JWT literal is in the repository
/// (INV-12).
/// </summary>
internal static class TelegramAuth
{
    /// <summary>An initData for this user, signed with <paramref name="botToken"/> as Telegram signs it.</summary>
    public static string InitData(string botToken, long telegramId, string firstName, string? username, DateTimeOffset authDate)
    {
        var user = new Dictionary<string, object> { ["id"] = telegramId, ["first_name"] = firstName };
        if (username is not null)
        {
            user["username"] = username;
        }

        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_date"] = authDate.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            ["query_id"] = "AAHdF6IQAAAAAN0XohDhrOrc",
            ["user"] = JsonSerializer.Serialize(user),
        };
        var dataCheckString = string.Join('\n', fields.Select(pair => $"{pair.Key}={pair.Value}"));
        var secret = HMACSHA256.HashData(Encoding.UTF8.GetBytes("WebAppData"), Encoding.UTF8.GetBytes(botToken));
        var hash = Convert.ToHexStringLower(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(dataCheckString)));
        return string.Join('&', fields.Select(pair => pair.Key + "=" + Uri.EscapeDataString(pair.Value))) + "&hash=" + hash;
    }

    /// <summary><c>POST /api/auth/telegram</c> with the given initData.</summary>
    public static Task<HttpResponseMessage> PostAsync(HttpClient client, string initData) =>
        client.PostAsJsonAsync(new Uri("/api/auth/telegram", UriKind.Relative), new Dictionary<string, string> { ["init_data"] = initData }, TestContext.Current.CancellationToken);

    /// <summary>Signs in and returns the access token, failing the test on anything but 200.</summary>
    public static async Task<string> SignInAsync(HttpClient client, string initData)
    {
        using var response = await PostAsync(client, initData);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("access_token").GetString()!;
    }

    /// <summary>A GET with the given Authorization header value (none when null).</summary>
    public static Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string? authorization)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>The header and the payload of a compact JWT.</summary>
    public static (JsonElement Header, JsonElement Payload) Decode(string token)
    {
        var parts = token.Split('.');
        parts.Length.ShouldBe(3);
        return (Parse(parts[0]), Parse(parts[1]));
    }

    /// <summary>
    /// A compact JWT with the given header and payload, signed with HMAC-SHA256 or HMAC-SHA512 under
    /// <paramref name="key"/>, or unsigned (empty signature) when <paramref name="key"/> is null.
    /// </summary>
    public static string Forge(object header, object payload, string? key, bool sha512 = false)
    {
        var signingInput = Base64Url(JsonSerializer.SerializeToUtf8Bytes(header)) + "." + Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload));
        if (key is null)
        {
            return signingInput + ".";
        }

        var keyBytes = Encoding.UTF8.GetBytes(key);
        var input = Encoding.ASCII.GetBytes(signingInput);
        var signature = sha512 ? HMACSHA512.HashData(keyBytes, input) : HMACSHA256.HashData(keyBytes, input);
        return signingInput + "." + Base64Url(signature);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static JsonElement Parse(string part)
    {
        var padded = part.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - (padded.Length % 4)) % 4);
        using var document = JsonDocument.Parse(Convert.FromBase64String(padded));
        return document.RootElement.Clone();
    }
}
