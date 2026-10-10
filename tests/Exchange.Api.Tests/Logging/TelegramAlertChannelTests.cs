using System.Net;
using System.Text.Json;
using Exchange.Api.Alerts;
using Exchange.Api.Settings;
using Exchange.Api.Tests.Rates;
using Exchange.Api.Tests.Settings;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Exchange.Api.Tests.Logging;

/// <summary>
/// Spec 4, Alerting, and the PHASE-3 gate: an ERROR reaches the operator chat through the Bot API.
/// A failed delivery names a reason and never the URL, which holds the bot token (RULE-SECRETS).
/// </summary>
public sealed class TelegramAlertChannelTests
{
    private const long OperatorChatId = -1001234;

    private static readonly Alert Sample = new("Exchange.Api.Rates | Rate fetch {Provider} failed", "Rate fetch \"cbr\" failed: timeout", DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task An_alert_is_posted_to_send_message_for_the_operator_chat()
    {
        var http = Answering(HttpStatusCode.OK);

        await Channel(http).SendAsync(Sample, TestContext.Current.CancellationToken);

        var request = http.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe(HttpMethod.Post);
        request.RequestUri!.ToString().ShouldBe("https://api.telegram.org/bot" + TestSettings.BotToken + "/sendMessage");
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken));
        body.RootElement.EnumerateObject().Select(p => p.Name).ShouldBe(["chat_id", "text", "disable_web_page_preview"], ignoreOrder: true);
        body.RootElement.GetProperty("chat_id").GetInt64().ShouldBe(OperatorChatId);
        body.RootElement.GetProperty("text").GetString().ShouldBe(Sample.Text);
        body.RootElement.GetProperty("disable_web_page_preview").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task A_text_over_the_telegram_limit_is_cut_with_a_mark()
    {
        var http = Answering(HttpStatusCode.OK);
        var longAlert = Sample with { Text = new string('x', 5000) };

        await Channel(http).SendAsync(longAlert, TestContext.Current.CancellationToken);

        using var body = JsonDocument.Parse(await http.Requests.Single().Content!.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var text = body.RootElement.GetProperty("text").GetString()!;
        text.Length.ShouldBe(4096);
        text.ShouldEndWith("\n[truncated]");
    }

    [Fact]
    public async Task A_cut_never_splits_a_surrogate_pair()
    {
        var http = Answering(HttpStatusCode.OK);
        var emoji = char.ConvertFromUtf32(0x1F600);
        var cutAt = 4096 - "\n[truncated]".Length;
        var text = new string('x', cutAt - 1) + emoji + new string('x', 100); // the pair spans the cut

        await Channel(http).SendAsync(Sample with { Text = text }, TestContext.Current.CancellationToken);

        using var body = JsonDocument.Parse(await http.Requests.Single().Content!.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var sent = body.RootElement.GetProperty("text").GetString()!;
        sent.ShouldBe(new string('x', cutAt - 1) + "\n[truncated]");
    }

    [Fact]
    public async Task A_text_at_the_limit_is_sent_whole()
    {
        var http = Answering(HttpStatusCode.OK);

        await Channel(http).SendAsync(Sample with { Text = new string('x', 4096) }, TestContext.Current.CancellationToken);

        using var body = JsonDocument.Parse(await http.Requests.Single().Content!.ReadAsStringAsync(TestContext.Current.CancellationToken));
        body.RootElement.GetProperty("text").GetString().ShouldBe(new string('x', 4096));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "http_400")]
    [InlineData(HttpStatusCode.Unauthorized, "http_401")]
    [InlineData(HttpStatusCode.TooManyRequests, "http_429")]
    [InlineData(HttpStatusCode.NoContent, "http_204")]
    public async Task A_non_200_answer_is_a_failed_delivery_without_the_token(HttpStatusCode status, string reason)
    {
        var error = await Should.ThrowAsync<AlertDeliveryException>(() => Channel(Answering(status)).SendAsync(Sample, TestContext.Current.CancellationToken));

        error.Reason.ShouldBe(reason);
        error.ToString().ShouldNotContain(TestSettings.BotToken);
    }

    [Fact]
    public async Task A_transport_failure_names_its_type_and_never_the_url()
    {
        var http = StubHttp.Throwing(new HttpRequestException("connection refused to https://api.telegram.org/bot" + TestSettings.BotToken + "/sendMessage"));

        var error = await Should.ThrowAsync<AlertDeliveryException>(() => Channel(http).SendAsync(Sample, TestContext.Current.CancellationToken));

        error.Reason.ShouldBe("transport_HttpRequestException");
        error.ToString().ShouldNotContain(TestSettings.BotToken);
        error.InnerException.ShouldBeNull();
    }

    [Fact]
    public async Task A_timeout_is_a_timeout()
    {
        var http = StubHttp.Throwing(new TaskCanceledException("timed out", new TimeoutException()));

        (await Should.ThrowAsync<AlertDeliveryException>(() => Channel(http).SendAsync(Sample, TestContext.Current.CancellationToken))).Reason.ShouldBe("timeout");
    }

    [Fact]
    public async Task A_stopping_app_is_not_a_failed_delivery()
    {
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => Channel(StubHttp.Throwing(new TaskCanceledException())).SendAsync(Sample, stopping.Token));
    }

    [Fact]
    public void Outside_demo_mode_the_app_alerts_through_telegram()
    {
        using var factory = new TestAppFactory(TestSettings.Production());

        factory.Services.GetRequiredService<IAlertChannel>().ShouldBeOfType<TelegramAlertChannel>();
    }

    [Fact]
    public void The_alert_client_has_a_timeout_and_a_reply_limit()
    {
        using var client = TelegramAlertSetup.CreateClient();

        client.Timeout.ShouldBe(TimeSpan.FromSeconds(10));
        client.MaxResponseContentBufferSize.ShouldBe(64 * 1024);
    }

    private static StubHttp Answering(HttpStatusCode status) =>
        new(_ => new HttpResponseMessage(status) { Content = new StringContent("{\"ok\":true}") });

#pragma warning disable CA2000 // the client owns the handler; both live as long as the test
    private static TelegramAlertChannel Channel(StubHttp http) => new(new HttpClient(http), new Secret(TestSettings.BotToken), OperatorChatId);
#pragma warning restore CA2000
}
