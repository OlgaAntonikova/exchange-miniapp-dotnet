using System.Data.Common;

namespace Exchange.Api.Settings;

/// <summary>
/// Converts <c>DATABASE_URL</c> (<c>postgresql://user:password@host:port/database</c>) into an
/// Npgsql connection string, in this one place (D-010). Error messages name the problem, never
/// the value.
/// </summary>
public static class DatabaseUrl
{
    /// <summary>The PostgreSQL port when the URL names none.</summary>
    public const int DefaultPort = 5432;

    private const int NoPort = -1;
    private const string SslModeParameter = "sslmode";

    private static readonly string[] Schemes = ["postgresql", "postgres"];

    private static readonly Dictionary<string, string> SslModes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["disable"] = "Disable",
        ["prefer"] = "Prefer",
        ["require"] = "Require",
        ["verify-ca"] = "VerifyCA",
        ["verify-full"] = "VerifyFull",
    };

    /// <summary>The outcome of a conversion.</summary>
    /// <param name="ConnectionString">The Npgsql connection string, as a <see cref="Secret"/> since it
    /// may hold the password; null when the URL was refused.</param>
    /// <param name="HasPassword">Whether the URL carries a password (demo mode refuses one, spec 1.9).</param>
    /// <param name="Problem">Why the URL was refused, without its value; null when accepted.</param>
    public sealed record Result(Secret? ConnectionString, bool HasPassword, string? Problem);

    /// <summary>Converts a URL; never throws on bad input.</summary>
    public static Result Convert(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !Schemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
        {
            return Refused("is not a postgresql:// URL");
        }

        if (string.IsNullOrEmpty(uri.Host))
        {
            return Refused("names no host");
        }

        var database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
        if (database.Length == 0 || database.Contains('/', StringComparison.Ordinal))
        {
            return Refused("names no single database");
        }

        if (uri.Fragment.Length > 0)
        {
            return Refused("has a fragment");
        }

        var builder = new DbConnectionStringBuilder
        {
            // DnsSafeHost drops the brackets of an IPv6 literal, which Npgsql does not take.
            ["Host"] = uri.DnsSafeHost,
            ["Port"] = uri.Port == NoPort ? DefaultPort : uri.Port,
            ["Database"] = database,

            // No Kerberos: the app signs in with a password or trust (D-044), and on Linux probing
            // for GSS encryption makes Npgsql load a Kerberos library the image does not carry.
            ["GSS Encryption Mode"] = "Disable",
        };

        var hasPassword = false;
        var sslModeSeen = false;
        if (uri.UserInfo.Length > 0)
        {
            var separator = uri.UserInfo.IndexOf(':', StringComparison.Ordinal);
            var user = separator < 0 ? uri.UserInfo : uri.UserInfo[..separator];
            builder["Username"] = Uri.UnescapeDataString(user);
            if (separator >= 0)
            {
                var password = Uri.UnescapeDataString(uri.UserInfo[(separator + 1)..]);
                if (password.Length > 0)
                {
                    builder["Password"] = password;
                    hasPassword = true;
                }
            }
        }

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var name = Uri.UnescapeDataString(parts[0]);
            if (!name.Equals(SslModeParameter, StringComparison.OrdinalIgnoreCase))
            {
                // Only sslmode is understood; anything else (a password among them) is refused.
                return Refused("has an unsupported query parameter; only sslmode is allowed");
            }

            if (parts.Length != 2 || !SslModes.TryGetValue(Uri.UnescapeDataString(parts[1]), out var mode))
            {
                return Refused("has an unknown sslmode");
            }

            if (sslModeSeen)
            {
                return Refused("names sslmode twice");
            }

            sslModeSeen = true;

            builder["SSL Mode"] = mode;
        }

        return new Result(new Secret(builder.ConnectionString), hasPassword, null);
    }

    private static Result Refused(string problem) => new(null, false, problem);
}
