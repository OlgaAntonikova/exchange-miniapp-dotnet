using System.Net;
using System.Text.Json;
using Exchange.Api.Bot;
using Exchange.Api.Settings;
using Exchange.Api.Tests.Rates;
using Exchange.Api.Tests.Settings;
using Shouldly;

namespace Exchange.Api.Tests.Bot;

/// <summary>The Bot API over plain JSON: the exact calls, and failures that never name the URL (it holds the token).</summary>
public sealed class TelegramBotClientTests
{
    [Fact]
    public async Task A_message_with_buttons_is_posted_with_one_button_per_row()
    {
        var http = Answering(HttpStatusCode.OK, """{"ok":true,"result":{}}""");

        await Client(http).SendMessageAsync(new OutgoingMessage(-100, "New order 7", [new BotButton("Confirm", "order:7:CONFIRMED"), new BotButton("Cancel", "order:7:CANCELLED")]), TestContext.Current.CancellationToken);

        var request = http.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe(HttpMethod.Post);
        request.RequestUri!.ToString().ShouldBe("https://api.telegram.org/bot" + TestSettings.BotToken + "/sendMessage");
        (await request.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe(
            """{"chat_id":-100,"text":"New order 7","disable_web_page_preview":true,"reply_markup":{"inline_keyboard":[[{"text":"Confirm","callback_data":"order:7:CONFIRMED"}],[{"text":"Cancel","callback_data":"order:7:CANCELLED"}]]}}""");
    }

    [Fact]
    public async Task A_message_without_buttons_has_no_keyboard()
    {
        var http = Answering(HttpStatusCode.OK, """{"ok":true}""");

        await Client(http).SendMessageAsync(new OutgoingMessage(5, "Your order 7: confirmed", []), TestContext.Current.CancellationToken);

        using var body = JsonDocument.Parse(await http.Requests.Single().Content!.ReadAsStringAsync(TestContext.Current.CancellationToken));
        body.RootElement.TryGetProperty("reply_markup", out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Done")]
    public async Task A_button_press_is_answered(string? notice)
    {
        var http = Answering(HttpStatusCode.OK, """{"ok":true}""");

        await Client(http).AnswerCallbackAsync("query-1", notice, TestContext.Current.CancellationToken);

        var request = http.Requests.Single();
        request.RequestUri!.AbsolutePath.ShouldEndWith("/answerCallbackQuery");
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken));
        body.RootElement.GetProperty("callback_query_id").GetString().ShouldBe("query-1");
        (body.RootElement.TryGetProperty("text", out var text) ? text.GetString() : null).ShouldBe(notice);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, """{"ok":false}""", "http_400")]
    [InlineData(HttpStatusCode.TooManyRequests, """{"ok":false}""", "http_429")]
    [InlineData(HttpStatusCode.OK, """{"ok":false}""", "not_ok")]
    [InlineData(HttpStatusCode.OK, "not json", "not_ok")]
    [InlineData(HttpStatusCode.OK, "{}", "not_ok")]
    public async Task An_unaccepted_call_is_a_failure_without_the_token(HttpStatusCode status, string answer, string reason)
    {
        var error = await Should.ThrowAsync<BotException>(() => Client(Answering(status, answer)).SendMessageAsync(new OutgoingMessage(1, "x", []), TestContext.Current.CancellationToken));

        error.Reason.ShouldBe(reason);
        error.ToString().ShouldNotContain(TestSettings.BotToken);
    }

    [Fact]
    public async Task A_transport_failure_never_carries_the_url()
    {
        var http = StubHttp.Throwing(new HttpRequestException("refused: https://api.telegram.org/bot" + TestSettings.BotToken + "/sendMessage"));

        var error = await Should.ThrowAsync<BotException>(() => Client(http).SendMessageAsync(new OutgoingMessage(1, "x", []), TestContext.Current.CancellationToken));

        error.Reason.ShouldBe("transport_HttpRequestException");
        error.ToString().ShouldNotContain(TestSettings.BotToken);
        error.InnerException.ShouldBeNull();
    }

    [Fact]
    public async Task A_timeout_is_a_timeout() =>
        (await Should.ThrowAsync<BotException>(() => Client(StubHttp.Throwing(new TaskCanceledException("t", new TimeoutException()))).SendMessageAsync(new OutgoingMessage(1, "x", []), TestContext.Current.CancellationToken))).Reason.ShouldBe("timeout");

    [Fact]
    public async Task A_stopping_app_is_a_cancellation_not_a_failure()
    {
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => Client(StubHttp.Throwing(new TaskCanceledException())).SendMessageAsync(new OutgoingMessage(1, "x", []), stopping.Token));
    }

    [Fact]
    public void The_bot_client_has_a_timeout_and_a_reply_limit()
    {
        using var client = BotSetup.CreateClient();

        client.Timeout.ShouldBe(TimeSpan.FromSeconds(10));
        client.MaxResponseContentBufferSize.ShouldBe(64 * 1024);
    }

    private static StubHttp Answering(HttpStatusCode status, string body) =>
        new(_ => new HttpResponseMessage(status) { Content = new StringContent(body) });

#pragma warning disable CA2000 // the client owns the handler; both live as long as the test
    private static TelegramBotClient Client(StubHttp http) => new(new HttpClient(http), new Secret(TestSettings.BotToken));
#pragma warning restore CA2000
}
