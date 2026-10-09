using System.Globalization;
using System.Security.Claims;
using System.Text;
using Exchange.Api.Settings;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Exchange.Api.Auth;

/// <summary>A token as <c>POST /api/auth/telegram</c> returns it (spec 1.10).</summary>
/// <param name="AccessToken">The compact JWT.</param>
/// <param name="TokenType">Always <c>bearer</c>.</param>
/// <param name="ExpiresIn">The lifetime, in seconds.</param>
public sealed record TokenOut(string AccessToken, string TokenType, int ExpiresIn);

/// <summary>
/// The app's own access tokens (D-029): HS256 under <c>JWT_SECRET</c>, with exactly the claims
/// <c>sub</c> (the Telegram user id), <c>iat</c>, <c>exp</c> (<c>JWT_TTL_SECONDS</c> later) and
/// <c>aud</c>. Validation accepts that algorithm only, requires every claim, and reads the clock
/// from <see cref="TimeProvider"/> with no skew.
/// </summary>
public sealed class AccessTokens(AppSettings settings, TimeProvider time)
{
    /// <summary>The audience every token carries and every request must present.</summary>
    public const string Audience = "exchange-miniapp-dotnet";

    /// <summary>The <c>token_type</c> of spec 1.10.</summary>
    public const string TokenType = "bearer";

    private static readonly JsonWebTokenHandler Handler = new() { SetDefaultTimesOnTokenCreation = false };

    /// <summary>A token for this Telegram user, valid from now for <c>JWT_TTL_SECONDS</c>.</summary>
    public TokenOut Issue(long telegramId)
    {
        // Whole seconds, as the claims carry them, so exp - iat is exactly the TTL.
        var now = DateTimeOffset.FromUnixTimeSeconds(time.GetUtcNow().ToUnixTimeSeconds()).UtcDateTime;
        var token = Handler.CreateToken(new SecurityTokenDescriptor
        {
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = telegramId.ToString(CultureInfo.InvariantCulture),
            },
            Audience = Audience,
            IssuedAt = now,
            Expires = now.AddSeconds(settings.JwtTtlSeconds),
            SigningCredentials = new SigningCredentials(Key(settings.JwtSecret), SecurityAlgorithms.HmacSha256),
        });
        return new TokenOut(token, TokenType, settings.JwtTtlSeconds);
    }

    /// <summary>How a presented token is checked.</summary>
    public static TokenValidationParameters ValidationParameters(Secret jwtSecret, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(jwtSecret);
        ArgumentNullException.ThrowIfNull(time);
        return new TokenValidationParameters
        {
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            IssuerSigningKey = Key(jwtSecret),
            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            ValidateIssuer = false, // the tokens carry no iss: this app is the only issuer and the key is its own
            ValidAudience = Audience,
            ValidateAudience = true,
            RequireAudience = true,
            RequireExpirationTime = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
            // The library's own check reads the system clock; this one reads the app's.
            LifetimeValidator = (_, expires, _, _) => expires is { } expiry && time.GetUtcNow().UtcDateTime < expiry,
            NameClaimType = JwtRegisteredClaimNames.Sub,
        };
    }

    /// <summary>The Telegram user id a validated principal speaks for, if its subject is plain digits.</summary>
    public static long? TelegramId(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var subject = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        return long.TryParse(subject, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
    }

    private static SymmetricSecurityKey Key(Secret jwtSecret) => new(Encoding.UTF8.GetBytes(jwtSecret.Reveal()));
}
