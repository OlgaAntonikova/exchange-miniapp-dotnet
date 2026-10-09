using System.Net;
using System.Text.Json;
using Exchange.Api.Logging;
using Exchange.Api.Tests.Auth;
using Exchange.Api.Tests.Data;
using Exchange.Api.Tests.Logging;
using Exchange.Api.Tests.Rates;
using Exchange.Api.Tests.Settings;
using Shouldly;

namespace Exchange.Api.Tests;

/// <summary>
/// INV-21: every error body of spec 1.10, exactly. PHASE-0 brings the 500 body, PHASE-2 the 401 and
/// PHASE-3 the 422 and 503 of <c>POST /api/rate</c>; the 404 and 409 bodies join this class with
/// the order endpoints.
/// </summary>
public sealed class ErrorContractTests(PostgresFixture database)
{
    [Fact]
    public async Task An_unhandled_failure_answers_500_with_the_error_kind_and_the_request_id_only()
    {
        await using var harness = await RequestPipelineHarness.StartAsync();

        using var response = await harness.Client.GetAsync(new Uri("/boom", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        var requestId = response.Headers.GetValues(RequestLogging.RequestIdHeader).ShouldHaveSingleItem();
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldBe(JsonSerializer.Serialize(new Dictionary<string, string> { ["error"] = "internal_error", ["request_id"] = requestId }));
        body.ShouldNotContain("boom");
        body.ShouldNotContain(RequestPipelineHarness.BotToken);
    }

    [Fact]
    public async Task A_route_that_needs_a_token_answers_401_without_one()
    {
        using var factory = new TestAppFactory(TestSettings.Production());
        using var client = factory.CreateClient();

        using var response = await TelegramAuth.GetAsync(client, "/api/users/me", null);

        await AuthApiTests.ShouldBeUnauthorizedAsync(response);
    }

    [Fact]
    public async Task A_bad_init_data_answers_401()
    {
        using var factory = new TestAppFactory(TestSettings.Production());
        using var client = factory.CreateClient();
        var altered = TelegramAuth.InitData(TestSettings.BotToken, 1, "Ann", "ann", DateTimeOffset.UtcNow).Replace("Ann", "Bob", StringComparison.Ordinal);

        using var response = await TelegramAuth.PostAsync(client, altered);

        await AuthApiTests.ShouldBeUnauthorizedAsync(response);
    }

    [Fact]
    public async Task A_quote_breaking_the_request_rules_answers_422_with_the_kind_and_the_fields()
    {
        using var factory = new TestAppFactory(await EnvironmentAsync(), services: FakeSources.Replace());
        using var client = factory.CreateClient();

        using var response = await RateApiTests.SendAsync(client, """{"give": "USD_CASH", "receive": "EUR_WIRE", "amount_give": 100}""");

        await RateApiTests.ShouldBe422Async(response, "invalid_request", ["body.amount_give"]);
    }

    [Fact]
    public async Task A_quote_without_a_fresh_rate_answers_503_with_the_kind_and_the_localized_message()
    {
        using var factory = new TestAppFactory(await EnvironmentAsync(), services: FakeSources.Replace(ecbWorks: false));
        using var client = factory.CreateClient();

        using var response = await RateApiTests.SendAsync(client, """{"give": "USD_CASH", "receive": "EUR_WIRE", "amount_give": "100"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        body.RootElement.EnumerateObject().Select(p => p.Name).ShouldBe(["error", "message"]);
        body.RootElement.GetProperty("error").GetString().ShouldBe("rates_unavailable");
        body.RootElement.GetProperty("message").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    private async Task<Dictionary<string, string?>> EnvironmentAsync()
    {
        var environment = TestSettings.Production();
        environment["DATABASE_URL"] = await database.CreateMigratedDatabaseUrlAsync();
        return environment;
    }
}
