using System.Net;
using Exchange.Api.Settings;

const int ServePort = 8000; // spec 1.11: the port is fixed, only the address is a setting
const string DotEnvFileName = ".env";

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

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

await app.RunAsync().ConfigureAwait(false);

/// <summary>The entry point; public so that integration tests can host the app.</summary>
public partial class Program;
