using System.Text.Json;
using System.Text.RegularExpressions;
using Serilog;
using Serilog.Context;
using Serilog.Events;

namespace Exchange.Api.Logging;

/// <summary>
/// The request side of spec 4: a <c>request_id</c> on every line of a request, one log line per
/// request, and the top-level handler that logs any unhandled exception and answers the 500 body of
/// spec 1.10.
/// </summary>
public static partial class RequestLogging
{
    /// <summary>The correlation header, read from the request and written to the response.</summary>
    public const string RequestIdHeader = "X-Request-ID";

    /// <summary>The correlation property on every log line of a request.</summary>
    public const string RequestIdProperty = "request_id";

    /// <summary>The longest client-supplied request id that is echoed, in characters.</summary>
    public const int MaxRequestIdChars = 64;

    private const string RequestIdItem = "Exchange.RequestId";
    private const string FailedItem = "Exchange.Failed";
    private const string UnmatchedRoute = "(unmatched)";

    /// <summary>
    /// Adds, outermost first: correlation, the request log line, the top-level exception handler.
    /// Call it before mapping endpoints.
    /// </summary>
    public static IApplicationBuilder UseRequestLogging(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.Use(CorrelateAsync);
        // The app's own logger (registered by AddSerilog); without it the request line would go to
        // the static logger, outside the app's outputs and alerts.
        var appLogger = app.ApplicationServices.GetRequiredService<Serilog.ILogger>();
        app.UseSerilogRequestLogging(options =>
        {
            options.Logger = appLogger;
            // The properties of spec 4 and nothing else: the route template, never the raw path.
            options.MessageTemplate = "HTTP {method} {route} responded {status} in {duration_ms:0} ms";
            options.GetMessageTemplateProperties = (context, _, elapsedMilliseconds, statusCode) =>
            [
                new LogEventProperty("method", new ScalarValue(context.Request.Method)),
                new LogEventProperty("route", new ScalarValue(RouteOf(context))),
                new LogEventProperty("status", new ScalarValue(statusCode)),
                new LogEventProperty("duration_ms", new ScalarValue(elapsedMilliseconds)),
            ];
            // The handler below logs every failure itself and lets no exception through; a failed
            // request's line stays a warning, so one failure raises one alert.
            options.GetLevel = (context, _, _) =>
                context.Items.ContainsKey(FailedItem) || context.Response.StatusCode >= StatusCodes.Status500InternalServerError
                    ? LogEventLevel.Warning
                    : LogEventLevel.Information;
        });
        app.Use(HandleUnhandledAsync);
        return app;
    }

    /// <summary>The request's correlation id.</summary>
    public static string RequestId(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items[RequestIdItem] as string ?? string.Empty;
    }

    private static async Task CorrelateAsync(HttpContext context, RequestDelegate next)
    {
        var offered = context.Request.Headers[RequestIdHeader].ToString();
        var requestId = offered.Length <= MaxRequestIdChars && SafeRequestIdPattern().IsMatch(offered)
            ? offered
            : Guid.NewGuid().ToString("N");
        context.Items[RequestIdItem] = requestId;
        context.Response.Headers[RequestIdHeader] = requestId;
        using (LogContext.PushProperty(RequestIdProperty, requestId))
        {
            await next(context).ConfigureAwait(false);
        }
    }

    private static async Task HandleUnhandledAsync(HttpContext context, RequestDelegate next)
    {
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client went away: not a failure of the app, so a warning and no alert.
            LogAborted(LoggerFor(context), context.Request.Method, RouteOf(context));
            context.Items[FailedItem] = true;
        }
        catch (Exception error)
        {
            LogUnhandled(LoggerFor(context), error, context.Request.Method, RouteOf(context));
            context.Items[FailedItem] = true;
            if (context.Response.HasStarted)
            {
                // Too late for the 500 body. The failure is logged above; ending the connection here,
                // rather than rethrowing, keeps the server from logging it a second time.
                context.Abort();
                return;
            }

            context.Response.Clear();
            context.Response.Headers[RequestIdHeader] = RequestId(context);
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                JsonSerializer.Serialize(new Dictionary<string, string> { ["error"] = "internal_error", ["request_id"] = RequestId(context) }),
                context.RequestAborted).ConfigureAwait(false);
        }
    }

    private static Microsoft.Extensions.Logging.ILogger LoggerFor(HttpContext context) =>
        context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(RequestLogging));

    private static string RouteOf(HttpContext context) =>
        (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? UnmatchedRoute;

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Unhandled exception in {Method} {Route}")]
    private static partial void LogUnhandled(Microsoft.Extensions.Logging.ILogger logger, Exception error, string method, string route);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Request {Method} {Route} aborted by the client")]
    private static partial void LogAborted(Microsoft.Extensions.Logging.ILogger logger, string method, string route);

    // A client-supplied id is echoed only when it is short (MaxRequestIdChars) and plain; anything
    // else is replaced.
    [GeneratedRegex("^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeRequestIdPattern();
}
