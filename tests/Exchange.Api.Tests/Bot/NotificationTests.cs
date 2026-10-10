using System.Collections.Concurrent;
using Exchange.Api.Bot;
using Exchange.Api.Data;
using Exchange.Api.Settings;
using Exchange.Api.Tests.Data;
using Exchange.Api.Tests.Logging;
using Exchange.Api.Tests.Orders;
using Exchange.Api.Tests.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Shouldly;

namespace Exchange.Api.Tests.Bot;

/// <summary>
/// Spec 1.5: a notification is tried 3 times with backoff (1 s, then 2 s), a retry is a WARNING,
/// giving up an ERROR naming the operation and the order, and the order stands. In demo mode
/// nothing is sent (spec 1.9). The text, which carries the client's details, is never logged.
/// </summary>
public sealed class NotificationTests(PostgresFixture database)
{
    private const string SecretDetail = "ME25505000012345678951";

    private static readonly Notification Sample = new(new OutgoingMessage(-100, "New order 7\nIBAN: " + SecretDetail, []), "new order", 7);

    private readonly CapturingSink _logs = new();
    private readonly FakeTimeProvider _time = new();

    [Fact]
    public async Task A_message_taken_at_once_is_sent_once()
    {
        var bot = new ScriptedBot([]);

        (await Dispatcher(bot).DeliverAsync(Sample, TestContext.Current.CancellationToken)).ShouldBeTrue();

        bot.Sent.Count.ShouldBe(1);
        _logs.Events.Where(e => e.Level == LogEventLevel.Information).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_failed_attempt_is_retried_after_one_second_then_two()
    {
        var bot = new ScriptedBot(["http_502", "timeout"]);
        var delivery = Dispatcher(bot).DeliverAsync(Sample, TestContext.Current.CancellationToken);

        await WaitForAsync(() => bot.Calls == 1);
        _time.Advance(TimeSpan.FromMilliseconds(999));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        bot.Calls.ShouldBe(1); // not before the first second
        _time.Advance(TimeSpan.FromMilliseconds(1));
        await WaitForAsync(() => bot.Calls == 2);
        _time.Advance(TimeSpan.FromMilliseconds(1999));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        bot.Calls.ShouldBe(2); // the second wait is two seconds
        _time.Advance(TimeSpan.FromMilliseconds(1));

        (await delivery).ShouldBeTrue();
        bot.Calls.ShouldBe(3);
        _logs.Events.Count(e => e.Level == LogEventLevel.Warning).ShouldBe(2);
        _logs.Events.ShouldNotContain(e => e.Level >= LogEventLevel.Error);
    }

    [Fact]
    public async Task After_three_failures_it_gives_up_with_an_error_naming_the_operation_and_the_order()
    {
        var bot = new ScriptedBot(["http_500", "http_500", "http_500"]);
        var delivery = Dispatcher(bot).DeliverAsync(Sample, TestContext.Current.CancellationToken);
        await WaitForAsync(() => bot.Calls == 1);
        _time.Advance(TimeSpan.FromSeconds(1));
        await WaitForAsync(() => bot.Calls == 2);
        _time.Advance(TimeSpan.FromSeconds(2));

        (await delivery).ShouldBeFalse();
        bot.Calls.ShouldBe(3);
        var error = _logs.Events.Where(e => e.Level == LogEventLevel.Error).ShouldHaveSingleItem();
        error.Properties["Operation"].ToString().ShouldContain("new order");
        error.Properties["OrderId"].ToString().ShouldBe("7");
        error.Properties["Reason"].ToString().ShouldContain("http_500");
    }

    [Fact]
    public async Task An_unexpected_exception_is_an_error_and_not_retried()
    {
        var bot = new ScriptedBot(["boom"]);

        (await Dispatcher(bot).DeliverAsync(Sample, TestContext.Current.CancellationToken)).ShouldBeFalse();

        bot.Calls.ShouldBe(1);
        _logs.Events.Where(e => e.Level == LogEventLevel.Error).ShouldHaveSingleItem().Properties["Reason"].ToString().ShouldContain("unexpected_InvalidOperationException");
    }

    [Fact]
    public async Task In_demo_mode_nothing_is_sent_and_one_info_names_the_kind_and_the_order()
    {
        var bot = new ScriptedBot([]);

        (await Dispatcher(bot, demo: true).DeliverAsync(Sample, TestContext.Current.CancellationToken)).ShouldBeTrue();

        bot.Calls.ShouldBe(0);
        var info = _logs.Events.ShouldHaveSingleItem();
        info.Level.ShouldBe(LogEventLevel.Information);
        info.MessageTemplate.Text.ShouldContain("suppressed");
    }

    [Fact]
    public async Task The_message_text_is_never_logged()
    {
        var bot = new ScriptedBot(["http_500", "http_500", "http_500"]);
        var delivery = Dispatcher(bot).DeliverAsync(Sample, TestContext.Current.CancellationToken);
        await WaitForAsync(() => bot.Calls == 1);
        _time.Advance(TimeSpan.FromSeconds(1));
        await WaitForAsync(() => bot.Calls == 2);
        _time.Advance(TimeSpan.FromSeconds(2));
        await delivery;

        _logs.Text.ShouldNotContain(SecretDetail);
        _logs.Text.ShouldNotContain("New order 7");
    }

    [Fact]
    public async Task A_stopping_app_during_a_delivery_is_a_cancellation_not_an_error()
    {
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();
        var bot = new ScriptedBot(["cancelled"]);

        await Should.ThrowAsync<OperationCanceledException>(() => Dispatcher(bot).DeliverAsync(Sample, stopping.Token));

        _logs.Events.ShouldNotContain(e => e.Level >= LogEventLevel.Error);
    }

    // --- Building a message never fails the change ---------------------------------------------------

    [Fact]
    public async Task A_new_order_message_that_cannot_be_built_is_an_error_not_an_exception()
    {
        var outbox = new NotificationOutbox(Logger<NotificationOutbox>());
        var notifications = new OrderNotifications(new BotMessages(TextsWithout("operator_new_order"), null!), outbox, Settings(), Logger<OrderNotifications>());

        await notifications.OrderCreatedAsync(new Order { Id = 7, RateProvider = "ecb" }, new AppUser { TelegramId = 5 });

        outbox.Reader.TryRead(out _).ShouldBeFalse();
        var error = _logs.Events.Where(e => e.Level == LogEventLevel.Error).ShouldHaveSingleItem();
        error.Properties["Operation"].ToString().ShouldContain("new order");
        error.Properties["OrderId"].ToString().ShouldBe("7");
    }

    [Theory]
    [InlineData("client_status_changed", "client status", "operator status")]
    [InlineData("operator_order_status", "operator status", "client status")]
    public void A_status_message_that_cannot_be_built_is_an_error_and_the_other_still_goes(string missingKey, string failed, string sent)
    {
        var outbox = new NotificationOutbox(Logger<NotificationOutbox>());
        var notifications = new OrderNotifications(new BotMessages(TextsWithout(missingKey), null!), outbox, Settings(), Logger<OrderNotifications>());

        notifications.StatusChanged(new Order { Id = 7, Status = Exchange.Domain.Orders.OrderStatus.Confirmed, RateProvider = "ecb" }, new AppUser { TelegramId = 5 });

        outbox.Reader.TryRead(out var queued).ShouldBeTrue();
        queued!.Operation.ShouldBe(sent);
        outbox.Reader.TryRead(out _).ShouldBeFalse();
        _logs.Events.Where(e => e.Level == LogEventLevel.Error).ShouldHaveSingleItem().Properties["Operation"].ToString().ShouldContain(failed);
    }

    // --- Through the app: placing and cancelling an order --------------------------------------------

    [Fact]
    public async Task A_placed_order_reaches_the_operator_chat_with_its_buttons()
    {
        var bot = new ScriptedBot([]);
        await using var app = await AppAsync(bot);

        var (_, order) = await app.PostAsync(7_500_001, OrderApiTests.WireOrder());

        await WaitForAsync(() => bot.Sent.Count == 1);
        var message = bot.Sent.Single();
        message.ChatId.ShouldBe(-1001234); // OPERATOR_CHAT_ID of the test settings
        message.Text.ShouldContain(order.GetProperty("id").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture));
        message.Text.ShouldContain("ME25505000012345678951"); // the operator sees the details in full
        message.Buttons.Select(b => b.CallbackData).ShouldBe([$"order:{order.GetProperty("id").GetInt32()}:CONFIRMED", $"order:{order.GetProperty("id").GetInt32()}:CANCELLED"]);
    }

    [Fact]
    public async Task A_cancel_messages_the_client_and_tells_the_operator_with_no_buttons_left()
    {
        var bot = new ScriptedBot([]);
        await using var app = await AppAsync(bot);
        var id = await OrderTransitionTests.PlaceAsync(app, 7_500_002);
        await WaitForAsync(() => bot.Sent.Count == 1);

        using var response = await app.SendAsync(7_500_002, HttpMethod.Post, $"/api/orders/{id}/cancel", null);

        await WaitForAsync(() => bot.Sent.Count == 3);
        var client = bot.Sent.Single(m => m.ChatId == 7_500_002);
        client.Buttons.ShouldBeEmpty();
        client.Text.ShouldContain(id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var operatorMessage = bot.Sent.Skip(1).Single(m => m.ChatId == -1001234);
        operatorMessage.Buttons.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_refused_cancel_messages_nobody()
    {
        var bot = new ScriptedBot([]);
        await using var app = await AppAsync(bot);
        var id = await OrderTransitionTests.PlaceAsync(app, 7_500_003);
        await WaitForAsync(() => bot.Sent.Count == 1);
        using (await app.SendAsync(7_500_003, HttpMethod.Post, $"/api/orders/{id}/cancel", null))
        {
        }

        await WaitForAsync(() => bot.Sent.Count == 3);

        using var refused = await app.SendAsync(7_500_003, HttpMethod.Post, $"/api/orders/{id}/cancel", null);
        await Task.Delay(300, TestContext.Current.CancellationToken);

        bot.Sent.Count.ShouldBe(3);
    }

    // The shipped texts, less one key.
    private static Exchange.Api.Locale.LocaleTexts TextsWithout(string key)
    {
        var path = System.IO.Path.Combine(Repository.RepositoryRoot.Path, "src", "Exchange.Api", "wwwroot", "locale", "ru.json");
        var texts = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!;
        texts.Remove(key);
        return Exchange.Api.Locale.LocaleTexts.Load(System.Text.Json.JsonSerializer.Serialize(texts), []);
    }

    private static AppSettings Settings() =>
        SettingsLoader.Load(AppEnvironment.Test, key => TestSettings.Production().GetValueOrDefault(key), length => new byte[length]);

    private Microsoft.Extensions.Logging.ILogger<T> Logger<T>()
    {
#pragma warning disable CA2000 // the factory lives as long as the test
        var factory = new SerilogLoggerFactory(new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(_logs).CreateLogger(), dispose: true);
#pragma warning restore CA2000
        return factory.CreateLogger<T>();
    }

    private NotificationDispatcher Dispatcher(IBotClient bot, bool demo = false)
    {
        var environment = demo ? TestSettings.Demo() : TestSettings.Production();
        var settings = SettingsLoader.Load(demo ? AppEnvironment.Demo : AppEnvironment.Test, key => environment.GetValueOrDefault(key), length => new byte[length]);
#pragma warning disable CA2000 // the factory lives as long as the test's dispatcher
        var factory = new SerilogLoggerFactory(new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(_logs).CreateLogger(), dispose: true);
#pragma warning restore CA2000
        return new NotificationDispatcher(new NotificationOutbox(factory.CreateLogger<NotificationOutbox>()), bot, settings, _time, factory.CreateLogger<NotificationDispatcher>());
    }

    private async Task<OrderApiTests.OrderApp> AppAsync(ScriptedBot bot)
    {
        var environment = TestSettings.Production();
        environment["DATABASE_URL"] = await database.CreateMigratedDatabaseUrlAsync();
        return new OrderApiTests.OrderApp(environment, services: services => services.AddSingleton<IBotClient>(bot));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var waited = 0; waited < 10_000 && !condition(); waited += 20)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        condition().ShouldBeTrue("the condition never held");
    }

    /// <summary>A bot that fails each call with the next scripted reason ("boom": an unexpected exception), then succeeds.</summary>
    internal sealed class ScriptedBot(IEnumerable<string> failures) : IBotClient
    {
        private readonly ConcurrentQueue<string> _failures = new(failures);
        private int _calls;

        public ConcurrentQueue<OutgoingMessage> Sent { get; } = new();

        public int Calls => _calls;

        public Task SendMessageAsync(OutgoingMessage message, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            if (_failures.TryDequeue(out var reason))
            {
                return Task.FromException(reason switch
                {
                    "boom" => new InvalidOperationException("unexpected"),
                    "cancelled" => new OperationCanceledException(cancellationToken),
                    _ => new BotException(reason),
                });
            }

            Sent.Enqueue(message);
            return Task.CompletedTask;
        }

        public ConcurrentQueue<(string Id, string? Notice)> Answers { get; } = new();

        public Task AnswerCallbackAsync(string callbackQueryId, string? notice, CancellationToken cancellationToken)
        {
            Answers.Enqueue((callbackQueryId, notice));
            return Task.CompletedTask;
        }

        public Task DeleteWebhookAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<System.Text.Json.JsonElement>> GetUpdatesAsync(long offset, int waitSeconds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<System.Text.Json.JsonElement>>([]);
    }
}
