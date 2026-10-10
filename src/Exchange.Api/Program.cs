using System.Net;
using Exchange.Api.Alerts;
using Exchange.Api.Auth;
using Exchange.Api.Data;
using Exchange.Api.Json;
using Exchange.Api.Locale;
using Exchange.Api.Logging;
using Exchange.Api.Pricing;
using Exchange.Api.Rates;
using Exchange.Api.Reference;
using Exchange.Api.Settings;
using Serilog;

const int ServePort = 8000; // spec 1.11: the port is fixed, only the address is a setting
const string DotEnvFileName = ".env";
const int ExitFailure = 1;
const int ExitSuccess = 0;

// Logging exists before anything that can fail (RULE-LOGGING): the bootstrap logger reports a
// failed start; the host's logger replaces it once the settings are known.
Log.Logger = LoggingSetup.CreateBootstrapLogger();
AppDomain.CurrentDomain.UnhandledException += (_, crash) =>
    LoggingSetup.LogFatal(crash.ExceptionObject as Exception ?? new InvalidOperationException("non-exception crash"));

try
{
    // APP_ENV comes from the process environment only, once; it decides ASP.NET Core's environment,
    // whether .env is read, and the settings rules (spec 1.11, D-017).
    var environment = AppEnvironments.Parse(Environment.GetEnvironmentVariable(AppEnvironments.VariableName));

    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,
        EnvironmentName = AppEnvironments.HostEnvironmentName(environment),
    });

    // D-017: .env in the working directory, in development only, below the process environment.
    var dotEnvPath = Path.Combine(Directory.GetCurrentDirectory(), DotEnvFileName);
    if (environment == AppEnvironment.Development && File.Exists(dotEnvPath))
    {
        DotEnvFile.AddBelow(builder.Configuration, await File.ReadAllTextAsync(dotEnvPath).ConfigureAwait(false));
    }

    // Settings are read from the final configuration and validated before the app serves anything
    // (INV-8, D-018); a problem stops the start with every variable named.
    builder.Services.AddSingleton(services =>
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        return SettingsLoader.Load(environment, key => configuration[key]);
    });

    // Demo mode migrates its throwaway database when the host starts, before anything else runs.
    if (environment == AppEnvironment.Demo)
    {
        builder.Services.AddHostedService<DemoMigrator>();
    }

    builder.Services.AddExchangeData();
    builder.Services.ConfigureHttpJsonOptions(options => ApiJson.Configure(options.SerializerOptions));
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddExchangeAuth();
    builder.Services.AddExchangeRates();
    builder.Services.AddScoped<Pricer>();

    // The page's own locale file, copied beside the assembly, read once; a missing key fails the
    // start (spec 1.7).
    builder.Services.AddSingleton(_ => LocaleTexts.Load(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, LocaleTexts.WebRootDirectory, LocaleTexts.RelativePath)),
        LocaleKeys.All));
    builder.Services.AddSingleton(services =>
    {
        var settings = services.GetRequiredService<AppSettings>();
        return new LogOutput(LoggingSetup.CreateDefaultOutput(settings.LogFormat, settings.Environment));
    });
    builder.Services.AddSingleton(services => new AlertSink(
        services.GetRequiredService<TimeProvider>(),
        TimeSpan.FromSeconds(services.GetRequiredService<AppSettings>().AlertCooldownSeconds)));
    builder.Services.AddAlertChannel();
    builder.Services.AddHostedService<AlertDispatcher>();
    builder.Services.AddHostedService<StartupRecord>();
    builder.Services.AddSerilog(
        (services, configuration) =>
        {
            var settings = services.GetRequiredService<AppSettings>();
            // Demo mode sends nothing to Telegram; its alerts are dropped (spec 1.9).
            var alerts = settings.Environment == AppEnvironment.Demo ? null : services.GetRequiredService<AlertSink>();
            LoggingSetup.Configure(configuration, settings.LogLevel, services.GetRequiredService<LogOutput>().Sink, alerts);
        },
        preserveStaticLogger: true);

    builder.WebHost.ConfigureKestrel(options =>
    {
        var host = options.ApplicationServices.GetRequiredService<AppSettings>().ServeHost;
        if (IPAddress.TryParse(host, out var address))
        {
            options.Listen(address, ServePort);
        }
        else
        {
            options.ListenLocalhost(ServePort);
        }
    });

    var app = builder.Build();
    _ = app.Services.GetRequiredService<AppSettings>();
    _ = app.Services.GetRequiredService<LocaleTexts>();

    // D-008: outside demo mode, migrations are applied by this command, which then exits.
    if (args is [DataSetup.MigrateCommand])
    {
        await DataSetup.MigrateAsync(app.Services, CancellationToken.None).ConfigureAwait(false);
        return ExitSuccess;
    }

    app.UseRequestLogging();
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
    app.MapReferenceEndpoints();
    app.MapAuthEndpoints();
    app.MapRateEndpoints();

    await app.RunAsync().ConfigureAwait(false);
    return ExitSuccess;
}
catch (Exception error) when (error is not HostAbortedException)
{
    // The top-level handler of the process (RULE-LOGGING): whatever stopped the start is logged.
    LoggingSetup.LogFatal(error);
    return ExitFailure;
}
finally
{
    await Log.CloseAndFlushAsync().ConfigureAwait(false);
}

/// <summary>The entry point; public so that integration tests can host the app.</summary>
public partial class Program;
