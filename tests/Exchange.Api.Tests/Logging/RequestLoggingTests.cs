using System.Net;
using Exchange.Api.Logging;
using Serilog.Events;
using Shouldly;

namespace Exchange.Api.Tests.Logging;

/// <summary>
/// Spec 4 for the request pipeline the app uses (<see cref="RequestLogging.UseRequestLogging"/>):
/// correlation, the request line, the top-level handler, and the ERROR reaching the alert queue.
/// The 500 body itself is <see cref="ErrorContractTests"/> (INV-21).
/// </summary>
public sealed class RequestLoggingTests
{
    [Fact]
    public async Task An_unhandled_exception_is_logged_once_at_error_with_its_stack()
    {
        await using var harness = await RequestPipelineHarness.StartAsync();

        using var response = await harness.Client.GetAsync(new Uri("/boom", UriKind.Relative), TestContext.Current.CancellationToken);

        var requestId = response.Headers.GetValues(RequestLogging.RequestIdHeader).ShouldHaveSingleItem();
        var error = harness.Logs.Events.Where(e => e.Level >= LogEventLevel.Error).ShouldHaveSingleItem();
        error.RenderMessage().ShouldBe("Unhandled exception in \"GET\" \"/boom\"");
        error.Exception.ShouldNotBeNull().ToString().ShouldContain(nameof(InvalidOperationException));
        error.Exception!.ToString().ShouldContain(" at ");
        RequestIdOf(error).ShouldBe(requestId);
        harness.Logs.Text.ShouldNotContain(RequestPipelineHarness.BotToken);

        var requestLine = RequestLine(harness);
        requestLine.Level.ShouldBe(LogEventLevel.Warning);
        requestLine.Properties["status"].ToString().ShouldBe("500");
    }

    [Fact]
    public async Task A_failure_after_the_response_started_is_logged_once_and_the_connection_ended()
    {
        // Real Kestrel: it logs an exception that escapes the app, so a second ERROR would show here.
        await using var harness = await RequestPipelineHarness.StartAsync(kestrel: true);

        try
        {
            using var response = await harness.Client.GetAsync(new Uri("/late-boom", UriKind.Relative), TestContext.Current.CancellationToken);
            _ = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        }
        catch (HttpRequestException)
        {
            // The connection is aborted: the client may see that as an error. Either way is fine.
        }
        catch (IOException)
        {
            // Same: an aborted body read.
        }

        // Stopping waits for the request and for anything Kestrel itself logs about it.
        await harness.StopAsync();
        harness.Logs.Events.Where(e => e.Level >= LogEventLevel.Error).ShouldHaveSingleItem()
            .RenderMessage().ShouldBe("Unhandled exception in \"GET\" \"/late-boom\"");
        RequestLine(harness).Level.ShouldBe(LogEventLevel.Warning);
    }

    [Fact]
    public async Task A_request_aborted_by_the_client_is_a_warning_without_an_alert()
    {
        await using var harness = await RequestPipelineHarness.StartAsync();

        try
        {
            using var response = await harness.Client.GetAsync(new Uri("/aborted", UriKind.Relative), TestContext.Current.CancellationToken);
        }
        catch (HttpRequestException)
        {
            // The server ended the connection on purpose.
        }
        catch (OperationCanceledException)
        {
            // Same, as the test client reports it.
        }

        // The client learns of the abort before the server has finished its request line.
        (await WaitForAsync(() => harness.Logs.Events.Any(e => e.MessageTemplate.Text.StartsWith("HTTP ", StringComparison.Ordinal))))
            .ShouldBeTrue("no request line was logged");
        harness.Logs.Events.ShouldNotContain(e => e.Level >= LogEventLevel.Error);
        harness.Logs.Events.Single(e => e.Level == LogEventLevel.Warning && e.MessageTemplate.Text.StartsWith("Request ", StringComparison.Ordinal))
            .RenderMessage().ShouldBe("Request \"GET\" \"/aborted\" aborted by the client");
        RequestLine(harness).Level.ShouldBe(LogEventLevel.Warning);
        harness.Alerts.Alerts.TryRead(out _).ShouldBeFalse();
    }

    [Fact]
    public async Task The_request_line_carries_exactly_the_spec_properties_and_the_request_id()
    {
        await using var harness = await RequestPipelineHarness.StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/items/42?secret=1");
        request.Headers.Add(RequestLogging.RequestIdHeader, "client-id.7");
        using var response = await harness.Client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues(RequestLogging.RequestIdHeader).ShouldBe(["client-id.7"]);
        var requestLine = RequestLine(harness);
        requestLine.Level.ShouldBe(LogEventLevel.Information);
        requestLine.RenderMessage().ShouldMatch("^HTTP \"GET\" \"/items/\\{id\\}\" responded 200 in [0-9]+ ms$");
        requestLine.Properties.Keys.Order(StringComparer.Ordinal)
            .ShouldBe(["SourceContext", "duration_ms", "method", "request_id", "route", "status"]);
        harness.Logs.Text.ShouldNotContain("/items/42");
        harness.Logs.Text.ShouldNotContain("secret=1");
        RequestIdOf(requestLine).ShouldBe("client-id.7");
        RequestIdOf(harness.Logs.Events.Single(e => e.MessageTemplate.Text == "Item {Id} read")).ShouldBe("client-id.7");
    }

    [Theory]
    [InlineData("has spaces")]
    [InlineData("new\\nline")]
    [InlineData("<script>")]
    [InlineData("")]
    public async Task An_unsafe_or_missing_request_id_is_replaced(string offered)
    {
        await using var harness = await RequestPipelineHarness.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/items/1");
        request.Headers.TryAddWithoutValidation(RequestLogging.RequestIdHeader, offered);

        using var response = await harness.Client.SendAsync(request, TestContext.Current.CancellationToken);

        var requestId = response.Headers.GetValues(RequestLogging.RequestIdHeader).ShouldHaveSingleItem();
        requestId.ShouldNotBe(offered);
        requestId.ShouldMatch("^[0-9a-f]{32}$");
    }

    [Theory]
    [InlineData(RequestLogging.MaxRequestIdChars, true)]
    [InlineData(RequestLogging.MaxRequestIdChars + 1, false)]
    public async Task A_request_id_is_echoed_up_to_its_length_limit(int length, bool echoed)
    {
        await using var harness = await RequestPipelineHarness.StartAsync();
        var offered = new string('a', length);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/items/1");
        request.Headers.Add(RequestLogging.RequestIdHeader, offered);

        using var response = await harness.Client.SendAsync(request, TestContext.Current.CancellationToken);

        (response.Headers.GetValues(RequestLogging.RequestIdHeader).ShouldHaveSingleItem() == offered).ShouldBe(echoed);
    }

    [Fact]
    public async Task The_error_reaches_the_alert_queue_with_its_type_and_without_its_message()
    {
        await using var harness = await RequestPipelineHarness.StartAsync();

        using var response = await harness.Client.GetAsync(new Uri("/boom", UriKind.Relative), TestContext.Current.CancellationToken);

        harness.Alerts.Alerts.TryRead(out var alert).ShouldBeTrue();
        alert!.Text.ShouldContain("/boom");
        alert.Text.ShouldEndWith("\nInvalidOperationException"); // the type; the message, which held the token, stays in the log
        alert.Text.ShouldNotContain(RequestPipelineHarness.BotToken);
        alert.Text.ShouldNotContain("boom failed with");
        harness.Alerts.Alerts.TryRead(out _).ShouldBeFalse();
    }

    private const int WaitMilliseconds = 10_000;
    private const int PollMilliseconds = 20;

    private static async Task<bool> WaitForAsync(Func<bool> condition)
    {
        for (var waited = 0; waited < WaitMilliseconds; waited += PollMilliseconds)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(PollMilliseconds, TestContext.Current.CancellationToken);
        }

        return condition();
    }

    private static LogEvent RequestLine(RequestPipelineHarness harness) =>
        harness.Logs.Events.Single(e => e.MessageTemplate.Text.StartsWith("HTTP ", StringComparison.Ordinal));

    private static string RequestIdOf(LogEvent logEvent) =>
        logEvent.Properties.TryGetValue(RequestLogging.RequestIdProperty, out var value) && value is ScalarValue { Value: string id } ? id : "(none)";
}
