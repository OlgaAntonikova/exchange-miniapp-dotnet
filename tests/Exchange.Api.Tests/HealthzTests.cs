using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;

namespace Exchange.Api.Tests;

/// <summary>Spec 1.10: <c>GET /healthz</c> answers 200 <c>{"status": "ok"}</c>.</summary>
public sealed class HealthzTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HealthzTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Healthz_answers_ok()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/healthz", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe("""{"status":"ok"}""");
    }
}
