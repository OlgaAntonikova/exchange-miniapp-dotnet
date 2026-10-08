using System.Diagnostics;
using Shouldly;

namespace Exchange.Api.Tests.Settings;

/// <summary>
/// INV-8 and RULE-LOGGING at the process: the real app, started with bad settings, logs a FATAL line
/// naming every bad variable and never a value, and exits non-zero (the PHASE-0 gate, automated).
/// </summary>
public sealed class StartupFailureTests
{
    private const int ExitTimeoutMilliseconds = 60_000;
    private const int ExpectedExitCode = 1;

    private static readonly string[] InheritedVariables =
    [
        "APP_ENV", "BOT_TOKEN", "BOT_WEBHOOK_SECRET", "JWT_SECRET", "PII_ENCRYPTION_KEY", "DATABASE_URL",
        "OPERATOR_CHAT_ID", "OPERATOR_TELEGRAM_IDS", "PGPASSWORD", "PGPASSFILE", "LOG_FORMAT", "LOG_LEVEL",
    ];

    [Fact]
    public async Task Missing_secrets_stop_the_start_with_every_variable_named()
    {
        var (exitCode, output) = await RunAppAsync(new() { ["APP_ENV"] = "production" });

        exitCode.ShouldBe(ExpectedExitCode);
        output.ShouldContain("Refusing to start: invalid settings");
        foreach (var name in new[] { "BOT_TOKEN", "BOT_WEBHOOK_SECRET", "JWT_SECRET", "PII_ENCRYPTION_KEY", "DATABASE_URL", "OPERATOR_CHAT_ID", "OPERATOR_TELEGRAM_IDS" })
        {
            output.ShouldContain($"{name} is missing");
        }
    }

    [Fact]
    public async Task A_refused_value_never_reaches_the_output()
    {
        var environment = TestSettings.Production();
        environment["APP_ENV"] = "production";
        environment["JWT_SECRET"] = "short-jwt-value";
        environment["DATABASE_URL"] = "postgresql://u:" + "db-pass-in-url" + "@db/x?password=y";

        var (exitCode, output) = await RunAppAsync(environment);

        exitCode.ShouldBe(ExpectedExitCode);
        output.ShouldContain("JWT_SECRET is shorter than 32 characters");
        output.ShouldNotContain("short-jwt-value");
        output.ShouldNotContain("db-pass-in-url");
    }

    [Fact]
    public async Task An_unknown_app_env_stops_the_start()
    {
        var (exitCode, output) = await RunAppAsync(new() { ["APP_ENV"] = "test,production" });

        exitCode.ShouldBe(ExpectedExitCode);
        output.ShouldContain("APP_ENV must be development, test, production or demo");
    }

    [Fact]
    public async Task Json_format_writes_the_fatal_line_as_json()
    {
        var (exitCode, output) = await RunAppAsync(new() { ["APP_ENV"] = "production", ["LOG_FORMAT"] = "json" });

        exitCode.ShouldBe(ExpectedExitCode);
        output.ShouldContain("\"@l\":\"Fatal\"");
        output.ShouldContain("JWT_SECRET is missing");
    }

    private static async Task<(int ExitCode, string Output)> RunAppAsync(Dictionary<string, string?> environment)
    {
        var appPath = Path.Combine(AppContext.BaseDirectory, "Exchange.Api.dll");
        File.Exists(appPath).ShouldBeTrue(appPath);
        var workingDirectory = Directory.CreateTempSubdirectory("exchange-start-").FullName;
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory, // no .env can be found here
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(appPath);
        foreach (var name in InheritedVariables)
        {
            start.Environment.Remove(name); // the machine's own variables must not leak into the run
        }

        foreach (var (name, value) in environment)
        {
            start.Environment[name] = value;
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var errors = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(ExitTimeoutMilliseconds);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }

        return (process.ExitCode, await output + await errors);
    }
}
