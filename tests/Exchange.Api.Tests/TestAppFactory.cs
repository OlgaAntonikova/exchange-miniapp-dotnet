using Exchange.Api.Tests.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Exchange.Api.Tests;

/// <summary>Hosts the app in memory with the given environment.</summary>
public class TestAppFactory : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> _environment;

    /// <summary>A factory with the given environment; a key mapped to null is left unset.</summary>
    public TestAppFactory(Dictionary<string, string?> environment) => _environment = environment;

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        foreach (var (key, value) in _environment)
        {
            builder.UseSetting(key, value);
        }
    }
}

/// <summary>The app with the valid production-shaped test environment; a class fixture.</summary>
public sealed class DefaultAppFactory : TestAppFactory
{
    /// <summary>Creates the factory.</summary>
    public DefaultAppFactory()
        : base(TestSettings.Production())
    {
    }
}
