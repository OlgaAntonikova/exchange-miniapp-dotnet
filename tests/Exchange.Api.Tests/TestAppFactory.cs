using Exchange.Api.Logging;
using Exchange.Api.Tests.Logging;
using Exchange.Api.Tests.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Exchange.Api.Tests;

/// <summary>Hosts the app in memory with the given environment.</summary>
public class TestAppFactory : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> _environment;
    private readonly CapturingSink? _logs;
    private readonly TimeProvider? _time;

    /// <summary>
    /// A factory with the given environment (a key mapped to null is left unset); when
    /// <paramref name="logs"/> is given, the app's log output goes there, still behind redaction;
    /// when <paramref name="time"/> is given, the app's clock is that one.
    /// </summary>
    public TestAppFactory(Dictionary<string, string?> environment, CapturingSink? logs = null, TimeProvider? time = null)
    {
        _environment = environment;
        _logs = logs;
        _time = time;
    }

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        foreach (var (key, value) in _environment)
        {
            builder.UseSetting(key, value);
        }

        if (_logs is not null)
        {
            builder.ConfigureTestServices(services => services.AddSingleton(new LogOutput(_logs)));
        }

        if (_time is not null)
        {
            builder.ConfigureTestServices(services => services.AddSingleton(_time));
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
