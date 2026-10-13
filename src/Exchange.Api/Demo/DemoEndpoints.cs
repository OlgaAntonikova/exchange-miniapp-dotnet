using Exchange.Api.Auth;
using Exchange.Api.Data;
using Exchange.Api.Settings;

namespace Exchange.Api.Demo;

/// <summary>
/// <c>POST /api/auth/demo</c> (spec 1.9, INV-15): the browser stands in for Telegram as the one demo
/// client and gets a token as <c>POST /api/auth/telegram</c> would give. Mapped in demo mode only;
/// in every other environment the route does not exist, so it answers 404 and creates nobody.
/// </summary>
public static partial class DemoEndpoints
{
    /// <summary>The demo client's Telegram id. The demo database is its own and thrown away, so no
    /// real account shares it.</summary>
    public const long DemoTelegramId = 100;

    /// <summary>The demo client's first name, which the profile shows.</summary>
    public const string DemoFirstName = "Demo";

    private const string LoggerCategory = "Exchange.Api.Demo";

    /// <summary>Maps the demo sign-in, in demo mode only.</summary>
    public static IEndpointRouteBuilder MapDemoEndpoints(this IEndpointRouteBuilder app, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Environment != AppEnvironment.Demo)
        {
            return app;
        }

        app.MapPost("/api/auth/demo", async (HttpContext http, AccessTokens tokens, ExchangeDbContext db, ILoggerFactory loggers) =>
        {
            await AuthEndpoints.UpsertUserAsync(db, DemoTelegramId, null, DemoFirstName, http.RequestAborted).ConfigureAwait(false);
            var logger = loggers.CreateLogger(LoggerCategory);
            LogSignedIn(logger);
            return Results.Ok(tokens.Issue(DemoTelegramId));
        });

        return app;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Signed in to the demo")]
    private static partial void LogSignedIn(ILogger logger);
}
