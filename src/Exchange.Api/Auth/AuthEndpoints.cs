using System.Text.Json;
using Exchange.Api.Data;
using Exchange.Api.Settings;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;

namespace Exchange.Api.Auth;

/// <summary>
/// Telegram sign-in and the caller's own profile (spec 1.10, PHASE-2), and the one 401 of INV-21:
/// whatever was wrong - no token, a bad one, a bad initData - the answer is the same body and
/// challenge, and only the log says why (RULE-LOGGING, at WARNING).
/// </summary>
public static partial class AuthEndpoints
{
    /// <summary>The caller, as <c>GET /api/users/me</c> returns them.</summary>
    /// <param name="TelegramId">Telegram's user id.</param>
    /// <param name="Username">Telegram username, if any.</param>
    /// <param name="FirstName">Telegram first name, if any.</param>
    /// <param name="LoyaltyPoints">Loyalty points, 0 to 10.</param>
    public sealed record MeOut(long TelegramId, string? Username, string? FirstName, int LoyaltyPoints);

    /// <summary>A sign-in body is <c>{"init_data": "..."}</c> with an initData of at most
    /// <see cref="InitDataValidator.MaxInitDataChars"/> ASCII characters; a longer body is refused
    /// after reading one byte past this.</summary>
    public const int MaxSignInBodyBytes = 2 * InitDataValidator.MaxInitDataChars;
    private const string InitDataProperty = "init_data";
    private const string LoggerCategory = "Exchange.Api.Auth";

    /// <summary>JWT bearer authentication with the app's tokens and the 401 of INV-21.</summary>
    public static IServiceCollection AddExchangeAuth(this IServiceCollection services)
    {
        services.AddSingleton<AccessTokens>();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme).Configure<AppSettings, TimeProvider>((options, settings, time) =>
        {
            options.MapInboundClaims = false;
            options.TokenValidationParameters = AccessTokens.ValidationParameters(settings.JwtSecret, time);
            options.Events = new JwtBearerEvents
            {
                OnChallenge = async context =>
                {
                    context.HandleResponse();
                    var reason = context.AuthenticateFailure?.GetType().Name
                        ?? (context.Request.Headers.Authorization.Count == 0 ? "no_token" : "no_bearer_token");
                    await RefuseAsync(context.HttpContext, reason).ConfigureAwait(false);
                },
            };
        });
        services.AddAuthorization();
        return services;
    }

    /// <summary>Maps <c>POST /api/auth/telegram</c> and <c>GET /api/users/me</c>.</summary>
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapPost("/auth/telegram", async (HttpContext http, AppSettings settings, AccessTokens tokens, TimeProvider time, ExchangeDbContext db) =>
        {
            var cancellationToken = http.RequestAborted;
            var (raw, bodyProblem) = await InitDataFromBodyAsync(http.Request, cancellationToken).ConfigureAwait(false);
            if (raw is null)
            {
                return Refusal(bodyProblem);
            }

            // Demo mode has no bot, so no initData can be valid there (spec 1.9).
            if (settings.BotToken is null)
            {
                return Refusal("telegram_disabled");
            }

            ValidatedInitData validated;
            try
            {
                validated = InitDataValidator.Validate(raw, settings.BotToken, settings.InitDataMaxAgeSeconds, time.GetUtcNow());
            }
            catch (InitDataException refused)
            {
                return Refusal(refused.Reason);
            }

            // One statement, so two sign-ins at once cannot both insert; the name is refreshed on
            // every sign-in, the loyalty points are kept.
            var user = validated.User;
            await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO users (telegram_id, username, first_name) VALUES ({user.Id}, {user.Username}, {user.FirstName})
                ON CONFLICT (telegram_id) DO UPDATE SET username = excluded.username, first_name = excluded.first_name
                """,
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(tokens.Issue(user.Id));
        });

        api.MapGet("/users/me", async (HttpContext http, ExchangeDbContext db) =>
        {
            var (me, refusal) = await CallerAsync(http, db).ConfigureAwait(false);
            return me is null ? refusal! : Results.Ok(new MeOut(me.TelegramId, me.Username, me.FirstName, me.LoyaltyPoints));
        }).RequireAuthorization();

        return app;
    }

    // The initData of a {"init_data": "<string>"} body, read up to the cap; null with the reason
    // otherwise. Nothing of the body is kept or logged.
    private static async Task<(string? InitData, string Problem)> InitDataFromBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (await ReadCappedAsync(request.Body, MaxSignInBodyBytes, cancellationToken).ConfigureAwait(false) is not { } body)
        {
            return (null, "body_too_long");
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(InitDataProperty, out var initData)
                && initData.ValueKind == JsonValueKind.String
                ? (initData.GetString(), string.Empty)
                : (null, "no_init_data");
        }
        catch (JsonException)
        {
            return (null, "malformed_body");
        }
    }

    /// <summary>
    /// The whole of <paramref name="stream"/> if it holds at most <paramref name="capBytes"/> bytes;
    /// null otherwise, having read no more than one byte past the cap.
    /// </summary>
    public static async Task<ReadOnlyMemory<byte>?> ReadCappedAsync(Stream stream, int capBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegative(capBytes);
        var buffer = new byte[capBytes + 1];
        var length = 0;
        int read;
        while (length < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false)) > 0)
        {
            length += read;
        }

        // Typed null: an untyped one would convert through byte[] into an empty Memory, not "none".
        return length > capBytes ? (ReadOnlyMemory<byte>?)null : buffer.AsMemory(0, length);
    }

    /// <summary>
    /// The user a validated token speaks for, or the 401 to answer instead: a valid signature on a
    /// subject that is not a user id, or on a user who is gone, is still no identity
    /// (RULE-SERVER-AUTHORITY).
    /// </summary>
    public static async Task<(AppUser? User, IResult? Refusal)> CallerAsync(HttpContext http, ExchangeDbContext db)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(db);
        if (AccessTokens.TelegramId(http.User) is not { } telegramId)
        {
            return (null, Refusal("bad_subject"));
        }

        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.TelegramId == telegramId, http.RequestAborted).ConfigureAwait(false);
        return user is null ? (null, Refusal("unknown_user")) : (user, null);
    }

    private static RefusalResult Refusal(string reason) => new(reason);

    private static async Task RefuseAsync(HttpContext http, string reason)
    {
        LogRefused(http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(LoggerCategory), reason);
        http.Response.StatusCode = StatusCodes.Status401Unauthorized;
        http.Response.Headers.WWWAuthenticate = "Bearer";
        await http.Response.WriteAsJsonAsync(new Dictionary<string, string> { ["error"] = "unauthorized" }, http.RequestAborted).ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Authentication refused: {Reason}")]
    private static partial void LogRefused(ILogger logger, string reason);

    private sealed class RefusalResult(string reason) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext) => RefuseAsync(httpContext, reason);
    }
}
