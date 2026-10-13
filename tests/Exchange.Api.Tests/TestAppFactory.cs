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
    private readonly Action<IServiceCollection>? _services;

    /// <summary>
    /// A factory with the given environment (a key mapped to null is left unset); when
    /// <paramref name="logs"/> is given, the app's log output goes there, still behind redaction;
    /// when <paramref name="time"/> is given, the app's clock is that one; <paramref name="services"/>
    /// replaces services after the app's own registrations.
    /// </summary>
    public TestAppFactory(Dictionary<string, string?> environment, CapturingSink? logs = null, TimeProvider? time = null, Action<IServiceCollection>? services = null)
    {
        _environment = environment;
        _logs = logs;
        _time = time;
        _services = services;
    }

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        foreach (var (key, value) in _environment)
        {
            builder.UseSetting(key, value);
        }

        // No test reaches Telegram: both Bot API clients get a handler that refuses every request
        // locally. The channels keep their real types; a test that needs to see what was sent
        // replaces the channel itself through the services hook.
        builder.ConfigureTestServices(services =>
        {
            services.AddKeyedSingleton(Exchange.Api.Bot.BotSetup.ClientKey, (_, _) => new HttpClient(new NoNetworkHandler()));
            services.AddKeyedSingleton(Exchange.Api.Bot.BotSetup.PollClientKey, (_, _) => new HttpClient(new NoNetworkHandler()));
            services.AddKeyedSingleton(Exchange.Api.Alerts.TelegramAlertSetup.ClientKey, (_, _) => new HttpClient(new NoNetworkHandler()));
        });

        if (_logs is not null)
        {
            builder.ConfigureTestServices(services => services.AddSingleton(new LogOutput(_logs)));
        }

        if (_time is not null)
        {
            builder.ConfigureTestServices(services => services.AddSingleton(_time));
        }

        if (_services is not null)
        {
            builder.ConfigureTestServices(_services);
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

/// <summary>Refuses every request without touching the network.</summary>
internal sealed class NoNetworkHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromException<HttpResponseMessage>(new HttpRequestException("no network in tests"));
}
