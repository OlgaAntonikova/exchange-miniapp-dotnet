using System.Diagnostics;
using System.Security.Cryptography;
using Exchange.Api.Data;
using Exchange.Api.Settings;
using Exchange.Api.Tests.Settings;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace Exchange.Api.Tests.Data;

/// <summary>D-008 and the PHASE-1 gate: <c>Exchange.Api migrate</c> applies the migrations to an empty database and exits.</summary>
public sealed class MigrateCommandTests(PostgresFixture database)
{
    private const int ExitTimeoutMilliseconds = 120_000;

    private static readonly string[] InheritedVariables =
    [
        "APP_ENV", "BOT_TOKEN", "BOT_WEBHOOK_SECRET", "JWT_SECRET", "PII_ENCRYPTION_KEY", "DATABASE_URL",
        "OPERATOR_CHAT_ID", "OPERATOR_TELEGRAM_IDS", "PGPASSWORD", "PGPASSFILE", "LOG_FORMAT", "LOG_LEVEL",
    ];

    [Fact]
    public async Task Migrate_creates_and_seeds_an_empty_database_then_finds_nothing_to_do()
    {
        var url = await database.CreateDatabaseUrlAsync();
        var environment = TestSettings.Production();
        environment["APP_ENV"] = "production";
        environment["DATABASE_URL"] = url;

        var first = await RunAsync(environment, DataSetup.MigrateCommand);
        var second = await RunAsync(environment, DataSetup.MigrateCommand);

        first.ExitCode.ShouldBe(0, first.Output);
        first.Output.ShouldContain("Database migrated: 1 migration(s) applied");
        second.ExitCode.ShouldBe(0, second.Output);
        second.Output.ShouldContain("Database migrated: 0 migration(s) applied");
        // A first migration is not a failure: nothing may be logged at ERROR, which would alert the operator.
        first.Output.ShouldNotContain("[ERR]");
        first.Output.ShouldNotContain("[FTL]");
        second.Output.ShouldNotContain("[ERR]");
        await using var db = PostgresFixture.CreateContext(
            DatabaseUrl.Convert(url).ConnectionString!.Reveal(), new PiiCipher(RandomNumberGenerator.GetBytes(PiiCipher.KeyLengthBytes)));
        (await db.Cities.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(6);
    }

    [Fact]
    public async Task A_failing_migration_command_is_not_hidden_by_the_quiet_log_level()
    {
        // A real SQL failure inside the migration - the event D-024 logs at DEBUG - must still stop
        // the command with a FATAL line: the table the migration creates already exists.
        var url = await database.CreateDatabaseUrlAsync();
        await using (var connection = new NpgsqlConnection(DatabaseUrl.Convert(url).ConnectionString!.Reveal()))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand("CREATE TABLE cities (id integer)", connection);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var environment = TestSettings.Production();
        environment["APP_ENV"] = "production";
        environment["DATABASE_URL"] = url;

        var run = await RunAsync(environment, DataSetup.MigrateCommand);

        run.ExitCode.ShouldBe(1, run.Output);
        run.Output.ShouldContain("[FTL]");
        run.Output.ShouldContain("42P07"); // PostgreSQL: duplicate table
        run.Output.ShouldNotContain("Database migrated");
    }

    [Fact]
    public async Task A_migration_that_really_fails_stops_with_a_fatal_line()
    {
        // Nothing listens on port 1: the connection is refused. (A missing database would not do:
        // EF creates one.)
        var environment = TestSettings.Production();
        environment["APP_ENV"] = "production";
        environment["DATABASE_URL"] = "postgresql://postgres@127.0.0.1:1/exchange";

        var run = await RunAsync(environment, DataSetup.MigrateCommand);

        run.ExitCode.ShouldBe(1, run.Output);
        run.Output.ShouldContain("[FTL]");
        run.Output.ShouldNotContain("Database migrated");
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(Dictionary<string, string?> environment, string argument)
    {
        var workingDirectory = Directory.CreateTempSubdirectory("exchange-migrate-").FullName;
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Exchange.Api.dll"));
        start.ArgumentList.Add(argument);
        foreach (var name in InheritedVariables)
        {
            start.Environment.Remove(name);
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
