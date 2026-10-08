using Exchange.Api.Data;
using Exchange.Api.Settings;
using Exchange.Api.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(PostgresFixture))]

namespace Exchange.Api.Tests.Data;

/// <summary>
/// One real PostgreSQL for the test run (spec 6: no mocked database), started by Testcontainers;
/// every test gets an empty database of its own. The server trusts connections, as the demo's does
/// (spec 1.9), so a database URL without a password works.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    /// <summary>The image, the same exact tag as the demo's compose file (RULE-DEPENDENCIES).</summary>
    public const string Image = "postgres:16.15-alpine";

    private const string User = "postgres";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(Image)
        .WithUsername(User)
        .WithEnvironment("POSTGRES_HOST_AUTH_METHOD", "trust")
        .Build();

    private int _databases;

    /// <inheritdoc />
    public ValueTask InitializeAsync() => new(_container.StartAsync());

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _container.DisposeAsync();

    /// <summary>Creates an empty database and returns its password-less <c>DATABASE_URL</c>.</summary>
    public async Task<string> CreateDatabaseUrlAsync()
    {
        var name = $"t{Interlocked.Increment(ref _databases)}_{Guid.NewGuid():N}"; // well under 63 characters
        await using (var connection = new NpgsqlConnection(_container.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
            await command.ExecuteNonQueryAsync();
        }

        return $"postgresql://{User}@{_container.Hostname}:{_container.GetMappedPublicPort(PostgreSqlBuilder.PostgreSqlPort)}/{name}";
    }

    /// <summary>A context on a new database with all migrations applied.</summary>
    public async Task<(ExchangeDbContext Context, string ConnectionString)> CreateMigratedAsync(PiiCipher cipher)
    {
        var connectionString = DatabaseUrl.Convert(await CreateDatabaseUrlAsync()).ConnectionString!.Reveal();
        var context = CreateContext(connectionString, cipher);
        await context.Database.MigrateAsync();
        return (context, connectionString);
    }

    /// <summary>A context on an existing database.</summary>
    public static ExchangeDbContext CreateContext(string connectionString, PiiCipher cipher) =>
        new(new DbContextOptionsBuilder<ExchangeDbContext>().UseNpgsql(connectionString).Options, cipher);
}
