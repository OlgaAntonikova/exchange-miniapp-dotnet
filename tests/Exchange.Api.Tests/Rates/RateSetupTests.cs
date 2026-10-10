using Exchange.Api.Rates;
using Exchange.Api.Tests.Settings;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Exchange.Api.Tests.Rates;

/// <summary>Spec 1.4 and 1.4a: the app wires both sources, and each client is bounded in time and size.</summary>
public sealed class RateSetupTests
{
    private const int TimeoutSeconds = 7;
    private const long OneMebibyteBytes = 1_048_576;

    [Fact]
    public void The_app_wires_the_ecb_and_the_bank_of_russia()
    {
        using var factory = new TestAppFactory(TestSettings.Production());

        var sources = factory.Services.GetRequiredService<RateSources>();

        sources.Services.Select(service => service.ProviderName).ShouldBe(["cbr", "ecb"]);
    }

    [Fact]
    public void Outgoing_clients_renew_their_pooled_connections_every_fifteen_minutes()
    {
        using var rateClient = RateSetup.CreateClient(TimeoutSeconds);
        using var alertClient = Exchange.Api.Alerts.TelegramAlertSetup.CreateClient();

        HandlerOf(rateClient).PooledConnectionLifetime.ShouldBe(TimeSpan.FromMinutes(15));
        HandlerOf(alertClient).PooledConnectionLifetime.ShouldBe(TimeSpan.FromMinutes(15));
    }

    // HttpClient keeps the handler it was built with in HttpMessageInvoker's private field; a client
    // built without one has a SocketsHttpHandler whose lifetime is infinite, which this test refuses.
    private static System.Net.Http.SocketsHttpHandler HandlerOf(HttpClient client) =>
        (System.Net.Http.SocketsHttpHandler)typeof(HttpMessageInvoker)
            .GetField("_handler", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(client)!;

    [Fact]
    public void A_source_client_has_the_fetch_timeout_and_a_one_mebibyte_body_limit()
    {
        using var client = RateSetup.CreateClient(TimeoutSeconds);

        client.Timeout.ShouldBe(TimeSpan.FromSeconds(TimeoutSeconds));
        client.MaxResponseContentBufferSize.ShouldBe(OneMebibyteBytes);
    }
}
