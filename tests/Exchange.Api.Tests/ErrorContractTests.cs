using System.Net;
using System.Text.Json;
using Exchange.Api.Logging;
using Exchange.Api.Tests.Logging;
using Shouldly;

namespace Exchange.Api.Tests;

/// <summary>
/// INV-21: every error body of spec 1.10, exactly. PHASE-0 brings the 500 body; the 401, 404, 409,
/// 422 and 503 bodies join this class with the endpoints that return them.
/// </summary>
public sealed class ErrorContractTests
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
}
