using Exchange.Api.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Exchange.Api.Data;

/// <summary>Registers the database and applies migrations (spec 1.9, D-008).</summary>
public static partial class DataSetup
{
    /// <summary>The command-line verb that applies the migrations and exits (D-008).</summary>
    public const string MigrateCommand = "migrate";

    /// <summary>Adds the PII cipher and the context, both built from the settings.</summary>
    public static IServiceCollection AddExchangeData(this IServiceCollection services)
    {
        services.AddSingleton(provider => PiiCipher.FromBase64(provider.GetRequiredService<AppSettings>().PiiEncryptionKey));
        services.AddDbContext<ExchangeDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<AppSettings>().DatabaseConnectionString.Reveal()));
        return services;
    }

    /// <summary>
    /// Applies every pending migration, logging what it did. EF probes the migrations history with
    /// a query that fails on a new database and logs that failure at ERROR, which would raise an
    /// operator alert for a normal first start; the migration therefore runs on a context of its
    /// own where EF's failed-command event is logged at DEBUG. A migration that really fails still
    /// throws, and the caller logs it (a FATAL start, D-020).
    /// </summary>
    public static async Task MigrateAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        var settings = services.GetRequiredService<AppSettings>();
        var options = new DbContextOptionsBuilder<ExchangeDbContext>()
            .UseNpgsql(settings.DatabaseConnectionString.Reveal())
            .UseLoggerFactory(services.GetRequiredService<ILoggerFactory>())
            .ConfigureWarnings(warnings => warnings.Log((RelationalEventId.CommandError, LogLevel.Debug)))
            .Options;
        await using var context = new ExchangeDbContext(options, services.GetRequiredService<PiiCipher>());
        var logger = services.GetRequiredService<ILogger<ExchangeDbContext>>();
        var pending = (await context.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToList();
        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        var names = string.Join(", ", pending);
        LogMigrated(logger, pending.Count, names);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Database migrated: {Count} migration(s) applied {Migrations}")]
    private static partial void LogMigrated(ILogger logger, int count, string migrations);
}

/// <summary>
/// Demo mode always starts on an empty database, so it applies the migrations itself when the host
/// starts (spec 1.9, D-008). Registered only in demo mode, before every other hosted service.
/// </summary>
public sealed class DemoMigrator(IServiceProvider services) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => DataSetup.MigrateAsync(services, cancellationToken);

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// For <c>dotnet ef</c> only: builds the model with no database and a zero key. Migrations never
/// encrypt anything, and this factory is never used at run time.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ExchangeDbContext>
{
    /// <inheritdoc />
    public ExchangeDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ExchangeDbContext>().UseNpgsql("Host=design-time-only").Options;
        return new ExchangeDbContext(options, new PiiCipher(new byte[PiiCipher.KeyLengthBytes]));
    }
}
