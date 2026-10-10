using System.Text.Json;
using Exchange.Api.Bot;
using Exchange.Api.Settings;
using Exchange.Api.Tests.Logging;
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
/// The development poller: it moves past each update before handling it, so a failing update is
/// not fetched forever; one bad update does not stop it; a failed poll pauses and keeps the offset;
/// it refuses demo mode.
/// </summary>
public sealed class BotPollerTests
{
    private readonly CapturingSink _logs = new();
    private readonly FakeTimeProvider _time = new();

    [Fact]
    public async Task Each_update_is_handled_and_the_offset_moves_past_the_last()
    {
        var handler = new RecordingHandler(failOn: null);

        var next = await Poller(new PollingBot([Update(5), Update(6)]), handler).PollOnceAsync(0, TestContext.Current.CancellationToken);

        next.ShouldBe(7);
        handler.Handled.ShouldBe([5, 6]);
    }

    [Fact]
    public async Task A_failing_update_is_logged_and_the_next_one_still_handled()
    {
        var handler = new RecordingHandler(failOn: 5);

        var next = await Poller(new PollingBot([Update(5), Update(6)]), handler).PollOnceAsync(0, TestContext.Current.CancellationToken);

        next.ShouldBe(7); // moved past the failing one too
        handler.Handled.ShouldBe([6]);
        _logs.Events.Where(e => e.Level == LogEventLevel.Error).ShouldHaveSingleItem().MessageTemplate.Text.ShouldContain("{UpdateId}");
    }

    [Fact]
    public async Task A_failed_poll_pauses_five_seconds_and_keeps_the_offset()
    {
        var round = Poller(new PollingBot(null), new RecordingHandler(null)).PollOnceAsync(3, TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        round.IsCompleted.ShouldBeFalse();
        _time.Advance(TimeSpan.FromMilliseconds(4999));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        round.IsCompleted.ShouldBeFalse();
        _time.Advance(TimeSpan.FromMilliseconds(1));

        (await round).ShouldBe(3);
        _logs.Events.Where(e => e.Level == LogEventLevel.Error).ShouldHaveSingleItem().MessageTemplate.Text.ShouldContain("polling failed");
    }

    [Fact]
    public async Task The_poller_refuses_demo_mode() =>
        await Should.ThrowAsync<InvalidOperationException>(() => Poller(new PollingBot([]), new RecordingHandler(null), demo: true).StartAsync(TestContext.Current.CancellationToken));

    [Fact]
    public void The_poll_client_outlasts_a_long_poll()
    {
        using var client = BotSetup.CreateClient(BotSetup.PollRequestTimeoutSeconds);

        client.Timeout.ShouldBeGreaterThan(TimeSpan.FromSeconds(BotPoller.LongPollSeconds));
    }

    private static JsonElement Update(long id)
    {
        using var document = JsonDocument.Parse("{\"update_id\": " + id.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}");
        return document.RootElement.Clone();
    }

    private BotPoller Poller(PollingBot bot, RecordingHandler handler, bool demo = false)
    {
        var environment = demo ? TestSettings.Demo() : TestSettings.Production();
        var settings = SettingsLoader.Load(demo ? AppEnvironment.Demo : AppEnvironment.Test, key => environment.GetValueOrDefault(key), length => new byte[length]);
        var services = new ServiceCollection().AddSingleton<IBotUpdateHandler>(handler).BuildServiceProvider();
#pragma warning disable CA2000 // the factory lives as long as the test
        var factory = new SerilogLoggerFactory(new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(_logs).CreateLogger(), dispose: true);
#pragma warning restore CA2000
        return new BotPoller(bot, services.GetRequiredService<IServiceScopeFactory>(), settings, _time, factory.CreateLogger<BotPoller>());
    }

    private sealed class RecordingHandler(long? failOn) : IBotUpdateHandler
    {
        public List<long> Handled { get; } = [];

        public Task HandleAsync(JsonElement update, CancellationToken cancellationToken)
        {
            var id = update.GetProperty("update_id").GetInt64();
            if (id == failOn)
            {
                return Task.FromException(new InvalidOperationException("bad update"));
            }

            Handled.Add(id);
            return Task.CompletedTask;
        }
    }

    /// <summary>Answers getUpdates with the given updates, or fails when given none.</summary>
    private sealed class PollingBot(IReadOnlyList<JsonElement>? updates) : IBotClient
    {
        public Task SendMessageAsync(OutgoingMessage message, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AnswerCallbackAsync(string callbackQueryId, string? notice, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteWebhookAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<JsonElement>> GetUpdatesAsync(long offset, int waitSeconds, CancellationToken cancellationToken) =>
            updates is null ? Task.FromException<IReadOnlyList<JsonElement>>(new BotException("http_502")) : Task.FromResult(updates);
    }
}
