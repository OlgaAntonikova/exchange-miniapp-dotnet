using Exchange.Api.Settings;
using Serilog;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Compact;
using Serilog.Formatting.Display;

namespace Exchange.Api.Logging;

/// <summary>
/// The one place logging is configured (RULE-LOGGING, spec 4), and the one file allowed to write to
/// the console (INV-20). Every output sits behind <see cref="RedactingSink"/>.
/// </summary>
public static class LoggingSetup
{
    /// <summary>The rolling development log file.</summary>
    public const string DevelopmentLogPath = "logs/app.log";

    /// <summary>Size of one development log file before it rolls, in bytes (10 MB).</summary>
    public const long DevelopmentLogFileSizeBytes = 10L * 1024 * 1024;

    /// <summary>Development log files kept.</summary>
    public const int DevelopmentLogFileCount = 5;

    private const string ConsoleTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj} {Properties:j}{NewLine}{Exception}";

    /// <summary>
    /// Categories raised to <c>Warning</c>: they log URLs, paths or SQL parameters, and the app's own
    /// request log replaces them (spec 4).
    /// </summary>
    public static readonly IReadOnlyList<string> QuietCategories =
        ["System.Net.Http.HttpClient", "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore"];

    /// <summary>
    /// Properties ASP.NET Core's request scope puts on every line of a request: the raw path, which
    /// spec 4 keeps out of the logs, and the server's own request id, which <c>request_id</c> replaces.
    /// </summary>
    public static readonly IReadOnlyList<string> DroppedProperties = ["RequestPath", "RequestId"];

    /// <summary>
    /// ASP.NET Core's data protection, raised to <c>Error</c>: the authentication setup registers it,
    /// but nothing the app keeps is protected with it (the tokens are JWTs under <c>JWT_SECRET</c>),
    /// so its warnings about an unpersisted, unencrypted key ring in a container are noise (D-044).
    /// </summary>
    public const string DataProtectionCategory = "Microsoft.AspNetCore.DataProtection";

    /// <summary>The formatter for a log format: JSON lines or the console template.</summary>
    public static ITextFormatter CreateFormatter(LogFormat format) =>
        format == LogFormat.Json
            ? new CompactJsonFormatter()
            : new MessageTemplateTextFormatter(ConsoleTemplate, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Configures <paramref name="configuration"/>: level, quiet categories, the log context, and
    /// every output - <paramref name="output"/> and, unless null, <paramref name="alerts"/> - behind
    /// one <see cref="RedactingSink"/>.
    /// </summary>
    public static LoggerConfiguration Configure(LoggerConfiguration configuration, string level, ILogEventSink output, ILogEventSink? alerts)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuration
            .MinimumLevel.Is(ParseLevel(level))
            .Enrich.FromLogContext()
            .Enrich.With(new DropPropertiesEnricher(DroppedProperties));
        foreach (var category in QuietCategories)
        {
            configuration.MinimumLevel.Override(category, LogEventLevel.Warning);
        }

        configuration.MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information);
        configuration.MinimumLevel.Override(DataProtectionCategory, LogEventLevel.Error);
        ILogEventSink[] outputs = alerts is null ? [output] : [output, alerts];
        return configuration.WriteTo.Sink(new RedactingSink(outputs));
    }

    /// <summary>The default output: stdout, plus the rolling file in development (spec 4).</summary>
    public static ILogEventSink CreateDefaultOutput(LogFormat format, AppEnvironment environment)
    {
        var outputs = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Console(CreateFormatter(format));
        if (environment == AppEnvironment.Development)
        {
            outputs.WriteTo.File(
                CreateFormatter(format),
                DevelopmentLogPath,
                fileSizeLimitBytes: DevelopmentLogFileSizeBytes,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: DevelopmentLogFileCount);
        }

        return outputs.CreateLogger();
    }

    /// <summary>
    /// The logger used until the host's is built, so that a failure to start is logged too. It
    /// reads <c>LOG_FORMAT</c> leniently: settings validation reports a bad value later.
    /// </summary>
    public static Logger CreateBootstrapLogger()
    {
        // Logging must never fail silently: Serilog's own errors go to stderr (spec 4, Alerting).
        SelfLog.Enable(Console.Error);
        var format = string.Equals(Environment.GetEnvironmentVariable("LOG_FORMAT"), "json", StringComparison.OrdinalIgnoreCase)
            ? LogFormat.Json
            : LogFormat.Console;
        return Configure(new LoggerConfiguration(), "Information", CreateDefaultOutput(format, AppEnvironment.Production), alerts: null)
            .CreateLogger();
    }

    /// <summary>
    /// Logs why the process could not start or crashed, at <c>FATAL</c>, through the bootstrap
    /// logger: for refused settings, each problem by variable name (never a value, INV-8).
    /// </summary>
    public static void LogFatal(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (error is SettingsException refused)
        {
            Log.Fatal("Refusing to start: invalid settings: {Problems}", refused.Problems);
        }
        else
        {
            Log.Fatal(error, "Process stopped by an unhandled exception");
        }
    }

    private sealed class DropPropertiesEnricher(IReadOnlyList<string> names) : ILogEventEnricher
    {
        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
        {
            foreach (var name in names)
            {
                logEvent.RemovePropertyIfPresent(name);
            }
        }
    }

    private static LogEventLevel ParseLevel(string level) => level switch
    {
        "Debug" => LogEventLevel.Debug,
        "Warning" => LogEventLevel.Warning,
        "Error" => LogEventLevel.Error,
        _ => LogEventLevel.Information,
    };
}
