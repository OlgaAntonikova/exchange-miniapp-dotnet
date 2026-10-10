using System.Net;
using System.Text;
using System.Text.Json;
using Exchange.Api.Bot;
using Exchange.Api.Tests.Auth;
using Exchange.Api.Tests.Data;
using Exchange.Api.Tests.Logging;
using Exchange.Api.Tests.Orders;
using Exchange.Api.Tests.Settings;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Events;
using Shouldly;

namespace Exchange.Api.Tests.Bot;

/// <summary>
/// INV-11 and spec 1.5: the webhook takes only calls carrying the registered secret, and a button
/// press changes an order only when it comes from an id in <c>OPERATOR_TELEGRAM_IDS</c> (111 and 222
/// in the test settings) and names a move that still applies; the client hears of every change.
/// Anything else changes nothing.
/// </summary>
public sealed class BotWebhookTests(PostgresFixture database)
{
    private const long ClientId = 7_600_001;
    private const long OperatorId = 111;
    private const long StrangerId = 999;
    private const string WebhookSecret = "webhook_secret-1";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("webhook_secret-2")]
    [InlineData("webhook_secret-")]
    [InlineData("WEBHOOK_SECRET-1")]
    public async Task A_call_without_the_secret_is_401_and_changes_nothing(string? secret)
    {
        var bot = new NotificationTests.ScriptedBot([]);
        await using var app = await AppAsync(bot);
        var id = await OrderTransitionTests.PlaceAsync(app, ClientId);

        using var response = await PostUpdateAsync(app, Press(OperatorId, $"order:{id}:CONFIRMED"), secret);

        await AuthApiTests.ShouldBeUnauthorizedAsync(response);
        (await app.ScalarAsync($"SELECT status FROM orders WHERE id = {id}")).ShouldBe("PENDING");
        bot.Answers.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_operator_press_moves_the_order_and_tells_the_client_and_the_operator()
    {
        var bot = new NotificationTests.ScriptedBot([]);
        await using var app = await AppAsync(bot);
        var id = await OrderTransitionTests.PlaceAsync(app, ClientId);
        await WaitForAsync(() => bot.Sent.Count == 1); // the new order's message

        using var response = await PostUpdateAsync(app, Press(OperatorId, $"order:{id}:CONFIRMED"), WebhookSecret);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe("""{"ok":true}""");
        (await app.ScalarAsync($"SELECT status FROM orders WHERE id = {id}")).ShouldBe("CONFIRMED");
        (await app.ScalarAsync($"SELECT count(*) FROM order_status_history WHERE order_id = {id} AND to_status = 'CONFIRMED' AND actor = 'operator' AND actor_telegram_id = {OperatorId}")).ShouldBe(1L);
        await WaitForAsync(() => bot.Sent.Count == 3);
        bot.Sent.ShouldContain(m => m.ChatId == ClientId);
        bot.Sent.Skip(1).Single(m => m.ChatId == -1001234).Buttons.Select(b => b.CallbackData).ShouldBe([$"order:{id}:PROCESSING", $"order:{id}:CANCELLED"]);
        bot.Answers.ShouldHaveSingleItem().Id.ShouldBe("press-1");
    }

    [Fact]
    public async Task The_operator_walks_an_order_to_completed_and_the_client_hears_each_step()
    {
        var bot = new NotificationTests.ScriptedBot([]);
        await using var app = await AppAsync(bot);
        var id = await OrderTransitionTests.PlaceAsync(app, ClientId);

        foreach (var status in new[] { "CONFIRMED", "PROCESSING", "COMPLETED" })
        {
            using var response = await PostUpdateAsync(app, Press(222, $"order:{id}:{status}"), WebhookSecret);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await app.ScalarAsync($"SELECT status FROM orders WHERE id = {id}")).ShouldBe(status);
        }

        await WaitForAsync(() => bot.Sent.Count(m => m.ChatId == ClientId) == 3);
        (await app.ScalarAsync($"SELECT closed_at IS NOT NULL FROM orders WHERE id = {id}")).ShouldBe(true);
    }

    [Fact]
    public async Task A_press_from_someone_who_is_not_an_operator_changes_nothing()
    {
        var bot = new NotificationTests.ScriptedBot([]);
        var logs = new CapturingSink();
        await using var app = await AppAsync(bot, logs);
        var id = await OrderTransitionTests.PlaceAsync(app, ClientId);

        using var stranger = await PostUpdateAsync(app, Press(StrangerId, $"order:{id}:CONFIRMED"), WebhookSecret);
        using var client = await PostUpdateAsync(app, Press(ClientId, $"order:{id}:CANCELLED"), WebhookSecret); // even the order's own client

        stranger.StatusCode.ShouldBe(HttpStatusCode.OK);
        client.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await app.ScalarAsync($"SELECT status FROM orders WHERE id = {id}")).ShouldBe("PENDING");
        (await app.ScalarAsync($"SELECT count(*) FROM order_status_history WHERE order_id = {id}")).ShouldBe(1L);
        bot.Answers.Count.ShouldBe(2);
        logs.Events.Count(e => e.Level == LogEventLevel.Warning && e.MessageTemplate.Text.StartsWith("Button press refused", StringComparison.Ordinal)).ShouldBe(2);
    }

    [Theory]
    [InlineData("order:{id}:DONE")]
    [InlineData("order:{id}")]
    [InlineData("other:{id}:CONFIRMED")]
    [InlineData("order:{id}:COMPLETED")] // skips a status
    [InlineData("order:99999:CONFIRMED")] // no such order
    public async Task A_press_with_data_that_names_no_allowed_move_changes_nothing(string data)
    {
        var bot = new NotificationTests.ScriptedBot([]);
        await using var app = await AppAsync(bot);
        var id = await OrderTransitionTests.PlaceAsync(app, ClientId);

        using var response = await PostUpdateAsync(app, Press(OperatorId, data.Replace("{id}", id.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)), WebhookSecret);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await app.ScalarAsync($"SELECT status FROM orders WHERE id = {id}")).ShouldBe("PENDING");
        bot.Answers.ShouldHaveSingleItem();
        await Task.Delay(300, TestContext.Current.CancellationToken);
        bot.Sent.ShouldNotContain(m => m.ChatId == ClientId); // nobody is told of a change that did not happen
    }

    [Fact]
    public async Task A_press_of_an_outdated_button_changes_nothing_more()
    {
        var bot = new NotificationTests.ScriptedBot([]);
        await using var app = await AppAsync(bot);
        var id = await OrderTransitionTests.PlaceAsync(app, ClientId);
        using (await PostUpdateAsync(app, Press(OperatorId, $"order:{id}:CONFIRMED"), WebhookSecret))
        {
        }

        using var again = await PostUpdateAsync(app, Press(OperatorId, $"order:{id}:CONFIRMED"), WebhookSecret);

        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await app.ScalarAsync($"SELECT count(*) FROM order_status_history WHERE order_id = {id} AND to_status = 'CONFIRMED'")).ShouldBe(1L);
        bot.Answers.Count.ShouldBe(2);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        bot.Sent.Count(m => m.ChatId == ClientId).ShouldBe(1); // only the first press told the client
    }

    [Theory]
    [InlineData("""{"update_id": 1, "message": {"text": "hello"}}""")]
    [InlineData("""{"update_id": 1}""")]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("""{"callback_query": {"id": "x", "from": {"id": "111"}, "data": "order:1:CONFIRMED"}}""")] // an id that is not a number
    public async Task Anything_but_a_valid_operator_press_is_accepted_and_ignored(string update)
    {
        var bot = new NotificationTests.ScriptedBot([]);
        await using var app = await AppAsync(bot);
        var id = await OrderTransitionTests.PlaceAsync(app, ClientId);

        using var response = await PostRawAsync(app, update, WebhookSecret);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await app.ScalarAsync($"SELECT status FROM orders WHERE id = {id}")).ShouldBe("PENDING");
    }

    internal static string Press(long presser, string data) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["update_id"] = 10,
        ["callback_query"] = new Dictionary<string, object>
        {
            ["id"] = "press-1",
            ["from"] = new Dictionary<string, object> { ["id"] = presser, ["is_bot"] = false, ["first_name"] = "Op" },
            ["data"] = data,
        },
    });

    private static Task<HttpResponseMessage> PostUpdateAsync(OrderApiTests.OrderApp app, string update, string? secret) => PostRawAsync(app, update, secret);

    private static async Task<HttpResponseMessage> PostRawAsync(OrderApiTests.OrderApp app, string body, string? secret)
    {
        using var client = app.Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/bot/webhook") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (secret is not null)
        {
            request.Headers.TryAddWithoutValidation(BotEndpoints.SecretHeader, secret);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var waited = 0; waited < 10_000 && !condition(); waited += 20)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        condition().ShouldBeTrue("the condition never held");
    }

    private async Task<OrderApiTests.OrderApp> AppAsync(NotificationTests.ScriptedBot bot, CapturingSink? logs = null)
    {
        var environment = TestSettings.Production();
        environment["DATABASE_URL"] = await database.CreateMigratedDatabaseUrlAsync();
        return new OrderApiTests.OrderApp(environment, services: services => services.AddSingleton<IBotClient>(bot), logs: logs);
    }
}
