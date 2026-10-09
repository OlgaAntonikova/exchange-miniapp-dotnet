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
    public void A_source_client_has_the_fetch_timeout_and_a_one_mebibyte_body_limit()
    {
        using var client = RateSetup.CreateClient(TimeoutSeconds);

        client.Timeout.ShouldBe(TimeSpan.FromSeconds(TimeoutSeconds));
        client.MaxResponseContentBufferSize.ShouldBe(OneMebibyteBytes);
    }
}
